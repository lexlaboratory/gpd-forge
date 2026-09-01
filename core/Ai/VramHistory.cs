// GPD Forge — remembering the UMA/VRAM split so a BIOS change can be CONFIRMED. GPL-3.0-or-later.
//
// The VRAM item used to end at advice: "change it in BIOS, it takes a reboot". That fails this
// repo's standard, which is that a setting is not applied until something provokes it and reads back
// the verdict. We cannot provoke this one — the write lives in firmware and any user-mode shortcut
// risks a black screen (see VramAdvisor.cs) — but we CAN read the verdict, if we remember what the
// split was before. So this stores one row: the reported MB, when it was first and last seen, and
// the boot it was seen under. After the user edits the BIOS and reboots, the next observation either
// differs (the edit took effect, and we say so, naming both values and the reboot) or does not (the
// edit did not take, which is exactly the failure a user would otherwise never be told about) —
// EXCEPT where the reading's 32-bit field makes the comparison meaningless, in which case we say that
// instead. A verdict this file cannot support is not softened, it is withheld.
//
// Design decisions worth defending:
//
//   * The BOOT IDENTITY is what turns a diff into evidence. A different value under the same boot is
//     NOT a confirmed BIOS change — UMA cannot be reassigned live, so that reading means the reader
//     changed its mind (a driver reload, a different controller enumerated first), and it is reported
//     as such instead of being dressed up as a successful firmware edit.
//   * Boot time comes from WMI LastBootUpTime, not from Environment.TickCount64. Deriving boot time
//     as "now minus tick count" was rejected: whether that counter includes suspended time is exactly
//     the kind of thing this handheld's Modern Standby behaviour makes unreliable (see
//     core/Standby/StandbyDrain.cs), and a drifting boot time would manufacture phantom reboots and
//     therefore phantom "confirmed" changes. If WMI cannot answer, the boot instant is NULL and every
//     verdict downgrades to "reboot could not be confirmed" rather than inventing one.
//   * A ±2 minute tolerance compares boot instants, SYMMETRICALLY. "Reboot" is a forward move past
//     the tolerance, "same boot" is |delta| within it, and anything else — including a boot instant
//     that moved BACKWARDS because NTP corrected RTC drift after the reboot — is neither. Defining
//     same-boot as merely "not a reboot" let a genuine post-reboot change be described as same-boot.
//   * NOTHING is recorded when the live read is unavailable. Overwriting a good history row with a
//     failed read would destroy the only baseline we have and then report "changed" on recovery.
//   * UNKNOWN STAYS UNKNOWN across ticks. If the stored row has no boot instant (WMI was down when it
//     was written) we do NOT stamp the current boot onto it when the value is unchanged: that would
//     silently upgrade "we never knew which boot this was seen under" into "seen under this boot",
//     and the verdict would then claim there has been no reboot since. The row keeps its null until
//     the value actually changes and a fresh row is written.
//   * The uint32 ceiling can VETO a verdict, not just annotate it. AdapterRAM saturates near 4 GiB
//     and wraps above it (VramAdvisor), so when either end of a comparison sits at the ceiling the
//     difference between the two numbers is not a measurement of the split: no "CONFIRMED ... across
//     the reboot" is emitted (it could otherwise report a 4 GB -> 8 GB edit as a DECREASE), and no
//     "it did NOT take effect" is emitted either (the same ceiling makes success invisible). Both
//     become an explicit "cannot be determined from this reading".
//   * Persistence copies core/Alerts/AlertStore.cs and core/Sessions/SessionStore.cs exactly: one
//     small indented JSON file under %ProgramData%\GPD Forge\, atomic temp-file replace on write,
//     and a corrupt file quarantined rather than taken as a reason to crash the daemon. It is a
//     single row, so no retention bounds are needed — the file cannot grow.
//   * A FAILED write is reported, never papered over. If %ProgramData%\GPD Forge\ is not writable by
//     the service account, the store throws, the daemon keeps serving — and the verdict says the
//     baseline could not be recorded, instead of promising a future detection that can never happen.
//   * Read and Write agree on what a valid row is (IsUsableBaseline). They did not: Write accepted a
//     row Read would reject, so a sub-megabyte reading caused a serialise + temp file + File.Replace
//     on every single call, forever, with no baseline ever readable.
//   * Writes are throttled. GET /ai calls Observe on every request (core/Program.cs); rewriting the
//     file each time would be a pointless write storm on a handheld's system drive, so we persist
//     only when something meaningful moved (a new value, a new boot, a new adapter) or the last-seen
//     stamp has gone stale by an hour.
using System.Globalization;
using System.Management;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace GpdForge.Ai;

