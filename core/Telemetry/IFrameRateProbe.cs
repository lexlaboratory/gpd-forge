// GPD Forge — optional frame-rate telemetry. GPL-3.0-or-later.
//
// READ-ONLY. Frame timing is an ETW capability (Present events), unrelated to the MSR/EC access the
// hardware sensors need, so it sits behind its own gate (GPDFORGE_ENABLE_FPS=1, see Program.cs) and
// a failure here can never drag the hardware path down with it.
//
// Deliberately NOT named IFpsSource: GpdForge.Tdp.IFpsSource already exists and flows the other way
// (it reads FPS back out of a snapshot to feed the PID controller). This one is upstream of the
// snapshot. Same defensive contract as IHardwareSensors: no reading means TryRead returns false and
// the caller leaves FPS at 0 — we report "not available", never a guess.
namespace GpdForge.Telemetry;

/// <summary>
/// A frame-rate reading over a short trailing window.
/// <paramref name="Fps"/> is the mean; <paramref name="Fps1PctLow"/> is the 1% low (the mean of the
/// slowest 1% of frames, expressed as FPS) — the number that actually tracks perceived stutter.
/// </summary>
public readonly record struct FpsSample(double Fps, double Fps1PctLow, string? Process);

public interface IFrameRateProbe : IDisposable
{
    bool TryRead(out FpsSample sample);
}
