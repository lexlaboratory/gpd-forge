// GPD Forge — the charge guard: what we can do about battery ageing without a charge threshold.
// GPL-3.0-or-later.
//
// WHAT THIS IS INSTEAD OF. The obvious feature is "stop charging at 80 %". It is not available on
// this board: the threshold is an EC/BIOS value with no documented driverless read or write path on
// the G1618-04, docs/hardware/ec-registers.md maps fan registers only, and the `ecoChargeMode` WMI
// class that looks like it might be the answer has no instances here — it is Windows' own schema,
// not a vendor implementation. Guessing an EC register for a CHARGE CONTROLLER on a board with no
// vendor recovery path would be the most destructive thing in this repository; a fan spinning wrong
// is at least audible in seconds.
//
// SO WHAT IS LEFT, AND WHY IT IS MOST OF THE VALUE. Lithium-ion ages from time spent at a high state
// of charge MULTIPLIED BY temperature. The daemon cannot stop the current, but it can see the
// pattern and it can influence the temperature — a cell held full at 30 °C ages far slower than one
// held full at 45 °C, and the SoC is what heats it. So this does two things:
//
//   1. Counts the hours spent plugged in at a high state of charge, and says so once per episode.
//      A number, not folklore: it makes the advice checkable and gives a future charge threshold
//      something to be judged against.
//   2. Optionally holds a lower sustained ceiling while that is happening.
//
// It is a CEILING, never a target — see ChargeGuardDecision.CoolToW. Raising power to meet a
// "cooling" setting would be an obvious absurdity and an easy one to write by accident.
using GpdForge.Telemetry;

namespace GpdForge.Battery;

/// <param name="HighSocPct">At or above this, plugged in, counts as high state of charge.</param>
/// <param name="AlertAfterHours">How long one episode may run before it is worth mentioning.</param>
/// <param name="CoolWhileCharging">Opt-in. Off by default: silently capping someone's performance
/// because their laptop is plugged in is not a decision to make on their behalf.</param>
/// <param name="CoolToW">The sustained ceiling to hold during an episode.</param>
/// <remarks>
/// A record CLASS, not a struct, for the same reason <see cref="Guardian.GuardianConfig"/> is — and
/// this file learned it the hard way. A struct always has an implicit parameterless constructor that
/// zeroes every field and IGNORES these defaults, so <c>new ChargeGuardConfig()</c> would have
/// produced <c>Enabled = false, HighSocPct = 0</c>: a guard that ships silently switched off, with a
/// threshold that would have counted every moment on AC as an episode had it ever run.
/// </remarks>
public sealed record ChargeGuardConfig(
    bool Enabled = true,
    int HighSocPct = 95,
    double AlertAfterHours = 4.0,
    bool CoolWhileCharging = false,
    int CoolToW = 15);

/// <param name="CoolToW">A ceiling to apply this tick, or null. Never a target: the caller must not
/// raise power to reach it.</param>
/// <param name="ClearCool">The episode ended; restore the active mode's profile.</param>
/// <param name="Alert">Text to publish, or null. Non-null at most once per episode.</param>
/// <param name="EpisodeHours">How long the current episode has run, or null when there is none.</param>
public readonly record struct ChargeGuardDecision(
    int? CoolToW,
    bool ClearCool,
    string? Alert,
    double? EpisodeHours);

/// <summary>Durable counters. Small on purpose — this file is written for the life of the machine.</summary>
public readonly record struct ChargeGuardState(
    double TotalHoursAtHighSoc,
    int Episodes,
    DateTimeOffset? EpisodeStartedUtc,
    bool AlertedThisEpisode);

public static class ChargeGuardPolicy
{
    /// <summary>
    /// Advances the guard by one observation and returns what to do.
    ///
    /// Pure: state in, state out, no clock of its own and no I/O. Time comes in as
    /// <paramref name="now"/> so a four-hour episode is testable in a millisecond — otherwise the
    /// only way to exercise this would be to leave a machine plugged in for an afternoon, which
    /// means in practice it would never be exercised at all.
    /// </summary>
    public static (ChargeGuardState State, ChargeGuardDecision Decision) Observe(
        ChargeGuardState prior,
        ChargeGuardConfig config,
        TelemetrySnapshot snapshot,
        DateTimeOffset now)
    {
        var highSoc = config.Enabled
                      && snapshot.AcConnected
                      && snapshot.BatteryPct >= config.HighSocPct;

        if (!highSoc)
        {
            // No episode running: nothing to do, and crucially nothing to clear. Emitting ClearCool
            // on every idle tick would make the worker re-apply the mode profile once a second
            // forever, which is both wasteful and indistinguishable in the logs from a real restore.
            if (prior.EpisodeStartedUtc is null)
                return (prior, new ChargeGuardDecision(null, ClearCool: false, null, null));

            // The episode just ended. Bank the time and ALWAYS clear — the clear is unconditional on
            // whether cooling was enabled, because the setting could have been turned off mid-episode
            // and the ceiling would otherwise stay applied with nothing left to remove it.
            var banked = prior.TotalHoursAtHighSoc + Elapsed(prior.EpisodeStartedUtc.Value, now);
            var ended = prior with
            {
                TotalHoursAtHighSoc = Math.Round(banked, 3),
                Episodes = prior.Episodes + 1,
                EpisodeStartedUtc = null,
                AlertedThisEpisode = false,
            };
            return (ended, new ChargeGuardDecision(null, ClearCool: true, null, null));
        }

        // An episode is running (or starts now).
        var started = prior.EpisodeStartedUtc ?? now;
        var hours = Elapsed(started, now);

        string? alert = null;
        var alerted = prior.AlertedThisEpisode;
        if (!alerted && hours >= config.AlertAfterHours)
        {
            alerted = true;
            alert = $"Plugged in at {snapshot.BatteryPct}% for {hours:0.#} h. Lithium-ion ages fastest "
                  + "when it sits full and warm — unplugging, or letting it run down before charging "
                  + "again, slows that. GPD Forge cannot stop the charge on this board.";
        }

        var next = prior with { EpisodeStartedUtc = started, AlertedThisEpisode = alerted };

        return (next, new ChargeGuardDecision(
            CoolToW: config.CoolWhileCharging ? config.CoolToW : null,
            ClearCool: false,
            Alert: alert,
            EpisodeHours: Math.Round(hours, 3)));
    }

    /// <summary>
    /// The ceiling to actually apply, given what the active mode would otherwise run at.
    ///
    /// Cooling is a CEILING, not a target. Without this, selecting battery mode (8 W) while plugged
    /// in at 100 % would make a feature whose entire purpose is to run cooler RAISE the sustained
    /// limit to 15 W. Returning null rather than the mode's own value keeps "nothing to do" distinct
    /// from "apply this", so the worker does not re-apply an unchanged profile every tick.
    /// </summary>
    public static int? EffectiveCeiling(int? coolToW, int modeStapmW)
        => coolToW is int w && w < modeStapmW ? w : null;

    private static double Elapsed(DateTimeOffset from, DateTimeOffset to)
    {
        var hours = (to - from).TotalHours;
        // A backwards clock (NTP correction, a resume, a timezone-naive write) must not subtract
        // hours from a counter that only ever grows, nor produce a negative episode length that
        // reads as "the future".
        return hours > 0 ? hours : 0;
    }
}
