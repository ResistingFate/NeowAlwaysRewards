using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Multiplayer.Game;

// Auto-import any unresolved relic/option namespaces Rider suggests.

namespace NeowAlwaysRewards.NeowAlwaysRewardsCode;

[HarmonyPatch]
public static class NeowPatch
{
    private const string LogPrefix = "[NeowAlwaysRewards]";
    private const string DefaultNeowEntry = "NEOW";

    private static MethodBase TargetMethod()
    {
        // Explicitly target the private OnModifierOptionSelected(Func<Task>, int) overload.
        return AccessTools.Method(
            typeof(Neow),
            "OnModifierOptionSelected",
            new[] { typeof(Func<Task>), typeof(int) }
        )!;
    }

    private static bool Prefix(Neow __instance, Func<Task> modifierFunc, int index, ref Task __result)
    {
        // Replace the full modifier flow again. This keeps the remote page progression in sync,
        // which the "last modifier only" patch failed to do in multiplayer.
        __result = RunModifierSelectionFlow(__instance, modifierFunc, index);
        return false;
    }

    private static async Task RunModifierSelectionFlow(Neow neow, Func<Task> modifierFunc, int index)
    {
        // This is the main replacement flow for modifier-backed Neow starts:
        // 1) run the selected modifier callback,
        // 2) move to the next modifier immediately when there is one,
        // 3) otherwise show a reconstructed blessing page built from cached vanilla options.
        //
        // The important difference from the older full replacement is that the final blessing page
        // is made of fresh wrapper EventOptions, not the exact EventOption instances cached earlier.
        // Each wrapper delegates its OnChosen back to the original vanilla option so the reward logic
        // still runs through the same callback path, while avoiding stale/shared EventOption instances.
        NeowRewardHelper.LogStateJson(neow, $"OnModifierOptionSelected.enter[{index}]");

        await modifierFunc();

        NeowRewardHelper.LogStateJson(neow, $"OnModifierOptionSelected.afterModifierFunc[{index}]");

        var neowTr = Traverse.Create(neow);

        // Read as object because the description is not guaranteed to be a plain string.
        object? initialDescription = GetInitialDescription(neowTr);
        if (initialDescription is null)
        {
            GD.PrintErr($"{LogPrefix} InitialDescription was null for owner={NeowRewardHelper.GetOwnerId(neow)}.");
            FinishNeowEvent(neow);
            return;
        }

        List<EventOption> modifierOptions =
            neowTr.Property("ModifierOptions").GetValue<List<EventOption>>() ?? new();

        GD.Print(
            $"{LogPrefix} OnModifierOptionSelected finished. owner={NeowRewardHelper.GetOwnerId(neow)}, index={index}, modifierOptions.Count={modifierOptions.Count}"
        );

        if (index + 1 < modifierOptions.Count)
        {
            EventOption nextModifier = modifierOptions[index + 1];

            GD.Print(
                $"{LogPrefix} Showing next modifier option for owner={NeowRewardHelper.GetOwnerId(neow)}. nextKey={NeowRewardHelper.DebugOptionKey(nextModifier)}"
            );

            SetEventState(neow, initialDescription, new[] { nextModifier });
            NeowRewardHelper.LogStateJson(neow, $"OnModifierOptionSelected.afterSetNextModifier[{index}]");
            return;
        }

        IReadOnlyList<EventOption> rewards = NeowRewardHelper.GetOrBuildWrappedVanillaRewards(
            neow,
            "OnModifierOptionSelected.final"
        );

        GD.Print(
            $"{LogPrefix} Last modifier completed for owner={NeowRewardHelper.GetOwnerId(neow)}. Wrapped reward count={rewards.Count}"
        );
        LogRewardOptions(neow, rewards);

        if (rewards.Count > 0)
        {
            SetEventState(neow, initialDescription, rewards);
            NeowRewardHelper.LogStateJson(neow, $"OnModifierOptionSelected.afterSetRewards[{index}]");
            return;
        }

        GD.PrintErr(
            $"{LogPrefix} No wrapped rewards were available after modifier queue for owner={NeowRewardHelper.GetOwnerId(neow)}; finishing event."
        );
        FinishNeowEvent(neow);
    }

    private static object? GetInitialDescription(Traverse neowTr)
    {
        return neowTr.Property("InitialDescription").GetValue();
    }

