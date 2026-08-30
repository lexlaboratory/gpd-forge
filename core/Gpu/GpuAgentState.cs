// GPD Forge — what the user-session GPU agent last reported. GPL-3.0-or-later.
//
// ADLX cannot be driven from the daemon. Measured on 2026-08-29: the identical code initialises fine
// in an interactive session and fails under the service with "ADLXInitialize did not return a system
// interface", because the service is LocalSystem in session 0 and ADLX needs the display driver stack
// of an interactive session. The interop was correct; where it ran was not.
//
// So the ADLX calls live in an agent running in the user's session (`--gpu-agent`, the same assembly
// so no new unsigned binary is introduced), and the daemon holds what that agent last told it.
//
// The daemon therefore reports SECOND-HAND information here, and the whole design of this type is
// about not pretending otherwise. A snapshot carries when it was taken; a snapshot nobody has
// refreshed is stale and says so; and "no agent has ever reported" is a distinct answer from "the
// agent reported that ADLX is unavailable". Collapsing those would tell a user their GPU cannot be
// controlled when the truth is that nothing has looked yet.
namespace GpdForge.Gpu;

/// <summary>One report from the agent. <paramref name="AtUtc"/> is when the AGENT read it.</summary>
public sealed record GpuAgentReport(
    bool Available,
    string Status,
    string? AdlxVersion,
    string Detail,
    GpuSettingsSnapshot? Settings,
    DateTimeOffset AtUtc);

/// <summary>
/// The last report, and how old it is. Written by the agent's POST, read by GET /gpu.
/// </summary>
public sealed class GpuAgentState
{
    /// <summary>
    /// How long a report stays credible. The agent polls every few seconds, so anything older than
    /// this means it stopped, crashed, or the user logged out — none of which are "the GPU is fine".
    /// Deliberately generous: a brief hiccup should not blank a working panel.
    /// </summary>
    public static readonly TimeSpan Freshness = TimeSpan.FromSeconds(30);

    private GpuAgentReport? _last;

    public void Report(GpuAgentReport report) => Volatile.Write(ref _last, report);

    /// <summary>The last report, or null if the agent has never checked in.</summary>
    public GpuAgentReport? Last => Volatile.Read(ref _last);

    /// <summary>
    /// Whether the last report is recent enough to describe the present. False when there is no
    /// report at all — which the caller must render as "not looked yet", not as "unavailable".
    /// </summary>
    public bool IsFresh(DateTimeOffset now) => Last is { } r && now - r.AtUtc <= Freshness;

    /// <summary>
    /// What GET /gpu should say, in one place so the daemon cannot accidentally present a stale
    /// reading as current. Returns the report plus an explanation when it should not be trusted.
    /// </summary>
    public (GpuAgentReport? Report, bool Usable, string Explanation) Current(DateTimeOffset now)
    {
        var last = Last;
        if (last is null)
            return (null, false,
                "The GPU agent has not reported yet. It runs in your Windows session (ADLX cannot be "
                + "reached from the service), so it starts when you log in.");

        if (now - last.AtUtc > Freshness)
            return (last, false,
                $"The GPU agent last reported {(int)(now - last.AtUtc).TotalSeconds}s ago and has gone "
                + "quiet — these values describe that moment, not now. It may have exited, or the "
                + "session may have been locked or logged out.");

        return (last, last.Available, last.Detail);
    }
}
