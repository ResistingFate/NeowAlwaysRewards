using System;
using System.Collections.Generic;
using System.IO;
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
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

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
    private const string ConfigFileName = "NeowAlwaysRewards.logconfig.cfg";

    public static LogLevel CurrentLevel { get; private set; } = LogLevel.Info;

    private sealed class LogConfigFile
    {
        public string? LogLevel { get; set; }
    }

    static ModLog()
    {
        // Resolve the active log level from a drop-in config file first, then allow an
        // environment variable override for ad-hoc testing without editing the file.
        string? rawLevel = TryReadLogLevelFromConfigFile();
        if (TryParseLogLevel(rawLevel, out LogLevel configLevel))
            CurrentLevel = configLevel;

        string? envLevel = SysEnv.GetEnvironmentVariable("NEOWALWAYSREWARDS_LOG_LEVEL");
        if (TryParseLogLevel(envLevel, out LogLevel parsed))
            CurrentLevel = parsed;
    }

    public static void SetLevel(LogLevel level) => CurrentLevel = level;

    public static bool IsDebugEnabled => CurrentLevel >= LogLevel.Debug;
    public static bool IsTraceEnabled => CurrentLevel >= LogLevel.Trace;

    //public static void Error(string message) => Write(LogLevel.Error, message, isError: true);
    //public static void Warn(string message) => Write(LogLevel.Warn, message);
    //public static void Info(string message) => Write(LogLevel.Info, message);
    //public static void Debug(string message) => Write(LogLevel.Debug, message);
    //public static void Trace(string message) => Write(LogLevel.Trace, message);

    /* Trying out Megacrit's Logger */
    private static readonly Logger _logger = new Logger("NeowAlwaysRewards", LogType.Generic);
    public static void Error(string message) => _logger.Error(message);
    public static void Warn(string message) => _logger.Warn(message);
    public static void Info(string message) => _logger.Info(message);
    public static void Debug(string message) => _logger.Debug(message);
    public static void Trace(string message) => _logger.VeryDebug(message);

    private static void Write(LogLevel level, string message, bool isError = false)
    {
        // Keep the old GD.Print-style logger around so the fallback behavior stays available
        // if the MegaCrit logger ever changes or becomes undesirable for local debugging.
        if (level > CurrentLevel)
            return;

        string line = $"{Prefix} [{level}] {message}";
        if (isError)
            GD.PrintErr(line);
        else
            GD.Print(line);
    }

    private static string? TryReadLogLevelFromConfigFile()
    {
        // Look for a JSON config file in the same folder as the mod DLL so testers can
        // change verbosity without environment variables or code changes.
        try
        {
            string assemblyLocation = typeof(ModLog).Assembly.Location;
            if (string.IsNullOrWhiteSpace(assemblyLocation))
                return null;

            string? assemblyFolder = Path.GetDirectoryName(assemblyLocation);
            if (string.IsNullOrWhiteSpace(assemblyFolder))
                return null;

            string configPath = Path.Combine(assemblyFolder, ConfigFileName);
            if (!File.Exists(configPath))
                return null;

            string json = File.ReadAllText(configPath);
            LogConfigFile? config = JsonSerializer.Deserialize<LogConfigFile>(json);

            if (config?.LogLevel is not null && CurrentLevel >= LogLevel.Trace)
            {
                Trace($"[Neow/Config] Loaded log level '{config.LogLevel}' from {configPath}.");
            }

            return config?.LogLevel;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryParseLogLevel(string? rawLevel, out LogLevel level)
    {
        // Parse log level text safely so config and environment values can both use the same rules.
        if (!string.IsNullOrWhiteSpace(rawLevel) && Enum.TryParse(rawLevel, true, out LogLevel parsed))
        {
            level = parsed;
            return true;
        }

        level = default;
        return false;
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
        // Read InitialDescription through Traverse because the underlying type is not guaranteed
        // to be a public plain string and may move across versions.
        return neowTr.Property("InitialDescription").GetValue();
    }

    private static void LogRewardOptions(Neow neow, IEnumerable<EventOption> rewards)
    {
        // Keep the original reward trace dump, then add locale-safe identity diagnostics next to it.
        if (!ModLog.IsTraceEnabled)
            return;

        foreach (EventOption? option in rewards)
        {
            ModLog.Trace(
                $"[Neow/Rewards] option owner={NeowRewardHelper.GetOwnerId(neow)} title={option?.Title?.GetFormattedText() ?? "<null>"} " +
                $"textKey={NeowRewardHelper.DebugOptionKey(option)} relic={option?.Relic?.Id?.Entry ?? option?.Relic?.GetType().Name ?? "<none>"}"
            );
            NeowLocaleDiagnostics.LogOption("Neow/Rewards.identity", option);
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
            NeowLocaleDiagnostics.LogOptions("Neow/State.optionIdentities", optionList);
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
        // Find the exact SetEventState overload that accepts the live description object type
        // without assuming the game still uses the same concrete LocString signature.
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
        // Find the SetEventFinished overload that matches the resolved description type so
        // we do not hard-code the event description implementation.
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
        // Invoke the private localization lookup helper so Neow is finished with its own
        // localized done-page description instead of a mod-created replacement string.
        MethodInfo? method = AccessTools.Method(neow.GetType(), "L10NLookup", new[] { typeof(string) });
        return method?.Invoke(neow, new object[] { key });
    }

    private static string GetNeowEntry(Traverse neowTr)
    {
        // Resolve the event entry id through reflection so the done-page key still works
        // even if the backing type is not directly accessible to the mod assembly.
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
        // Log the event state before a synchronized option choice so desyncs can be compared
        // against the exact visible option list on each peer.
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
                NeowLocaleDiagnostics.LogOptions("Neow/EventSync.beforeChoose.identities", currentOptions);
            }
        }
        catch (Exception ex)
        {
            ModLog.Error($"[Neow/EventSync.beforeChoose] logging failed: {ex}");
        }
    }

    private static void Postfix(EventSynchronizer __instance, Player player, int optionIndex)
    {
        // Log the event state after a synchronized option choice so post-selection page changes
        // can be compared across peers and locales.
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
                NeowLocaleDiagnostics.LogOptions("Neow/EventSync.afterChoose.identities", currentOptions);
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
        public string DebugKey { get; init; } = string.Empty;
        public string RelicEntry { get; init; } = string.Empty;
        public CachedRewardSourceKind SourceKind { get; init; }
        public EventOption? GeneratedOption { get; init; }
    }

    private sealed class CachedRewardKeys
    {
        public List<CachedRewardOption> Options { get; set; } = new();
        public List<string> Keys { get; set; } = new();
        public List<string> DebugKeys { get; set; } = new();
        public List<string> RelicEntries { get; set; } = new();
        public string Reason { get; set; } = string.Empty;
    }

    private static readonly ConditionalWeakTable<Neow, CachedRewardKeys> CachedRewardKeysByNeow = new();

    public static bool IsBuildingRewardsIgnoringModifiers()
    {
        // Expose the temporary reward-generation guard so other patches can avoid recursive
        // cache generation while modifiers are intentionally suppressed.
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
        // Return the existing cached reward order when available so multiplayer peers keep using
        // the same authoritative snapshot through the modifier chain.
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
        // Rebuild the visible blessing page from live options using the exact same composite
        // identity that was cached earlier so locale-dependent text differences do not break matching.
        CachedRewardKeys snapshot = GetOrBuildCachedRewardSnapshot(neow, reason);
        if (snapshot.Options.Count <= 0)
            return Array.Empty<EventOption>();

        List<EventOption> allLiveOptions = neow.AllPossibleOptions?.ToList() ?? new List<EventOption>();
        NeowLocaleDiagnostics.LogDuplicateIdentities("Neow/Rewards.liveDuplicates", allLiveOptions);
        NeowLocaleDiagnostics.LogOptions("Neow/Rewards.liveAllPossible", allLiveOptions);

        Dictionary<string, EventOption> byKey = allLiveOptions
            .Where(option => option is not null)
            .GroupBy(NeowLocaleDiagnostics.BuildRewardIdentity)
            .Where(group => !string.IsNullOrEmpty(group.Key) && group.Key != NeowLocaleDiagnostics.UnidentifiedIdentity)
            .ToDictionary(group => group.Key, group => group.First());

        List<EventOption> rebuilt = new();
        foreach (CachedRewardOption cached in snapshot.Options)
        {
            if (byKey.TryGetValue(cached.Key, out EventOption? live))
            {
                AttachDebugBeforeChosen(neow, live, reason);
                rebuilt.Add(live);

                if (ModLog.IsTraceEnabled)
                {
                    ModLog.Trace(
                        $"[Neow/Rewards] Matched cached identity for owner={GetOwnerId(neow)} reason={reason} " +
                        $"identity={cached.Key} debugKey={cached.DebugKey} relic={cached.RelicEntry}"
                    );
                    NeowLocaleDiagnostics.LogOption("Neow/Rewards.matchedLive", live);
                }

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

                if (ModLog.IsTraceEnabled)
                {
                    NeowLocaleDiagnostics.LogOption("Neow/Rewards.generatedFallback", cached.GeneratedOption);
                }

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
            NeowLocaleDiagnostics.LogOptions("Neow/Rewards.rebuiltIdentities", rebuilt);
        }

        return rebuilt;
    }

    private static CachedRewardKeys GetOrBuildCachedRewardSnapshot(Neow neow, string reason)
    {
        // Ensure callers always get a snapshot object even if cache generation fails once, so
        // the final reward reconstruction path never has to null-check the cache table directly.
        if (CachedRewardKeysByNeow.TryGetValue(neow, out CachedRewardKeys? cached))
            return cached;

        BuildAndCacheVanillaRewardKeys(neow, reason);

        if (CachedRewardKeysByNeow.TryGetValue(neow, out cached))
            return cached;

        return new CachedRewardKeys();
    }

    private static IReadOnlyList<string> BuildAndCacheVanillaRewardKeys(Neow neow, string reason)
    {
        // Generate a stable reward snapshot with modifiers temporarily suppressed so the cache
        // represents the vanilla blessing page that should appear after the modifier queue finishes.
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

            List<EventOption> allLiveOptions = neow.AllPossibleOptions?.ToList() ?? new List<EventOption>();
            HashSet<string> liveKeys = allLiveOptions
                .Select(NeowLocaleDiagnostics.BuildRewardIdentity)
                .Where(identity => !string.IsNullOrEmpty(identity) && identity != NeowLocaleDiagnostics.UnidentifiedIdentity)
                .ToHashSet();

            NeowLocaleDiagnostics.LogDuplicateIdentities("Neow/Cache.generatedDuplicates", generated);
            NeowLocaleDiagnostics.LogDuplicateIdentities("Neow/Cache.liveDuplicates", allLiveOptions);
            NeowLocaleDiagnostics.LogOptions("Neow/Cache.generated", generated);
            NeowLocaleDiagnostics.LogOptions("Neow/Cache.liveAllPossible", allLiveOptions);

            CachedRewardKeys snapshot = new()
            {
                Options = generated
                    .Select(option =>
                    {
                        string key = NeowLocaleDiagnostics.BuildRewardIdentity(option);
                        string debugKey = DebugOptionKey(option);
                        string relicEntry = option?.Relic?.Id?.Entry ?? option?.Relic?.GetType().Name ?? "<none>";
                        bool hasLiveMatch = !string.IsNullOrEmpty(key)
                            && key != NeowLocaleDiagnostics.UnidentifiedIdentity
                            && liveKeys.Contains(key);

                        return new CachedRewardOption
                        {
                            Key = key,
                            DebugKey = debugKey,
                            RelicEntry = relicEntry,
                            SourceKind = hasLiveMatch ? CachedRewardSourceKind.LiveVanilla : CachedRewardSourceKind.GeneratedOnly,
                            GeneratedOption = hasLiveMatch ? null : option
                        };
                    })
                    .Where(entry => !string.IsNullOrEmpty(entry.Key) && entry.Key != NeowLocaleDiagnostics.UnidentifiedIdentity)
                    .ToList(),
                Reason = reason
            };

            snapshot.Keys = snapshot.Options.Select(option => option.Key).ToList();
            snapshot.DebugKeys = snapshot.Options.Select(option => option.DebugKey).ToList();
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
                ModLog.Trace(
                    $"[Neow/Cache] owner={GetOwnerId(owner)} debugKeys={string.Join(",", snapshot.DebugKeys)}"
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
                NeowLocaleDiagnostics.LogOption("Neow/Rewards.beforeChosenIdentity", chosen);
                NeowLocaleDiagnostics.LogOptions("Neow/Rewards.visibleIdentities", neow.CurrentOptions);
            }
            LogStateJson(neow, $"LiveReward.beforeChosen[{DebugOptionKey(chosen)}]");
            return Task.CompletedTask;
        };
    }

    public static void LogStateJson(Neow neow, string stage)
    {
        // Dump a structured snapshot of Neow state at critical transition points so testers
        // can compare peers and locales without relying on formatted display text.
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
                currentOptionIdentities = neow.CurrentOptions?.Select(NeowLocaleDiagnostics.BuildRewardIdentity).ToArray() ?? Array.Empty<string>(),
                modifierOptionCount = modifierOptions.Count,
                modifierOptionKeys = modifierOptions.Select(DebugOptionKey).ToArray(),
                modifierOptionIdentities = modifierOptions.Select(NeowLocaleDiagnostics.BuildRewardIdentity).ToArray(),
                cachedKeyCount = cached?.Keys.Count ?? 0,
                cachedKeys = cached?.Keys.ToArray() ?? Array.Empty<string>(),
                cachedDebugKeys = cached?.DebugKeys.ToArray() ?? Array.Empty<string>(),
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
        // Resolve the event owner through Traverse so all logging paths can consistently
        // identify which peer's Neow state is being inspected.
        return GetOwnerId(Traverse.Create(neow).Property("Owner").GetValue<Player>());
    }

    public static string GetOwnerId(Player? owner)
    {
        // Prefer the stable network id for logs, then fall back to ToString if the player
        // object is present but cannot expose NetId in the current context.
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
        // Resolve a stable event id for logs without assuming the Id backing type remains
        // directly accessible or shaped the same across game versions.
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
        // Join the human-friendly debug keys for a whole option list so page snapshots stay concise.
        if (options is null)
            return string.Empty;

        return string.Join(",", options.Select(DebugOptionKey));
    }

    public static string DebugOptionKey(EventOption? option)
    {
        // Keep the original lightweight debug key for logs, but do not use it for reward
        // identity matching because locale-safe reconstruction now uses composite identities.
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