    private static void LogRewardOptions(Neow neow, IEnumerable<EventOption> rewards)
    {
        foreach (EventOption? option in rewards)
        {
            GD.Print(
                $"{LogPrefix} Reward option for owner={NeowRewardHelper.GetOwnerId(neow)}: {option?.Title?.GetFormattedText() ?? "<null>"} " +
                $"(textKey={NeowRewardHelper.DebugOptionKey(option)}, relic={option?.Relic?.Id?.Entry ?? option?.Relic?.GetType().Name ?? "<none>"})"
            );
        }
    }

    private static void SetEventState(Neow neow, object description, IEnumerable<EventOption> options)
    {
        // Route the page swap through the same private method vanilla uses, but log the exact
        // option list first so we can compare both peers around the failure point.
        List<EventOption> optionList = options?.ToList() ?? new List<EventOption>();

        GD.Print(
            $"{LogPrefix} SetEventState owner={NeowRewardHelper.GetOwnerId(neow)} " +
            $"descriptionType={description.GetType().FullName} optionCount={optionList.Count} " +
            $"optionKeys={NeowRewardHelper.DebugOptionKeys(optionList)}"
        );

        MethodInfo? setEventState = FindCompatibleSetEventStateMethod(neow.GetType(), description);
        if (setEventState is null)
        {
            GD.PrintErr(
                $"{LogPrefix} Could not find a compatible SetEventState overload for description type {description.GetType().FullName}."
            );
            return;
        }

        setEventState.Invoke(neow, new object[] { description, optionList });
    }

    private static MethodInfo? FindCompatibleSetEventStateMethod(Type neowType, object description)
    {
        return neowType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method =>
            {
                if (method.Name != "SetEventState")
                    return false;

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != 2)
                    return false;

                return parameters[0].ParameterType.IsInstanceOfType(description)
                    && parameters[1].ParameterType.IsAssignableFrom(typeof(List<EventOption>));
            });
    }

    private static void FinishNeowEvent(Neow neow)
    {
        // Fallback finish path for the rare cases where we cannot show a blessing page at all.
        var neowTr = Traverse.Create(neow);
        string entry = GetNeowEntry(neowTr);

        MethodInfo? localizationLookupMethod = AccessTools.Method(
            neow.GetType(),
            "L10NLookup",
            new[] { typeof(string) }
        );

        object doneDescription =
            localizationLookupMethod?.Invoke(neow, new object[] { $"{entry}.pages.DONE.description" })
            ?? GetInitialDescription(neowTr)
            ?? string.Empty;

        MethodInfo? setEventFinishedMethod = FindCompatibleSetEventFinishedMethod(neow.GetType(), doneDescription);
        if (setEventFinishedMethod is null)
        {
            GD.PrintErr(
                $"{LogPrefix} Could not find a compatible SetEventFinished overload for description type {doneDescription.GetType().FullName}."
            );
            return;
        }

        GD.Print(
            $"{LogPrefix} FinishNeowEvent owner={NeowRewardHelper.GetOwnerId(neow)} description={NeowRewardHelper.DebugDescription(doneDescription)}"
        );
        setEventFinishedMethod.Invoke(neow, new[] { doneDescription });
    }

    private static MethodInfo? FindCompatibleSetEventFinishedMethod(Type neowType, object description)
    {
        return neowType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method =>
            {
                if (method.Name != "SetEventFinished")
                    return false;

                ParameterInfo[] parameters = method.GetParameters();
                return parameters.Length == 1 && parameters[0].ParameterType.IsInstanceOfType(description);
            });
    }

    private static string GetNeowEntry(Traverse neowTr)
    {
        object? id = neowTr.Property("Id").GetValue();
        return id is null
            ? DefaultNeowEntry
            : Traverse.Create(id).Property("Entry").GetValue<string>() ?? DefaultNeowEntry;
    }
}

