// GPD Forge — which application's frames the FPS reading is about. GPL-3.0-or-later.
//
// PresentMon traces every process on the machine. Until 2026-09-24 the reading belonged to whichever
// presented the most rows in the window — the compositor, a browser, a 144 Hz overlay — and the app
// the user was actually looking at had no say. The order here is the user's intent first, then the
// user's own rules, then continuity; never raw volume.
using GpdForge.Profiles;

namespace GpdForge.Telemetry;

public static class FrameTarget
{
    /// <summary>
    /// Chooses the target among the applications presenting right now:
    /// <list type="number">
    /// <item>the foreground process, when it is presenting — even at 2 fps, it is what is on screen;</item>
    /// <item>otherwise an app a user rule names (<paramref name="isRuleMatched"/>). Several can match
    /// — the shipped "steam" rule also names steamwebhelper — so among THOSE the one presenting the
    /// most frames wins: the game rather than its launcher;</item>
    /// <item>otherwise the previous target, if it is still presenting: the Forge overlay or the Steam
    /// QAM takes the foreground while the game keeps rendering underneath;</item>
    /// <item>otherwise nothing. "No reading" is honest; "whatever presents most" is not.</item>
    /// </list>
    /// </summary>
    /// <param name="foreground">Foreground process name as Win32 reports it (no ".exe"), or null.</param>
    public static string? Choose(
        IReadOnlyCollection<AppActivity> presenting,
        string? foreground,
        Func<string, bool> isRuleMatched,
        string? previous)
    {
        ArgumentNullException.ThrowIfNull(presenting);
        ArgumentNullException.ThrowIfNull(isRuleMatched);

        var candidates = presenting.Where(a => IsNamed(a.Application)).ToArray();
        if (candidates.Length == 0) return null;

        string fg = AppRulePolicy.Normalize(foreground);
        if (fg.Length > 0)
        {
            foreach (var a in candidates)
                if (AppRulePolicy.Normalize(a.Application) == fg) return a.Application;
        }

        var ruled = candidates.Where(a => isRuleMatched(a.Application))
                              .OrderByDescending(a => a.Frames)
                              .ThenByDescending(a => a.LastFrameAt)
                              .FirstOrDefault();
        if (ruled.Application is not null) return ruled.Application;

        if (previous is not null)
        {
            foreach (var a in candidates)
                if (string.Equals(a.Application, previous, StringComparison.Ordinal)) return a.Application;
        }
        return null;
    }

    // Without elevation PresentMon cannot name some processes and prints "<unknown>"; that is a
    // placeholder, not an app, and must never become the reading's owner.
    private static bool IsNamed(string app) =>
        !string.IsNullOrWhiteSpace(app) && !app.StartsWith('<');
}
