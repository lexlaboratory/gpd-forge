using GpdForge.Profiles;
using GpdForge.Tdp;
using Xunit;

namespace GpdForge.Core.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ModePresetCollection
{
    public const string Name = "mode preset state";
}

[Collection(ModePresetCollection.Name)]
public sealed class ModePresetStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gpdforge-presets-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        ModeProfiles.Initialize([]);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Saved_twelve_watt_preset_is_loaded_by_a_new_store_and_applied_on_boot()
    {
        var store = new ModePresetStore(_dir);
        ModeProfiles.PersistSet(store, "battery", new TdpProfile(12, 15, 13, 90));

        ModeProfiles.Initialize(new ModePresetStore(_dir).Read());
        var preset = ModeProfiles.For("battery");

        Assert.Equal(12, preset!.Value.StapmW);
        var tdp = new FakeTdp();
        var applier = new ProfileApplier(tdp, new NoRivals());
        Assert.Equal(ApplyOutcome.AppliedVerified, await applier.ApplyAsync("battery", CancellationToken.None));
        Assert.Equal(12, tdp.LastApplied!.Value.StapmW);
    }

    [Fact]
    public void Corrupt_store_falls_back_to_catalogue_defaults_and_is_quarantined()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "mode-presets.json"), "{ not json");

        var overrides = new ModePresetStore(_dir).Read();
        ModeProfiles.Initialize(overrides);

        Assert.Equal(8, ModeProfiles.For("battery")!.Value.StapmW);
        Assert.NotEmpty(Directory.GetFiles(_dir, "mode-presets.json.corrupt-*"));
    }

    [Fact]
    public void Unknown_disk_entries_are_ignored_while_known_overlays_load()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "mode-presets.json"), """
            { "battery": { "stapmW": 12, "fastW": 15, "slowW": 13, "tctlC": 90 },
              "overdrive": { "stapmW": 40, "fastW": 45, "slowW": 45, "tctlC": 95 } }
            """);

        var overlays = new ModePresetStore(_dir).Read();
        ModeProfiles.Initialize(overlays);

        Assert.Single(overlays);
        Assert.Equal(12, ModeProfiles.For("battery")!.Value.StapmW);
        Assert.DoesNotContain("overdrive", ModeProfiles.Snapshot().Keys);
    }

    [Fact]
    public void Case_colliding_disk_entries_are_canonicalized_with_last_value_winning()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "mode-presets.json"), """
            { "battery": { "stapmW": 12, "fastW": 15, "slowW": 13, "tctlC": 90 },
              "BATTERY": { "stapmW": 13, "fastW": 16, "slowW": 14, "tctlC": 91 } }
            """);

        var overlays = new ModePresetStore(_dir).Read();
        ModeProfiles.Initialize(overlays);

        Assert.Single(overlays);
        Assert.Equal(13, ModeProfiles.For("battery")!.Value.StapmW);
    }

    [Fact]
    public void Json_null_is_quarantined_as_corrupt_instead_of_becoming_an_empty_overlay()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "mode-presets.json"), "null");

        Assert.Empty(new ModePresetStore(_dir).Read());

        Assert.NotEmpty(Directory.GetFiles(_dir, "mode-presets.json.corrupt-*"));
    }

    [Fact]
    public void Invalid_profile_entry_is_ignored_so_it_cannot_apply_zero_watts()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "mode-presets.json"), "{ \"battery\": {} }");

        var overlays = new ModePresetStore(_dir).Read();
        ModeProfiles.Initialize(overlays);

        Assert.Empty(overlays);
        Assert.Equal(8, ModeProfiles.For("battery")!.Value.StapmW);
    }

    [Fact]
    public void Out_of_range_persisted_profile_is_ignored_instead_of_being_clamped_into_a_new_limit()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "mode-presets.json"),
            "{ \"battery\": { \"stapmW\": 999, \"fastW\": 20, \"slowW\": 17, \"tctlC\": 90 } }");

        var overlays = new ModePresetStore(_dir).Read();
        ModeProfiles.Initialize(overlays);

        Assert.Empty(overlays);
        Assert.Equal(8, ModeProfiles.For("battery")!.Value.StapmW);
    }

    [Fact]
    public void Unknown_modes_are_rejected_and_never_persisted()
    {
        var store = new ModePresetStore(_dir);
        Assert.Throws<ArgumentException>(() => ModeProfiles.PersistSet(store, "overdrive", new TdpProfile(12, 12, 12, 90)));
        Assert.DoesNotContain("overdrive", ModeProfiles.Snapshot().Keys);
        Assert.Empty(store.Read());
    }

    [Fact]
    public void Failed_disk_write_does_not_change_the_map_or_report_a_saved_value()
    {
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(Path.Combine(_dir, "mode-presets.json"));
        var before = ModeProfiles.For("battery")!.Value;

        Assert.ThrowsAny<IOException>(() => ModeProfiles.PersistSet(new ModePresetStore(_dir), "battery", new TdpProfile(12, 15, 13, 90)));
        Assert.ThrowsAny<IOException>(() => ModeProfiles.PersistMany(new ModePresetStore(_dir), new Dictionary<string, TdpProfile>
        {
            ["battery"] = new(12, 15, 13, 90),
            ["gaming"] = new(21, 30, 26, 92),
        }));

        Assert.Equal(before, ModeProfiles.For("battery")!.Value);
        Assert.Equal(25, ModeProfiles.For("gaming")!.Value.StapmW);
        Assert.Empty(Directory.GetFiles(_dir, "mode-presets.json.tmp-*"));
    }

    [Fact]
    public void Import_persists_as_one_overlay_and_survives_reload()
    {
        var store = new ModePresetStore(_dir);
        ModeProfiles.PersistMany(store, new Dictionary<string, TdpProfile>
        {
            ["battery"] = new(12, 15, 13, 90),
            ["gaming"] = new(21, 30, 26, 92),
        });

        ModeProfiles.Initialize(new ModePresetStore(_dir).Read());

        Assert.Equal(12, ModeProfiles.For("battery")!.Value.StapmW);
        Assert.Equal(21, ModeProfiles.For("gaming")!.Value.StapmW);
        Assert.Equal(25, ModeProfiles.For("ai")!.Value.StapmW);
    }

    private sealed class FakeTdp : ITdpController
    {
        public TdpProfile? LastApplied { get; private set; }
        public Task<TdpApplyResult> ApplyAsync(TdpProfile profile, string owner, CancellationToken ct)
        {
            LastApplied = profile;
            return Task.FromResult(new TdpApplyResult(profile, new TdpReadout(profile.StapmW, profile.FastW), true, 1));
        }
    }

    private sealed class NoRivals : IPowerControllerDetector
    {
        public bool OthersRunning(out string[] names) { names = []; return false; }
    }
}
