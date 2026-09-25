// GPD Forge — editable, persisted per-app profile rules. GPL-3.0-or-later.
//
// Backs the foreground matcher with a file the user owns (%ProgramData%\GPD Forge\app-rules.json)
// instead of a constant compiled into the daemon. Precedence is list order: the first ENABLED rule
// whose Match is a substring of the foreground process wins, so reordering is how ambiguity is
// resolved, and two rules can never claim the same process at once (Add/Update reject duplicates).
using System.Text.Json;

namespace GpdForge.Profiles;

public interface IAppRuleStore : IModeResolver
{
    /// <summary>Rules in precedence order.</summary>
    IReadOnlyList<AppRule> List();

    /// <summary>Appends a rule (lowest precedence). Throws <see cref="AppRuleRejectedException"/> if invalid.</summary>
    AppRule Add(string? match, string? mode, bool enabled = true, RuleOverrides? overrides = null);

    /// <summary>Replaces a rule in place, keeping its position AND its overrides. Throws if unknown or
    /// invalid. Callers that only know match/mode/enabled (the Profiles page's toggle) use this one.</summary>
    AppRule Update(Guid id, string? match, string? mode, bool enabled);

    /// <summary>As above, but also replaces the overrides (null clears them).</summary>
    AppRule Update(Guid id, string? match, string? mode, bool enabled, RuleOverrides? overrides);

    bool Delete(Guid id);

    /// <summary>Shifts a rule by <paramref name="delta"/> positions (negative = higher precedence).
    /// False when the id is unknown or the position did not actually change.</summary>
    bool Move(Guid id, int delta);

    /// <summary>The rule that claims this process, or null.</summary>
    AppRule? RuleFor(string? processName);

    /// <summary>What decided the mode on the most recent tick; null until the worker has run.</summary>
    AppRuleMatch? LastMatch { get; }

    /// <summary>Records the resolution for <see cref="LastMatch"/> and returns it.</summary>
    AppRuleMatch RecordMatch(string? processName, string mode, bool acConnected);
}

public sealed class AppRuleStore : IAppRuleStore
{
    private readonly object gate = new();
    private readonly string filePath;
    private readonly JsonSerializerOptions json = new() { WriteIndented = true };
    private List<AppRule> rules;
    private AppRuleMatch? lastMatch;

    /// <param name="seedDefaults">On a fresh install, start from the ruleset the daemon shipped
    /// with so turning rules into data does not change day-one behaviour.</param>
    public AppRuleStore(string directory, bool seedDefaults = true)
    {
        Directory.CreateDirectory(directory);
        filePath = Path.Combine(directory, "app-rules.json");
        rules = Load(seedDefaults, out var mustPersist);
        if (mustPersist) Persist();
    }

    public IReadOnlyList<AppRule> List() { lock (gate) return rules.ToArray(); }

    public AppRuleMatch? LastMatch { get { lock (gate) return lastMatch; } }

    public string? ModeFor(string? processName) => RuleFor(processName)?.Mode;

    public AppRule? RuleFor(string? processName)
    {
        lock (gate) return rules.FirstOrDefault(r => r.Enabled && AppRulePolicy.Matches(r.Match, processName));
    }

    public AppRule Add(string? match, string? mode, bool enabled = true, RuleOverrides? overrides = null)
    {
        lock (gate)
        {
            Guard(match, mode, overrides, excluding: null);
            var rule = new AppRule(Guid.NewGuid(), AppRulePolicy.Normalize(match), mode!, enabled,
                RuleOverridesPolicy.Normalize(overrides));
            rules = [.. rules, rule];
            Persist();
            return rule;
        }
    }

    public AppRule Update(Guid id, string? match, string? mode, bool enabled)
    {
        lock (gate)
        {
            var existing = rules.Find(r => r.Id == id) ?? throw new KeyNotFoundException($"No rule with id {id}.");
            return Replace(id, match, mode, enabled, existing.Overrides);
        }
    }

    public AppRule Update(Guid id, string? match, string? mode, bool enabled, RuleOverrides? overrides)
    {
        lock (gate) return Replace(id, match, mode, enabled, overrides);
    }

    // Caller holds the gate.
    private AppRule Replace(Guid id, string? match, string? mode, bool enabled, RuleOverrides? overrides)
    {
        var index = rules.FindIndex(r => r.Id == id);
        if (index < 0) throw new KeyNotFoundException($"No rule with id {id}.");
        Guard(match, mode, overrides, excluding: id);
        var rule = new AppRule(id, AppRulePolicy.Normalize(match), mode!, enabled, RuleOverridesPolicy.Normalize(overrides));
        rules = [.. rules];
        rules[index] = rule;
        Persist();
        return rule;
    }

    public bool Delete(Guid id)
    {
        lock (gate)
        {
            var next = rules.Where(r => r.Id != id).ToList();
            if (next.Count == rules.Count) return false;
            rules = next;
            Persist();
            return true;
        }
    }

