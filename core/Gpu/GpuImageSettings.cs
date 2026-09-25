// GPD Forge — Radeon Super Resolution and Image Sharpening as desired state. GPL-3.0-or-later.
//
// F4 (plan 2026-09-24). At the 22 W this handheld holds, rendering fewer pixels is what buys the most
// frames: RSR renders the game below the panel's resolution and upscales it in the driver, and RIS
// sharpens what that softens. Both change how the picture looks, so neither is ever switched on by a
// MODE (GpuModeProfiles stays image-neutral on purpose) — only by a person: a game profile, or the
// Display page.
//
// The shape follows the frame cap rather than Anti-Lag / Chill, because these are VALUES the user may
// also set in Adrenalin: a request is carried out ONCE per version (GpuImageReconciler), never
// re-asserted every tick over a change the user made since. A null field means "leave it as the driver
// has it", which is different from "off".
//
// The ordering rules the agent applies are pure (Plan) so they are tested here; the agent only walks
// the steps, because ADLX itself runs in the user's session and cannot be unit-tested.
using System.Text.Json;

namespace GpdForge.Gpu;

/// <summary>What to set on RSR and RIS. Every field optional; null = the driver keeps its own.</summary>
public sealed record GpuImageRequest(bool? Rsr = null, int? RsrSharpness = null, bool? Ris = null, int? RisSharpness = null)
{
    public bool IsEmpty => Rsr is null && RsrSharpness is null && Ris is null && RisSharpness is null;

    /// <summary>The sharpness band both features accept on every driver measured (0–100 %). The
    /// driver's own range, when reported, is checked on top (<see cref="Reject"/>).</summary>
    public const int MinSharpness = 0;
    public const int MaxSharpness = 100;

    /// <summary>Why a sharpness is refused, or null. Null range = the driver did not report one; then
    /// only the 0–100 band applies — a limit we did not read is not one we can enforce.</summary>
    public static string? Reject(int? sharpness, int? min, int? max, string name)
    {
        if (sharpness is not int v) return null;
        if (v is < MinSharpness or > MaxSharpness)
            return $"{name} sharpness must be between {MinSharpness} and {MaxSharpness} %.";
        if (min is int lo && v < lo) return $"The driver's lowest {name} sharpness is {lo} %.";
        if (max is int hi && v > hi) return $"The driver's highest {name} sharpness is {hi} %.";
        return null;
    }

    /// <summary>The request as the driver's state before it: only the fields this request sets, read
    /// from <paramref name="rsr"/> / <paramref name="ris"/>. Null when the driver's state for a set
    /// field is unknown — then there is nothing honest to restore to.</summary>
    public GpuImageRequest? PreviousFrom(GpuFeatureState? rsr, GpuFeatureState? ris)
    {
        bool touchesRsr = Rsr is not null || RsrSharpness is not null;
        bool touchesRis = Ris is not null || RisSharpness is not null;
        if (touchesRsr && rsr is not { Supported: true }) return null;
        if (touchesRis && ris is not { Supported: true }) return null;
        return new GpuImageRequest(
            Rsr: touchesRsr ? rsr!.Enabled : null,
            RsrSharpness: RsrSharpness is not null ? rsr!.Value : null,
            Ris: touchesRis ? ris!.Enabled : null,
            RisSharpness: RisSharpness is not null ? ris!.Value : null);
    }

    /// <summary>Reads the `image` object of GET /gpu/desired. Null for anything that is not one.</summary>
    public static GpuImageRequest? Parse(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        return new GpuImageRequest(Bool(e, "rsr"), Int(e, "rsrSharpness"), Bool(e, "ris"), Int(e, "risSharpness"));
    }

    private static bool? Bool(JsonElement o, string n) =>
        o.TryGetProperty(n, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static int? Int(JsonElement o, string n) =>
        o.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
}

/// <summary>One driver write, in the order the agent must make it.</summary>
public enum GpuImageStepKind { BoostOff, RsrEnabled, RsrSharpness, RisEnabled, RisSharpness }

public sealed record GpuImageStep(GpuImageStepKind Kind, int Value = 0)
{
    /// <summary>The key the agent reports the result under ("rsr", "rsrSharpness", ...).</summary>
    public string Field => Kind switch
    {
        GpuImageStepKind.BoostOff => "boost",
        GpuImageStepKind.RsrEnabled => "rsr",
        GpuImageStepKind.RsrSharpness => "rsrSharpness",
        GpuImageStepKind.RisEnabled => "ris",
        _ => "risSharpness",
    };
}

public static class GpuImagePlan
{
    /// <summary>
    /// The writes that carry out <paramref name="r"/>, in the only order the driver accepts:
    ///
    ///  - Boost OFF before RSR on. AMD documents RSR as unavailable while Radeon Boost is on — both
    ///    change the render resolution — so, as with Chill, the conflicting feature is turned off first
    ///    rather than leaving the driver to refuse RSR silently.
    ///  - Enabled BEFORE sharpness. Measured on FRTC (AdlxSettings.SetFrameRateCapDetailed): a value
    ///    written to a disabled feature comes back ADLX_FAIL. The same interface family, the same order.
    ///  - No sharpness after turning a feature OFF: it would be refused for the same reason, and a
    ///    sharpness for a feature that is off changes nothing the user can see.
    /// </summary>
    public static IReadOnlyList<GpuImageStep> Steps(GpuImageRequest? r)
    {
        var steps = new List<GpuImageStep>();
        if (r is null) return steps;

        if (r.Rsr == true) steps.Add(new(GpuImageStepKind.BoostOff));
        if (r.Rsr is bool rsr) steps.Add(new(GpuImageStepKind.RsrEnabled, rsr ? 1 : 0));
        if (r.RsrSharpness is int rs && r.Rsr != false) steps.Add(new(GpuImageStepKind.RsrSharpness, rs));

        if (r.Ris is bool ris) steps.Add(new(GpuImageStepKind.RisEnabled, ris ? 1 : 0));
        if (r.RisSharpness is int ss && r.Ris != false) steps.Add(new(GpuImageStepKind.RisSharpness, ss));
        return steps;
    }
}

/// <summary>Which image requests the agent has carried out. Keyed on the request's version, like the
/// cap (GpuCapReconciler): a NEW request is written even when its values are the ones last written —
/// the user may have changed them in Adrenalin since — and an OLD one is never re-asserted.</summary>
public sealed class GpuImageReconciler
{
    private long? _handled;

    public bool ShouldApply(GpuImageRequest? image, long? version) =>
        image is { IsEmpty: false } && version is long v && v != _handled;

    /// <summary>Recorded even when a write failed: retrying a refused write every 3 s would only repeat
    /// the refusal, and the result is on the agent's console and in the next report.</summary>
    public void Handled(long? version) => _handled = version;
}
