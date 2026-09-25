// GPD Forge — read-only telemetry via WMI. GPL-3.0-or-later.
//
// NO kernel driver: this reads only what WMI exposes (battery, AC, discharge, CPU clock,
// ACPI thermal zone). Package power (RAPL), per-core temps, the effective CPU clock and fan RPM come
// from the optional PawnIO/LHM sensors, and FPS from the optional PresentMon probe. Each is injected
// only when its gate is on; whatever is absent is null and reported as "n/a" — never guessed.
//
// This is the HARDWARE READER, and since 2026-09-24 it has exactly one caller: TelemetrySampler.
// Everything else reads the sampler's cached reading (ITelemetrySource).
using GpdForge.Fan;
using Microsoft.Extensions.Logging;

namespace GpdForge.Telemetry;

public sealed class WmiTelemetryService(
    IHardwareSensors? sensors = null,
    IFanRpm? fanRpmSource = null,
    IFrameRateProbe? frameRateProbe = null,
    // Optional so every existing test that news this up directly keeps working — and when it is
    // absent TdpVerified is null, which is the correct answer for "nobody is tracking TDP here".
    GpdForge.Tdp.TdpState? tdpState = null,
    ILogger<WmiTelemetryService>? logger = null,
    IWmiTelemetryQueries? wmi = null,
    TimeProvider? time = null) : ITelemetryService
{
    /// <summary>
    /// How often the slow WMI classes are actually queried. Battery charge, the discharge rate and the
    /// ACPI thermal zone do not move meaningfully in a second, and each query is a round trip through
    /// the WMI provider host; in between, the last answer is served. The AC flag rides along with the
    /// battery query, so a plug-in is noticed within this window rather than within one tick.
    /// </summary>
    public static readonly TimeSpan SlowCadence = TimeSpan.FromSeconds(5);

    private readonly IWmiTelemetryQueries _wmi = wmi ?? new WmiTelemetryQueries(logger);
    private readonly Cadenced<WmiBatteryReading?> _battery = new(SlowCadence, time ?? TimeProvider.System);
    private readonly Cadenced<double?> _discharge = new(SlowCadence, time ?? TimeProvider.System);
    private readonly Cadenced<double?> _thermal = new(SlowCadence, time ?? TimeProvider.System);
    private readonly Win32ClockFilter _win32Clock = new();
    private readonly Lock _gate = new();

    public Task<TelemetrySnapshot> ReadAsync(CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(Read());
    }

    private TelemetrySnapshot Read()
    {
        // `?? 0` on every one of these until 2026-09-01. A failed read became a confident zero, and
        // a CPU reported at 0 °C is worse than no reading at all: the panel cannot tell it from cold,
        // and the guardian cannot tell it from safe.
        //
        // The charge percentage is nullable because the old fallback of 0 was the most dangerous zero
        // in this file: the guardian raises a CRITICAL battery alert below 8 %, so a failed WMI query
        // announced an emergency on a machine that might be at 90 %. acConnected stays a plain bool
        // and falls back to false — a deliberate asymmetry: every consumer treats "on battery" as the
        // more conservative state, so an unknown power source behaves cautiously.
        var battery = _battery.Get(_wmi.ReadBattery);
        int? batteryPct = battery?.Pct;
        bool acConnected = battery?.Ac ?? false;
        double? dischargeW = _discharge.Get(_wmi.ReadDischargeRateMw) is double mW
            ? Math.Round(mW / 1000.0, 1)
            : null;

        // Package power / GPU temp / fan RPM need a driver. Filled by the optional read-only LHM
        // sensors when hardware access is enabled; NULL otherwise, because WMI genuinely cannot
        // provide them and saying "0 W" would be inventing a measurement.
        double? cpuTempC = null, gpuTempC = null, packageW = null;
        int? fanRpm = null, cpuClockMhz = null;
        double? fps = null, fps1PctLow = null;

        // Fan duty is not measured anywhere yet — it was a `const int fanDutyPct = 0` presented as a
        // reading. Null until something actually reads the EC's duty register back.
        int? fanDutyPct = null;

        if (sensors is not null && sensors.TryRead(out var hw))
        {
            if (hw.PackageW > 0) packageW = hw.PackageW;
            if (hw.GpuTempC > 0) gpuTempC = hw.GpuTempC;
            if (hw.CpuTempC > 0) cpuTempC = hw.CpuTempC; // LHM per-core temp beats the ACPI zone
            if (hw.FanRpm > 0) fanRpm = hw.FanRpm;
            if (hw.CpuClockMhz is double clock && clock > 0)
                cpuClockMhz = (int)Math.Round(clock, MidpointRounding.AwayFromZero);
        }

        // The ACPI zone is only the fallback, so it is only queried when LHM had no temperature.
        cpuTempC ??= _thermal.Get(_wmi.ReadThermalZoneTenthsKelvin) is double tenthsK
            ? Math.Round(KelvinTenthsToCelsius(tenthsK), 1)
            : null;

        // Win32_Processor only when LHM has no clock — and even then through the filter, because on
        // this HX 370 it is the fixed 2000 MHz base clock (see CpuClock.cs). Read every tick: the
        // filter can only notice a live clock by seeing it move.
        cpuClockMhz ??= _win32Clock.Observe(_wmi.ReadProcessorClockMhz());

        // Real GPD fan RPM via the PawnIO EC read (LHM doesn't expose it). Read-only; only present
        // when hardware access is enabled. Wins over LHM's fan reading (which is 0 on these boards).
        if (fanRpmSource?.ReadRpm() is int rpm && rpm > 0) fanRpm = rpm;

        // Frame rate via the optional PresentMon probe. Null in BOTH the no-probe and the
        // probe-with-no-sample cases, and that is the honest reading rather than a shortcut: a probe
        // returning no sample does not distinguish "nothing is presenting frames" from "PresentMon
        // has not produced a window of data yet". Reporting 0.0 would assert the first when only the
        // second is known. A genuine 0.0 still arrives when the probe measures one.
        if (frameRateProbe is not null && frameRateProbe.TryRead(out var frames))
        {
            fps = frames.Fps;
            fps1PctLow = frames.Fps1PctLow;
        }

        // Was `TdpVerified: true` — a literal, on every snapshot, regardless of whether anything had
        // ever written a power limit or whether the write was confirmed. Now it reports what the last
        // write actually observed, and null when there has not been one.
        return new TelemetrySnapshot(
            cpuTempC, gpuTempC, packageW, cpuClockMhz, fanRpm, fanDutyPct,
            fps, fps1PctLow, batteryPct, dischargeW, acConnected,
            TdpVerified: tdpState?.Last?.Verified);
    }

    /// <summary>ACPI thermal zone reports tenths of a Kelvin. Pure + unit-tested.</summary>
    public static double KelvinTenthsToCelsius(double tenthsKelvin) => tenthsKelvin / 10.0 - 273.15;

    /// <summary>
    /// A value re-read at most once per interval. A failed read (null) is cached like any other
    /// answer: retrying a broken WMI class every tick is exactly the cost this exists to remove.
    ///
    /// Expiry is checked on BOTH clocks. The monotonic one keeps a wall-clock correction from
    /// pinning the cache; the wall one covers a suspend, which a monotonic counter may not count —
    /// and a battery figure from before the lid closed, served as current after it opens, is the
    /// exact error the standby drain measurement cannot survive.
    /// </summary>
    private sealed class Cadenced<T>(TimeSpan interval, TimeProvider time)
    {
        private T _value = default!;
        private long? _readAt;
        private DateTimeOffset _readAtWall;

        public T Get(Func<T> read)
        {
            var wallElapsed = time.GetUtcNow() - _readAtWall;
            if (_readAt is not long at || time.GetElapsedTime(at) >= interval
                || wallElapsed >= interval || wallElapsed < TimeSpan.Zero)
            {
                _value = read();
                _readAt = time.GetTimestamp();
                _readAtWall = time.GetUtcNow();
            }
            return _value;
        }
    }
}