    public bool Move(Guid id, int delta)
    {
        lock (gate)
        {
            var from = rules.FindIndex(r => r.Id == id);
            if (from < 0) return false;
            var to = Math.Clamp(from + delta, 0, rules.Count - 1);
            if (to == from) return false;
            var next = new List<AppRule>(rules);
            var rule = next[from];
            next.RemoveAt(from);
            next.Insert(to, rule);
            rules = next;
            Persist();
            return true;
        }
    }

    public AppRuleMatch RecordMatch(string? processName, string mode, bool acConnected)
    {
        lock (gate)
        {
            var rule = rules.FirstOrDefault(r => r.Enabled && AppRulePolicy.Matches(r.Match, processName));
            lastMatch = new AppRuleMatch(rule?.Id, rule?.Match, mode, processName, acConnected, DateTimeOffset.UtcNow);
            return lastMatch;
        }
    }

    private void Guard(string? match, string? mode, RuleOverrides? overrides, Guid? excluding)
    {
        // No paramName: the message is written for the user and gets shown verbatim by the API. An
        // override error carries its own code beside it (400 { error, code }); a plain rule error stays
        // the ArgumentException it always was and the API files it under `bad_rule`.
        var error = AppRulePolicy.Validate(match, mode, rules, excluding);
        if (error is not null) throw new ArgumentException(error);
        if (RuleOverridesPolicy.Validate(overrides) is OverrideError bad) throw new AppRuleRejectedException(bad.Code, bad.Message);
    }

    /// <summary>Flattens the shipped needle lists into ordered rules — same order, same matches.</summary>
    private static List<AppRule> Defaults() =>
        ModeRules.DefaultRuleSet
            .SelectMany(g => g.needles.Select(n => new AppRule(Guid.NewGuid(), AppRulePolicy.Normalize(n), g.mode, true)))
            .ToList();

    // Every field nullable: a field an older file lacks must read as "missing", never as a default that
    // means something (Enabled = false, StapmW = 0). Unknown fields — a newer build's, after a
    // downgrade — are ignored by the deserializer rather than quarantining the file.
    private sealed record RuleDto(Guid? Id, string? Match, string? Mode, bool? Enabled, OverridesDto? Overrides);

    private sealed record OverridesDto(int? StapmW, int? FrameCapFps, string? FanMode, GpuOverrides? Gpu, List<string?>? Freeze)
    {
        public RuleOverrides ToModel() =>
            new(StapmW, FrameCapFps, FanMode, Gpu, Freeze?.OfType<string>().ToArray());
    }

    private List<AppRule> Load(bool seedDefaults, out bool mustPersist)
    {
        mustPersist = true;
        if (!File.Exists(filePath)) return seedDefaults ? Defaults() : [];
        try
        {
            var raw = JsonSerializer.Deserialize<List<RuleDto?>>(File.ReadAllText(filePath), json) ?? [];
            var loaded = Normalize(raw, out var repaired);
            mustPersist = loaded.Count != raw.Count || repaired;
            return loaded;
        }
        catch
        {
            var corrupt = filePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            try { File.Move(filePath, corrupt); } catch { /* best effort: a lost quarantine copy must not block startup */ }
            return seedDefaults ? Defaults() : [];
        }
    }

    /// <summary>
    /// Drops entries the running matcher could not honour (blank match, unknown mode, a second rule
    /// for a process an earlier one already claims) and fills in what older files lack: a missing
    /// "Enabled" would otherwise deserialize as false and silently switch a rule off, and a missing
    /// id would leave the UI unable to address the row.
    /// <para>
    /// Overrides are REPAIRED rather than dropping the rule (RuleOverridesPolicy.Sanitize): one
    /// hand-edited watt figure out of band must not cost the user the rule, let alone the file.
    /// <paramref name="repaired"/> says whether anything changed, so the fix is written back once.
    /// </para>
    /// </summary>
    private static List<AppRule> Normalize(List<RuleDto?> raw, out bool repaired)
    {
        repaired = false;
        var result = new List<AppRule>();
        foreach (var dto in raw)
        {
            if (dto is null) continue;
            var match = AppRulePolicy.Normalize(dto.Match);
            if (AppRulePolicy.Validate(match, dto.Mode, result) is not null) continue;
            var id = dto.Id is null || dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id.Value;
            var asRead = dto.Overrides?.ToModel();
            var overrides = RuleOverridesPolicy.Sanitize(asRead);
            if (!Equals(overrides, asRead)) repaired = true;
            result.Add(new AppRule(id, match, dto.Mode!, dto.Enabled ?? true, overrides));
        }
        return result;
    }

    private void Persist()
    {
        var temp = filePath + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, JsonSerializer.Serialize(rules, json));
        try { if (File.Exists(filePath)) File.Replace(temp, filePath, null); else File.Move(temp, filePath); }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { /* temp cleanup is best effort */ } }
    }
}
