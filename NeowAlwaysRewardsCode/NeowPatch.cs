using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using SysEnv = System.Environment;
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


public enum LogLevel
{
    Error = 0,
    Warn = 1,
    Info = 2,
    Debug = 3,
    Trace = 4
}

internal static class ModLog
{
    private const string Prefix = "[NeowAlwaysRewards]";

    public static LogLevel CurrentLevel { get; private set; } = LogLevel.Info;

    static ModLog()
    {
        string? rawLevel = SysEnv.GetEnvironmentVariable("NEOWALWAYSREWARDS_LOG_LEVEL");
        if (!string.IsNullOrWhiteSpace(rawLevel) && Enum.TryParse(rawLevel, true, out LogLevel parsed))
            CurrentLevel = parsed;
    }

    public static void SetLevel(LogLevel level) => CurrentLevel = level;

    public static bool IsDebugEnabled => CurrentLevel >= LogLevel.Debug;
    public static bool IsTraceEnabled => CurrentLevel >= LogLevel.Trace;

    public static void Error(string message) => Write(LogLevel.Error, message, isError: true);
    public static void Warn(string message) => Write(LogLevel.Warn, message);
    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Debug(string message) => Write(LogLevel.Debug, message);
    public static void Trace(string message) => Write(LogLevel.Trace, message);

