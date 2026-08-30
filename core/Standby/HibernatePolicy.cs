// GPD Forge — hibernate instead of draining in Modern Standby. GPL-3.0-or-later.
//
// The roadmap item was "fingerprint toggle / S0↔S3 switch", and that was written before anyone asked
// the firmware. `powercfg /a` on this board reports S1, S2 and S3 as *unsupported by the system
// firmware*: the only choices are S0 low-power idle (Modern Standby) and Hibernate. So the useful
// control is not a sleep-state switch — it is "after a while asleep, stop idling and hibernate".
//
// That is not a cosmetic preference on this machine. Modern Standby keeps drawing power; the
// overnight drain this project has chased all along is what S0 idle costs when a lid stays shut for
// eight hours. Hibernate costs nothing because the machine is off.
//
// Values are READ FROM THE REGISTRY rather than parsed out of `powercfg /q`. That output is
// localised — on this device it reads "Índice de configuración de corriente continua actual" — and a
// parser keyed on those words finds nothing the moment the OS language changes. The registry keys
// and the GUIDs do not translate. Writes still go through powercfg, because it is what makes a
// setting take effect rather than merely recording it.
using Microsoft.Win32;

namespace GpdForge.Standby;

/// <summary>
/// The idle timeouts that decide what a closed lid costs. Null means the value could not be read —
/// never a stand-in, because "we do not know when this machine hibernates" and "it hibernates in
/// 0 seconds" are opposite claims.
/// </summary>
/// <param name="StandbyIdleSeconds">Seconds of idle before Modern Standby. 0 means never.</param>
/// <param name="HibernateIdleSeconds">Seconds of idle before hibernating. 0 means never.</param>
public sealed record IdleTimeouts(int? StandbyIdleSeconds, int? HibernateIdleSeconds);

public sealed record HibernatePolicyState(
    bool HibernateAvailable,
    string? Unavailable,
    IdleTimeouts OnAc,
    IdleTimeouts OnBattery);

public static class HibernatePolicy
{
    // Documented Windows power-setting GUIDs. Constants, not localised, and stable across releases.
    private const string SleepSubgroup = "238c9fa8-0aad-41ed-83f4-97be242c8f20";
    public const string StandbyIdleSetting = "29f6c1db-86da-48c5-9fdb-f2b67b1f44da";
    public const string HibernateIdleSetting = "9d7815a6-7ee4-497e-8888-515a05f02364";

    /// <summary>
    /// Reads one power setting's AC and DC index straight from the active scheme's registry key.
    /// Returns nulls rather than zeros when the key is absent: a missing setting is unknown, and
    /// zero already means something specific here ("never").
    /// </summary>
    public static (int? Ac, int? Dc) ReadSetting(string activeSchemeGuid, string settingGuid)
    {
        var path = $@"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\{activeSchemeGuid}\{SleepSubgroup}\{settingGuid}";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            if (key is null) return (null, null);
            return (key.GetValue("ACSettingIndex") as int?, key.GetValue("DCSettingIndex") as int?);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Whether `powercfg /a` output says hibernate is available, without depending on its wording.
    ///
    /// The section headings are localised, but the STATE NAMES are not translated in the available
    /// list on any locale this has been seen on — and more usefully, an unavailable state is always
    /// followed by an indented reason line. So availability is decided by position: a state named in
    /// the output with no indented explanation under it is one the machine can enter.
    /// </summary>
    public static bool ParseHibernateAvailable(string powercfgOutput, out string? reason)
    {
        reason = null;
        var lines = powercfgOutput.Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            if (!line.Contains("Hibern", StringComparison.OrdinalIgnoreCase)) continue;

            // A reason line is indented further than the state name it explains, and only the FIRST
            // line after the state matters — powercfg lists reasons immediately beneath it.
            //
            // Tabs are expanded before measuring. powercfg indents state names with SPACES and their
            // reasons with a TAB, so counting raw characters makes the reason look less indented than
            // the state (1 < 4) and every unavailable state reads as available. Caught by a test
            // using this device's real output rather than invented text.
            var indent = IndentWidth(line);
            if (i + 1 < lines.Length)
            {
                var next = lines[i + 1].TrimEnd();
                var nextIndent = IndentWidth(next);
                if (next.Trim().Length > 0 && nextIndent > indent)
                {
                    reason = next.Trim();
                    return false;
                }
            }
            return true;
        }

        reason = "powercfg did not mention hibernate at all.";
        return false;
    }

    /// <summary>Indentation in columns, counting a tab as 8. Public so the tab-vs-space rule that
    /// broke this parser once is testable on its own.</summary>
    public static int IndentWidth(string line)
    {
        var width = 0;
        foreach (var c in line)
        {
            if (c == ' ') width++;
            else if (c == '	') width += 8;
            else break;
        }
        return width;
    }

    /// <summary>
    /// Whether a requested timeout is sane. Rejected rather than clamped: silently turning a
    /// mistyped 100000 into an hour would apply something the user never asked for.
    /// </summary>
    public static string? Reject(int seconds)
    {
        if (seconds < 0) return "A timeout cannot be negative. Use 0 for 'never'.";
        // 24 h. Beyond that the setting is indistinguishable from "never" and is almost certainly a
        // units mistake — minutes typed where seconds were wanted.
        if (seconds > 86_400) return "That is over 24 hours; use 0 if you mean 'never'.";
        return null;
    }
}
