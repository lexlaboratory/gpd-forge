// GPD Forge — per-app rule store tests (persistence, precedence, validation). GPL-3.0-or-later.
using GpdForge.Profiles;
using Xunit;

namespace GpdForge.Core.Tests;

public sealed class AppRuleStoreTests
{
    private const string FileName = "app-rules.json";

    [Fact]
    public void First_run_seeds_the_engine_defaults_and_writes_them_out()
    {
        using var temp = new TempDir();
        var store = new AppRuleStore(temp.Path);

        Assert.NotEmpty(store.List());
        Assert.True(File.Exists(Path.Combine(temp.Path, FileName)));
        // Seeded rules must reproduce the matcher the engine shipped with.
        Assert.Equal("ai", store.ModeFor("ollama.exe"));
        Assert.Equal("gaming", store.ModeFor("steam"));
        Assert.Null(store.ModeFor("notepad"));
    }

    [Fact]
    public void Rules_survive_a_reload()
    {
        using var temp = new TempDir();
        var added = new AppRuleStore(temp.Path).Add("MyGame.exe", "gaming");

        var reloaded = new AppRuleStore(temp.Path);
        var found = reloaded.List().Single(r => r.Id == added.Id);
        Assert.Equal("mygame", found.Match);
        Assert.Equal("gaming", found.Mode);
        Assert.True(found.Enabled);
    }

    [Fact]
    public void A_seeded_match_cannot_be_shadowed_by_a_second_rule_for_the_same_process()
    {
        using var temp = new TempDir();
        var store = new AppRuleStore(temp.Path);

        // Two rules for one process is the ambiguity this store exists to prevent: edit the seeded
        // one instead. Without this the "which rule is deciding" readout would be a coin flip.
        Assert.Throws<ArgumentException>(() => store.Add("steam", "ai"));
        Assert.Equal("gaming", store.ModeFor("steam"));
    }

    [Fact]
    public void Precedence_is_list_order_and_the_first_match_wins()
    {
        using var temp = new TempDir();
        var store = new AppRuleStore(temp.Path, seedDefaults: false);
        var broad = store.Add("game", "gaming");
        var narrow = store.Add("gamesim", "ai");

        // Appended last, so the broad rule still wins until it is moved.
        Assert.Equal("gaming", store.ModeFor("gamesim.exe"));

        Assert.True(store.Move(narrow.Id, -1));
        Assert.Equal("ai", store.ModeFor("gamesim.exe"));
        Assert.Equal(new[] { narrow.Id, broad.Id }, store.List().Select(r => r.Id));
    }

    [Fact]
    public void Move_clamps_at_the_edges_and_reports_no_change()
    {
        using var temp = new TempDir();
        var store = new AppRuleStore(temp.Path, seedDefaults: false);
        var first = store.Add("a", "ai");
        var last = store.Add("b", "gaming");

        Assert.False(store.Move(first.Id, -1));
        Assert.False(store.Move(last.Id, 5));
        Assert.False(store.Move(Guid.NewGuid(), 1));
        Assert.True(store.Move(first.Id, 99));
        Assert.Equal(new[] { last.Id, first.Id }, store.List().Select(r => r.Id));
    }

    [Fact]
    public void A_disabled_rule_never_matches()
    {
        using var temp = new TempDir();
        var store = new AppRuleStore(temp.Path, seedDefaults: false);
        var rule = store.Add("steam", "gaming");
        Assert.Equal("gaming", store.ModeFor("steam.exe"));

        store.Update(rule.Id, "steam", "gaming", enabled: false);
        Assert.Null(store.ModeFor("steam.exe"));
        Assert.Null(store.RuleFor("steam.exe"));
    }

    [Fact]
    public void Invalid_input_is_rejected_whole_and_nothing_is_written()
    {
        using var temp = new TempDir();
        var store = new AppRuleStore(temp.Path, seedDefaults: false);
        store.Add("steam", "gaming");
        var before = File.ReadAllText(Path.Combine(temp.Path, FileName));

        Assert.Throws<ArgumentException>(() => store.Add("  ", "gaming"));
        Assert.Throws<ArgumentException>(() => store.Add("vlc", "turbo"));
        Assert.Throws<ArgumentException>(() => store.Add("STEAM.exe", "ai"));

        Assert.Single(store.List());
        Assert.Equal(before, File.ReadAllText(Path.Combine(temp.Path, FileName)));
    }

