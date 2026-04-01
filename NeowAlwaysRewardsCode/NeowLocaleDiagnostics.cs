using Godot;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Events;

namespace NeowAlwaysRewards.NeowAlwaysRewardsCode;

internal static class NeowLocaleDiagnostics
{
    public const string UnidentifiedIdentity = "<unidentified-option>";

    public static string BuildRewardIdentity(EventOption? option)
    {
        // Build a locale-safe composite identity for matching cached rewards back to live options.
        string textKey = option?.TextKey ?? string.Empty;
        string locTable = option?.Title?.LocTable ?? string.Empty;
        string locKey = option?.Title?.LocEntryKey ?? string.Empty;
        string relicEntry = option?.Relic?.Id?.Entry ?? option?.Relic?.GetType().FullName ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(textKey))
            return $"text:{textKey}|relic:{relicEntry}";

        if (!string.IsNullOrWhiteSpace(locKey))
            return $"loc:{locTable}.{locKey}|relic:{relicEntry}";

        if (!string.IsNullOrWhiteSpace(relicEntry))
            return $"relic:{relicEntry}";

        return UnidentifiedIdentity;
    }

    public static string DescribeOption(EventOption? option)
    {
        // Describe the raw localization-bearing fields for one option without relying on formatted text.
        return
            $"identity={BuildRewardIdentity(option)} " +
            $"textKey={option?.TextKey ?? "<null>"} " +
            $"locTable={option?.Title?.LocTable ?? "<null>"} " +
            $"locKey={option?.Title?.LocEntryKey ?? "<null>"} " +
            $"relic={option?.Relic?.Id?.Entry ?? option?.Relic?.GetType().Name ?? "<none>"}";
    }

    public static string GetGameLocaleSafe()
    {
        // Read the currently active Godot translation locale without letting diagnostics throw.
        try
        {
            return TranslationServer.GetLocale();
        }
        catch
        {
            return "<game-locale-error>";
        }
    }

    public static string GetOsLocaleSafe()
    {
        // Read the OS locale separately so reward logs can show whether the game changed locale independently.
        try
        {
            return OS.GetLocale();
        }
        catch
        {
            return "<os-locale-error>";
        }
    }

    public static bool IsSuspectedIssueLocale()
    {
        // Identify whether the runtime appears to be in the locale family reported by the original issue giver.
        string gameLocale = GetGameLocaleSafe();
        return gameLocale.StartsWith("zh", System.StringComparison.OrdinalIgnoreCase);
    }

    public static void LogRuntimeLocale(string category)
    {
        // Emit a trace-level snapshot of the active runtime locales near reward cache and rebuild work.
        if (!ModLog.IsTraceEnabled)
            return;

        ModLog.Trace($"[{category}] gameLocale={GetGameLocaleSafe()} osLocale={GetOsLocaleSafe()}");
    }

    public static void LogOption(string category, EventOption? option)
    {
        // Emit a single trace-level diagnostic line for one option identity.
        if (!ModLog.IsTraceEnabled)
            return;

        ModLog.Trace($"[{category}] {DescribeOption(option)}");
    }

    public static void LogOptions(string category, IEnumerable<EventOption>? options)
    {
        // Emit trace-level diagnostics for each option in a list so cache and live pages can be compared.
        if (!ModLog.IsTraceEnabled || options is null)
            return;

        foreach (EventOption? option in options)
            LogOption(category, option);
    }

    public static void LogDuplicateIdentities(string category, IEnumerable<EventOption>? options)
    {
        // Emit trace-level diagnostics when multiple options collapse to the same composite identity.
        if (!ModLog.IsTraceEnabled || options is null)
            return;

        List<IGrouping<string, EventOption>> duplicates = options
            .Where(option => option is not null)
            .GroupBy(BuildRewardIdentity)
            .Where(group => !string.IsNullOrEmpty(group.Key) && group.Key != UnidentifiedIdentity && group.Count() > 1)
            .ToList();

        foreach (IGrouping<string, EventOption> group in duplicates)
        {
            ModLog.Trace($"[{category}] identity={group.Key} count={group.Count()}");

            foreach (EventOption option in group)
                LogOption($"{category}.member", option);
        }
    }
}
