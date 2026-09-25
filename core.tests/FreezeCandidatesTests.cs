// GPD Forge — F5: the Games editor's "freeze while playing" checklist ranking. GPL-3.0-or-later.
using GpdForge.SystemControl;
using Xunit;

namespace GpdForge.Core.Tests;

public class FreezeCandidatesTests
{
    private const long Mb = 1024 * 1024;

    [Fact]
    public void Suggested_apps_come_first_even_when_small_then_heavy_ones_by_memory()
    {
        var rows = FreezeCandidates.Rank(
        [
            ("OneDrive", 80 * Mb), ("chrome", 300 * Mb), ("chrome", 250 * Mb), ("ollama", 40 * Mb),
            ("Discord", 420 * Mb), ("notepad", 20 * Mb),
        ]);

        Assert.Equal(["onedrive", "ollama", "chrome", "discord"], rows.Select(r => r.Name));
        Assert.Equal(550, rows.Single(r => r.Name == "chrome").MemoryMb);   // summed per name
        Assert.True(rows[0].Suggested);
        Assert.False(rows.Single(r => r.Name == "discord").Suggested);
    }

    [Fact]
    public void Protected_and_session_critical_processes_and_the_game_itself_are_never_offered()
    {
        var rows = FreezeCandidates.Rank(
        [
            ("explorer", 900 * Mb), ("svchost", 900 * Mb), ("dwm", 900 * Mb), ("audiodg", 900 * Mb),
            ("Memory Compression", 900 * Mb), ("GpdForge.Agent", 900 * Mb), ("eldenring", 4000 * Mb),
            ("steam", 400 * Mb),
        ], game: "eldenring");

        Assert.Equal(["steam"], rows.Select(r => r.Name));
    }

    [Fact]
    public void The_list_is_capped()
    {
        var many = Enumerable.Range(0, 40).Select(i => ($"app{i}", (300 + i) * Mb));
        Assert.Equal(FreezeCandidates.MaxRows, FreezeCandidates.Rank(many).Count);
    }
}