[HarmonyPatch(typeof(Neow), "GenerateInitialOptions")]
public static class NeowGenerateInitialOptionsCachePatch
{
    [HarmonyPriority(Priority.Low)]
    private static void Postfix(Neow __instance, ref IReadOnlyList<EventOption> __result)
    {
        // Cache the vanilla blessing page early, before the modifier chain advances.
        // This locks in the intended blessing choices without regenerating them later.
        Player? owner = Traverse.Create(__instance).Property("Owner").GetValue<Player>();
        if (owner is null)
            return;

        if (!NeowRewardHelper.IsBuildingRewardsIgnoringModifiers() && owner.RunState.Modifiers.Count > 0)
        {
            IReadOnlyList<EventOption> templates = NeowRewardHelper.GetOrBuildCachedVanillaRewardTemplates(
                __instance,
                "GenerateInitialOptions.Postfix"
            );

            GD.Print(
                $"[NeowAlwaysRewards] Cached vanilla reward templates for owner={NeowRewardHelper.GetOwnerId(owner)} count={templates.Count} " +
                $"keys={NeowRewardHelper.DebugOptionKeys(templates)}"
            );
        }

        // If a custom run had modifiers but none of them produced a Neow option, fall back to
        // the reconstructed vanilla blessing page instead of leaving Neow empty.
        if (owner.RunState.Modifiers.Count <= 0)
            return;

        if (__result.Count > 0)
            return;

        if (NeowRewardHelper.IsBuildingRewardsIgnoringModifiers())
            return;

        IReadOnlyList<EventOption> wrappedRewards = NeowRewardHelper.GetOrBuildWrappedVanillaRewards(
            __instance,
            "GenerateInitialOptions.Fallback"
        );

        if (wrappedRewards.Count > 0)
        {
            GD.Print(
                $"[NeowAlwaysRewards] No modifier Neow options for owner={NeowRewardHelper.GetOwnerId(owner)}; using wrapped vanilla rewards."
            );
            __result = wrappedRewards;
        }
    }
}

[HarmonyPatch]
public static class EventSynchronizerChooseOptionForEventLogPatch
{
    private const string LogPrefix = "[NeowAlwaysRewards]";

    private static MethodBase TargetMethod()
    {
        // Patch the private method that both local and remote event choices flow through.
        return AccessTools.Method(
            typeof(EventSynchronizer),
            "ChooseOptionForEvent",
            new[] { typeof(Player), typeof(int) }
        )!;
    }

    private static void Prefix(EventSynchronizer __instance, Player player, int optionIndex)
    {
        try
        {
            EventModel eventForPlayer = __instance.GetEventForPlayer(player);
            IReadOnlyList<EventOption> currentOptions = eventForPlayer.CurrentOptions;

            GD.Print(
                $"{LogPrefix} [EventSynchronizer.beforeChoose] player={NeowRewardHelper.GetOwnerId(player)} " +
                $"event={NeowRewardHelper.GetEventIdSafe(eventForPlayer)} finished={eventForPlayer.IsFinished} " +
                $"requestedIndex={optionIndex} currentCount={currentOptions.Count} " +
                $"currentKeys={NeowRewardHelper.DebugOptionKeys(currentOptions)}"
            );
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{LogPrefix} [EventSynchronizer.beforeChoose] logging failed: {ex}");
        }
    }

    private static void Postfix(EventSynchronizer __instance, Player player, int optionIndex)
    {
        try
        {
            EventModel eventForPlayer = __instance.GetEventForPlayer(player);
            IReadOnlyList<EventOption> currentOptions = eventForPlayer.CurrentOptions;

            GD.Print(
                $"{LogPrefix} [EventSynchronizer.afterChoose] player={NeowRewardHelper.GetOwnerId(player)} " +
                $"event={NeowRewardHelper.GetEventIdSafe(eventForPlayer)} finished={eventForPlayer.IsFinished} " +
                $"chosenIndex={optionIndex} currentCount={currentOptions.Count} " +
                $"currentKeys={NeowRewardHelper.DebugOptionKeys(currentOptions)}"
            );
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{LogPrefix} [EventSynchronizer.afterChoose] logging failed: {ex}");
        }
    }
}

public static class NeowRewardHelper
{
    private const string LogPrefix = "[NeowAlwaysRewards]";

    [ThreadStatic]
    private static bool _buildingRewardsIgnoringModifiers;

    private sealed class CachedVanillaRewards
    {
        public List<EventOption> Templates { get; init; } = new();
    }

    private static readonly ConditionalWeakTable<Neow, CachedVanillaRewards> _cachedVanillaRewards = new();

    public static bool IsBuildingRewardsIgnoringModifiers()
    {
        return _buildingRewardsIgnoringModifiers;
    }

    public static string GetOwnerId(Neow neow)
    {
        Player? owner = Traverse.Create(neow).Property("Owner").GetValue<Player>();
        return GetOwnerId(owner);
    }

    public static string GetOwnerId(Player? owner)
    {
        return owner?.NetId.ToString() ?? "<null-owner>";
    }

    public static string GetEventIdSafe(EventModel? eventModel)
    {
        try
        {
            return eventModel?.Id?.Entry ?? "<null-event>";
        }
        catch
        {
            return eventModel?.ToString() ?? "<null-event>";
        }
    }

    public static string DebugDescription(object? description)
    {
        if (description is null)
            return "<null>";

        if (description is LocString loc)
            return DebugLocSafe(loc);

        return description.ToString() ?? "<null>";
    }

