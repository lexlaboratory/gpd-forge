// GPD Forge — reading `overrides` off a POST/PUT /app-rules body. GPL-3.0-or-later.
//
// Parsed by hand from the JsonElement rather than bound to a record, for two reasons:
//
//  - ABSENT and NULL must mean different things. The Profiles page toggles `enabled` with a PUT that
//    has never carried overrides; if a missing key read as null, that toggle would wipe the game's
//    profile. Absent = keep, null = clear, an object = replace.
//  - A wrong TYPE ("stapmW": "22") must come back as a 400 that names the field, like a wrong value
//    does. Model binding would fail the whole body with a framework error the UI cannot show.
//
// Unknown keys are ignored — a newer client (F4's `rsr`) talking to this daemon loses the field, not
// the request.
using System.Text.Json;

namespace GpdForge.Profiles;

public static class RuleOverridesJson
{
    /// <param name="Present">False when the body had no `overrides` key at all.</param>
    public sealed record Result(bool Present, RuleOverrides? Value, OverrideError? Error);

    private static readonly Result Absent = new(false, null, null);

    public static Result Parse(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Undefined) return Absent;
        if (e.ValueKind == JsonValueKind.Null) return new(true, null, null);
        if (e.ValueKind != JsonValueKind.Object)
            return Fail("bad_overrides", "overrides must be an object or null.");

        if (!TryInt(e, "stapmW", out var stapm)) return Fail("bad_stapm", "stapmW must be a whole number of watts or null.");
        if (!TryInt(e, "frameCapFps", out var cap)) return Fail("bad_frame_cap", "frameCapFps must be a whole number or null.");
        if (!TryString(e, "fanMode", out var fan)) return Fail("bad_fan_mode", "fanMode must be a string or null.");
        if (!TryGpu(e, out var gpu)) return Fail("bad_gpu", "gpu must be an object with boolean-or-null antiLag and chill.");
        if (!TryFreeze(e, out var freeze)) return Fail("bad_freeze", "freeze must be an array of process names or null.");

        var parsed = new RuleOverrides(stapm, cap, fan, gpu, freeze);
        if (RuleOverridesPolicy.Validate(parsed) is OverrideError error) return new(true, null, error);
        return new(true, RuleOverridesPolicy.Normalize(parsed), null);
    }

    private static Result Fail(string code, string message) => new(true, null, new OverrideError(code, message));

    // Case-insensitive like the rest of the HTTP binding (JsonSerializerDefaults.Web).
    private static JsonElement Get(JsonElement obj, string name)
    {
        foreach (var p in obj.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return default;
    }

    private static bool IsNothing(JsonElement v) => v.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null;

    private static bool TryInt(JsonElement obj, string name, out int? value)
    {
        value = null;
        var v = Get(obj, name);
        if (IsNothing(v)) return true;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) { value = i; return true; }
        return false;
    }

    private static bool TryBool(JsonElement obj, string name, out bool? value)
    {
        value = null;
        var v = Get(obj, name);
        if (IsNothing(v)) return true;
        if (v.ValueKind is JsonValueKind.True or JsonValueKind.False) { value = v.GetBoolean(); return true; }
        return false;
    }

    private static bool TryString(JsonElement obj, string name, out string? value)
    {
        value = null;
        var v = Get(obj, name);
        if (IsNothing(v)) return true;
        if (v.ValueKind != JsonValueKind.String) return false;
        value = v.GetString();
        return true;
    }

    private static bool TryGpu(JsonElement obj, out GpuOverrides? gpu)
    {
        gpu = null;
        var v = Get(obj, "gpu");
        if (IsNothing(v)) return true;
        if (v.ValueKind != JsonValueKind.Object) return false;
        if (!TryBool(v, "antiLag", out var antiLag) || !TryBool(v, "chill", out var chill)) return false;
        gpu = new GpuOverrides(antiLag, chill);
        return true;
    }

    private static bool TryFreeze(JsonElement obj, out IReadOnlyList<string>? freeze)
    {
        freeze = null;
        var v = Get(obj, "freeze");
        if (IsNothing(v)) return true;
        if (v.ValueKind != JsonValueKind.Array) return false;
        var names = new List<string>();
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) return false;
            names.Add(item.GetString()!);
        }
        freeze = names;
        return true;
    }
}
