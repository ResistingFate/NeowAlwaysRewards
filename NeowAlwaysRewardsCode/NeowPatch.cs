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
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Random;

// Auto-import any unresolved relic/option namespaces Rider suggests.

namespace NeowAlwaysRewards.NeowAlwaysRewardsCode;


[HarmonyPatch]
public static class NeowPatch
{
    static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(Neow),
            "OnModifierOptionSelected",
            new[] { typeof(Func<Task>), typeof(int) }
        )!;
    }

    // Not sure how other mods will use this method yetand where this should be by context
    static bool Prefix(Neow __instance, Func<Task> modifierFunc, int index, ref Task __result)
    {
        __result = Run(__instance, modifierFunc, index);
        return false;
    }

    private static async Task Run(Neow neow, Func<Task> modifierFunc, int index)
    {
        await modifierFunc();

        var tr = Traverse.Create(neow);

        object? initialDescription = tr.Property("InitialDescription").GetValue();
        if (initialDescription is null)
        {
            GD.PrintErr("[NeowAlwaysRewards] InitialDescription was null.");
            FinishNeowEvent(neow);
            return;
        }

        List<EventOption> modifierOptions =
            tr.Property("ModifierOptions").GetValue<List<EventOption>>() ?? new();

        GD.Print($"[NeowAlwaysRewards] OnModifierOptionSelected finished. index={index}, modifierOptions.Count={modifierOptions.Count}");

        if (index + 1 < modifierOptions.Count)
        {
            GD.Print("[NeowAlwaysRewards] Showing next modifier option.");
            SetEventState(neow, initialDescription, new List<EventOption> { modifierOptions[index + 1] });
            return;
        }

        GD.Print("[NeowAlwaysRewards] Last modifier option completed. Generating vanilla Neow rewards.");

        IReadOnlyList<EventOption> rewards = NeowRewardHelper.BuildVanillaRewardsIgnoringModifiers(neow);

        GD.Print($"[NeowAlwaysRewards] Vanilla reward count after modifier queue: {rewards.Count}");
        foreach (var opt in rewards)
        {
            GD.Print(
                $"[NeowAlwaysRewards] Reward option: {opt?.Title?.GetFormattedText() ?? "<null>"} " +
                $"(key={opt?.Title?.LocTable}:{opt?.Title?.LocEntryKey}, relic={opt?.Relic?.GetType().Name ?? "<none>"})");
        }
        
        if (rewards.Count > 0)
        {
            GD.Print($"[NeowAlwaysRewards] rewards.Count = {rewards.Count}");
            if (initialDescription is LocString loc)
            {
                GD.Print($"[NeowAlwaysRewards] initialDescription = {NeowRewardHelper.DebugLocSafe(loc)}");
            }
            SetEventState(neow, initialDescription, rewards);
            return;
        }

        GD.PrintErr("[NeowAlwaysRewards] No rewards generated after modifier queue; finishing event.");
        FinishNeowEvent(neow);
    }
    
    private static IReadOnlyList<EventOption> BuildVanillaRewardsIgnoringModifiers(Neow neow)
    {
        var neowTr = Traverse.Create(neow);

        Player? owner = neowTr.Property("Owner").GetValue<Player>();
        if (owner is null)
            return Array.Empty<EventOption>();

        var runStateTr = Traverse.Create(owner.RunState);

        IReadOnlyList<ModifierModel> savedModifiers =
            runStateTr.Property("Modifiers").GetValue<IReadOnlyList<ModifierModel>>()
            ?? Array.Empty<ModifierModel>();

        try
        {
            runStateTr.Property("Modifiers").SetValue((IReadOnlyList<ModifierModel>)Array.Empty<ModifierModel>());

            var method = AccessTools.Method(typeof(Neow), "GenerateInitialOptions");
            if (method is null)
            {
                GD.PrintErr("[NeowAlwaysRewards] Could not find Neow.GenerateInitialOptions.");
                return Array.Empty<EventOption>();
            }

            return method.Invoke(neow, null) as IReadOnlyList<EventOption>
                   ?? Array.Empty<EventOption>();
        }
        finally
        {
            runStateTr.Property("Modifiers").SetValue(savedModifiers);
        }
    } 
    
    private static void SetEventState(Neow neow, object description, IEnumerable<EventOption> options)
    {
        Type descriptionType = description.GetType();
        MethodInfo? setEventState = AccessTools.Method(
            neow.GetType(),
            "SetEventState",
            new[] { descriptionType, typeof(IEnumerable<EventOption>) }
        );

        setEventState?.Invoke(neow, new object[] { description, options });
    }

    private static void FinishNeowEvent(Neow neow)
    {
        var tr = Traverse.Create(neow);

        object? id = tr.Property("Id").GetValue();
        string entry =
            id is null
                ? "NEOW"
                : Traverse.Create(id).Property("Entry").GetValue<string>() ?? "NEOW";

        MethodInfo? l10nLookup = AccessTools.Method(
            neow.GetType(),
            "L10NLookup",
            new[] { typeof(string) }
        );

        string doneDescription =
            l10nLookup?.Invoke(neow, new object[] { entry + ".pages.DONE.description" }) as string
            ?? tr.Property("InitialDescription").GetValue<string>()
            ?? string.Empty;

        MethodInfo? setEventFinished = AccessTools.Method(
            neow.GetType(),
            "SetEventFinished",
            new[] { typeof(string) }
        );

        setEventFinished?.Invoke(neow, new object[] { doneDescription });
    }
}


[HarmonyPatch(typeof(Neow), "GenerateInitialOptions")]
public static class NeowGenerateInitialOptionsFallbackPatch
{
    [HarmonyPriority(Priority.Low)]
    // Can add more rules if needed using [HarmonyAfter(new[] { "other.mod.id" })]
    static void Postfix(Neow __instance, ref IReadOnlyList<EventOption> __result)
    {
        var tr = Traverse.Create(__instance);

        Player? owner = tr.Property("Owner").GetValue<Player>();
        if (owner is null)
            return;

        if (owner.RunState.Modifiers.Count <= 0)
            return; // standard runs unchanged

        if (__result.Count > 0)
            return; // vanilla already gave us modifier options
        
        if (NeowRewardHelper.ShouldSkipGenerateInitialOptionsPostfix())
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
    
    public static bool ShouldSkipGenerateInitialOptionsPostfix()
    {
        return _buildingRewardsIgnoringModifiers;
    }
    
    public static IReadOnlyList<EventOption> BuildVanillaRewardsIgnoringModifiers(Neow neow)
    {
        if (_buildingRewardsIgnoringModifiers)
            return Array.Empty<EventOption>();

        var neowTr = Traverse.Create(neow);

        Player? owner = neowTr.Property("Owner").GetValue<Player>();
        if (owner is null)
            return Array.Empty<EventOption>();

        var runStateTr = Traverse.Create(owner.RunState);

        IReadOnlyList<ModifierModel> savedModifiers =
            runStateTr.Property("Modifiers").GetValue<IReadOnlyList<ModifierModel>>()
            ?? Array.Empty<ModifierModel>();

        try
        {
            _buildingRewardsIgnoringModifiers = true;

            runStateTr.Property("Modifiers")
                .SetValue((IReadOnlyList<ModifierModel>)Array.Empty<ModifierModel>());

            var method = AccessTools.Method(typeof(Neow), "GenerateInitialOptions");
            if (method is null)
                return Array.Empty<EventOption>();

            return method.Invoke(neow, null) as IReadOnlyList<EventOption>
                   ?? Array.Empty<EventOption>();
        }
        finally
        {
            runStateTr.Property("Modifiers").SetValue(savedModifiers);
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