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
        // Replace the vanilla modifier flow so custom run modifiers still chain one-by-one,
        // but rebuild the final blessing page from fresh live vanilla EventOptions.
        //
        // The key difference from the previous version is:
        // - we do NOT reuse cached EventOption instances
        // - we do NOT wrap cached EventOption callbacks
        // - we only cache the final blessing TEXT KEYS early
        // - when the last modifier finishes, we rebuild fresh live options from neow.AllPossibleOptions
        //
        // That keeps the page progression fix that multiplayer needed, while letting the final
        // blessing choice run through a much more vanilla-looking callback path.
        __result = RunModifierSelectionFlow(__instance, modifierFunc, index);
        return false;
    }

    private static async Task RunModifierSelectionFlow(Neow neow, Func<Task> modifierFunc, int index)
    {
        // Main replacement flow:
        // 1) run the chosen modifier callback,
        // 2) show the next modifier immediately if there is one,
        // 3) otherwise reconstruct the cached blessing page from fresh live vanilla options.
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

        IReadOnlyList<EventOption> rewards = NeowRewardHelper.GetOrBuildLiveVanillaRewardsFromCachedKeys(
            neow,
            "OnModifierOptionSelected.final"
        );

        GD.Print(
            $"{LogPrefix} Last modifier completed for owner={NeowRewardHelper.GetOwnerId(neow)}. Live reward count={rewards.Count}"
        );
        LogRewardOptions(neow, rewards);

        if (rewards.Count > 0)
        {
            SetEventState(neow, initialDescription, rewards);
            NeowRewardHelper.LogStateJson(neow, $"OnModifierOptionSelected.afterSetRewards[{index}]");
            return;
        }

        GD.PrintErr(
            $"{LogPrefix} No live rewards were available after modifier queue for owner={NeowRewardHelper.GetOwnerId(neow)}; finishing event."
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
        // option list first so both peers can be compared around a desync.
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
        // Finish Neow in the same general way vanilla does when there is no page to show.
        var neowTr = Traverse.Create(neow);
        string entry = GetNeowEntry(neowTr);

        object? doneDescription = InvokeL10NLookup(neow, $"{entry}.pages.DONE.description");
        if (doneDescription is null)
            doneDescription = GetInitialDescription(neowTr);

        MethodInfo? setEventFinished = FindCompatibleSetEventFinishedMethod(neow.GetType(), doneDescription);
        if (setEventFinished is null)
        {
            GD.PrintErr(
                $"{LogPrefix} Could not find a compatible SetEventFinished overload for owner={NeowRewardHelper.GetOwnerId(neow)}."
            );
            return;
        }

        GD.Print(
            $"{LogPrefix} FinishNeowEvent owner={NeowRewardHelper.GetOwnerId(neow)} descriptionType={doneDescription?.GetType().FullName ?? "<null>"}"
        );

        setEventFinished.Invoke(neow, new[] { doneDescription });
    }

    private static MethodInfo? FindCompatibleSetEventFinishedMethod(Type neowType, object? description)
    {
        if (description is null)
            return null;

        return neowType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method =>
            {
                if (method.Name != "SetEventFinished")
                    return false;

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != 1)
                    return false;

                return parameters[0].ParameterType.IsInstanceOfType(description);
            });
    }

    private static object? InvokeL10NLookup(Neow neow, string key)
    {
        MethodInfo? method = AccessTools.Method(neow.GetType(), "L10NLookup", new[] { typeof(string) });
        return method?.Invoke(neow, new object[] { key });
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
        // Cache the final blessing KEYS early, before the modifier chain advances.
        // This freezes the RNG result up front without reusing old EventOption instances later.
        Player? owner = Traverse.Create(__instance).Property("Owner").GetValue<Player>();
        if (owner is null)
            return;

        if (!NeowRewardHelper.IsBuildingRewardsIgnoringModifiers() && owner.RunState.Modifiers.Count > 0)
        {
            IReadOnlyList<string> keys = NeowRewardHelper.GetOrBuildCachedVanillaRewardKeys(
                __instance,
                "GenerateInitialOptions.Postfix"
            );

            GD.Print(
                $"[NeowAlwaysRewards] Cached vanilla reward keys for owner={NeowRewardHelper.GetOwnerId(owner)} count={keys.Count} " +
                $"keys={string.Join(",", keys)}"
            );
        }

        // If a custom run had modifiers but none of them produced a Neow option, fall back to
        // the live reconstructed vanilla blessing page instead of leaving Neow empty.
        if (owner.RunState.Modifiers.Count <= 0)
            return; // Standard runs stay unchanged

        if (__result.Count > 0)
            return; // Vanilla already produced modifier options.

        if (NeowRewardHelper.IsBuildingRewardsIgnoringModifiers())
            return;

        IReadOnlyList<EventOption> liveRewards = NeowRewardHelper.GetOrBuildLiveVanillaRewardsFromCachedKeys(
            __instance,
            "GenerateInitialOptions.Fallback"
        );

        if (liveRewards.Count > 0)
        {
            GD.Print(
                $"[NeowAlwaysRewards] No modifier Neow options for owner={NeowRewardHelper.GetOwnerId(owner)}; using live vanilla rewards."
            );
            __result = liveRewards;
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

    private sealed class CachedRewardKeys
    {
        public List<string> Keys { get; init; } = new();
        public List<string> RelicEntries { get; init; } = new();
        public string Reason { get; init; } = string.Empty;
    }

    private static readonly ConditionalWeakTable<Neow, CachedRewardKeys> CachedRewardKeysByNeow = new();

    public static bool IsBuildingRewardsIgnoringModifiers()
    {
        return _buildingRewardsIgnoringModifiers;
    }

    /// <summary>
    /// Build the vanilla blessing page by temporarily ignoring custom-run modifiers,
    /// but only keep a lightweight snapshot of the chosen option keys and relic ids.
    /// </summary>
    public static IReadOnlyList<string> GetOrBuildCachedVanillaRewardKeys(Neow neow, string reason)
    {
        if (CachedRewardKeysByNeow.TryGetValue(neow, out CachedRewardKeys? cached))
            return cached.Keys.ToList();

        return BuildAndCacheVanillaRewardKeys(neow, reason);
    }

    /// <summary>
    /// Rebuild fresh live EventOptions from neow.AllPossibleOptions using the cached key order.
    /// This avoids reusing stale EventOption instances and keeps the live callbacks vanilla.
    /// </summary>
    public static IReadOnlyList<EventOption> GetOrBuildLiveVanillaRewardsFromCachedKeys(Neow neow, string reason)
    {
        IReadOnlyList<string> keys = GetOrBuildCachedVanillaRewardKeys(neow, reason);
        if (keys.Count <= 0)
            return Array.Empty<EventOption>();

        List<EventOption> allLiveOptions = neow.AllPossibleOptions?.ToList() ?? new List<EventOption>();
        Dictionary<string, EventOption> byKey = allLiveOptions
            .Where(option => option?.TextKey is not null)
            .GroupBy(option => option.TextKey)
            .ToDictionary(group => group.Key, group => group.First());

        List<EventOption> rebuilt = new();
        foreach (string key in keys)
        {
            if (!byKey.TryGetValue(key, out EventOption? live))
            {
                GD.PrintErr(
                    $"{LogPrefix} Failed to rebuild live vanilla reward for owner={GetOwnerId(neow)} key={key}. " +
                    $"Available keys={string.Join(",", byKey.Keys.OrderBy(x => x))}"
                );
                continue;
            }

            AttachDebugBeforeChosen(neow, live, reason);
            rebuilt.Add(live);
        }

        GD.Print(
            $"{LogPrefix} CreateLiveVanillaRewards owner={GetOwnerId(neow)} reason={reason} count={rebuilt.Count} keys={DebugOptionKeys(rebuilt)}"
        );

        return rebuilt;
    }

    private static IReadOnlyList<string> BuildAndCacheVanillaRewardKeys(Neow neow, string reason)
    {
        if (_buildingRewardsIgnoringModifiers)
            return Array.Empty<string>();

        var neowTr = Traverse.Create(neow);
        Player? owner = neowTr.Property("Owner").GetValue<Player>();
        if (owner is null)
            return Array.Empty<string>();

        var runStateTr = Traverse.Create(owner.RunState);
        var modifiersProperty = runStateTr.Property("Modifiers");

        IReadOnlyList<ModifierModel> savedModifiers =
            modifiersProperty.GetValue<IReadOnlyList<ModifierModel>>() ?? Array.Empty<ModifierModel>();

        try
        {
            _buildingRewardsIgnoringModifiers = true;
            // Traverse can temporarily replace a property value through reflection.
            modifiersProperty.SetValue((IReadOnlyList<ModifierModel>)Array.Empty<ModifierModel>());

            MethodInfo? generateInitialOptionsMethod = AccessTools.Method(typeof(Neow), "GenerateInitialOptions");
            if (generateInitialOptionsMethod is null)
            {
                GD.PrintErr($"{LogPrefix} Could not find GenerateInitialOptions.");
                return Array.Empty<string>();
            }

            IReadOnlyList<EventOption> generated =
                generateInitialOptionsMethod.Invoke(neow, Array.Empty<object>()) as IReadOnlyList<EventOption>
                ?? Array.Empty<EventOption>();

            CachedRewardKeys snapshot = new()
            {
                Keys = generated.Select(DebugOptionKey).Where(key => !string.IsNullOrEmpty(key)).ToList(),
                RelicEntries = generated
                    .Select(option => option?.Relic?.Id?.Entry ?? option?.Relic?.GetType().Name ?? "<none>")
                    .ToList(),
                Reason = reason
            };

            CachedRewardKeysByNeow.Remove(neow);
            CachedRewardKeysByNeow.Add(neow, snapshot);

            GD.Print(
                $"{LogPrefix} Built cached vanilla reward keys for owner={GetOwnerId(owner)} reason={reason} " +
                $"count={snapshot.Keys.Count} keys={string.Join(",", snapshot.Keys)} relics={string.Join(",", snapshot.RelicEntries)}"
            );

            return snapshot.Keys.ToList();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{LogPrefix} Failed to build cached vanilla reward keys for owner={GetOwnerId(owner)}: {ex}");
            return Array.Empty<string>();
        }
        finally
        {
            modifiersProperty.SetValue(savedModifiers);
            _buildingRewardsIgnoringModifiers = false;
        }
    }

    private static void AttachDebugBeforeChosen(Neow neow, EventOption option, string reason)
    {
        // Attach logging without changing the OnChosen callback itself.
        option.BeforeChosen += chosen =>
        {
            GD.Print(
                $"{LogPrefix} LiveReward.BeforeChosen owner={GetOwnerId(neow)} reason={reason} " +
                $"key={DebugOptionKey(chosen)} relic={chosen.Relic?.Id?.Entry ?? chosen.Relic?.GetType().Name ?? "<none>"} " +
                $"visibleCount={neow.CurrentOptions.Count} visibleKeys={DebugOptionKeys(neow.CurrentOptions)}"
            );
            LogStateJson(neow, $"LiveReward.beforeChosen[{DebugOptionKey(chosen)}]");
            return Task.CompletedTask;
        };
    }

    public static void LogStateJson(Neow neow, string stage)
    {
        try
        {
            var neowTr = Traverse.Create(neow);
            List<EventOption> modifierOptions =
                neowTr.Property("ModifierOptions").GetValue<List<EventOption>>() ?? new();

            CachedRewardKeysByNeow.TryGetValue(neow, out CachedRewardKeys? cached);

            object payload = new
            {
                stage,
                owner = GetOwnerId(neow),
                eventId = GetEventIdSafe(neow),
                isFinished = neow.IsFinished,
                currentOptionCount = neow.CurrentOptions?.Count ?? 0,
                currentOptionKeys = neow.CurrentOptions?.Select(DebugOptionKey).ToArray() ?? Array.Empty<string>(),
                modifierOptionCount = modifierOptions.Count,
                modifierOptionKeys = modifierOptions.Select(DebugOptionKey).ToArray(),
                cachedKeyCount = cached?.Keys.Count ?? 0,
                cachedKeys = cached?.Keys.ToArray() ?? Array.Empty<string>(),
                cachedRelics = cached?.RelicEntries.ToArray() ?? Array.Empty<string>(),
                cacheReason = cached?.Reason ?? string.Empty,
                initialDescription = DebugLocSafe(neow.InitialDescription)
            };

            GD.Print($"{LogPrefix} STATE {JsonSerializer.Serialize(payload)}");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{LogPrefix} Failed to log Neow state JSON at stage {stage}: {ex}");
        }
    }

    public static string GetOwnerId(Neow neow)
    {
        return GetOwnerId(Traverse.Create(neow).Property("Owner").GetValue<Player>());
    }

    public static string GetOwnerId(Player? owner)
    {
        if (owner is null)
            return "<null-owner>";

        try
        {
            return owner.NetId.ToString();
        }
        catch
        {
            return owner.ToString() ?? "<unknown-owner>";
        }
    }

    public static string GetEventIdSafe(EventModel eventModel)
    {
        try
        {
            object? id = Traverse.Create(eventModel).Property("Id").GetValue();
            string? entry = id is null ? null : Traverse.Create(id).Property("Entry").GetValue<string>();
            return string.IsNullOrEmpty(entry) ? eventModel.Id?.ToString() ?? "<null-event-id>" : entry;
        }
        catch
        {
            return eventModel.Id?.ToString() ?? "<null-event-id>";
        }
    }

    public static string DebugOptionKeys(IEnumerable<EventOption>? options)
    {
        if (options is null)
            return string.Empty;

        return string.Join(",", options.Select(DebugOptionKey));
    }

    public static string DebugOptionKey(EventOption? option)
    {
        return option?.TextKey ?? option?.Title?.LocEntryKey ?? "<null-option>";
    }

    public static string DebugLocSafe(object? description)
    {
        if (description is null)
            return string.Empty;

        if (description is LocString loc)
        {
            try
            {
                return loc.GetFormattedText();
            }
            catch
            {
                try
                {
                    return $"<{loc.LocTable}:{loc.LocEntryKey}>";
                }
                catch
                {
                    return "<loc-error>";
                }
            }
        }

        return description.ToString() ?? string.Empty;
    }
}
