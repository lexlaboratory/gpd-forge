// GPD Forge — which application's frames the FPS reading is about. GPL-3.0-or-later.
//
// PresentMon traces every process on the machine. Until 2026-09-24 the reading belonged to whichever
// presented the most rows in the window — the compositor, a browser, a 144 Hz overlay — and the app
// the user was actually looking at had no say. The order here is the user's intent first, then the
// user's own rules, then continuity; never raw volume across everything.
//
// The foreground is often UNKNOWN, and the order has to work then too. The daemon is a LocalSystem
// service in session 0, where GetForegroundWindow is always NULL (audit, 2026-09-24: GET /app-rules
// reported `process: null` three times while the user had windows open). The user-session agent
// reports the real foreground when it runs (SessionForegroundApp); when it does not, a Steam game no
// rule names left the shipped "steam" rule matching only steamwebhelper — the launcher — and the
// reading was Steam's UI or nothing. So presenters that are never the game are excluded from the
// rule step, and an unknown foreground gets a fallback of its own.
using GpdForge.Profiles;

namespace GpdForge.Telemetry;

public static class FrameTarget
{
    /// <summary>
    /// Processes that present frames but are never the game being played: the compositor and shell,
    /// browsers and web views, launchers and overlays, and GPD Forge's own window. Normalised names
    /// (<see cref="AppRulePolicy.Normalize"/>). A deliberately short list of the usual suspects on
    /// this machine, not an attempt to classify every program.
    /// </summary>
    public static readonly IReadOnlySet<string> NonGamePresenters = new HashSet<string>(StringComparer.Ordinal)
    {
        // Windows itself
        "dwm", "explorer", "applicationframehost", "shellexperiencehost", "startmenuexperiencehost",
        "searchhost", "textinputhost", "lockapp", "systemsettings",
        // Browsers and embedded web views (Tauri, and so GPD Forge's own window, presents through WebView2)
        "chrome", "msedge", "msedgewebview2", "firefox", "brave", "opera", "vivaldi",
        // Launchers and overlays
        "steam", "steamwebhelper", "epicgameslauncher", "eadesktop", "galaxyclient", "ubisoftconnect",
        "gamebar", "gamebarftserver", "xboxpcappft", "radeonsoftware", "discord", "motionassistant",
        // GPD Forge
        "gpd forge", "gpd-forge",
    };

    /// <summary>
    /// Chooses the target among the applications presenting right now:
    /// <list type="number">
    /// <item>the foreground process, when it is presenting — even at 2 fps, it is what is on screen;</item>
    /// <item>otherwise an app a user rule names (<paramref name="isRuleMatched"/>) that is not a known
    /// non-game presenter. Several can match, so among THOSE the one presenting the most frames wins;
    /// the launcher is excluded outright — the shipped "steam" rule also names steamwebhelper, and a
    /// game with no rule of its own must not lose to it;</item>
    /// <item>otherwise the previous target, if it is still presenting: the Forge overlay or the Steam
    /// QAM takes the foreground while the game keeps rendering underneath;</item>
    /// <item>otherwise, ONLY when the foreground is unknown, the busiest presenter that is not a known
    /// non-game one. With no statement of intent available, the app rendering the most frames that
    /// could be a game is the best evidence there is;</item>
    /// <item>otherwise nothing. When the foreground is known and is not presenting (the desktop, with
    /// the game minimised), "no reading" is honest; "whatever presents most" is not.</item>
    /// </list>
    /// </summary>
    /// <param name="foreground">Foreground process name (no ".exe"), or null when it cannot be seen.</param>
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

        var ruled = Busiest(candidates.Where(a => !IsNonGame(a.Application) && isRuleMatched(a.Application)));
        if (ruled is not null) return ruled;

        if (previous is not null)
        {
            foreach (var a in candidates)
                if (string.Equals(a.Application, previous, StringComparison.Ordinal)) return a.Application;
        }

        return fg.Length == 0 ? Busiest(candidates.Where(a => !IsNonGame(a.Application))) : null;
    }

    /// <summary>Whether <paramref name="application"/> (any case, with or without ".exe") is on
    /// <see cref="NonGamePresenters"/>.</summary>
    public static bool IsNonGame(string application) =>
        NonGamePresenters.Contains(AppRulePolicy.Normalize(application));

    private static string? Busiest(IEnumerable<AppActivity> apps) =>
        apps.OrderByDescending(a => a.Frames)
            .ThenByDescending(a => a.LastFrameAt)
            .Select(a => a.Application)
            .FirstOrDefault();

    // Without elevation PresentMon cannot name some processes and prints "<unknown>"; that is a
    // placeholder, not an app, and must never become the reading's owner.
    private static bool IsNamed(string app) =>
        !string.IsNullOrWhiteSpace(app) && !app.StartsWith('<');
}