    private static void Write(LogLevel level, string message, bool isError = false)
    {
        if (level > CurrentLevel)
            return;

        string line = $"{Prefix} [{level}] {message}";
        if (isError)
            GD.PrintErr(line);
        else
            GD.Print(line);
    }
}

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
        // but rebuild the final blessing page from fresh live vanilla EventOptions where possible.
        //
        // The key difference from the previous version is:
        // - we do NOT reuse cached vanilla EventOption instances
        // - we do NOT wrap cached EventOption callbacks
        // - we cache the final blessing TEXT KEYS early, plus any generated-only third-party options
        // - when the last modifier finishes, we rebuild fresh live options from neow.AllPossibleOptions
        //   and only fall back to the cached generated option when no live vanilla option exists
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
            ModLog.Error($"[Neow/Flow] InitialDescription was null for owner={NeowRewardHelper.GetOwnerId(neow)}.");
            FinishNeowEvent(neow);
            return;
        }

        List<EventOption> modifierOptions =
            neowTr.Property("ModifierOptions").GetValue<List<EventOption>>() ?? new();

        ModLog.Debug(
            $"[Neow/Flow] Modifier selected for owner={NeowRewardHelper.GetOwnerId(neow)} index={index} modifierCount={modifierOptions.Count}"
        );

        if (index + 1 < modifierOptions.Count)
        {
            EventOption nextModifier = modifierOptions[index + 1];

            ModLog.Debug(
                $"[Neow/Flow] Showing next modifier for owner={NeowRewardHelper.GetOwnerId(neow)} nextKey={NeowRewardHelper.DebugOptionKey(nextModifier)}"
            );

            SetEventState(neow, initialDescription, new[] { nextModifier });
            NeowRewardHelper.LogStateJson(neow, $"OnModifierOptionSelected.afterSetNextModifier[{index}]");
            return;
        }

        IReadOnlyList<EventOption> rewards = NeowRewardHelper.GetOrBuildLiveVanillaRewardsFromCachedKeys(
            neow,
            "OnModifierOptionSelected.final"
        );

        ModLog.Debug(
            $"[Neow/Flow] Final modifier completed for owner={NeowRewardHelper.GetOwnerId(neow)} liveRewardCount={rewards.Count}"
        );
        LogRewardOptions(neow, rewards);

        if (rewards.Count > 0)
        {
            SetEventState(neow, initialDescription, rewards);
            NeowRewardHelper.LogStateJson(neow, $"OnModifierOptionSelected.afterSetRewards[{index}]");
            return;
        }

        ModLog.Warn(
            $"[Neow/Flow] No live rewards were available after modifier queue for owner={NeowRewardHelper.GetOwnerId(neow)}; finishing event."
        );
        FinishNeowEvent(neow);
    }

    private static object? GetInitialDescription(Traverse neowTr)
    {
        return neowTr.Property("InitialDescription").GetValue();
    }

    private static void LogRewardOptions(Neow neow, IEnumerable<EventOption> rewards)
    {
        if (!ModLog.IsTraceEnabled)
            return;

        foreach (EventOption? option in rewards)
        {
            ModLog.Trace(
                $"[Neow/Rewards] option owner={NeowRewardHelper.GetOwnerId(neow)} title={option?.Title?.GetFormattedText() ?? "<null>"} " +
                $"textKey={NeowRewardHelper.DebugOptionKey(option)} relic={option?.Relic?.Id?.Entry ?? option?.Relic?.GetType().Name ?? "<none>"}"
            );
        }
    }

    private static void SetEventState(Neow neow, object description, IEnumerable<EventOption> options)
    {
        // Route the page swap through the same private method vanilla uses, but log the exact
        // option list first so both peers can be compared around a desync.
        List<EventOption> optionList = options?.ToList() ?? new List<EventOption>();

        ModLog.Debug(
            $"[Neow/State] owner={NeowRewardHelper.GetOwnerId(neow)} descriptionType={description.GetType().FullName} optionCount={optionList.Count}"
        );
        if (ModLog.IsTraceEnabled)
        {
            ModLog.Trace(
                $"[Neow/State] owner={NeowRewardHelper.GetOwnerId(neow)} optionKeys={NeowRewardHelper.DebugOptionKeys(optionList)}"
            );
        }

        MethodInfo? setEventState = FindCompatibleSetEventStateMethod(neow.GetType(), description);
        if (setEventState is null)
        {
            ModLog.Error(
                $"[Neow/State] Could not find a compatible SetEventState overload for description type {description.GetType().FullName}."
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
            ModLog.Error(
                $"[Neow/State] Could not find a compatible SetEventFinished overload for owner={NeowRewardHelper.GetOwnerId(neow)}."
            );
            return;
        }

        ModLog.Debug(
            $"[Neow/Flow] FinishNeowEvent owner={NeowRewardHelper.GetOwnerId(neow)} descriptionType={doneDescription?.GetType().FullName ?? "<null>"}"
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
        // Cache the final blessing result early, before the modifier chain advances.
        // Vanilla blessings are remembered by key so they can be rebuilt live later,
        // while generated-only third-party options can be preserved as a fallback.
        Player? owner = Traverse.Create(__instance).Property("Owner").GetValue<Player>();
        if (owner is null)
            return;

        if (!NeowRewardHelper.IsBuildingRewardsIgnoringModifiers() && owner.RunState.Modifiers.Count > 0)
        {
            IReadOnlyList<string> keys = NeowRewardHelper.GetOrBuildCachedVanillaRewardKeys(
                __instance,
                "GenerateInitialOptions.Postfix"
            );

            ModLog.Debug(
                $"[Neow/Cache] Cached reward keys for owner={NeowRewardHelper.GetOwnerId(owner)} count={keys.Count}"
            );
            if (ModLog.IsTraceEnabled)
            {
                ModLog.Trace(
                    $"[Neow/Cache] owner={NeowRewardHelper.GetOwnerId(owner)} keys={string.Join(",", keys)}"
                );
            }
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
            ModLog.Info(
                $"[Neow/Fallback] No modifier Neow options for owner={NeowRewardHelper.GetOwnerId(owner)}; using live vanilla rewards."
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

            ModLog.Debug(
                $"[Neow/EventSync.beforeChoose] player={NeowRewardHelper.GetOwnerId(player)} " +
                $"event={NeowRewardHelper.GetEventIdSafe(eventForPlayer)} finished={eventForPlayer.IsFinished} " +
                $"requestedIndex={optionIndex} currentCount={currentOptions.Count}"
            );
            if (ModLog.IsTraceEnabled)
            {
                ModLog.Trace(
                    $"[Neow/EventSync.beforeChoose] player={NeowRewardHelper.GetOwnerId(player)} " +
                    $"event={NeowRewardHelper.GetEventIdSafe(eventForPlayer)} currentKeys={NeowRewardHelper.DebugOptionKeys(currentOptions)}"
                );
            }
        }
        catch (Exception ex)
        {
            ModLog.Error($"[Neow/EventSync.beforeChoose] logging failed: {ex}");
        }
    }

    private static void Postfix(EventSynchronizer __instance, Player player, int optionIndex)
    {
        try
        {
            EventModel eventForPlayer = __instance.GetEventForPlayer(player);
            IReadOnlyList<EventOption> currentOptions = eventForPlayer.CurrentOptions;

            ModLog.Debug(
                $"[Neow/EventSync.afterChoose] player={NeowRewardHelper.GetOwnerId(player)} " +
                $"event={NeowRewardHelper.GetEventIdSafe(eventForPlayer)} finished={eventForPlayer.IsFinished} " +
                $"chosenIndex={optionIndex} currentCount={currentOptions.Count}"
            );
            if (ModLog.IsTraceEnabled)
            {
                ModLog.Trace(
                    $"[Neow/EventSync.afterChoose] player={NeowRewardHelper.GetOwnerId(player)} " +
                    $"event={NeowRewardHelper.GetEventIdSafe(eventForPlayer)} currentKeys={NeowRewardHelper.DebugOptionKeys(currentOptions)}"
                );
            }
        }
        catch (Exception ex)
        {
            ModLog.Error($"[Neow/EventSync.afterChoose] logging failed: {ex}");
        }
    }
}

public static class NeowRewardHelper
{
    private const string LogPrefix = "[NeowAlwaysRewards]";

    [ThreadStatic]
    private static bool _buildingRewardsIgnoringModifiers;

    private enum CachedRewardSourceKind
    {
        LiveVanilla,
        GeneratedOnly
    }

    private sealed class CachedRewardOption
    {
        public string Key { get; init; } = string.Empty;
        public string RelicEntry { get; init; } = string.Empty;
        public CachedRewardSourceKind SourceKind { get; init; }
        public EventOption? GeneratedOption { get; init; }
    }

    private sealed class CachedRewardKeys
    {
        public List<CachedRewardOption> Options { get; set; } = new();
        public List<string> Keys { get; set; } = new();
        public List<string> RelicEntries { get; set; } = new();
        public string Reason { get; set; } = string.Empty;
    }

    private static readonly ConditionalWeakTable<Neow, CachedRewardKeys> CachedRewardKeysByNeow = new();

    public static bool IsBuildingRewardsIgnoringModifiers()
    {
        return _buildingRewardsIgnoringModifiers;
    }

    /// <summary>
    /// Build the blessing page by temporarily ignoring custom-run modifiers,
    /// then cache a lightweight snapshot of the chosen option keys and relic ids.
    ///
    /// Vanilla rewards are cached by key so they can be rebuilt from live options later.
    /// Third-party rewards that only exist in the generated result are kept as generated-option fallbacks.
    /// </summary>
    public static IReadOnlyList<string> GetOrBuildCachedVanillaRewardKeys(Neow neow, string reason)
    {
        if (CachedRewardKeysByNeow.TryGetValue(neow, out CachedRewardKeys? cached))
            return cached.Keys.ToList();

        return BuildAndCacheVanillaRewardKeys(neow, reason);
    }

    /// <summary>
    /// Rebuild fresh live EventOptions from neow.AllPossibleOptions using the cached key order.
    /// This avoids reusing stale vanilla EventOption instances and keeps the live callbacks vanilla.
    ///
    /// If a cached reward came from another mod and only existed in the generated result,
    /// fall back to that cached generated option so compatibility options like Soul Capture survive.
    /// </summary>
    public static IReadOnlyList<EventOption> GetOrBuildLiveVanillaRewardsFromCachedKeys(Neow neow, string reason)
    {
        CachedRewardKeys snapshot = GetOrBuildCachedRewardSnapshot(neow, reason);
        if (snapshot.Options.Count <= 0)
            return Array.Empty<EventOption>();

        List<EventOption> allLiveOptions = neow.AllPossibleOptions?.ToList() ?? new List<EventOption>();
        Dictionary<string, EventOption> byKey = allLiveOptions
            .Where(option => option?.TextKey is not null)
            .GroupBy(option => option.TextKey)
            .ToDictionary(group => group.Key, group => group.First());

        List<EventOption> rebuilt = new();
        foreach (CachedRewardOption cached in snapshot.Options)
        {
            if (byKey.TryGetValue(cached.Key, out EventOption? live))
            {
                AttachDebugBeforeChosen(neow, live, reason);
                rebuilt.Add(live);
                continue;
            }

            if (cached.GeneratedOption is not null)
            {
                ModLog.Debug(
                    $"[Neow/Rewards] Using cached generated reward fallback for owner={GetOwnerId(neow)} reason={reason} " +
                    $"key={cached.Key} relic={cached.RelicEntry}"
                );

                AttachDebugBeforeChosen(neow, cached.GeneratedOption, reason);
                rebuilt.Add(cached.GeneratedOption);
                continue;
            }

            ModLog.Warn(
                $"[Neow/Rewards] Failed to rebuild reward for owner={GetOwnerId(neow)} key={cached.Key}."
            );
            if (ModLog.IsTraceEnabled)
            {
                ModLog.Trace(
                    $"[Neow/Rewards] owner={GetOwnerId(neow)} availableLiveKeys={string.Join(",", byKey.Keys.OrderBy(x => x))}"
                );
            }
        }

        ModLog.Debug(
            $"[Neow/Rewards] CreateLiveVanillaRewards owner={GetOwnerId(neow)} reason={reason} count={rebuilt.Count}"
        );
        if (ModLog.IsTraceEnabled)
        {
            ModLog.Trace(
                $"[Neow/Rewards] owner={GetOwnerId(neow)} rebuiltKeys={DebugOptionKeys(rebuilt)}"
            );
        }

        return rebuilt;
    }

    private static CachedRewardKeys GetOrBuildCachedRewardSnapshot(Neow neow, string reason)
    {
        if (CachedRewardKeysByNeow.TryGetValue(neow, out CachedRewardKeys? cached))
            return cached;

        BuildAndCacheVanillaRewardKeys(neow, reason);

        if (CachedRewardKeysByNeow.TryGetValue(neow, out cached))
            return cached;

        return new CachedRewardKeys();
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
                ModLog.Error("[Neow/Cache] Could not find GenerateInitialOptions.");
                return Array.Empty<string>();
            }

            IReadOnlyList<EventOption> generated =
                generateInitialOptionsMethod.Invoke(neow, Array.Empty<object>()) as IReadOnlyList<EventOption>
                ?? Array.Empty<EventOption>();

            HashSet<string> liveKeys = (neow.AllPossibleOptions ?? Array.Empty<EventOption>())
                .Select(DebugOptionKey)
                .Where(key => !string.IsNullOrEmpty(key))
                .ToHashSet();

            CachedRewardKeys snapshot = new()
            {
                Options = generated
                    .Select(option =>
                    {
                        string key = DebugOptionKey(option);
                        string relicEntry = option?.Relic?.Id?.Entry ?? option?.Relic?.GetType().Name ?? "<none>";
                        bool hasLiveMatch = !string.IsNullOrEmpty(key) && liveKeys.Contains(key);

                        return new CachedRewardOption
                        {
                            Key = key,
                            RelicEntry = relicEntry,
                            SourceKind = hasLiveMatch ? CachedRewardSourceKind.LiveVanilla : CachedRewardSourceKind.GeneratedOnly,
                            GeneratedOption = hasLiveMatch ? null : option
                        };
                    })
                    .Where(entry => !string.IsNullOrEmpty(entry.Key))
                    .ToList(),
                Reason = reason
            };

            snapshot.Keys = snapshot.Options.Select(option => option.Key).ToList();
            snapshot.RelicEntries = snapshot.Options.Select(option => option.RelicEntry).ToList();

            CachedRewardKeysByNeow.Remove(neow);
            CachedRewardKeysByNeow.Add(neow, snapshot);

            ModLog.Debug(
                $"[Neow/Cache] Built cached reward snapshot for owner={GetOwnerId(owner)} reason={reason} count={snapshot.Keys.Count}"
            );
            if (ModLog.IsTraceEnabled)
            {
                ModLog.Trace(
                    $"[Neow/Cache] owner={GetOwnerId(owner)} keys={string.Join(",", snapshot.Keys)} relics={string.Join(",", snapshot.RelicEntries)} " +
                    $"generatedOnlyKeys={string.Join(",", snapshot.Options.Where(option => option.SourceKind == CachedRewardSourceKind.GeneratedOnly).Select(option => option.Key))}"
                );
            }

            return snapshot.Keys.ToList();
        }
        catch (Exception ex)
        {
            ModLog.Error($"[Neow/Cache] Failed to build cached vanilla reward keys for owner={GetOwnerId(owner)}: {ex}");
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
            ModLog.Debug(
                $"[Neow/Rewards] LiveReward.BeforeChosen owner={GetOwnerId(neow)} reason={reason} " +
                $"key={DebugOptionKey(chosen)} relic={chosen.Relic?.Id?.Entry ?? chosen.Relic?.GetType().Name ?? "<none>"} " +
                $"visibleCount={neow.CurrentOptions.Count}"
            );
            if (ModLog.IsTraceEnabled)
            {
                ModLog.Trace(
                    $"[Neow/Rewards] owner={GetOwnerId(neow)} visibleKeys={DebugOptionKeys(neow.CurrentOptions)}"
                );
            }
            LogStateJson(neow, $"LiveReward.beforeChosen[{DebugOptionKey(chosen)}]");
            return Task.CompletedTask;
        };
    }

    public static void LogStateJson(Neow neow, string stage)
    {
        if (!ModLog.IsTraceEnabled)
            return;

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
                cachedKinds = cached?.Options.Select(option => option.SourceKind.ToString()).ToArray() ?? Array.Empty<string>(),
                generatedOnlyKeys = cached?.Options
                    .Where(option => option.SourceKind == CachedRewardSourceKind.GeneratedOnly)
                    .Select(option => option.Key)
                    .ToArray() ?? Array.Empty<string>(),
                cacheReason = cached?.Reason ?? string.Empty,
                initialDescription = DebugLocSafe(neow.InitialDescription)
            };

            ModLog.Trace($"[Neow/StateJson] {JsonSerializer.Serialize(payload)}");
        }
        catch (Exception ex)
        {
            ModLog.Error($"[Neow/StateJson] Failed to log Neow state JSON at stage {stage}: {ex}");
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
        // Keep localization logging side-effect free.
        //
        // Calling LocString.GetFormattedText() during debug dumps can trigger BaseLib
        // missing-key warnings for internal event descriptions such as NEOW.EVENT.description.
        // For release-safe diagnostics, prefer the stable localization identity over
        // resolving the actual text at log time.
        if (description is null)
            return string.Empty;

        if (description is LocString loc)
        {
            try
            {
                string table = string.IsNullOrWhiteSpace(loc.LocTable) ? "<null-table>" : loc.LocTable;
                string key = string.IsNullOrWhiteSpace(loc.LocEntryKey) ? "<null-key>" : loc.LocEntryKey;
                return $"{table}.{key}";
            }
            catch
            {
                return "<loc-error>";
            }
        }

        return description.ToString() ?? string.Empty;
    }
}
