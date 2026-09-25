// GPD Forge — holds Forge's own heavy periodic work while a game profile is in force. GPL-3.0-or-later.
//
// F5 (2026-09-25). The sleep study (`powercfg /sleepstudy`: tens of seconds, ~9 MB of HTML to parse)
// and the battery report (`powercfg /batteryreport`: a process and a 76 KB XML) are the daemon's two
// heaviest jobs. Both run on timers that know nothing about games, so either could land in the middle
// of a session and cost frames inside the same 22 W the game is using. Neither is urgent — one reports
// on last week's nights, the other samples a curve that moves over months — so they wait for the game
// to end. A wait, not a skip: the run happens as soon as the profile comes off.
namespace GpdForge.Profiles;

public static class GamePause
{
    /// <summary>How often a held job looks again. Coarse: nothing waiting here is time-critical.</summary>
    public static readonly TimeSpan DefaultPoll = TimeSpan.FromSeconds(30);

    /// <summary>True while a game profile is in force.</summary>
    public static bool Playing(ActiveGameProfileState? game) => game?.Current is not null;

    /// <summary>Returns once no game profile is in force (at once when none is, or when
    /// <paramref name="game"/> is null — no game state wired, nothing to wait for). Throws
    /// OperationCanceledException on shutdown, like the Task.Delay it wraps.</summary>
    public static async Task WaitUntilIdleAsync(ActiveGameProfileState? game, CancellationToken ct, TimeSpan? poll = null)
    {
        while (Playing(game)) await Task.Delay(poll ?? DefaultPoll, ct);
    }
}
