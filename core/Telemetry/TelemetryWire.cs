// GPD Forge — GET /telemetry's wire form. GPL-3.0-or-later.
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GpdForge.Telemetry;

/// <summary>
/// The snapshot exactly as it always serialised, plus two additive fields that only make sense now
/// that the endpoint serves a cached sample instead of reading hardware on request:
/// <c>sampledAtMs</c> (Unix ms, when the hardware was read) and <c>sampleAgeMs</c> (how old that was
/// when this response was built). Both null before the first sample.
///
/// Built from the serialised snapshot rather than from a parallel record so the two can never drift:
/// a field added to <see cref="TelemetrySnapshot"/> reaches the wire with no edit here.
/// </summary>
public static class TelemetryWire
{
    public static JsonObject ToJson(TelemetryReading reading, DateTimeOffset now, JsonSerializerOptions options)
    {
        var json = JsonSerializer.SerializeToNode(reading.Snapshot, options)!.AsObject();
        json["sampledAtMs"] = reading.SampledAt?.ToUnixTimeMilliseconds();
        json["sampleAgeMs"] = reading.AgeMs(now);
        return json;
    }
}
