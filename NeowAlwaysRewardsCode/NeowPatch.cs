using System.Collections.Generic;
using System.Linq;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Extensions;

namespace NeowAlwaysRewards.NeowAlwaysRewardsCode;

[HarmonyPatch(typeof(Neow), "GenerateInitialOptions")]
public static class NeowPatch
{
    static bool Prefix(Neow __instance, ref IReadOnlyList<EventOption> __result)
    {
        var tr = Traverse.Create(__instance);

        Player owner = tr.Property("Owner").GetValue<Player>();
        Rng rng = tr.Property("Rng").GetValue<Rng>();

        var curseOptions = tr.Property("CurseOptions").GetValue<IEnumerable<EventOption>>();
        var positiveOptions = tr.Property("PositiveOptions").GetValue<IEnumerable<EventOption>>();

        var bundleOption = tr.Property("BundleOption").GetValue<EventOption>();
        var empowerOption = tr.Property("EmpowerOption").GetValue<EventOption>();
        var clericOption = tr.Property("ClericOption").GetValue<EventOption>();
        var toughnessOption = tr.Property("ToughnessOption").GetValue<EventOption>();
        var safetyOption = tr.Property("SafetyOption").GetValue<EventOption>();
        var patienceOption = tr.Property("PatienceOption").GetValue<EventOption>();
        var scavengerOption = tr.Property("ScavengerOption").GetValue<EventOption>();

        var modifierOptions = tr.Property("ModifierOptions").GetValue<List<EventOption>>();

        GD.Print("[NeowAlwaysRewards] Patched Neow.GenerateInitialOptions");
        if (curseOptions is null)
        {
            GD.PrintErr("[NeowAlwaysRewards] CurseOptions was null");
            return true; // let original method run
        }

        if (positiveOptions is null)
        {
            GD.PrintErr("[NeowAlwaysRewards] PositiveOptions was null");
            return true; // let original method run
        }

        List<EventOption> list1 = curseOptions.ToList();

        if (ScrollBoxes.CanGenerateBundles(owner))
            list1.Add(bundleOption);

        if (owner.RunState.Players.Count == 1)
            list1.Add(empowerOption);
        
        EventOption? eventOption = rng.NextItem<EventOption>(list1);
        if (eventOption is null)
        {
            GD.PrintErr("[NeowAlwaysRewards] rng.NextItem returned null; falling back to original.");
            return true;
        }

        List<EventOption> list2 = positiveOptions.ToList();

        if (eventOption.Relic is CursedPearl)
            list2.RemoveAll(o => o.Relic is GoldenPearl);

        if (eventOption.Relic is PrecariousShears)
            list2.RemoveAll(o => o.Relic is PreciseScissors);

        if (eventOption.Relic is LeafyPoultice)
            list2.RemoveAll(o => o.Relic is NewLeaf);

        if (owner.RunState.Players.Count > 1)
            list2.Add(clericOption);

        if (rng.NextBool())
            list2.Add(toughnessOption);
        else
            list2.Add(safetyOption);

        if (eventOption.Relic is not LargeCapsule)
        {
            if (rng.NextBool())
                list2.Add(patienceOption);
            else
                list2.Add(scavengerOption);
        }

        List<EventOption> list3 =  MegaCrit.Sts2.Core.Extensions.ListExtensions
            .UnstableShuffle(list2.ToList(), (Rng)rng)
            .Take(2)
            .ToList();

        list3.Add(eventOption);

        __result = list3;
        return false;
    }
}