    public static string DebugLocSafe(LocString? loc)
    {
        if (loc is null)
            return "<null>";

        try
        {
            return $"{loc.GetFormattedText()} (key={loc.LocTable}:{loc.LocEntryKey})";
        }
        catch
        {
            return $"<{loc.LocTable}:{loc.LocEntryKey}>";
        }
    }

    public static string DebugOptionKey(EventOption? option)
    {
        if (option is null)
            return "<null>";

        if (!string.IsNullOrEmpty(option.TextKey))
            return option.TextKey;

        if (option.Title is not null)
            return $"{option.Title.LocTable}:{option.Title.LocEntryKey}";

        return "<unknown-option>";
    }

    public static string DebugOptionKeys(IEnumerable<EventOption>? options)
    {
        if (options is null)
            return "<null>";

        return string.Join(",", options.Select(DebugOptionKey));
    }

    public static void LogStateJson(Neow neow, string stage)
    {
        try
        {
            var neowTr = Traverse.Create(neow);
            Player? owner = neowTr.Property("Owner").GetValue<Player>();
            List<EventOption> modifierOptions = neowTr.Property("ModifierOptions").GetValue<List<EventOption>>() ?? new();

            IReadOnlyList<EventOption> currentOptions = neow.CurrentOptions;
            IReadOnlyList<EventOption> cachedTemplates = GetCachedVanillaRewardTemplates(neow);

            var payload = new
            {
                stage,
                owner = GetOwnerId(owner),
                eventId = GetEventIdSafe(neow),
                isFinished = neow.IsFinished,
                currentOptionCount = currentOptions.Count,
                currentOptionKeys = currentOptions.Select(DebugOptionKey).ToArray(),
                modifierOptionCount = modifierOptions.Count,
                modifierOptionKeys = modifierOptions.Select(DebugOptionKey).ToArray(),
                cachedTemplateCount = cachedTemplates.Count,
                cachedTemplateKeys = cachedTemplates.Select(DebugOptionKey).ToArray(),
                initialDescription = DebugDescription(neowTr.Property("InitialDescription").GetValue())
            };

            GD.Print($"{LogPrefix} STATE {JsonSerializer.Serialize(payload)}");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{LogPrefix} Failed to log state JSON at stage={stage}: {ex}");
        }
    }

    public static IReadOnlyList<EventOption> GetCachedVanillaRewardTemplates(Neow neow)
    {
        return _cachedVanillaRewards.TryGetValue(neow, out CachedVanillaRewards? cached)
            ? cached.Templates
            : Array.Empty<EventOption>();
    }

    public static IReadOnlyList<EventOption> GetOrBuildCachedVanillaRewardTemplates(Neow neow, string reason)
    {
        if (_cachedVanillaRewards.TryGetValue(neow, out CachedVanillaRewards? cached) && cached.Templates.Count > 0)
            return cached.Templates;

        IReadOnlyList<EventOption> built = BuildVanillaRewardsIgnoringModifiers(neow);
        CacheVanillaRewardTemplates(neow, built, reason);
        return GetCachedVanillaRewardTemplates(neow);
    }

    public static IReadOnlyList<EventOption> GetOrBuildWrappedVanillaRewards(Neow neow, string reason)
    {
        IReadOnlyList<EventOption> templates = GetOrBuildCachedVanillaRewardTemplates(neow, $"{reason}.templates");
        if (templates.Count <= 0)
            return Array.Empty<EventOption>();

        return CreateWrappedVanillaRewards(neow, templates, reason);
    }

