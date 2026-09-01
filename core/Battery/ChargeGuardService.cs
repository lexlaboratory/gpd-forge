// GPD Forge — the charge guard's live state and persistence. GPL-3.0-or-later.
//
// Shaped like GuardianService on purpose: Observe(snapshot) each tick returns a decision, and
// ForgeWorker applies or clears it. That pattern already solves the hard half of this feature —
// applying a temporary ceiling and reliably taking it off again — and using a second shape for the
// same problem would mean two places to get disengagement wrong.
using System.Text.Json;
using GpdForge.Telemetry;

namespace GpdForge.Battery;

public interface IChargeGuardStore
{
    ChargeGuardState Read();
    void Write(ChargeGuardState state);
}

/// <summary>
/// Persists the counters. Same file discipline as the other stores here: atomic replace, and a
/// corrupt file is quarantined rather than deleted — hours accumulated over months are not
/// recoverable from anywhere else, and silently starting from zero would hide that they were lost.
/// </summary>
public sealed class FileChargeGuardStore : IChargeGuardStore
{
    private readonly Lock _gate = new();
    private readonly string _filePath;

    // Case-insensitive for the same reason FileBatteryHealthStore is: these options do not use
    // JsonSerializerDefaults.Web, so the file is written in PascalCase, and a later switch to Web
    // defaults would otherwise read every existing field as null and reset the counters to zero.
    private readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public FileChargeGuardStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        try { Directory.CreateDirectory(directory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        _filePath = Path.Combine(directory, "charge-guard.json");
    }

    public ChargeGuardState Read()
    {
        lock (_gate)
        {
            if (!File.Exists(_filePath)) return new ChargeGuardState(0, 0, null, false);
            try
            {
                return JsonSerializer.Deserialize<ChargeGuardState>(File.ReadAllText(_filePath), _json);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                var corrupt = _filePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
                try { File.Move(_filePath, corrupt); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                return new ChargeGuardState(0, 0, null, false);
            }
        }
    }

    public void Write(ChargeGuardState state)
    {
        lock (_gate)
        {
            var temp = _filePath + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, JsonSerializer.Serialize(state, _json));
            try { if (File.Exists(_filePath)) File.Replace(temp, _filePath, null); else File.Move(temp, _filePath); }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } }
        }
    }
}

public sealed class ChargeGuardService(IChargeGuardStore store)
{
    private readonly Lock _gate = new();
    private ChargeGuardState _state = store.Read();

    public ChargeGuardConfig Config { get; private set; } = new();

    public ChargeGuardState State { get { lock (_gate) return _state; } }

    /// <summary>Hours spent plugged in at a high state of charge, including the episode in progress.</summary>
    public double TotalHoursAtHighSoc(DateTimeOffset now)
    {
        lock (_gate)
        {
            var running = _state.EpisodeStartedUtc is { } started && now > started
                ? (now - started).TotalHours
                : 0;
            return Math.Round(_state.TotalHoursAtHighSoc + running, 2);
        }
    }

    public ChargeGuardConfig Configure(ChargeGuardConfig config)
    {
        lock (_gate)
        {
            Config = config with
            {
                // Clamped rather than rejected: these arrive from a UI slider, and a request that is
                // merely out of range should land somewhere sane rather than fail the call.
                HighSocPct = Math.Clamp(config.HighSocPct, 50, 100),
                AlertAfterHours = Math.Clamp(config.AlertAfterHours, 0.25, 72),
                // Never below the guardian's own floor: a "cooling" ceiling that starved the machine
                // harder than the thermal safety net would is not cooling, it is a stall.
                CoolToW = Math.Clamp(config.CoolToW, 8, 30),
            };
            return Config;
        }
    }

    /// <summary>Advances the guard one tick. Returns what ForgeWorker should do.</summary>
    public ChargeGuardDecision Observe(TelemetrySnapshot snapshot, DateTimeOffset now)
    {
        lock (_gate)
        {
            var (next, decision) = ChargeGuardPolicy.Observe(_state, Config, snapshot, now);

            // Persist only when the durable part changed — the episode start, the banked total or the
            // alerted flag. Writing on every tick would put a file write in a 1 Hz loop for a value
            // that changes a few times a day.
            var durableChanged = next.TotalHoursAtHighSoc != _state.TotalHoursAtHighSoc
                              || next.Episodes != _state.Episodes
                              || next.EpisodeStartedUtc != _state.EpisodeStartedUtc
                              || next.AlertedThisEpisode != _state.AlertedThisEpisode;

            _state = next;
            if (durableChanged)
            {
                try { store.Write(next); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A counter we cannot persist is a worse report next boot, not a reason to stop
                    // guarding — and certainly not a reason to take the daemon down.
                }
            }

            return decision;
        }
    }
}
