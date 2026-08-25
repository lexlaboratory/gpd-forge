// GPD Forge — TDP value types. GPL-3.0-or-later.
namespace GpdForge.Tdp;

/// <summary>A requested sustained power profile (milliwatts as whole watts, °C for tctl).</summary>
public readonly record struct TdpProfile(int StapmW, int FastW, int SlowW, int TctlC);

/// <summary>What the PM table actually reports after an apply.</summary>
public readonly record struct TdpReadout(int StapmW, int PptW);

/// <summary>Outcome of a closed-loop apply: what was requested, what held, and whether it stuck.</summary>
public readonly record struct TdpApplyResult(TdpProfile Requested, TdpReadout Observed, bool Verified, int Attempts);