    public static IReadOnlyList<EventOption> BuildVanillaRewardsIgnoringModifiers(Neow neow)
    {
        // Temporarily remove modifiers, run GenerateInitialOptions, then restore the original list.
        // This gives us the same vanilla blessing page Neow would normally have shown in a non-custom run.
        if (_buildingRewardsIgnoringModifiers)
            return Array.Empty<EventOption>();

        var neowTr = Traverse.Create(neow);
        Player? owner = neowTr.Property("Owner").GetValue<Player>();
        if (owner is null)
            return Array.Empty<EventOption>();

        var runStateTr = Traverse.Create(owner.RunState);
        var modifiersProperty = runStateTr.Property("Modifiers");

        IReadOnlyList<ModifierModel> savedModifiers =
            modifiersProperty.GetValue<IReadOnlyList<ModifierModel>>() ?? Array.Empty<ModifierModel>();

        try
        {
            _buildingRewardsIgnoringModifiers = true;
            modifiersProperty.SetValue((IReadOnlyList<ModifierModel>)Array.Empty<ModifierModel>());

            MethodInfo? generateInitialOptionsMethod = AccessTools.Method(typeof(Neow), "GenerateInitialOptions");
            if (generateInitialOptionsMethod is null)
            {
                GD.PrintErr($"{LogPrefix} Could not find GenerateInitialOptions.");
                return Array.Empty<EventOption>();
            }

            IReadOnlyList<EventOption> result =
                generateInitialOptionsMethod.Invoke(neow, null) as IReadOnlyList<EventOption>
                ?? Array.Empty<EventOption>();

            GD.Print(
                $"{LogPrefix} BuildVanillaRewardsIgnoringModifiers owner={GetOwnerId(owner)} count={result.Count} keys={DebugOptionKeys(result)}"
            );

            return result;
        }
        finally
        {
            modifiersProperty.SetValue(savedModifiers);
            _buildingRewardsIgnoringModifiers = false;
        }
    }

    private static void CacheVanillaRewardTemplates(Neow neow, IReadOnlyList<EventOption> templates, string reason)
    {
        _cachedVanillaRewards.Remove(neow);
        _cachedVanillaRewards.Add(
            neow,
            new CachedVanillaRewards
            {
                Templates = templates.Where(t => t is not null).ToList()
            }
        );

        GD.Print(
            $"{LogPrefix} CacheVanillaRewardTemplates owner={GetOwnerId(neow)} reason={reason} count={templates.Count} keys={DebugOptionKeys(templates)}"
        );
    }

    private static IReadOnlyList<EventOption> CreateWrappedVanillaRewards(
        Neow neow,
        IReadOnlyList<EventOption> templates,
        string reason
    )
    {
        // Rebuild the visible blessing page as fresh wrapper EventOptions. Each wrapper forwards
        // its choice to the original vanilla option callback, which keeps the reward logic close
        // to vanilla while avoiding reusing the exact cached EventOption objects in the live UI state.
        List<EventOption> wrappedRewards = new();

        for (int index = 0; index < templates.Count; index++)
        {
            EventOption template = templates[index];
            EventOption wrapped = BuildWrappedRewardOption(neow, template, index, reason);
            wrappedRewards.Add(wrapped);
        }

        GD.Print(
            $"{LogPrefix} CreateWrappedVanillaRewards owner={GetOwnerId(neow)} reason={reason} count={wrappedRewards.Count} keys={DebugOptionKeys(wrappedRewards)}"
        );

        return wrappedRewards;
    }

    private static EventOption BuildWrappedRewardOption(Neow neow, EventOption template, int index, string reason)
    {
        IReadOnlyList<IHoverTip> hoverTips = template.HoverTips?.ToArray() ?? Array.Empty<IHoverTip>();

        EventOption wrapped = new EventOption(
            (EventModel)neow,
            async () =>
            {
                GD.Print(
                    $"{LogPrefix} WrappedReward.OnChosen owner={GetOwnerId(neow)} reason={reason} index={index} " +
                    $"key={DebugOptionKey(template)} relic={template.Relic?.Id?.Entry ?? template.Relic?.GetType().Name ?? "<none>"}"
                );
                LogStateJson(neow, $"WrappedReward.beforeOriginal[{index}]");

                await template.Chosen();

                LogStateJson(neow, $"WrappedReward.afterOriginal[{index}]");
            },
            template.Title,
            template.Description,
            template.TextKey,
            hoverTips
        );

        if (template.HistoryName is not null)
            wrapped = wrapped.WithOverridenHistoryName(template.HistoryName);

        if (!template.ShouldSaveChoiceToHistory)
            wrapped = wrapped.ThatWontSaveToChoiceHistory();

        if (template.Relic is not null)
        {
            try
            {
                wrapped = wrapped.WithRelic(template.Relic);
            }
            catch (Exception ex)
            {
                GD.PrintErr(
                    $"{LogPrefix} Failed to copy relic onto wrapped option for owner={GetOwnerId(neow)} key={DebugOptionKey(template)}: {ex}"
                );
            }
        }

        wrapped.BeforeChosen += option =>
        {
            GD.Print(
                $"{LogPrefix} WrappedReward.BeforeChosen owner={GetOwnerId(neow)} reason={reason} index={index} " +
                $"visibleCount={neow.CurrentOptions.Count} visibleKeys={DebugOptionKeys(neow.CurrentOptions)} key={DebugOptionKey(option)}"
            );
            return Task.CompletedTask;
        };

        return wrapped;
    }
}
