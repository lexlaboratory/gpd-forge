// GPD Forge — Forge Advisor: dismissals, Apply, and the record of what was accepted (plan F3). GPL-3.0-or-later.
//
// The advisor only ever proposes. Apply writes a suggestion into the game's profile — the F1 rule
// overrides, through the same IAppRuleStore the Games page edits — and nothing else: the focus loop
// then applies the profile the way it applies one the user typed. The accepted list is kept so the
// user can see what the advisor changed and when (the plan's "registro de qué se aceptó").
using System.Text.Json;
using GpdForge.Profiles;

namespace GpdForge.Advisor;

public sealed record AppliedSuggestion(string Id, string Game, string Kind, int? StapmW, int? FrameCapFps, DateTimeOffset AtUtc);

public sealed class AdvisorService
{
    public const string FileName = "advisor.json";
    /// <summary>Bounds on the stored lists. Ids carry their value, so a game whose advice keeps
    /// changing would otherwise grow the dismissed list for ever.</summary>
    public const int MaxDismissed = 200;
    public const int MaxApplied = 50;

    private sealed record State(List<string>? Dismissed, List<AppliedSuggestion>? Applied);

    private readonly Lock _gate = new();
    private readonly string? _filePath;
    private readonly List<string> _dismissed;
    private readonly List<AppliedSuggestion> _applied;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <param name="directory">Where to keep the file; null keeps everything in memory.</param>
    public AdvisorService(string? directory = null)
    {
        if (directory is not null)
        {
            try { Directory.CreateDirectory(directory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            _filePath = Path.Combine(directory, FileName);
        }
        var state = Load();
        _dismissed = (state.Dismissed ?? []).Where(d => !string.IsNullOrWhiteSpace(d)).TakeLast(MaxDismissed).ToList();
        _applied = (state.Applied ?? []).Where(a => a is not null && !string.IsNullOrWhiteSpace(a.Id)).Take(MaxApplied).ToList();
    }

    /// <summary>What was applied, newest first.</summary>
    public IReadOnlyList<AppliedSuggestion> Applied { get { lock (_gate) return _applied.ToArray(); } }

    public IReadOnlyList<AdvisorSuggestion> Visible(IEnumerable<AdvisorSuggestion> suggestions)
    {
        lock (_gate) return suggestions.Where(s => !_dismissed.Contains(s.Id, StringComparer.Ordinal)).ToArray();
    }

    public void Dismiss(string id)
    {
        lock (_gate)
        {
            if (_dismissed.Contains(id, StringComparer.Ordinal)) return;
            _dismissed.Add(id);
            if (_dismissed.Count > MaxDismissed) _dismissed.RemoveRange(0, _dismissed.Count - MaxDismissed);
            Save();
        }
    }

    /// <summary>Writes the suggestion into the game's own rule: merged into its overrides when it has
    /// one, else a new rule in the mode the game already runs in, moved ahead of any broader rule that
    /// would otherwise keep claiming the game (the same plan as the UI's saveGameProfile). The rule is
    /// enabled — pressing Apply asks for it to be in force. Throws InvalidOperationException for a hint,
    /// and lets the store's AppRuleRejectedException through for a value it refuses.</summary>
    public AppliedSuggestion Apply(AdvisorSuggestion s, IAppRuleStore rules, DateTimeOffset now)
    {
        if (!s.Applicable) throw new InvalidOperationException("This suggestion is advice only; there is nothing to apply.");
        var game = AppRulePolicy.Normalize(s.Game);
        lock (_gate)
        {
            var own = rules.List().FirstOrDefault(r => r.Match == game);
            var o = own?.Overrides ?? new RuleOverrides();
            var patch = o with { StapmW = s.StapmW ?? o.StapmW, FrameCapFps = s.FrameCapFps ?? o.FrameCapFps };
            if (own is not null)
            {
                rules.Update(own.Id, own.Match, own.Mode, enabled: true, patch);
            }
            else
            {
                var governing = rules.RuleFor(game);
                var added = rules.Add(game, governing?.Mode ?? ModeCatalogue.Gaming, enabled: true, patch);
                if (governing is not null)
                {
                    var list = rules.List().ToList();
                    var from = list.FindIndex(r => r.Id == added.Id);
                    var to = list.FindIndex(r => r.Id == governing.Id);
                    if (from > to) rules.Move(added.Id, to - from);
                }
            }

            var entry = new AppliedSuggestion(s.Id, game, s.Kind, s.StapmW, s.FrameCapFps, now);
            _applied.Insert(0, entry);
            if (_applied.Count > MaxApplied) _applied.RemoveRange(MaxApplied, _applied.Count - MaxApplied);
            Save();
            return entry;
        }
    }

    private State Load()
    {
        if (_filePath is null || !File.Exists(_filePath)) return new(null, null);
        try { return JsonSerializer.Deserialize<State>(File.ReadAllText(_filePath), Json) ?? new(null, null); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            var corrupt = _filePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            try { File.Move(_filePath, corrupt); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return new(null, null);
        }
    }

    private void Save()
    {
        if (_filePath is null) return;
        try { AtomicFile.Write(_filePath, JsonSerializer.Serialize(new State(_dismissed, _applied), Json)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>The body of GET /advisor/suggestions (and of apply / dismiss, which answer with the new
/// state). <see cref="Game"/> is null when no game is in front and none was asked about.</summary>
public sealed record AdvisorView(
    string? Game,
    bool Live,
    int? RefreshHz,
    double? LearnedCeilingW,
    IReadOnlyList<AdvisorSuggestion> Suggestions,
    IReadOnlyList<AppliedSuggestion> Applied);
