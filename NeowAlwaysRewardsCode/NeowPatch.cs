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
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game.Sync;
using MegaCrit.Sts2.Core.Random;
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

    public static LogLevel CurrentLevel { get; private set; } = LogLevel.Debug;
    public static string CurrentLevelSource { get; private set; } = "default";

    private sealed class LogConfigFile
    {
        public string? LogLevel { get; set; }
    }

    static ModLog()
    {
        // Resolve the log level from a drop-in config file first, then allow an environment
        // variable override so issue repro testers can switch levels without rebuilding the mod.
        string? rawLevel = TryReadLogLevelFromConfigFile();
        if (TryParseLogLevel(rawLevel, out LogLevel configLevel))
        {
            CurrentLevel = configLevel;
            CurrentLevelSource = "config";
        }

        string? envLevel = SysEnv.GetEnvironmentVariable("NEOWALWAYSREWARDS_LOG_LEVEL");
        if (TryParseLogLevel(envLevel, out LogLevel envParsed))
        {
            CurrentLevel = envParsed;
            CurrentLevelSource = "env";
        }

        Info($"[Startup] Active log level: {CurrentLevel} (source={CurrentLevelSource}).");
    }

    public static void SetLevel(LogLevel level)
    {
        // Allow future runtime configuration hooks to update the mod log level intentionally.
        CurrentLevel = level;
        CurrentLevelSource = "runtime";
        Info($"[Startup] Active log level: {CurrentLevel} (source={CurrentLevelSource}).");
    }

    public static bool IsDebugEnabled => CurrentLevel >= LogLevel.Debug;
    public static bool IsTraceEnabled => CurrentLevel >= LogLevel.Trace;

    public static void Error(string message) => Write(LogLevel.Error, message, isError: true);
    public static void Warn(string message) => Write(LogLevel.Warn, message);
    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Debug(string message) => Write(LogLevel.Debug, message);
    public static void Trace(string message) => Write(LogLevel.Trace, message);

    private static void Write(LogLevel level, string message, bool isError = false)
    {
        // Route all logging through the same level gate so trace diagnostics are controlled
        // by the mod's own setting rather than a second logger backend with different rules.
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
        // Read a JSON config file from the same folder as the mod DLL so users can enable
        // trace logging for issue repros without touching environment variables.
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

            string raw = File.ReadAllText(configPath);
            raw = raw.Trim();

            if (string.IsNullOrWhiteSpace(raw))
                return null;

            if (raw.StartsWith("{"))
            {
                LogConfigFile? parsed = JsonSerializer.Deserialize<LogConfigFile>(raw);
                return parsed?.LogLevel;
            }

            return raw;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryParseLogLevel(string? rawLevel, out LogLevel level)
    {
        // Share the same parsing rules between config-file and environment-based log settings.
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

    [HarmonyPriority(Priority.Low)]
    private static bool Prefix(Neow __instance, Func<Task> modifierFunc, int index, ref Task __result)
    {
        // Only replace the FINAL modifier hop.
        //
        // Let vanilla handle intermediate modifier pages so other mods that expect the
        // normal one-by-one modifier flow keep seeing the original method behavior.
        //
        // On the last modifier, avoid finishing Neow immediately. Instead:
        // 1) run the chosen modifier callback,
        // 2) preserve any non-modifier options another mod already placed on the event,
        // 3) merge in the cached vanilla blessing page as a fallback/addition.
        List<EventOption> modifierOptions =
            Traverse.Create(__instance).Property("ModifierOptions").GetValue<List<EventOption>>() ?? new();

        if (index + 1 < modifierOptions.Count)
            return true;

        if (!NeowRewardHelper.HasUsableCachedVanillaRewardFallback(__instance))
        {
            ModLog.Debug(
                $"[Neow/Flow] No cached vanilla fallback was available for owner={NeowRewardHelper.GetOwnerId(__instance)}; using vanilla final modifier flow."
            );
            return true;
        }

        __result = RunFinalModifierSelectionFlow(__instance, modifierFunc, index);
        return false;
    }

    private static async Task RunFinalModifierSelectionFlow(Neow neow, Func<Task> modifierFunc, int index)
    {
        try
        {
            // Compatibility-first final modifier flow:
            // - keep the real modifier callback intact,
            // - preserve any live non-modifier page another mod produced,
            // - only synthesize vanilla blessings when there is no better live page.
            NeowRewardHelper.LogStateJson(neow, $"OnModifierOptionSelected.final.enter[{index}]");

            await modifierFunc();

            NeowRewardHelper.LogStateJson(neow, $"OnModifierOptionSelected.final.afterModifierFunc[{index}]");

            var neowTr = Traverse.Create(neow);

            object? rewardPageDescription = GetRewardPageDescription(neow, neowTr);
            if (rewardPageDescription is null)
            {
                ModLog.Error($"[Neow/Flow] Reward page description was null for owner={NeowRewardHelper.GetOwnerId(neow)}.");
                FinishNeowEvent(neow);
                return;
            }

            List<EventOption> modifierOptions =
                neowTr.Property("ModifierOptions").GetValue<List<EventOption>>() ?? new();

            IReadOnlyList<EventOption> preservedLiveOptions =
                NeowRewardHelper.ExtractVisibleNonModifierOptions(neow, modifierOptions);

            IReadOnlyList<EventOption> vanillaRewards = NeowRewardHelper.GetOrBuildLiveVanillaRewardsFromCachedKeys(
                neow,
                preservedLiveOptions.Count > 0
                    ? "OnModifierOptionSelected.final.mergeVanilla"
                    : "OnModifierOptionSelected.final.vanillaOnly"
            );

            IReadOnlyList<EventOption> finalOptions = preservedLiveOptions.Count > 0
                ? NeowRewardHelper.MergeUniqueOptionsInOrder(preservedLiveOptions, vanillaRewards)
                : vanillaRewards;

            ModLog.Debug(
                $"[Neow/Flow] Final modifier completed for owner={NeowRewardHelper.GetOwnerId(neow)} " +
                $"preservedLiveCount={preservedLiveOptions.Count} vanillaRewardCount={vanillaRewards.Count} finalCount={finalOptions.Count}"
            );
            LogRewardOptions(neow, finalOptions);

            if (finalOptions.Count > 0)
            {
                if (ModLog.IsTraceEnabled)
                {
                    ModLog.Trace(
                        $"[Neow/Flow] Showing post-modifier rewards page owner={NeowRewardHelper.GetOwnerId(neow)} " +
                        $"event={NeowRewardHelper.GetEventIdSafe(neow)} optionCount={finalOptions.Count} finished={neow.IsFinished}"
                    );
                    NeowLocaleDiagnostics.LogOptions("Neow/Flow.finalPageIdentities", finalOptions);
                }

                EnsureNeowIsOpenForRewardPage(neow);
                SetEventState(neow, rewardPageDescription, finalOptions);
                NeowRewardHelper.LogStateJson(neow, $"OnModifierOptionSelected.final.afterSetRewards[{index}]");
                return;
            }

            ModLog.Warn(
                $"[Neow/Flow] No post-modifier rewards were available for owner={NeowRewardHelper.GetOwnerId(neow)}; finishing event."
            );
            FinishNeowEvent(neow);
        }
        catch (Exception ex)
        {
            ModLog.Error($"[Neow/Flow] Final modifier reward flow failed for owner={NeowRewardHelper.GetOwnerId(neow)} index={index}: {ex}");
            FinishNeowEvent(neow);
        }
    }

    private static object? GetRewardPageDescription(Neow neow, Traverse neowTr)
    {
        // Do not reuse Neow.InitialDescription here.
        //
        // In custom runs, v0.107.1 overrides InitialDescription to the special
        // NEOW.EVENT.description text while modifiers are active. Showing that again after
        // the final modifier makes the reward page look like it is still on the Ancient
        // dialogue step. Use Neow's vanilla generic DONE description instead: it is an
        // existing game localization key, so this mod does not need to ship custom text.
        object? doneDescription = InvokeL10NLookup(neow, $"{GetNeowEntry(neowTr)}.pages.DONE.description");
        return doneDescription ?? neowTr.Property("InitialDescription").GetValue();
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
            NeowLocaleDiagnostics.LogOption("Neow/Rewards.identity", option);
        }
    }

    private static void EnsureNeowIsOpenForRewardPage(Neow neow)
    {
        // Some v1.0.7 modifier callbacks can leave the event marked finished before we swap
        // to the reward page. If that flag stays true, the UI can sit on Neow's/Ancient's
        // dialogue without accepting reward choices. Re-open it immediately before SetEventState.
        try
        {
            NeowRewardHelper.TrySetInstancePropertyOrBackingField(neow, "IsFinished", false, "re-opening Neow before reward page");
        }
        catch (Exception ex)
        {
            ModLog.Warn($"[Neow/State] Could not reset IsFinished before reward page: {ex.Message}");
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
        for (Type? type = neowType; type is not null; type = type.BaseType)
        {
            MethodInfo? match = type
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
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

            if (match is not null)
                return match;
        }

        return null;
    }

    private static void FinishNeowEvent(Neow neow)
    {
        // Finish Neow in the same general way vanilla does when there is no page to show.
        var neowTr = Traverse.Create(neow);
        string entry = GetNeowEntry(neowTr);

        object? doneDescription = InvokeL10NLookup(neow, $"{entry}.pages.DONE.description");
        if (doneDescription is null)
            doneDescription = neowTr.Property("InitialDescription").GetValue();

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

        for (Type? type = neowType; type is not null; type = type.BaseType)
        {
            MethodInfo? match = type
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .FirstOrDefault(method =>
                {
                    if (method.Name != "SetEventFinished")
                        return false;

                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length != 1)
                        return false;

                    return parameters[0].ParameterType.IsInstanceOfType(description);
                });

            if (match is not null)
                return match;
        }

        return null;
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
            if (ModLog.IsTraceEnabled)
            {
                ModLog.Trace(
                    $"[Neow/Fallback] owner={NeowRewardHelper.GetOwnerId(owner)} event={NeowRewardHelper.GetEventIdSafe(__instance)} locale={NeowLocaleDiagnostics.GetGameLocaleSafe()} modifierCount={owner.RunState.Modifiers.Count} rebuiltRewardCount={liveRewards.Count}"
                );
                NeowLocaleDiagnostics.LogOptions("Neow/Fallback.rebuiltIdentities", liveRewards);
            }
            __result = liveRewards;
        }
    }
}