/// <summary>What the persisted history says about the CURRENT reading.</summary>
public enum VramChangeKind
{
    /// <summary>The live read failed, so nothing was compared and nothing was recorded.</summary>
    NotObserved,

    /// <summary>Nothing was stored before this — a baseline was just written.</summary>
    FirstObservation,

    /// <summary>Same value, and no reboot was proven between the two readings — either because the
    /// boot instants match, or because one of them was never known. The summary distinguishes those
    /// two; the second one proves nothing about a BIOS edit.</summary>
    Unchanged,

    /// <summary>Same value across a reboot: a BIOS edit, if one was attempted, did NOT take — UNLESS
    /// the reading is at the AdapterRAM ceiling, where a successful edit is invisible and the summary
    /// withholds the failure claim.</summary>
    UnchangedAcrossReboot,

    /// <summary>Different value across a reboot. The BIOS edit is confirmed only when neither reading
    /// is at the AdapterRAM ceiling; at the ceiling the numeric difference is a field artefact, and
    /// the summary says so instead of confirming a change.</summary>
    ChangedAcrossReboot,

    /// <summary>Different value within one boot — impossible for a real UMA split; a reporting change.</summary>
    ChangedSameBoot,

    /// <summary>Different value, but the boot instant is unknown, so it cannot be attributed.</summary>
    ChangedRebootUnknown,
}

/// <summary>The one persisted row. Nullable fields are null when genuinely unknown, never defaulted.</summary>
public sealed record VramObservation(
    double ReportedMb,
    string? AdapterName,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc,
    DateTimeOffset? BootUtc,
    double? PreviousMb = null,
    DateTimeOffset? PreviousSeenUtc = null,
    DateTimeOffset? PreviousBootUtc = null);

/// <summary>The verdict handed to the API. <paramref name="SinceUtc"/> is when the CURRENT value was
/// first seen; <paramref name="RebootConfirmed"/> is only true when both boot instants were known and
/// actually differ. It says a REBOOT was confirmed — not that a change in the split was: at the
/// AdapterRAM ceiling the two readings cannot be compared at all, and only <paramref name="Summary"/>
/// carries that. Read the summary, not the numbers, before telling a user their edit worked.</summary>
public sealed record VramHistoryReport(
    VramChangeKind Kind,
    string Summary,
    double? PreviousMb,
    DateTimeOffset? SinceUtc,
    DateTimeOffset? BootUtc,
    bool RebootConfirmed);

/// <summary>The machine's boot instant and wall clock. Injected so tests never touch WMI.</summary>
public interface IBootClock
{
    DateTimeOffset UtcNow { get; }

    /// <summary>When this Windows session booted, or null if it could not be established.</summary>
    DateTimeOffset? BootUtc { get; }
}

/// <summary>Real clock: WMI <c>Win32_OperatingSystem.LastBootUpTime</c>, read once and cached — it
/// cannot change while the process lives, and re-querying WMI on every /ai request would be waste.
/// Any failure yields null, which every consumer treats as "cannot confirm a reboot".</summary>
public sealed class WmiBootClock(ILogger<WmiBootClock>? logger = null) : IBootClock
{
    private readonly Lazy<DateTimeOffset?> _boot = new(() => ReadBootTime(logger), isThreadSafe: true);

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public DateTimeOffset? BootUtc => _boot.Value;

    private static DateTimeOffset? ReadBootTime(ILogger? logger)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT LastBootUpTime FROM Win32_OperatingSystem");
            foreach (var mo in searcher.Get())
            {
                using (mo)
                {
                    if (mo["LastBootUpTime"] is not string raw || string.IsNullOrWhiteSpace(raw)) continue;
                    var local = ManagementDateTimeConverter.ToDateTime(raw);
                    return new DateTimeOffset(local).ToUniversalTime();
                }
            }
        }
        catch (Exception ex) { logger?.LogDebug(ex, "Boot time unavailable (Win32_OperatingSystem.LastBootUpTime)"); }
        return null;
    }
}

/// <summary>Where the single history row lives. An interface so tests use an in-memory fake and never
/// touch %ProgramData%.</summary>
public interface IVramHistoryStore
{
    VramObservation? Read();

    void Write(VramObservation observation);
}

/// <summary>%ProgramData%\GPD Forge\vram-history.json — same file discipline as AlertStore/SessionStore.</summary>
public sealed class FileVramHistoryStore : IVramHistoryStore
{
    private readonly Lock _gate = new();
    private readonly string _filePath;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    /// <summary>The same root AlertStore and SessionStore use — see <see cref="SystemControl.DataRoot"/>,
    /// which is the single place that honours GPDFORGE_DATA_DIR.</summary>
    public static string DefaultDirectory => SystemControl.DataRoot.Current;

