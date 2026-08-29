// GPD Forge — session open/close state machine. GPL-3.0-or-later.
//
// Pure: it never reads a clock, only the timestamp on the tick it is handed, so its behaviour is
// fully determined by its input and the tests can play out an entire evening in microseconds.
// Fed once per worker tick (1 Hz). See SessionModels.cs for the thresholds and why they are what
// they are.

namespace GpdForge.Sessions;

public sealed class SessionTracker
{
    private readonly SessionPolicy _policy;
    private readonly Lock _gate = new();
    private SessionBuilder? _open;

    public SessionTracker(SessionPolicy? policy = null)
    {
        _policy = policy ?? SessionPolicy.Default;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_policy.IdleTimeout, TimeSpan.Zero, nameof(policy));
        ArgumentOutOfRangeException.ThrowIfLessThan(_policy.MinDuration, TimeSpan.Zero, nameof(policy));
        ArgumentOutOfRangeException.ThrowIfLessThan(_policy.TrendPoints, 1, nameof(policy));
    }

    public bool HasOpenSession { get { lock (_gate) return _open is not null; } }

    public string? CurrentApp { get { lock (_gate) return _open?.App; } }

    /// <summary>
    /// Feeds one observation. Returns the session that this tick ended, if any — at most one session
    /// can close per tick, because a session only ends when the app stops presenting or another app
    /// takes over, and both are single events.
    /// </summary>
    public GameSession? Observe(SessionTick tick)
    {
        lock (_gate)
        {
            bool presenting = tick.App is not null && tick.Fps is > 0;

            if (_open is not null)
            {
                // A different app taking over the reading ends the previous session immediately:
                // FrameWindow already resolved which process owns the frame rate, and mixing two
                // games into one row would be a lie about both.
                bool appChanged = presenting && !string.Equals(_open.App, tick.App, StringComparison.OrdinalIgnoreCase);
                bool wentQuiet = tick.At - _open.LastFrameAt > _policy.IdleTimeout;
                if (appChanged || wentQuiet)
                {
                    var closed = Close();
                    if (presenting) _open = new SessionBuilder(tick.App!, tick);
                    return closed;
                }

                // A tick where nothing is presenting is not part of the session: it is the gap we are
                // waiting out. Only ticks that belong to this app are recorded — with or without an
                // FPS aggregate, so a probe that names the app but cannot aggregate this second is
                // counted as coverage we did not get, rather than silently averaged in as zero.
                bool sameApp = tick.App is not null
                    && string.Equals(_open.App, tick.App, StringComparison.OrdinalIgnoreCase);
                if (sameApp) _open.Add(tick, countsAsFrame: presenting);
                return null;
            }

            if (presenting) _open = new SessionBuilder(tick.App!, tick);
            return null;
        }
    }

    /// <summary>Closes any open session — service shutdown, or a caller that wants the current
    /// session filed now. <paramref name="now"/> is accepted for symmetry but never becomes the end
    /// time: a session ends when its last frame was presented, not when we noticed.</summary>
    public GameSession? Flush(DateTimeOffset now)
    {
        _ = now;
        lock (_gate) return Close();
    }

    private GameSession? Close()
    {
        var builder = _open;
        _open = null;
        if (builder is null) return null;
        var session = builder.Build(_policy.TrendPoints);
        // Too short to be a play session — see SessionPolicy.MinDuration.
        return session.DurationSeconds < _policy.MinDuration.TotalSeconds ? null : session;
    }
}