[HarmonyPatch]
public static class EventSynchronizerRemoteNeowChoiceQueuePatch
{
    private sealed class QueueState
    {
        public readonly object Gate = new();
        public Task Tail = Task.CompletedTask;
    }

    private static readonly ConditionalWeakTable<EventSynchronizer, QueueState> Queues = new();

    private static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(EventSynchronizer),
            "HandleEventOptionChosenMessage",
            new[] { typeof(OptionIndexChosenMessage), typeof(ulong) }
        )!;
    }

    private static bool Prefix(EventSynchronizer __instance, OptionIndexChosenMessage message, ulong senderId)
    {
        if (message.type != OptionIndexType.Event)
            return true;

        Player? player = GetPlayer(__instance, senderId);
        if (player is null)
            return true;

        EventModel? eventForPlayer = GetEventForPlayerSafe(__instance, player);
        if (!IsNeowEvent(eventForPlayer))
            return true;

        QueueState state = Queues.GetValue(__instance, _ => new QueueState());
        lock (state.Gate)
        {
            state.Tail = RunAfterPreviousRemoteNeowChoice(
                __instance,
                state.Tail,
                player,
                (int)message.optionIndex,
                senderId
            );
        }

        return false;
    }

    private static async Task RunAfterPreviousRemoteNeowChoice(
        EventSynchronizer synchronizer,
        Task previous,
        Player player,
        int optionIndex,
        ulong senderId)
    {
        try
        {
            await previous;

            // EventSynchronizer normally queues EventOption.Chosen() tasks and immediately
            // accepts the next network message. That is too racy for Neow modifier chains:
            // a remote client can reach our injected vanilla reward page and send option 2
            // while this peer's remote copy is still on the one-option modifier page.
            await WaitForPendingEventOptionTasks(synchronizer);

            EventModel eventForPlayer = synchronizer.GetEventForPlayer(player);
            if (eventForPlayer.IsFinished)
            {
                ModLog.Trace(
                    $"[Neow/EventSync.remoteQueue] sender={senderId} player={NeowRewardHelper.GetOwnerId(player)} " +
                    $"option={optionIndex} ignored because event is already finished."
                );
                return;
            }

            ModLog.Debug(
                $"[Neow/EventSync.remoteQueue] applying queued remote Neow choice sender={senderId} " +
                $"player={NeowRewardHelper.GetOwnerId(player)} option={optionIndex} currentCount={eventForPlayer.CurrentOptions.Count}"
            );

            MethodInfo? choose = AccessTools.Method(
                typeof(EventSynchronizer),
                "ChooseOptionForEvent",
                new[] { typeof(Player), typeof(int) }
            );

            if (choose is null)
            {
                ModLog.Error("[Neow/EventSync.remoteQueue] Could not find EventSynchronizer.ChooseOptionForEvent.");
                return;
            }

            choose.Invoke(synchronizer, new object[] { player, optionIndex });
        }
        catch (Exception ex)
        {
            ModLog.Error(
                $"[Neow/EventSync.remoteQueue] Failed applying queued remote Neow choice " +
                $"sender={senderId} player={NeowRewardHelper.GetOwnerId(player)} option={optionIndex}: {UnwrapReflectionException(ex)}"
            );
        }
    }

    private static async Task WaitForPendingEventOptionTasks(EventSynchronizer synchronizer)
    {
        for (int i = 0; i < 20; i++)
        {
            List<Task> pending =
                Traverse.Create(synchronizer).Field("_pendingOptionTasks").GetValue<List<Task>>() ?? new();

            Task[] unfinished = pending.Where(task => task is { IsCompleted: false }).ToArray();
            if (unfinished.Length == 0)
                return;

            await Task.WhenAll(unfinished);
        }
    }

    private static Player? GetPlayer(EventSynchronizer synchronizer, ulong senderId)
    {
        object? playerCollection = Traverse.Create(synchronizer).Field("_playerCollection").GetValue();
        MethodInfo? getPlayer = playerCollection?.GetType().GetMethod("GetPlayer", new[] { typeof(ulong) });
        return getPlayer?.Invoke(playerCollection, new object[] { senderId }) as Player;
    }

    private static EventModel? GetEventForPlayerSafe(EventSynchronizer synchronizer, Player player)
    {
        try
        {
            return synchronizer.GetEventForPlayer(player);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsNeowEvent(EventModel? eventModel)
    {
        if (eventModel is not Neow)
            return false;

        string id = NeowRewardHelper.GetEventIdSafe(eventModel);
        return string.Equals(id, "NEOW", StringComparison.OrdinalIgnoreCase)
            || string.Equals(id, "EVENT.NEOW", StringComparison.OrdinalIgnoreCase);
    }

    private static Exception UnwrapReflectionException(Exception ex)
    {
        return ex is TargetInvocationException { InnerException: not null } tie
            ? tie.InnerException
            : ex;
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

            if (ModLog.IsTraceEnabled)
            {
                ModLog.Trace(
                    $"[Neow/EventSync.afterChoose.queued] player={NeowRewardHelper.GetOwnerId(player)} " +
                    $"event={NeowRewardHelper.GetEventIdSafe(eventForPlayer)} finished={eventForPlayer.IsFinished} " +
                    $"chosenIndex={optionIndex} currentCount={currentOptions.Count} " +
                    $"currentKeys={NeowRewardHelper.DebugOptionKeys(currentOptions)}"
                );
            }
        }
        catch (Exception ex)
        {
            ModLog.Error($"[Neow/EventSync.afterChoose] logging failed: {ex}");
        }
    }
}

