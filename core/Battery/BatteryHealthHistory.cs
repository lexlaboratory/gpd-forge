// GPD Forge — health over time, so degradation is a trend rather than a reading. GPL-3.0-or-later.
//
// A single health figure answers "how is the pack now". It cannot answer the question anyone
// actually has, which is "is it getting worse, and how fast" — and that is the question a charge
// guard would eventually have to be judged against.
//
// ONE SAMPLE PER DAY, deliberately. This pack loses single-digit percent over YEARS: the reference
// device is at 91.2 % after enough use to lose 3,881 mWh. Sampling more often would fill the file
// with measurement jitter and let a six-hour wobble render as a cliff. Daily is already finer than
// the phenomenon.
//
// File discipline copied from VramHistory: atomic replace, and a corrupt file is quarantined rather
// than deleted or silently treated as empty — losing years of samples to a half-written write would
// be irreversible, and pretending they were never there hides that it happened.
using System.Text.Json;
using GpdForge.Alerts;

namespace GpdForge.Battery;

public interface IBatteryHealthStore
{
    IReadOnlyList<BatteryHealthSample> Read();
    void Write(IReadOnlyList<BatteryHealthSample> samples);
}

public sealed class FileBatteryHealthStore : IBatteryHealthStore
{
    private readonly Lock _gate = new();
    private readonly string _filePath;
    // PropertyNameCaseInsensitive is not cosmetic here. This file accumulates for YEARS, and these
    // options do not use JsonSerializerDefaults.Web, so it is written in PascalCase. The day someone
    // switches to Web defaults — a one-line change that looks like tidying — every existing sample
    // would deserialise to nulls, the history would silently read as empty, and the trend a user had
    // been watching for two years would vanish with no error. Reading either casing costs nothing
    // and makes that change survivable.
    private readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public FileBatteryHealthStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        // A directory we cannot create is a reason for the history to be empty and say so, not a
        // reason to take the daemon down at startup.
        try { Directory.CreateDirectory(directory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        _filePath = Path.Combine(directory, "battery-health.json");
    }

    public IReadOnlyList<BatteryHealthSample> Read()
    {
        lock (_gate)
        {
            if (!File.Exists(_filePath)) return [];
            try
            {
                return JsonSerializer.Deserialize<List<BatteryHealthSample>>(File.ReadAllText(_filePath), _json)
                       ?? [];
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                var corrupt = _filePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
                try { File.Move(_filePath, corrupt); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                return [];
            }
        }
    }

    public void Write(IReadOnlyList<BatteryHealthSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        lock (_gate)
        {
            var temp = _filePath + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, JsonSerializer.Serialize(samples, _json));
            try { if (File.Exists(_filePath)) File.Replace(temp, _filePath, null); else File.Move(temp, _filePath); }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } }
        }
    }
}

/// <summary>
/// Records at most one sample per calendar day and answers the trend question.
/// The decision logic is pure and takes a clock, so it is tested without waiting a day.
/// </summary>
public sealed class BatteryHealthHistory(IBatteryHealthStore store, IAlertClock clock)
{
    /// <summary>Roughly three years of daily samples. Long enough that the trend covers the life of
    /// the machine, bounded so the file cannot grow without limit on a device that runs for years.</summary>
    public const int MaxSamples = 1100;

    /// <summary>
    /// Offers a reading to the history. Returns true if it was recorded.
    ///
    /// A reading with no health figure is REFUSED rather than stored as a null row: the trend is
    /// computed from the oldest and newest samples, and a null-health row at either end would make
    /// DegradationPoints return null — so storing them would let a run of failed reads silently
    /// disable the trend for as long as it lasted.
    /// </summary>
    public bool Observe(BatteryHealthReading reading)
    {
        if (reading.HealthPercent is null) return false;

        var now = clock.UtcNow;
        var samples = store.Read().ToList();

        // Compared by DATE, not by elapsed time. A 24-hour window would drift the sampling moment
        // later every day until it wrapped, producing two samples on one day and none on the next.
        if (samples.Count > 0 && samples.Max(s => s.AtUtc).UtcDateTime.Date == now.UtcDateTime.Date)
            return false;

        samples.Add(new BatteryHealthSample(now, reading.FullChargeMilliwattHours, reading.HealthPercent));

        // Trim the OLDEST, keeping the newest. The recent end is what a user is looking at; the
        // ancient end is what they have already seen.
        if (samples.Count > MaxSamples)
            samples = samples.OrderBy(s => s.AtUtc).TakeLast(MaxSamples).ToList();

        store.Write(samples);
        return true;
    }

    public IReadOnlyList<BatteryHealthSample> Samples() => store.Read();

    /// <summary>Percentage points lost between the oldest and newest sample, or null when there is
    /// not enough history to say. See <see cref="BatteryHealthMath.DegradationPoints"/>.</summary>
    public double? DegradationPoints() => BatteryHealthMath.DegradationPoints(store.Read());

    /// <summary>
    /// Why there is no trend yet, in words a user can act on — or null when there IS one.
    /// A card that just shows nothing is indistinguishable from a card that is broken.
    /// </summary>
    public string? TrendUnavailableReason()
    {
        var samples = store.Read();
        if (samples.Count == 0)
            return "No history yet. GPD Forge records one health sample per day; the trend appears after the second.";
        if (BatteryHealthMath.DegradationPoints(samples) is null)
            return $"Only {samples.Count} sample(s) so far, all from the same day. " +
                   "Degradation is measured across days because this pack loses single-digit percent over years.";
        return null;
    }
}