    [Fact]
    public void Update_rejects_colliding_with_another_rule_but_allows_editing_in_place()
    {
        using var temp = new TempDir();
        var store = new AppRuleStore(temp.Path, seedDefaults: false);
        var steam = store.Add("steam", "gaming");
        var vlc = store.Add("vlc", "windows");

        Assert.Throws<ArgumentException>(() => store.Update(vlc.Id, "Steam", "windows", true));
        Assert.Throws<KeyNotFoundException>(() => store.Update(Guid.NewGuid(), "x", "ai", true));

        var edited = store.Update(steam.Id, "Steam.exe", "ai", true);
        Assert.Equal(steam.Id, edited.Id);
        Assert.Equal("steam", edited.Match);
        Assert.Equal("ai", edited.Mode);
    }

    [Fact]
    public void Delete_is_idempotent()
    {
        using var temp = new TempDir();
        var store = new AppRuleStore(temp.Path, seedDefaults: false);
        var rule = store.Add("steam", "gaming");

        Assert.True(store.Delete(rule.Id));
        Assert.False(store.Delete(rule.Id));
        Assert.Empty(store.List());
        Assert.Empty(new AppRuleStore(temp.Path, seedDefaults: false).List());
    }

    [Fact]
    public void A_corrupt_file_is_quarantined_and_the_defaults_come_back()
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, FileName), "{not-json");

        var store = new AppRuleStore(temp.Path);

        Assert.NotEmpty(store.List());
        Assert.Single(Directory.GetFiles(temp.Path, FileName + ".corrupt-*"));
        Assert.Equal("gaming", store.ModeFor("steam"));
    }

    [Fact]
    public void An_older_file_without_enabled_or_ids_is_normalized_not_discarded()
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, FileName), """
        [
          { "Match": "Steam.exe", "Mode": "gaming" },
          { "Match": "steam", "Mode": "ai" },
          { "Match": "   ", "Mode": "ai" },
          { "Match": "vlc", "Mode": "turbo" },
          { "Match": "ollama", "Mode": "ai", "Enabled": false }
        ]
        """);

        var store = new AppRuleStore(temp.Path);
        var rules = store.List();

        // Duplicate, blank and unknown-mode entries are dropped; the survivors get real ids.
        Assert.Equal(2, rules.Count);
        Assert.All(rules, r => Assert.NotEqual(Guid.Empty, r.Id));
        Assert.Equal(new[] { "steam", "ollama" }, rules.Select(r => r.Match));
        Assert.True(rules[0].Enabled);      // missing "Enabled" must not read as disabled
        Assert.False(rules[1].Enabled);
    }

    [Fact]
    public void RecordMatch_remembers_which_rule_is_deciding_right_now()
    {
        using var temp = new TempDir();
        var store = new AppRuleStore(temp.Path);
        Assert.Null(store.LastMatch);

        var steam = store.List().First(r => r.Match == "steam");
        var match = store.RecordMatch("Steam.exe", "gaming", acConnected: true);

        Assert.Equal(steam.Id, match.RuleId);
        Assert.Equal("steam", match.Match);
        Assert.Equal("gaming", match.Mode);
        Assert.Equal("Steam.exe", match.Process);
        Assert.True(match.AcConnected);
        Assert.Equal(match, store.LastMatch);
    }

    [Fact]
    public void RecordMatch_reports_the_power_fallback_when_no_rule_matched()
    {
        using var temp = new TempDir();
        var store = new AppRuleStore(temp.Path);

        var match = store.RecordMatch("notepad.exe", "battery", acConnected: false);

        Assert.Null(match.RuleId);
        Assert.Null(match.Match);
        Assert.Equal("battery", match.Mode);
    }

    [Fact]
    public void The_store_drives_the_focus_engine()
    {
        using var temp = new TempDir();
        var store = new AppRuleStore(temp.Path, seedDefaults: false);
        store.Add("mygame", "gaming");
        var engine = new FocusProfileEngine("windows", store, stabilityTicks: 1);

        Assert.Equal("gaming", engine.Tick("MyGame.exe", acConnected: true));
        Assert.Equal("windows", engine.Tick("explorer.exe", acConnected: true));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gpd-rules-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