    public FileVramHistoryStore() : this(DefaultDirectory) { }

    public FileVramHistoryStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        // A directory we cannot create is a reason for Write to fail and SAY so, not a reason to take
        // the daemon down at startup. The failure surfaces in the verdict (see VramHistory.Observe).
        try { Directory.CreateDirectory(directory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        _filePath = Path.Combine(directory, "vram-history.json");
    }

    public VramObservation? Read()
    {
        lock (_gate)
        {
            if (!File.Exists(_filePath)) return null;
            try
            {
                var row = JsonSerializer.Deserialize<VramObservation>(File.ReadAllText(_filePath), _json);
                // A row with no usable reading is not a baseline; treating it as one would compare
                // the next real reading against zero and announce a "change" that never happened.
                // Write applies the SAME predicate, so a row can never be written and then refused.
                return row is not null && VramHistory.IsUsableBaseline(row) ? row : null;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                var corrupt = _filePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
                try { File.Move(_filePath, corrupt); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                return null;
            }
        }
    }

    public void Write(VramObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        // Refuse — before touching the disk — exactly what Read refuses. Accepting it produced a
        // serialise + temp file + File.Replace on every call while no baseline was ever readable.
        if (!VramHistory.IsUsableBaseline(observation))
            throw new ArgumentOutOfRangeException(nameof(observation), observation.ReportedMb,
                "A VRAM observation below 1 MB is not a baseline and would never be read back.");
        lock (_gate)
        {
            var temp = _filePath + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, JsonSerializer.Serialize(observation, _json));
            try { if (File.Exists(_filePath)) File.Replace(temp, _filePath, null); else File.Move(temp, _filePath); }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } }
        }
    }
}

/// <summary>
/// Compares the live reading against the persisted one and produces a verdict a user can act on.
/// The comparison logic is pure and lives in static methods so it is unit-tested without a clock,
/// a file, or WMI.
/// </summary>
public sealed class VramHistory(IVramHistoryStore store, IBootClock clock, ILogger<VramHistory>? logger = null)
{
    /// <summary>See the header: absorbs converter/format noise in the boot instant without being long
    /// enough to swallow a real reboot.</summary>
    public static readonly TimeSpan BootTolerance = TimeSpan.FromMinutes(2);

    /// <summary>How stale the last-seen stamp may get before an otherwise unchanged reading is
    /// rewritten. Keeps "unchanged since" honest without writing on every request.</summary>
    public static readonly TimeSpan LastSeenRefresh = TimeSpan.FromHours(1);

    private readonly Lock _gate = new();

    /// <summary>Records one observation of the live reading and returns what it proves. Never throws
    /// out to the API: a store that cannot be read or written degrades to a verdict that says so.</summary>
    public VramHistoryReport Observe(VramInfo live)
    {
        lock (_gate)
        {
            var now = clock.UtcNow;
            var boot = clock.BootUtc;

            if (!live.Available)
                return new VramHistoryReport(VramChangeKind.NotObserved,
                    "No live VRAM reading on this tick, so nothing was compared and the stored history was left untouched.",
                    null, null, boot, false);

            VramObservation? previous;
            try { previous = store.Read(); }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "VRAM history unreadable");
                return new VramHistoryReport(VramChangeKind.NotObserved,
                    "The VRAM history file could not be read, so this reading could not be compared against a previous one.",
                    null, null, boot, false);
            }

            var kind = Classify(previous, live.ReportedMb, boot);
            var next = Next(previous, live, now, boot, kind);

            // A write that THREW is not a write. The verdict below has to know, because the whole
            // value of a baseline is the promise that a later change will be noticed — and that
            // promise is empty if nothing reached the disk.
            string? persistError = null;
            if (ShouldPersist(previous, next, kind, now))
            {
                try { store.Write(next); }
                catch (Exception ex)
                {
                    logger?.LogDebug(ex, "VRAM history could not be persisted");
                    persistError = ex.Message;
                }
            }