[HarmonyPatch(typeof(EventOption), nameof(EventOption.Chosen))]
public static class EventOptionChosenCompletedLogPatch
{
    private static void Postfix(EventOption __instance, Task __result)
    {
        try
        {
            if (!NeowRewardHelper.TryGetTrackedLiveRewardOwner(__instance, out Neow? neow) || neow is null)
                return;

            _ = LogAfterChosenCompletes(__result, __instance, neow);
        }
        catch (Exception ex)
        {
            ModLog.Error($"[Neow/Rewards] LiveReward.AfterChosen hook failed: {ex}");
        }
    }

    private static async Task LogAfterChosenCompletes(Task chosenTask, EventOption option, Neow neow)
    {
        try
        {
            await chosenTask;

            IReadOnlyList<EventOption> currentOptions = neow.CurrentOptions;
            ModLog.Debug(
                $"[Neow/Rewards] LiveReward.AfterChosen owner={NeowRewardHelper.GetOwnerId(neow)} " +
                $"event={NeowRewardHelper.GetEventIdSafe(neow)} key={NeowRewardHelper.DebugOptionKey(option)} " +
                $"finished={neow.IsFinished} currentCount={currentOptions.Count} " +
                $"stillVisible={currentOptions.Contains(option)}"
            );

            if (ModLog.IsTraceEnabled)
            {
                ModLog.Trace(
                    $"[Neow/Rewards] LiveReward.AfterChosen owner={NeowRewardHelper.GetOwnerId(neow)} " +
                    $"currentKeys={NeowRewardHelper.DebugOptionKeys(currentOptions)}"
                );
                NeowLocaleDiagnostics.LogOptions("Neow/Rewards.afterChosenVisibleIdentities", currentOptions);
            }

            NeowRewardHelper.LogStateJson(neow, $"LiveReward.afterChosen[{NeowRewardHelper.DebugOptionKey(option)}]");
        }
        catch (Exception ex)
        {
            ModLog.Error(
                $"[Neow/Rewards] LiveReward.AfterChosen failed owner={NeowRewardHelper.GetOwnerId(neow)} " +
                $"key={NeowRewardHelper.DebugOptionKey(option)}: {ex}"
            );
        }
    }
}

