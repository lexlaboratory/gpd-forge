// GPD Forge — the charge guard's rules. GPL-3.0-or-later.
//
// The plan for this feature named its own gate: prove the cool-while-charging ceiling ENGAGES and,
// crucially, DISENGAGES. A guard that lowers TDP and forgets to restore it is worse than no guard —
// the machine silently stays slow, the cause is invisible, and the user blames the hardware.
//
// Every test here drives a fake clock, so a four-hour episode runs in a millisecond. Left to real
// time, none of this would ever be exercised.
using GpdForge.Battery;
using GpdForge.Telemetry;
using Xunit;

namespace GpdForge.Core.Tests;

public class ChargeGuardTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

    private static TelemetrySnapshot Snap(int batteryPct, bool ac) =>
        new(CpuTempC: 55, GpuTempC: 45, PackageW: 8, CpuClockMhz: 2000, FanRpm: 3000, FanDutyPct: 0,
            Fps: 0, Fps1PctLow: 0, BatteryPct: batteryPct, DischargeW: 0, AcConnected: ac,
            TdpVerified: true);

    private static ChargeGuardState Fresh => new(0, 0, null, false);

    [Fact]
    public void A_default_constructed_config_is_actually_the_documented_default()
    {
        // ChargeGuardConfig was written as a `record struct` first, and every banking test failed
        // with zero hours. A struct always has an implicit parameterless constructor that zeroes
        // every field and ignores the primary constructor's defaults, so `new ChargeGuardConfig()`
        // silently meant "disabled, threshold 0" — a feature that ships switched off and, had it
        // ever run, would have counted every moment on AC as high state of charge.
        //
        // GuardianConfig carries a comment saying exactly this. The lesson had already been paid for
        // once in this repository; this test is what makes it stick.
        var cfg = new ChargeGuardConfig();

        Assert.True(cfg.Enabled);
        Assert.Equal(95, cfg.HighSocPct);
        Assert.Equal(4.0, cfg.AlertAfterHours);
        Assert.False(cfg.CoolWhileCharging);   // opt-in, deliberately
        Assert.Equal(15, cfg.CoolToW);
    }

    [Fact]
    public void On_battery_the_guard_does_nothing()
    {
        var (state, decision) = ChargeGuardPolicy.Observe(Fresh, new ChargeGuardConfig(), Snap(100, ac: false), T0);

        Assert.Null(decision.CoolToW);
        Assert.Null(decision.Alert);
        Assert.Null(state.EpisodeStartedUtc);
    }

    [Fact]
    public void Plugged_in_below_the_threshold_is_not_an_episode()
    {
        // Charging to 80 % is the normal, healthy case. Only sitting at the top matters.
        var (state, _) = ChargeGuardPolicy.Observe(Fresh, new ChargeGuardConfig(), Snap(80, ac: true), T0);
        Assert.Null(state.EpisodeStartedUtc);
    }

    [Fact]
    public void An_idle_tick_does_not_emit_a_clear()
    {
        // Emitting ClearCool on every tick with no episode running would make the worker re-apply the
        // mode profile once a second forever — wasteful, and indistinguishable in a log from a real
        // restore, which is the thing anyone would be looking for.
        var (_, decision) = ChargeGuardPolicy.Observe(Fresh, new ChargeGuardConfig(), Snap(50, ac: false), T0);
        Assert.False(decision.ClearCool);
    }

    [Fact]
    public void The_alert_fires_once_per_episode_and_names_the_hours()
    {
        var cfg = new ChargeGuardConfig(AlertAfterHours: 4);
        var state = Fresh;

        // Start the episode, then sit there.
        (state, var d0) = ChargeGuardPolicy.Observe(state, cfg, Snap(100, ac: true), T0);
        Assert.Null(d0.Alert);

        (state, var d1) = ChargeGuardPolicy.Observe(state, cfg, Snap(100, ac: true), T0.AddHours(3.9));
        Assert.Null(d1.Alert);

        (state, var d2) = ChargeGuardPolicy.Observe(state, cfg, Snap(100, ac: true), T0.AddHours(4.1));
        Assert.NotNull(d2.Alert);
        Assert.Contains("4.1 h", d2.Alert);
        Assert.Contains("100%", d2.Alert);

        // And it stays quiet afterwards. The alert store coalesces repeats, but a guard that
        // republishes every second is relying on that to hide its own noise.
        (_, var d3) = ChargeGuardPolicy.Observe(state, cfg, Snap(100, ac: true), T0.AddHours(9));
        Assert.Null(d3.Alert);
    }

    [Fact]
    public void The_alert_admits_the_daemon_cannot_stop_the_charge()
    {
        // The whole feature exists because a charge threshold is unavailable on this board. An alert
        // that just said "unplug it" would imply GPD Forge had chosen not to act.
        var cfg = new ChargeGuardConfig(AlertAfterHours: 1);
        var (state, _) = ChargeGuardPolicy.Observe(Fresh, cfg, Snap(100, ac: true), T0);
        var (_, d) = ChargeGuardPolicy.Observe(state, cfg, Snap(100, ac: true), T0.AddHours(2));

        Assert.Contains("cannot stop the charge", d.Alert);
    }

    // --- the gate: engage AND disengage -----------------------------------------------------------

    [Fact]
    public void Cooling_is_off_unless_it_is_asked_for()
    {
        // Silently capping someone's performance because their machine is plugged in is not a
        // decision to make on their behalf.
        var (_, d) = ChargeGuardPolicy.Observe(Fresh, new ChargeGuardConfig(), Snap(100, ac: true), T0);
        Assert.Null(d.CoolToW);
        Assert.False(new ChargeGuardConfig().CoolWhileCharging);
    }

    [Fact]
    public void Cooling_engages_during_an_episode_when_enabled()
    {
        var cfg = new ChargeGuardConfig(CoolWhileCharging: true, CoolToW: 15);
        var (_, d) = ChargeGuardPolicy.Observe(Fresh, cfg, Snap(100, ac: true), T0);
        Assert.Equal(15, d.CoolToW);
    }

    [Fact]
    public void Cooling_disengages_when_the_machine_is_unplugged()
    {
        // THE test this feature's plan named. Without ClearCool the ceiling stays applied with
        // nothing left to remove it: the machine is quietly slow forever and nothing on screen says
        // why. That is strictly worse than never having cooled at all.
        var cfg = new ChargeGuardConfig(CoolWhileCharging: true, CoolToW: 15);

        var (state, engaged) = ChargeGuardPolicy.Observe(Fresh, cfg, Snap(100, ac: true), T0);
        Assert.Equal(15, engaged.CoolToW);

        var (after, released) = ChargeGuardPolicy.Observe(state, cfg, Snap(99, ac: false), T0.AddHours(2));

        Assert.True(released.ClearCool, "Unplugging must restore the mode profile.");
        Assert.Null(released.CoolToW);
        Assert.Null(after.EpisodeStartedUtc);
    }

    [Fact]
    public void Cooling_disengages_when_the_charge_falls_below_the_threshold()
    {
        // The other way an episode ends, and the easier one to forget: still on AC, but the pack has
        // drifted down out of the danger band.
        var cfg = new ChargeGuardConfig(CoolWhileCharging: true, CoolToW: 15, HighSocPct: 95);

        var (state, _) = ChargeGuardPolicy.Observe(Fresh, cfg, Snap(100, ac: true), T0);
        var (_, released) = ChargeGuardPolicy.Observe(state, cfg, Snap(90, ac: true), T0.AddHours(1));

        Assert.True(released.ClearCool);
        Assert.Null(released.CoolToW);
    }

    [Fact]
    public void Turning_cooling_off_mid_episode_still_clears_when_the_episode_ends()
    {
        // The clear is unconditional on the setting for exactly this reason: if it were gated on
        // CoolWhileCharging, disabling the option while a ceiling was applied would strand it.
        var on = new ChargeGuardConfig(CoolWhileCharging: true, CoolToW: 15);
        var off = on with { CoolWhileCharging = false };

        var (state, _) = ChargeGuardPolicy.Observe(Fresh, on, Snap(100, ac: true), T0);
        var (state2, _) = ChargeGuardPolicy.Observe(state, off, Snap(100, ac: true), T0.AddHours(1));
        var (_, released) = ChargeGuardPolicy.Observe(state2, off, Snap(100, ac: false), T0.AddHours(2));

        Assert.True(released.ClearCool);
    }

    [Fact]
    public void A_cooling_ceiling_never_raises_power()
    {
        // A ceiling, not a target. Battery mode runs at 8 W; a "cooling" feature that pushed it to 15
        // would be doing the opposite of its name, and it is an easy line to write by accident.
        Assert.Null(ChargeGuardPolicy.EffectiveCeiling(coolToW: 15, modeStapmW: 8));
        Assert.Equal(15, ChargeGuardPolicy.EffectiveCeiling(coolToW: 15, modeStapmW: 25));

        // Equal is also "nothing to do": re-applying an unchanged profile every tick is noise.
        Assert.Null(ChargeGuardPolicy.EffectiveCeiling(coolToW: 15, modeStapmW: 15));
        Assert.Null(ChargeGuardPolicy.EffectiveCeiling(coolToW: null, modeStapmW: 25));
    }

    // --- counters -------------------------------------------------------------------------------

    [Fact]
    public void Hours_are_banked_when_an_episode_ends()
    {
        var cfg = new ChargeGuardConfig();
        var (state, _) = ChargeGuardPolicy.Observe(Fresh, cfg, Snap(100, ac: true), T0);
        var (after, _) = ChargeGuardPolicy.Observe(state, cfg, Snap(100, ac: false), T0.AddHours(6));

        Assert.Equal(6, after.TotalHoursAtHighSoc);
        Assert.Equal(1, after.Episodes);
    }

    [Fact]
    public void Hours_accumulate_across_episodes()
    {
        var cfg = new ChargeGuardConfig();
        var state = Fresh;

        (state, _) = ChargeGuardPolicy.Observe(state, cfg, Snap(100, ac: true), T0);
        (state, _) = ChargeGuardPolicy.Observe(state, cfg, Snap(100, ac: false), T0.AddHours(6));
        (state, _) = ChargeGuardPolicy.Observe(state, cfg, Snap(100, ac: true), T0.AddHours(20));
        (state, _) = ChargeGuardPolicy.Observe(state, cfg, Snap(100, ac: false), T0.AddHours(23));

        Assert.Equal(9, state.TotalHoursAtHighSoc);
        Assert.Equal(2, state.Episodes);
    }

    [Fact]
    public void A_clock_that_goes_backwards_cannot_subtract_hours()
    {
        // NTP corrections, resumes and hand-edited state files all produce this. A counter that only
        // ever grows must not shrink, and an episode must never report a negative length that reads
        // as being in the future — the exact bug the vault records from Jano's agent_runs table.
        var cfg = new ChargeGuardConfig();
        var (state, _) = ChargeGuardPolicy.Observe(Fresh, cfg, Snap(100, ac: true), T0);
        var (after, d) = ChargeGuardPolicy.Observe(state, cfg, Snap(100, ac: true), T0.AddHours(-3));

        Assert.Equal(0, d.EpisodeHours);
        Assert.True(after.TotalHoursAtHighSoc >= 0);
    }

    [Fact]
    public void Disabling_the_guard_ends_a_running_episode_rather_than_freezing_it()
    {
        // Otherwise EpisodeStartedUtc stays set, and re-enabling months later would bank the entire
        // gap as time spent at high charge.
        var (state, _) = ChargeGuardPolicy.Observe(Fresh, new ChargeGuardConfig(), Snap(100, ac: true), T0);
        var (after, d) = ChargeGuardPolicy.Observe(state, new ChargeGuardConfig(Enabled: false),
                                                   Snap(100, ac: true), T0.AddHours(2));

        Assert.Null(after.EpisodeStartedUtc);
        Assert.True(d.ClearCool, "Disabling the guard must also release any ceiling it applied.");
        Assert.Equal(2, after.TotalHoursAtHighSoc);
    }
}
