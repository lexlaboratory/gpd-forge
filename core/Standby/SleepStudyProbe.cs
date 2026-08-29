// GPD Forge - runs powercfg /sleepstudy and parses the result. GPL-3.0-or-later.
//
// Kept apart from SleepStudyParser so the parsing stays pure and unit-testable: everything here
// touches the filesystem and a child process, and none of it can be exercised without them.
using GpdForge.Tdp;

namespace GpdForge.Standby;

/// <summary>A sleep study attempt, and — when it failed — why. Never throws at the caller.</summary>
public sealed record SleepStudyOutcome(bool Available, string? Error, SleepStudyReport? Report);

public sealed class SleepStudyProbe(IProcessRunner runner, string? tempDirectory = null)
{
    private readonly string _temp = tempDirectory ?? Path.GetTempPath();

    /// <summary>
    /// Generates a report over the last <paramref name="days"/> days and parses it.
    ///
    /// Unlike /requests and /lastwake this genuinely requires elevation — it is refused outright in
    /// a user session. The daemon runs as LocalSystem so it is available there, and the CLI probe
    /// simply reports that it could not look rather than pretending the machine slept well.
    /// </summary>
    public async Task<SleepStudyOutcome> RunAsync(int days, CancellationToken ct)
    {
        var path = Path.Combine(_temp, $"gpdforge-sleepstudy-{Guid.NewGuid():N}.html");
        try
        {
            await runner.RunAsync("powercfg", $"/sleepstudy /output \"{path}\" /duration {days}", ct);

            // powercfg reports refusal on stderr and still exits cleanly, so the file's absence is
            // the reliable signal, not the exit code or the captured stdout.
            if (!File.Exists(path))
                return new(false, "powercfg produced no sleep study report — /sleepstudy requires an elevated session.", null);

            var html = await File.ReadAllTextAsync(path, ct);
            return new(true, null, SleepStudyParser.Parse(html));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new(false, $"sleep study could not be read: {ex.Message}", null);
        }
        finally
        {
            // An 9 MB report per poll is not worth keeping; losing the delete is not worth failing over.
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
        }
    }
}
