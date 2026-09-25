// GPD Forge — TDP value types. GPL-3.0-or-later.
namespace GpdForge.Tdp;

/// <summary>A requested sustained power profile (milliwatts as whole watts, °C for tctl).</summary>
public readonly record struct TdpProfile(int StapmW, int FastW, int SlowW, int TctlC);

/// <summary>
/// What the PM table actually reported. Both fields are NULLABLE, and that is the point.
///
/// Until 2026-09-02 they were plain ints and <c>RyzenAdjOutput.Parse</c> ended each with <c>?? 0</c>,
/// so a `ryzenadj --info` run that did not contain a "STAPM LIMIT" line — a failed read, a changed
/// output format, a tool that is not there — produced a confident <c>0 W</c>. Zero watts is not a
/// reading a CPU can give; it is the absence of one wearing a number.
///
/// It mattered downstream: <c>Holds</c> compares this against what was requested to decide
/// <c>verified</c>, and a failed read is not the same fact as a firmware that refused the write.
///
/// <see cref="PptSlowW"/> and <see cref="TctlC"/> were added in audit round 2 (2026-09-24). The 30 s
/// reassert judged "still holding" by STAPM and fast alone, so a firmware that put back only its own
/// slow limit or Tctl was never corrected. They are optional and null by default: a PM table without
/// those rows, and every older two-limit construction, means "not measured", never "moved".
/// </summary>
public readonly record struct TdpReadout(int? StapmW, int? PptW, int? PptSlowW = null, int? TctlC = null);

/// <summary>Outcome of a closed-loop apply: what was requested, what held, and whether it stuck.</summary>
public readonly record struct TdpApplyResult(TdpProfile Requested, TdpReadout Observed, bool Verified, int Attempts);