public static class NeowRewardHelper
{
    private const string LogPrefix = "[NeowAlwaysRewards]";

    [ThreadStatic]
    private static bool _buildingRewardsIgnoringModifiers;

    public static bool TrySetInstancePropertyOrBackingField(object? target, string propertyName, object? value, string reason)
    {
        if (target is null)
            return false;

        Type? targetType = target.GetType();
        try
        {
            PropertyInfo? property = AccessTools.Property(targetType, propertyName);
            MethodInfo? setter = property?.GetSetMethod(nonPublic: true);
            if (setter is not null)
            {
                setter.Invoke(target, new[] { value });
                return true;
            }

            FieldInfo? field = FindInstanceField(targetType, $"<{propertyName}>k__BackingField")
                ?? FindInstanceField(targetType, "_" + char.ToLowerInvariant(propertyName[0]) + propertyName[1..])
                ?? FindInstanceField(targetType, char.ToLowerInvariant(propertyName[0]) + propertyName[1..]);

            if (field is null)
            {
                ModLog.Warn($"[Reflection] Could not find setter/backing field for {targetType.FullName}.{propertyName} while {reason}.");
                return false;
            }

            field.SetValue(target, value);
            return true;
        }
        catch (Exception ex)
        {
            ModLog.Warn($"[Reflection] Failed to set {targetType?.FullName}.{propertyName} while {reason}: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static FieldInfo? FindInstanceField(Type? type, string fieldName)
    {
        for (Type? current = type; current is not null; current = current.BaseType)
        {
            FieldInfo? field = current.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field is not null)
                return field;
        }

        return null;
    }

    private enum CachedRewardSourceKind
    {
        LiveVanilla,
        GeneratedOnly
    }

    private sealed class CachedRewardOption
    {
        public string Key { get; init; } = string.Empty;
        public string DebugKey { get; init; } = string.Empty;
        public string TextKey { get; init; } = string.Empty;
        public string TitleLocTable { get; init; } = string.Empty;
        public string TitleLocKey { get; init; } = string.Empty;
        public string RelicEntry { get; init; } = string.Empty;
        public CachedRewardSourceKind SourceKind { get; init; }
        public EventOption? GeneratedOption { get; init; }
    }

    private sealed class CachedRewardKeys
    {
        public List<CachedRewardOption> Options { get; set; } = new();
        public List<string> Keys { get; set; } = new();
        public List<string> DebugKeys { get; set; } = new();
        public List<string> TextKeys { get; set; } = new();
        public List<string> TitleLocKeys { get; set; } = new();
        public List<string> RelicEntries { get; set; } = new();
        public string Reason { get; set; } = string.Empty;
    }
    
    private static readonly ConditionalWeakTable<Neow, CachedRewardKeys> CachedRewardKeysByNeow = new();

    public static bool IsBuildingRewardsIgnoringModifiers()
    {
        return _buildingRewardsIgnoringModifiers;
    }

    public static bool HasUsableCachedVanillaRewardFallback(Neow neow)
    {
        // Only intercept the final modifier hop when we already captured a real fallback page.
        // If the synthetic cache is missing or empty (for example, CustomStart produced only
        // navigation/modifier options during the stripped-modifier probe), let vanilla handle
        // the final transition instead of finishing Neow with an empty page.
        if (!CachedRewardKeysByNeow.TryGetValue(neow, out CachedRewardKeys? cached))
            return false;

        bool usable = cached.Options.Count > 0;
        if (ModLog.IsTraceEnabled)
        {
            ModLog.Trace(
                $"[Neow/Cache] HasUsableCachedVanillaRewardFallback owner={GetOwnerId(neow)} usable={usable} cachedCount={cached.Options.Count} reason={cached.Reason}"
            );
        }

        return usable;
    }


    /// <summary>
    /// Build a VANILLA-ONLY blessing snapshot by temporarily ignoring custom-run modifiers.
    ///
    /// This snapshot exists purely so the mod can restore Neow blessings later without
    /// treating a synthetic stripped-modifier result as authoritative for third-party options.
    /// </summary>
    public static IReadOnlyList<string> GetOrBuildCachedVanillaRewardKeys(Neow neow, string reason)
    {
        if (CachedRewardKeysByNeow.TryGetValue(neow, out CachedRewardKeys? cached))
            return cached.Keys.ToList();

        return BuildAndCacheVanillaRewardKeys(neow, reason);
    }

    /// <summary>
    /// Rebuild fresh live VANILLA EventOptions from neow.AllPossibleOptions using the cached key order.
    ///
    /// Third-party pages should be preserved by the caller from the live event state and merged with
    /// this vanilla fallback, rather than being inferred from the synthetic snapshot.
    /// </summary>
    public static IReadOnlyList<EventOption> GetOrBuildLiveVanillaRewardsFromCachedKeys(Neow neow, string reason)
    {
        // Rebuild fresh live vanilla EventOptions from the cached vanilla-only snapshot.
        //
        // This helper is intentionally conservative:
        // - it never treats the synthetic stripped-modifier snapshot as authoritative for third-party rewards
        // - it only rebuilds options that can be matched back to live vanilla AllPossibleOptions
        // - mod-added live pages should be preserved/merged by the caller before falling back here
        CachedRewardKeys snapshot = GetOrBuildCachedRewardSnapshot(neow, reason);
        if (snapshot.Options.Count <= 0)
            return Array.Empty<EventOption>();

        List<EventOption> allLiveOptions = neow.AllPossibleOptions?.ToList() ?? new List<EventOption>();
        List<EventOption> unmatchedLiveOptions = allLiveOptions.ToList();

        NeowLocaleDiagnostics.LogRuntimeLocale("Neow/Rewards.locale");
        if (ModLog.IsTraceEnabled && !NeowLocaleDiagnostics.IsSuspectedIssueLocale())
        {
            ModLog.Trace(
                $"[Neow/Rewards.locale] Not running in suspected issue locale; gameLocale={NeowLocaleDiagnostics.GetGameLocaleSafe()} osLocale={NeowLocaleDiagnostics.GetOsLocaleSafe()}"
            );
        }
        NeowLocaleDiagnostics.LogDuplicateIdentities("Neow/Rewards.liveDuplicates", allLiveOptions);
        NeowLocaleDiagnostics.LogOptions("Neow/Rewards.liveAllPossible", allLiveOptions);

        List<EventOption> rebuilt = new();
        foreach (CachedRewardOption cached in snapshot.Options)
        {
            if (TryMatchCachedRewardToLiveOption(neow, cached, unmatchedLiveOptions, reason, out EventOption? live))
            {
                AttachDebugBeforeChosen(neow, live!, reason);
                rebuilt.Add(live!);
                continue;
            }

            ModLog.Warn(
                $"[Neow/Rewards] Failed to rebuild cached vanilla reward for owner={GetOwnerId(neow)} key={cached.Key} reason={reason}."
            );
            if (ModLog.IsTraceEnabled)
            {
                ModLog.Trace(
                    $"[Neow/Rewards] owner={GetOwnerId(neow)} availableLiveIdentities={string.Join(",", unmatchedLiveOptions.Select(NeowLocaleDiagnostics.BuildRewardIdentity).OrderBy(x => x))}"
                );
            }
        }

        ModLog.Debug(
            $"[Neow/Rewards] CreateLiveVanillaRewards owner={GetOwnerId(neow)} reason={reason} count={rebuilt.Count}"
        );
        if (rebuilt.Count != snapshot.Options.Count)
        {
            ModLog.Warn(
                $"[Neow/Rewards] Rebuilt reward count mismatch for owner={GetOwnerId(neow)} reason={reason} rebuilt={rebuilt.Count} cached={snapshot.Options.Count}."
            );
        }
        if (ModLog.IsTraceEnabled)
        {
            ModLog.Trace(
                $"[Neow/Rewards] owner={GetOwnerId(neow)} rebuiltKeys={DebugOptionKeys(rebuilt)}"
            );
            NeowLocaleDiagnostics.LogOptions("Neow/Rewards.rebuiltIdentities", rebuilt);
        }

        return rebuilt;
    }

    private static bool TryMatchCachedRewardToLiveOption(
        Neow neow,
        CachedRewardOption cached,
        List<EventOption> unmatchedLiveOptions,
        string reason,
        out EventOption? matched)
    {
        // Match cached rewards back to live options using progressively weaker stable identifiers.
        if (TryTakeUniqueMatch(
                unmatchedLiveOptions,
                option => NeowLocaleDiagnostics.BuildRewardIdentity(option) == cached.Key,
                out matched))
        {
            LogMatchedReward("identity", neow, cached, matched, reason);
            return true;
        }

        if (!string.IsNullOrEmpty(cached.TextKey)
            && !string.IsNullOrEmpty(cached.RelicEntry)
            && TryTakeUniqueMatch(
                unmatchedLiveOptions,
                option => string.Equals(option?.TextKey ?? string.Empty, cached.TextKey, StringComparison.Ordinal)
                    && string.Equals(GetRelicEntry(option), cached.RelicEntry, StringComparison.Ordinal),
                out matched))
        {
            LogMatchedReward("textKey+relic", neow, cached, matched, reason);
            return true;
        }

        if (!string.IsNullOrEmpty(cached.TitleLocKey)
            && !string.IsNullOrEmpty(cached.RelicEntry)
            && TryTakeUniqueMatch(
                unmatchedLiveOptions,
                option => string.Equals(option?.Title?.LocEntryKey ?? string.Empty, cached.TitleLocKey, StringComparison.Ordinal)
                    && string.Equals(GetRelicEntry(option), cached.RelicEntry, StringComparison.Ordinal),
                out matched))
        {
            LogMatchedReward("locKey+relic", neow, cached, matched, reason);
            return true;
        }

        if (!string.IsNullOrEmpty(cached.TextKey)
            && TryTakeUniqueMatch(
                unmatchedLiveOptions,
                option => string.Equals(option?.TextKey ?? string.Empty, cached.TextKey, StringComparison.Ordinal),
                out matched))
        {
            LogMatchedReward("textKey", neow, cached, matched, reason);
            return true;
        }

        if (!string.IsNullOrEmpty(cached.TitleLocKey)
            && TryTakeUniqueMatch(
                unmatchedLiveOptions,
                option => string.Equals(option?.Title?.LocEntryKey ?? string.Empty, cached.TitleLocKey, StringComparison.Ordinal),
                out matched))
        {
            LogMatchedReward("locKey", neow, cached, matched, reason);
            return true;
        }

        if (!string.IsNullOrEmpty(cached.RelicEntry)
            && TryTakeUniqueMatch(
                unmatchedLiveOptions,
                option => string.Equals(GetRelicEntry(option), cached.RelicEntry, StringComparison.Ordinal),
                out matched))
        {
            LogMatchedReward("relic", neow, cached, matched, reason);
            return true;
        }

        if (!string.IsNullOrEmpty(cached.DebugKey)
            && TryTakeUniqueMatch(
                unmatchedLiveOptions,
                option => string.Equals(DebugOptionKey(option), cached.DebugKey, StringComparison.Ordinal),
                out matched))
        {
            LogMatchedReward("debugKey", neow, cached, matched, reason);
            return true;
        }

        matched = null;
        if (ModLog.IsTraceEnabled)
        {
            ModLog.Trace(
                $"[Neow/Rewards] No live match found for owner={GetOwnerId(neow)} reason={reason} " +
                $"identity={cached.Key} textKey={cached.TextKey} locKey={cached.TitleLocKey} relic={cached.RelicEntry} debugKey={cached.DebugKey}"
            );
        }

        return false;
    }

    private static bool TryTakeUniqueMatch(
        List<EventOption> unmatchedLiveOptions,
        Func<EventOption, bool> predicate,
        out EventOption? matched)
    {
        // Only consume a live option when a rule identifies exactly one candidate so retries cannot
        // accidentally bind multiple cached rewards to the same live option instance.
        List<EventOption> candidates = unmatchedLiveOptions.Where(predicate).ToList();
        if (candidates.Count != 1)
        {
            matched = null;
            return false;
        }

        matched = candidates[0];
        unmatchedLiveOptions.Remove(matched);
        return true;
    }

    private static void LogMatchedReward(
        string strategy,
        Neow neow,
        CachedRewardOption cached,
        EventOption? matched,
        string reason)
    {
        // Keep successful reward-match diagnostics at trace level so repro logs show which rule won.
        if (!ModLog.IsTraceEnabled)
            return;

        ModLog.Trace(
            $"[Neow/Rewards] Matched cached reward for owner={GetOwnerId(neow)} reason={reason} strategy={strategy} " +
            $"identity={cached.Key} textKey={cached.TextKey} locKey={cached.TitleLocKey} relic={cached.RelicEntry} debugKey={cached.DebugKey}"
        );
        NeowLocaleDiagnostics.LogOption("Neow/Rewards.matchedLive", matched);
    }

    private static string GetRelicEntry(EventOption? option)
    {
        // Normalize relic identity access so all reward-match strategies compare the same stable value.
        return option?.Relic?.Id?.Entry ?? option?.Relic?.GetType().Name ?? "<none>";
    }

    public static IReadOnlyList<EventOption> ExtractVisibleNonModifierOptions(
        Neow neow,
        IEnumerable<EventOption>? modifierOptions)
    {
        List<EventOption> currentOptions = neow.CurrentOptions?.ToList() ?? new List<EventOption>();
        if (currentOptions.Count <= 0)
            return Array.Empty<EventOption>();

        HashSet<EventOption> modifierRefs = modifierOptions?
            .Where(option => option is not null)
            .ToHashSet() ?? new HashSet<EventOption>();

        HashSet<string> modifierKeys = modifierOptions?
            .Select(DebugOptionKey)
            .Where(key => !string.IsNullOrWhiteSpace(key) && key != "<null-option>")
            .ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);

        List<EventOption> preserved = currentOptions
            .Where(option =>
                option is not null
                && !modifierRefs.Contains(option)
                && !modifierKeys.Contains(DebugOptionKey(option)))
            .ToList();

        if (ModLog.IsTraceEnabled)
        {
            ModLog.Trace(
                $"[Neow/Flow] ExtractVisibleNonModifierOptions owner={GetOwnerId(neow)} currentCount={currentOptions.Count} modifierCount={modifierRefs.Count} preservedCount={preserved.Count}"
            );
            NeowLocaleDiagnostics.LogOptions("Neow/Flow.currentVisibleOptions", currentOptions);
            NeowLocaleDiagnostics.LogOptions("Neow/Flow.preservedVisibleOptions", preserved);
        }

        return preserved;
    }

    public static IReadOnlyList<EventOption> MergeUniqueOptionsInOrder(
        IEnumerable<EventOption>? preferredOptions,
        IEnumerable<EventOption>? appendedOptions)
    {
        List<EventOption> merged = new();
        HashSet<string> seen = new(StringComparer.Ordinal);

        void addRange(IEnumerable<EventOption>? options)
        {
            if (options is null)
                return;

            foreach (EventOption? option in options)
            {
                if (option is null)
                    continue;

                string identity = BuildStableOptionIdentity(option);
                if (!seen.Add(identity))
                    continue;

                merged.Add(option);
            }
        }

        addRange(preferredOptions);
        addRange(appendedOptions);

        return merged;
    }

    private static string BuildStableOptionIdentity(EventOption? option)
    {
        string identity = NeowLocaleDiagnostics.BuildRewardIdentity(option);
        if (!string.IsNullOrWhiteSpace(identity) && identity != NeowLocaleDiagnostics.UnidentifiedIdentity)
            return identity;

        string debugKey = DebugOptionKey(option);
        if (!string.IsNullOrWhiteSpace(debugKey) && debugKey != "<null-option>")
            return $"debug:{debugKey}";

        return option is null
            ? "null-option"
            : $"ref:{RuntimeHelpers.GetHashCode(option)}";
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

        var rngProperty = neowTr.Property("Rng");

        IReadOnlyList<ModifierModel> savedModifiers =
            owner.RunState.Modifiers ?? Array.Empty<ModifierModel>();

        Rng? savedRng = rngProperty.GetValue<Rng>();
        Rng? syntheticRng = savedRng is null
            ? null
            : new Rng(savedRng.Seed, savedRng.Counter);

        try
        {
            _buildingRewardsIgnoringModifiers = true;

            // Temporarily remove custom modifiers and ask vanilla Neow which blessings it would
            // have shown. In v1.0.7 GenerateInitialOptions consumes Neow.Rng while choosing the
            // curse/positive relics, so run the synthetic lookup on a cloned RNG and restore the
            // real RNG afterwards. Otherwise this diagnostic/fallback cache changes actual run RNG.
            if (!TrySetInstancePropertyOrBackingField(owner.RunState, "Modifiers", new List<ModifierModel>(), "building vanilla Neow reward cache"))
                return Array.Empty<string>();

            if (syntheticRng is not null)
                TrySetInstancePropertyOrBackingField(neow, "Rng", syntheticRng, "building vanilla Neow reward cache");

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
                .Select(NeowLocaleDiagnostics.BuildRewardIdentity)
                .Where(identity => !string.IsNullOrEmpty(identity) && identity != NeowLocaleDiagnostics.UnidentifiedIdentity)
                .ToHashSet();

            NeowLocaleDiagnostics.LogRuntimeLocale("Neow/Cache.locale");
            if (ModLog.IsTraceEnabled && !NeowLocaleDiagnostics.IsSuspectedIssueLocale())
            {
                ModLog.Trace(
                    $"[Neow/Cache.locale] Not running in suspected issue locale; gameLocale={NeowLocaleDiagnostics.GetGameLocaleSafe()} osLocale={NeowLocaleDiagnostics.GetOsLocaleSafe()}"
                );
            }
            NeowLocaleDiagnostics.LogDuplicateIdentities("Neow/Cache.generatedDuplicates", generated);
            NeowLocaleDiagnostics.LogDuplicateIdentities("Neow/Cache.liveDuplicates", neow.AllPossibleOptions ?? Array.Empty<EventOption>());
            NeowLocaleDiagnostics.LogOptions("Neow/Cache.generated", generated);
            NeowLocaleDiagnostics.LogOptions("Neow/Cache.liveAllPossible", neow.AllPossibleOptions ?? Array.Empty<EventOption>());

            List<CachedRewardOption> vanillaOnlyOptions = generated
                .Select(option =>
                {
                    string key = NeowLocaleDiagnostics.BuildRewardIdentity(option);

                    return new CachedRewardOption
                    {
                        Key = key,
                        DebugKey = DebugOptionKey(option),
                        TextKey = option?.TextKey ?? string.Empty,
                        TitleLocTable = option?.Title?.LocTable ?? string.Empty,
                        TitleLocKey = option?.Title?.LocEntryKey ?? string.Empty,
                        RelicEntry = GetRelicEntry(option),
                        SourceKind = CachedRewardSourceKind.LiveVanilla,
                        GeneratedOption = null
                    };
                })
                .Where(entry =>
                    !string.IsNullOrEmpty(entry.Key)
                    && entry.Key != NeowLocaleDiagnostics.UnidentifiedIdentity
                    && liveKeys.Contains(entry.Key))
                .ToList();

            CachedRewardKeys snapshot = new()
            {
                Options = vanillaOnlyOptions,
                Reason = reason
            };

            snapshot.Keys = snapshot.Options.Select(option => option.Key).ToList();
            snapshot.DebugKeys = snapshot.Options.Select(option => option.DebugKey).ToList();
            snapshot.TextKeys = snapshot.Options.Select(option => option.TextKey).ToList();
            snapshot.TitleLocKeys = snapshot.Options.Select(option => option.TitleLocKey).ToList();
            snapshot.RelicEntries = snapshot.Options.Select(option => option.RelicEntry).ToList();

            CachedRewardKeysByNeow.Remove(neow);
            CachedRewardKeysByNeow.Add(neow, snapshot);

            ModLog.Debug(
                $"[Neow/Cache] Built cached vanilla reward snapshot for owner={GetOwnerId(owner)} reason={reason} count={snapshot.Keys.Count}"
            );
            if (ModLog.IsTraceEnabled)
            {
                IEnumerable<string> discardedKeys = generated
                    .Select(NeowLocaleDiagnostics.BuildRewardIdentity)
                    .Where(identity =>
                        !string.IsNullOrEmpty(identity)
                        && identity != NeowLocaleDiagnostics.UnidentifiedIdentity
                        && !liveKeys.Contains(identity))
                    .Distinct()
                    .OrderBy(identity => identity);

                ModLog.Trace(
                    $"[Neow/Cache] owner={GetOwnerId(owner)} keys={string.Join(",", snapshot.Keys)} relics={string.Join(",", snapshot.RelicEntries)}"
                );
                ModLog.Trace(
                    $"[Neow/Cache] owner={GetOwnerId(owner)} debugKeys={string.Join(",", snapshot.DebugKeys)}"
                );
                ModLog.Trace(
                    $"[Neow/Cache] owner={GetOwnerId(owner)} textKeys={string.Join(",", snapshot.TextKeys)} locKeys={string.Join(",", snapshot.TitleLocKeys)}"
                );
                ModLog.Trace(
                    $"[Neow/Cache] owner={GetOwnerId(owner)} discardedNonVanillaKeys={string.Join(",", discardedKeys)}"
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
            TrySetInstancePropertyOrBackingField(owner.RunState, "Modifiers", savedModifiers, "restoring modifiers after vanilla Neow reward cache");
            if (savedRng is not null)
                TrySetInstancePropertyOrBackingField(neow, "Rng", savedRng, "restoring Neow RNG after vanilla reward cache");
            _buildingRewardsIgnoringModifiers = false;
        }
    }

    private static readonly ConditionalWeakTable<EventOption, HashSet<string>> BeforeChosenDebugReasonsByOption = new();
    private static readonly ConditionalWeakTable<EventOption, Neow> LiveRewardOwnerByOption = new();

    public static bool TryGetTrackedLiveRewardOwner(EventOption option, out Neow? neow)
    {
        return LiveRewardOwnerByOption.TryGetValue(option, out neow);
    }

    private static void TrackLiveRewardOwner(Neow neow, EventOption option)
    {
        // Rebuilt reward options are live vanilla EventOption instances. Track them so the
        // EventOption.Chosen postfix can log the real post-await state, after the reward's
        // own async OnChosen callback has finished.
        LiveRewardOwnerByOption.Remove(option);
        LiveRewardOwnerByOption.Add(option, neow);
    }

    private static void AttachDebugBeforeChosen(Neow neow, EventOption option, string reason)
    {
        TrackLiveRewardOwner(neow, option);

        // Avoid stacking identical debug handlers on the same EventOption instance across retries.
        HashSet<string> attachedReasons = BeforeChosenDebugReasonsByOption.GetValue(option, _ => new HashSet<string>());
        string attachmentKey = $"{GetOwnerId(neow)}::{reason}";
        if (!attachedReasons.Add(attachmentKey))
            return;

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
                cachedDebugKeys = cached?.DebugKeys.ToArray() ?? Array.Empty<string>(),
                cachedTextKeys = cached?.TextKeys.ToArray() ?? Array.Empty<string>(),
                cachedTitleLocKeys = cached?.TitleLocKeys.ToArray() ?? Array.Empty<string>(),
                activeGameLocale = NeowLocaleDiagnostics.GetGameLocaleSafe(),
                activeOsLocale = NeowLocaleDiagnostics.GetOsLocaleSafe(),
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
