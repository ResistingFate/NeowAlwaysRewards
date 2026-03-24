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

        IReadOnlyList<EventOption> rewards = BuildVanillaRewardsIgnoringModifiers(neow);

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
                GD.Print($"[NeowAlwaysRewards] initialDescription = {loc.GetFormattedText()}");
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

        IReadOnlyList<EventOption> rewards = NeowRewardHelper.BuildNormalRewards(__instance);
        if (rewards.Count > 0)
        {
            GD.Print("[NeowAlwaysRewards] No modifier Neow options; using normal Neow rewards.");
            __result = rewards;
        }
    }
}

public static class NeowRewardHelper
{
    public static IReadOnlyList<EventOption> BuildNormalRewards(Neow neow)
    {
        var tr = Traverse.Create(neow);

        Player? owner = tr.Property("Owner").GetValue<Player>();
        Rng? rng = tr.Property("Rng").GetValue<Rng>();

        if (owner is null || rng is null)
            return Array.Empty<EventOption>();

        List<EventOption> list1 =
            tr.Property("CurseOptions").GetValue<IEnumerable<EventOption>>()?.ToList()
            ?? new List<EventOption>();

        List<EventOption> list2 =
            tr.Property("PositiveOptions").GetValue<IEnumerable<EventOption>>()?.ToList()
            ?? new List<EventOption>();

        if (ScrollBoxes.CanGenerateBundles(owner))
            AddIfNotNull(list1, tr.Property("ScrollBoxesOption").GetValue<EventOption>());

        if (owner.RunState.Players.Count == 1)
            AddIfNotNull(list1, tr.Property("SilverCrucibleOption").GetValue<EventOption>());

        if (list1.Count == 0 || list2.Count == 0)
            return Array.Empty<EventOption>();

        EventOption? eventOption = rng.NextItem<EventOption>(list1);
        if (eventOption is null)
            return Array.Empty<EventOption>();

        if (eventOption.Relic is CursedPearl)
            list2.RemoveAll(o => o.Relic is GoldenPearl);

        if (eventOption.Relic is HeftyTablet)
            list2.RemoveAll(o => o.Relic is ArcaneScroll);

        if (eventOption.Relic is LeafyPoultice)
            list2.RemoveAll(o => o.Relic is NewLeaf);

        if (eventOption.Relic is PrecariousShears)
            list2.RemoveAll(o => o.Relic is PreciseScissors);

        if (eventOption.Relic is not LargeCapsule)
        {
            AddIfNotNull(
                list2,
                rng.NextBool()
                    ? tr.Property("LavaRockOption").GetValue<EventOption>()
                    : tr.Property("SmallCapsuleOption").GetValue<EventOption>());
        }

        AddIfNotNull(
            list2,
            rng.NextBool()
                ? tr.Property("NutritiousOysterOption").GetValue<EventOption>()
                : tr.Property("StoneHumidifierOption").GetValue<EventOption>());

        AddIfNotNull(
            list2,
            rng.NextBool()
                ? tr.Property("NeowsTalismanOption").GetValue<EventOption>()
                : tr.Property("PomanderOption").GetValue<EventOption>());

        if (owner.RunState.Players.Count > 1)
            AddIfNotNull(list2, tr.Property("MassiveScrollOption").GetValue<EventOption>());

        List<EventOption> items = new();
        items.AddRange(list2.UnstableShuffle(rng).Take(2));
        items.Add(eventOption);

        return items;
    }

    private static void AddIfNotNull(List<EventOption> list, EventOption? option)
    {
        if (option is not null)
            list.Add(option);
    }
}