            bool rebootConfirmed = kind is VramChangeKind.ChangedAcrossReboot or VramChangeKind.UnchangedAcrossReboot;
            return new VramHistoryReport(kind, Describe(kind, previous, next, live, boot, persistError),
                next.PreviousMb, next.FirstSeenUtc, boot, rebootConfirmed);
        }
    }

    /// <summary>The single definition of "this row is a baseline", shared by
    /// <see cref="FileVramHistoryStore"/>'s reader and its writer so they cannot disagree. Anything
    /// that rounds below one megabyte is a reporting artefact, not a UMA split.</summary>
    public static bool IsUsableBaseline(VramObservation row) =>
        row is not null && row.ReportedMb >= 1.0;

    /// <summary>Two readings are the same split when their whole-megabyte values match. AdapterRAM is
    /// integral; a sub-megabyte difference would be reader noise, not a firmware change.</summary>
    public static bool SameSize(double a, double b) => Math.Abs(Math.Round(a) - Math.Round(b)) < 1.0;

    /// <summary>Only a boot instant that is known on BOTH sides and moved forward past the tolerance
    /// counts as a reboot. Unknown on either side means unknown, never "probably".</summary>
    public static bool IsReboot(DateTimeOffset? stored, DateTimeOffset? current) =>
        stored is DateTimeOffset s && current is DateTimeOffset c && (c - s) > BootTolerance;

    /// <summary>Positive evidence that both readings happened in ONE boot: both instants known and
    /// within the tolerance of each other, in either direction. Deliberately NOT the negation of
    /// <see cref="IsReboot"/> — a boot instant that moved backwards (NTP correcting RTC drift after
    /// the reboot) is not a reboot by that test and is not the same boot either. It is unknown, and
    /// claiming "within the SAME boot" there described a real post-reboot change as impossible.</summary>
    public static bool IsSameBoot(DateTimeOffset? stored, DateTimeOffset? current) =>
        stored is DateTimeOffset s && current is DateTimeOffset c &&
        (c - s).Duration() <= BootTolerance;

    public static VramChangeKind Classify(VramObservation? previous, double liveMb, DateTimeOffset? boot)
    {
        if (previous is null) return VramChangeKind.FirstObservation;
        bool same = SameSize(previous.ReportedMb, liveMb);
        if (IsReboot(previous.BootUtc, boot))
            return same ? VramChangeKind.UnchangedAcrossReboot : VramChangeKind.ChangedAcrossReboot;
        if (same) return VramChangeKind.Unchanged;
        // Different value, no confirmed reboot: either we have positive evidence of one boot, or we
        // have nothing. Those are genuinely different claims, so they get different kinds.
        return IsSameBoot(previous.BootUtc, boot)
            ? VramChangeKind.ChangedSameBoot
            : VramChangeKind.ChangedRebootUnknown;
    }

    /// <summary>True when a comparison endpoint sits at the uint32 ceiling, which makes the numeric
    /// difference between the two readings meaningless (see the header and VramAdvisor).</summary>
    private static bool AtCeiling(double? mb) =>
        mb is double v && VramAdvisor.IsAtReportingCeilingMb(v);

    private static VramObservation Next(VramObservation? previous, VramInfo live, DateTimeOffset now, DateTimeOffset? boot, VramChangeKind kind)
    {
        // An unchanged value keeps its original FirstSeenUtc — that timestamp is the "since <when>"
        // the user reads, and resetting it every tick would make the split look brand new forever.
        if (previous is not null && kind is VramChangeKind.Unchanged or VramChangeKind.UnchangedAcrossReboot)
            return previous with
            {
                AdapterName = live.AdapterName ?? previous.AdapterName,
                LastSeenUtc = now,
                // Only refresh a boot instant the row ALREADY had. Writing the current boot onto a
                // row stored with a null one would record the baseline as if it had been seen under
                // this boot, and the next verdict would then assert "no reboot since" on evidence
                // that never existed. Unknown stays unknown until a real change writes a fresh row.
                BootUtc = previous.BootUtc is null ? null : boot ?? previous.BootUtc,
            };

        return new VramObservation(
            live.ReportedMb, live.AdapterName, now, now, boot,
            PreviousMb: previous?.ReportedMb,
            PreviousSeenUtc: previous?.LastSeenUtc,
            PreviousBootUtc: previous?.BootUtc);
    }

    private static bool ShouldPersist(VramObservation? previous, VramObservation next, VramChangeKind kind, DateTimeOffset now)
    {
        if (previous is null) return true;
        if (kind is not (VramChangeKind.Unchanged or VramChangeKind.UnchangedAcrossReboot)) return true;
        if (!string.Equals(previous.AdapterName, next.AdapterName, StringComparison.Ordinal)) return true;
        if (previous.BootUtc != next.BootUtc) return true;
        return now - previous.LastSeenUtc >= LastSeenRefresh;
    }

    private static string Describe(VramChangeKind kind, VramObservation? previous, VramObservation next,
        VramInfo live, DateTimeOffset? boot, string? persistError)
    {
        string mb = Mb(next.ReportedMb);
        string since = Stamp(next.FirstSeenUtc);
        string bootAt = boot is DateTimeOffset b ? Stamp(b) : next.BootUtc is DateTimeOffset nb ? Stamp(nb) : "an unknown time";
        // Either endpoint pinned at the uint32 ceiling means the two numbers cannot be subtracted for
        // meaning: the true split behind a 4095 MB reading is "4 GB or more", and a bigger split can
        // wrap to a smaller number. So no confirmed delta, and no "the edit failed" either.
        bool ceilingBlind = live.AtReportingCeiling || AtCeiling(previous?.ReportedMb) || AtCeiling(next.ReportedMb);
        string body = kind switch
        {
            VramChangeKind.FirstObservation when persistError is not null =>
                $"A baseline of {mb} was read but could NOT be recorded ({persistError}). Until the history file under " +
                "%ProgramData%\\GPD Forge\\ can be written by the service account, a later change to the UMA split " +
                "cannot be detected here.",
            VramChangeKind.FirstObservation =>
                $"Baseline recorded: {mb} at {since}. A later change to the UMA split will be detected and reported here.",
            // "No reboot since" is a claim about the STORED row's boot instant. When that is null, or
            // the current one is, or the two are not within tolerance of each other, we did not
            // compare anything and must not imply that we did.
            VramChangeKind.Unchanged when IsSameBoot(previous?.BootUtc, boot) =>
                $"Unchanged at {mb} since {since} (no reboot since that observation).",
            VramChangeKind.Unchanged =>
                $"Unchanged at {mb} since {since}. Whether this machine has rebooted since that observation could not be " +
                "established (no usable boot time was recorded with it), so this says nothing about whether a BIOS edit took.",
            VramChangeKind.UnchangedAcrossReboot when ceilingBlind =>
                $"Still reading {mb} after the reboot at {bootAt}, but that value is at the 32-bit AdapterRAM ceiling, " +
                "which cannot tell a 4 GB split from an 8 GB or 16 GB one. Whether a BIOS edit took effect CANNOT be " +
                "determined from this reading — an edit that worked would look exactly like this.",
            VramChangeKind.UnchangedAcrossReboot =>
                $"Still {mb} after the reboot at {bootAt} — unchanged since {since}. If you edited the UMA split in BIOS, " +
                "it did NOT take effect.",
            VramChangeKind.ChangedAcrossReboot when ceilingBlind =>
                $"The reported allocation went from {Mb(previous?.ReportedMb)} to {mb} across the reboot at {bootAt}, but " +
                "at least one of those readings is at the 32-bit AdapterRAM ceiling, where the field saturates and a " +
                "larger split wraps to a smaller number. That difference is NOT a measurement of the UMA split and is " +
                "neither a confirmed increase nor a confirmed decrease.",
            VramChangeKind.ChangedAcrossReboot =>
                $"CONFIRMED: changed from {Mb(previous?.ReportedMb)} to {mb} across the reboot at {bootAt}. " +
                "The BIOS UMA change is applied and read back from the running machine.",
            VramChangeKind.ChangedSameBoot =>
                $"Reported allocation moved from {Mb(previous?.ReportedMb)} to {mb} within the SAME boot ({bootAt}). " +
                "A UMA split cannot change without a reboot, so this is a change in what the graphics stack reports " +
                "(driver reload, or a different controller enumerated first) — not a confirmed BIOS edit.",
            VramChangeKind.ChangedRebootUnknown =>
                $"Changed from {Mb(previous?.ReportedMb)} to {mb} at {since}, but this machine's boot time could not be " +
                "read, so the change cannot be attributed to a reboot.",
            _ => "Nothing was compared.",
        };
        // A failed write on any other verdict does not invalidate THIS comparison, but it does mean
        // the next one will be made against a stale row, so it is stated rather than swallowed.
        if (persistError is not null && kind is not VramChangeKind.FirstObservation)
            body += $" (This observation could not be saved: {persistError}. The next comparison will still use the " +
                "previously stored row.)";
        // The ceiling caveat is appended to the VERDICT as well as to the live reading: near 4 GiB an
        // "unchanged" conclusion is exactly the one most likely to be wrong.
        return live.AtReportingCeiling ? body + " " + VramAdvisor.CeilingCaveat : body;
    }

    private static string Mb(double? value) =>
        value is double v ? v.ToString("0", CultureInfo.InvariantCulture) + " MB" : "an unrecorded value";

    private static string Stamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
}
