// GPD Forge — the fan preference survives a restart. GPL-3.0-or-later.
//
// FanState lived only in memory, so every reboot, service restart or reinstall silently handed the
// fan back to firmware (seen three times on 2026-09-24: Balanced set, reinstall, back to Auto).
using GpdForge.Fan;
using Xunit;

namespace GpdForge.Core.Tests;

public sealed class FanPreferenceStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "forge-fanpref-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void No_file_means_the_firmware_default()
    {
        var p = new FanPreferenceStore(_dir).Read();
        Assert.Equal(new FanPreference("Auto", 128), p);
    }

    [Fact]
    public void A_saved_preference_is_read_back_by_a_new_instance()
    {
        new FanPreferenceStore(_dir).Write(new FanPreference("Balanced", 90));
        Assert.Equal(new FanPreference("Balanced", 90), new FanPreferenceStore(_dir).Read());
    }

    [Fact]
    public void A_corrupt_file_falls_back_to_the_default_and_is_kept_aside()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "fan.json"), "{ not json");
        Assert.Equal(new FanPreference("Auto", 128), new FanPreferenceStore(_dir).Read());
        Assert.Single(Directory.GetFiles(_dir, "fan.json.corrupt-*"));
    }

    [Fact]
    public void An_unknown_mode_in_the_file_is_not_trusted()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "fan.json"), """{ "Mode": "Turbo", "ManualDuty": 999 }""");
        var p = new FanPreferenceStore(_dir).Read();
        Assert.Equal("Auto", p.Mode);
        Assert.Equal(255, p.ManualDuty);
    }
}
