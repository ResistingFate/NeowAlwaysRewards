using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;

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
        // Skip the vanilla method and run the replacement flow against the live Neow instance.
        __result = RunModifierSelectionFlow(__instance, modifierFunc, index);
        return false;
    }

    private static async Task RunModifierSelectionFlow(Neow neow, Func<Task> modifierFunc, int index)
    {
        // Preserve the original async shape by awaiting the modifier callback first.
        await modifierFunc();

        var neowTr = Traverse.Create(neow);

        // Read as object because the description is not guaranteed to be a plain string.
        object? initialDescription = GetInitialDescription(neowTr);
        if (initialDescription is null)
        {
            GD.PrintErr($"{LogPrefix} InitialDescription was null.");
            FinishNeowEvent(neow);
            return;
        }

        List<EventOption> modifierOptions =
            neowTr.Property("ModifierOptions").GetValue<List<EventOption>>() ?? new();

        GD.Print(
            $"{LogPrefix} OnModifierOptionSelected finished. index={index}, modifierOptions.Count={modifierOptions.Count}"
        );

        if (index + 1 < modifierOptions.Count)
        {
            GD.Print($"{LogPrefix} Showing next modifier option.");
            SetEventState(neow, initialDescription, new[] { modifierOptions[index + 1] });
            return;
        }

        GD.Print($"{LogPrefix} Last modifier option completed. Generating vanilla Neow rewards.");

        IReadOnlyList<EventOption> rewards = NeowRewardHelper.BuildVanillaRewardsIgnoringModifiers(neow);

        GD.Print($"{LogPrefix} Vanilla reward count after modifier queue: {rewards.Count}");
        LogRewardOptions(rewards);

        if (rewards.Count > 0)
        {
            if (initialDescription is LocString loc)
            {
                GD.Print($"{LogPrefix} initialDescription = {NeowRewardHelper.DebugLocSafe(loc)}");
            }

            SetEventState(neow, initialDescription, rewards);
            return;
        }

        GD.PrintErr($"{LogPrefix} No rewards generated after modifier queue; finishing event.");
        FinishNeowEvent(neow);
    }

    private static object? GetInitialDescription(Traverse neowTr)
    {
        return neowTr.Property("InitialDescription").GetValue();
    }

    private static void LogRewardOptions(IEnumerable<EventOption> rewards)
    {
        foreach (EventOption? option in rewards)
        {
            GD.Print(
                $"{LogPrefix} Reward option: {option?.Title?.GetFormattedText() ?? "<null>"} " +
                $"(key={option?.Title?.LocTable}:{option?.Title?.LocEntryKey}, relic={option?.Relic?.GetType().Name ?? "<none>"})"
            );
        }
    }

    private static void SetEventState(Neow neow, object description, IEnumerable<EventOption> options)
    {
        MethodInfo? setEventState = FindCompatibleSetEventStateMethod(neow.GetType(), description);
        if (setEventState is null)
        {
            GD.PrintErr(
                $"{LogPrefix} Could not find a compatible SetEventState overload for description type {description.GetType().FullName}."
            );
            return;
        }

        setEventState.Invoke(neow, new object[] { description, options });
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
        var neowTr = Traverse.Create(neow);

        string entry = GetNeowEntry(neowTr);

        MethodInfo? localizationLookupMethod = AccessTools.Method(
            neow.GetType(),
            "L10NLookup",
            new[] { typeof(string) }
        );

        // "Neow.pages.DONE.description is how the original function finishes the Neow Event
        string doneDescription =
            localizationLookupMethod?.Invoke(neow, new object[] { $"{entry}.pages.DONE.description" }) as string
            ?? GetDescriptionText(GetInitialDescription(neowTr));

        MethodInfo? setEventFinishedMethod = AccessTools.Method(
            neow.GetType(),
            "SetEventFinished",
            new[] { typeof(string) }
        );

        if (setEventFinishedMethod is null)
        {
            GD.PrintErr($"{LogPrefix} Could not find SetEventFinished(string).");
            return;
        }

        setEventFinishedMethod.Invoke(neow, new object[] { doneDescription });
    }

    private static string GetNeowEntry(Traverse neowTr)
    {
        object? id = neowTr.Property("Id").GetValue();
        return id is null
            ? DefaultNeowEntry
            : Traverse.Create(id).Property("Entry").GetValue<string>() ?? DefaultNeowEntry;
    }

    private static string GetDescriptionText(object? description)
    {
        if (description is string text)
            return text;

        if (description is not LocString loc)
            return string.Empty;

        try
        {
            return loc.GetFormattedText();
        }
        catch
        {
            return string.Empty;
        }
    }
}

[HarmonyPatch(typeof(Neow), "GenerateInitialOptions")]
public static class NeowGenerateInitialOptionsFallbackPatch
{
    [HarmonyPriority(Priority.Low)]
    // This postfix acts as a fallback so other patches can modify the result first.
    private static void Postfix(Neow __instance, ref IReadOnlyList<EventOption> __result)
    {
        var neowTr = Traverse.Create(__instance);

        Player? owner = neowTr.Property("Owner").GetValue<Player>();
        if (owner is null)
            return;

        if (owner.RunState.Modifiers.Count <= 0)
            return; // Standard runs stay unchanged.

        if (__result.Count > 0)
            return; // Vanilla already produced modifier options.

        if (NeowRewardHelper.IsBuildingRewardsIgnoringModifiers())
            return;

        IReadOnlyList<EventOption> rewards = NeowRewardHelper.BuildVanillaRewardsIgnoringModifiers(__instance);

        if (rewards.Count > 0)
        {
            GD.Print("[NeowAlwaysRewards] No modifier Neow options; using normal Neow rewards.");
            __result = rewards;
        }
    }
}

public static class NeowRewardHelper
{
    [ThreadStatic]
    private static bool _buildingRewardsIgnoringModifiers;

    public static bool IsBuildingRewardsIgnoringModifiers()
    {
        return _buildingRewardsIgnoringModifiers;
    }

    public static IReadOnlyList<EventOption> BuildVanillaRewardsIgnoringModifiers(Neow neow)
    {
        // Temporarily remove modifiers, run GenerateInitialOptions, then restore the original list.
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

            // Traverse can temporarily replace a property value through reflection.
            modifiersProperty.SetValue((IReadOnlyList<ModifierModel>)Array.Empty<ModifierModel>());

            MethodInfo? generateInitialOptionsMethod = AccessTools.Method(typeof(Neow), "GenerateInitialOptions");
            if (generateInitialOptionsMethod is null)
            {
                GD.PrintErr("[NeowAlwaysRewards] Could not find GenerateInitialOptions.");
                return Array.Empty<EventOption>();
            }

            return generateInitialOptionsMethod.Invoke(neow, null) as IReadOnlyList<EventOption>
                ?? Array.Empty<EventOption>();
        }
        finally
        {
            modifiersProperty.SetValue(savedModifiers);
            _buildingRewardsIgnoringModifiers = false;
        }
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
            return $"<{loc.LocTable}.{loc.LocEntryKey}>";
        }
    }
}
