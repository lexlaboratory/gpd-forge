// GPD Forge — the one place that decides where persistent state lives. GPL-3.0-or-later.
//
// Four call sites used to compute %ProgramData%\GPD Forge independently (AlertStore, SessionStore,
// AppRuleStore, VramHistory). That was fine until something needed to run the daemon WITHOUT
// touching the installed service's state — and on 2026-08-31 it turned out something already did:
// ApiStartupTests starts the real daemon as a process, and it had been reading and writing the live
// alert store, session store and audit state of the machine running the tests. The suite was
// mutating the user's data on every run, and the alerts it validated were whatever the installed
// service happened to have recorded that day.
//
// That second half is the subtler damage. A test whose input is the machine's history passes or
// fails for reasons nobody chose: on this device /alerts had 13 real entries and the shape checks
// ran; on a clean CI runner the array is empty, every item check is skipped, and the guard reports
// success having verified nothing.
//
// GPDFORGE_DATA_DIR is therefore a production feature with a test as its first user, not a test
// hook bolted onto production: a service that cannot be told where to keep its state cannot be run
// twice on one machine, cannot be exercised without side effects, and cannot be backed up
// selectively.
namespace GpdForge.SystemControl;

public static class DataRoot
{
    /// <summary>Set this to run the daemon against isolated state. Absolute paths only.</summary>
    public const string OverrideVariable = "GPDFORGE_DATA_DIR";

    /// <summary>
    /// Where alerts, sessions, app rules and VRAM history are kept. Honours
    /// <c>GPDFORGE_DATA_DIR</c> when it names an absolute path; otherwise %ProgramData%\GPD Forge.
    /// </summary>
    public static string Current
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable(OverrideVariable);

            // A relative path is rejected rather than resolved against the current directory. The
            // service's working directory is not something a caller controls or can predict, so
            // honouring a relative path would scatter state somewhere nobody could find it — and
            // silently ignoring it would send writes to the live store while the caller believed
            // they were isolated. Refusing is the only option that cannot mislead.
            if (!string.IsNullOrWhiteSpace(custom))
            {
                if (!Path.IsPathFullyQualified(custom))
                    throw new InvalidOperationException(
                        $"{OverrideVariable} must be an absolute path; got '{custom}'. A relative path " +
                        "would resolve against the service's working directory, which the caller does " +
                        "not control.");

                return custom;
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "GPD Forge");
        }
    }
}
