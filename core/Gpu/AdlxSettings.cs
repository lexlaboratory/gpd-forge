// GPD Forge — reading and writing the Radeon 3D settings through ADLX. GPL-3.0-or-later.
//
// Built on the foundation AdlxInterop proves at startup: the vtable layout is transcribed from the
// SDK headers and confirmed on this machine before anything here is called. Every slot index below
// comes from those headers, and the comment above each table is the header's own declaration order —
// it is load-bearing, because calling the wrong index calls the wrong driver function.
//
// Scalar widths matter and are not defaults: ADLXDefines.h defines adlx_bool as adlx_uint8 (ONE byte)
// on the C path, and adlx_long as C `long`, which is 32-bit on Windows. Marshalling a .NET bool here
// would be four bytes against a one-byte field and would read whatever happened to be next to it.
//
// Reference counting is COM-shaped: every Get* hands back an interface the caller owns and must
// Release (slot 1). A leaked reference keeps a driver object alive for the life of the daemon, so the
// acquire/use/release cycle is centralised in WithFeature rather than repeated per call site.
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace GpdForge.Gpu;

/// <summary>One Radeon feature as the driver currently reports it. `Supported` false means this GPU
/// cannot do it at all; the value fields are null when they were not readable, never a stand-in.</summary>
public sealed record GpuFeatureState(bool Supported, bool Enabled, int? Value = null, int? Min = null, int? Max = null);

/// <summary>ADLX_IntRange, transcribed from ADLXStructures.h: three adlx_int (int32) fields in this
/// order. Marshalled explicitly rather than relying on default layout, because a wrong size here
/// would silently read the wrong bytes rather than fail.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AdlxIntRange
{
    public int MinValue;
    public int MaxValue;
    public int Step;
}

/// <summary>Everything read in one pass. A null entry means the feature could not be queried.</summary>
public sealed record GpuSettingsSnapshot(
    GpuFeatureState? AntiLag,
    GpuFeatureState? Chill,
    GpuFeatureState? Boost,
    GpuFeatureState? ImageSharpening,
    GpuFeatureState? FrameRateTargetControl);

/// <summary>
/// The 3D settings, driven through ADLX's C interface. Not thread-safe by design: it is called from
/// the profile worker on one thread, and ADLX makes no reentrancy promises worth relying on.
/// </summary>
public sealed class AdlxSettings(IntPtr system, ILogger? logger = null)
{
    private const int ADLX_OK = 0;

    // IADLXSystemVtbl — GetGPUs is slot 1 (see AdlxInterop for the full table).
    private const int SlotSystemGetGPUs = 1;
    private const int SlotSystemGet3DSettingsServices = 7;

    // IADLXInterface, the base of every ADLX object.
    private const int SlotRelease = 1;

    // IADLXGPUListVtbl: 0 Acquire, 1 Release, 2 QueryInterface, 3 Size, 4 Empty, 5 Begin, 6 End,
    //                   7 At, 8 Clear, 9 Remove_Back, 10 Add_Back, 11 At_GPUList, 12 Add_Back_GPUList
    //
    // At_GPUList (11), NOT the inherited generic At (7). Both compile and both are "there", but the
    // generic one yields IADLXInterface** and performs an internal QueryInterface that this list
    // refuses: measured on the device, At(0) returned ADLX_UNKNOWN_INTERFACE (6) on a list whose Size
    // was 1. The typed accessor is the one that hands back an IADLXGPU.
    private const int SlotListBegin = 5;
    private const int SlotListAtGpu = 11;

    // IADLX3DSettingsServicesVtbl: 0 Acquire, 1 Release, 2 QueryInterface,
    //   3 GetAntiLag, 4 GetChill, 5 GetBoost, 6 GetImageSharpening, 7 GetEnhancedSync,
    //   8 GetWaitForVerticalRefresh, 9 GetFrameRateTargetControl, 10 GetAntiAliasing, ...
    private const int SlotGetAntiLag = 3;
    private const int SlotGetChill = 4;
    private const int SlotGetBoost = 5;
    private const int SlotGetImageSharpening = 6;
    private const int SlotGetFrameRateTargetControl = 9;

    // Every feature interface shares this prefix: 0 Acquire, 1 Release, 2 QueryInterface,
    // 3 IsSupported, 4 IsEnabled. What follows differs per feature, hence the per-feature constants.
    private const int SlotIsSupported = 3;
    private const int SlotIsEnabled = 4;

    // IADLX3DAntiLagVtbl:        5 SetEnabled
    private const int SlotAntiLagSetEnabled = 5;
    // IADLX3DChillVtbl:          5 GetFPSRange, 6 GetMinFPS, 7 GetMaxFPS, 8 SetEnabled, 9 SetMinFPS, 10 SetMaxFPS
    private const int SlotChillGetMinFps = 6;
    private const int SlotChillSetEnabled = 8;
    // IADLX3DBoostVtbl:          5 GetResolutionRange, 6 GetResolution, 7 SetEnabled, 8 SetResolution
    private const int SlotBoostGetResolution = 6;
    private const int SlotBoostSetEnabled = 7;
    // IADLX3DImageSharpeningVtbl 5 GetSharpnessRange, 6 GetSharpness, 7 SetEnabled, 8 SetSharpness
    private const int SlotSharpGetSharpness = 6;
    private const int SlotSharpSetEnabled = 7;
    // IADLX3DFrameRateTargetControlVtbl 5 GetFPSRange, 6 GetFPS, 7 SetEnabled, 8 SetFPS
    private const int SlotFrtcGetFpsRange = 5;
    private const int SlotFrtcGetFps = 6;
    private const int SlotFrtcSetEnabled = 7;
    private const int SlotFrtcSetFps = 8;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int OutPtrFn(IntPtr pThis, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GpuArgOutPtrFn(IntPtr pThis, IntPtr pGpu, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int OutByteFn(IntPtr pThis, out byte value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int OutIntFn(IntPtr pThis, out int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int OutRangeFn(IntPtr pThis, out AdlxIntRange range);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetByteFn(IntPtr pThis, byte value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetIntFn(IntPtr pThis, int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int UIntArgOutPtrFn(IntPtr pThis, uint location, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint OutUIntNoArgFn(IntPtr pThis);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ReleaseFn(IntPtr pThis);

    private static T Fn<T>(IntPtr pThis, int slot) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(AdlxInterop.VtableSlot(pThis, slot));

    private static void ReleaseCom(IntPtr p)
    {
        if (p == IntPtr.Zero) return;
        try { Fn<ReleaseFn>(p, SlotRelease)(p); } catch { /* releasing an object we already distrust */ }
    }

    /// <summary>The first GPU in the system list. Caller releases. IntPtr.Zero when unavailable.</summary>
    private IntPtr AcquireFirstGpu()
    {
        IntPtr list = IntPtr.Zero;
        try
        {
            if (Fn<OutPtrFn>(system, SlotSystemGetGPUs)(system, out list) != ADLX_OK || list == IntPtr.Zero)
                return IntPtr.Zero;

            // Begin() rather than a hard-coded 0: ADLX lists carry their own start index, and on a
            // hybrid-graphics machine assuming zero is how you end up configuring the wrong adapter.
            var begin = Fn<OutUIntNoArgFn>(list, SlotListBegin)(list);
            return Fn<UIntArgOutPtrFn>(list, SlotListAtGpu)(list, begin, out var gpu) == ADLX_OK ? gpu : IntPtr.Zero;
        }
        catch (Exception e)
        {
            logger?.LogWarning(e, "ADLX: could not enumerate GPUs.");
            return IntPtr.Zero;
        }
        finally { ReleaseCom(list); }
    }

    /// <summary>
    /// Acquire one feature interface, hand it to <paramref name="use"/>, and always release it.
    /// Returns default(T) when the feature could not be obtained — which is not the same as the
    /// feature being off, and callers must keep that distinction.
    /// </summary>
    private T? WithFeature<T>(int servicesSlot, Func<IntPtr, T> use)
    {
        IntPtr services = IntPtr.Zero, gpu = IntPtr.Zero, feature = IntPtr.Zero;
        try
        {
            if (Fn<OutPtrFn>(system, SlotSystemGet3DSettingsServices)(system, out services) != ADLX_OK
                || services == IntPtr.Zero)
                return default;

            gpu = AcquireFirstGpu();
            if (gpu == IntPtr.Zero) return default;

            if (Fn<GpuArgOutPtrFn>(services, servicesSlot)(services, gpu, out feature) != ADLX_OK
                || feature == IntPtr.Zero)
                return default;

            return use(feature);
        }
        catch (Exception e)
        {
            // Includes SEHException if a slot were wrong. AdlxInterop's canary makes that unlikely,
            // but a driver call that throws must never take the daemon with it.
            logger?.LogWarning(e, "ADLX: 3D settings call failed (services slot {Slot}).", servicesSlot);
            return default;
        }
        finally
        {
            ReleaseCom(feature);
            ReleaseCom(gpu);
            ReleaseCom(services);
        }
    }

    /// <summary>Reads support/enabled, plus the feature's own value when it has one.</summary>
    private GpuFeatureState? ReadFeature(int servicesSlot, int? valueSlot, int? rangeSlot = null)
        => WithFeature(servicesSlot, p =>
        {
            if (Fn<OutByteFn>(p, SlotIsSupported)(p, out var supported) != ADLX_OK) return null;
            if (supported == 0) return new GpuFeatureState(false, false);

            var enabled = Fn<OutByteFn>(p, SlotIsEnabled)(p, out var on) == ADLX_OK && on != 0;

            int? value = null;
            if (valueSlot is int vs && Fn<OutIntFn>(p, vs)(p, out var v) == ADLX_OK) value = v;

            // The range is what makes a rejected value explainable. Without it we could only say "the
            // driver refused", which tells the user nothing about what WOULD work.
            int? min = null, max = null;
            if (rangeSlot is int rs && Fn<OutRangeFn>(p, rs)(p, out var range) == ADLX_OK)
            {
                min = range.MinValue;
                max = range.MaxValue;
            }

            return new GpuFeatureState(true, enabled, value, min, max);
        });

    public GpuSettingsSnapshot Read() => new(
        AntiLag: ReadFeature(SlotGetAntiLag, null),
        Chill: ReadFeature(SlotGetChill, SlotChillGetMinFps),
        Boost: ReadFeature(SlotGetBoost, SlotBoostGetResolution),
        ImageSharpening: ReadFeature(SlotGetImageSharpening, SlotSharpGetSharpness),
        FrameRateTargetControl: ReadFeature(SlotGetFrameRateTargetControl, SlotFrtcGetFps, SlotFrtcGetFpsRange));

    /// <summary>Sets one feature's enabled flag. False when unsupported or the write did not succeed —
    /// never optimistic, because "we asked" is not "it applied".</summary>
    private bool SetEnabled(int servicesSlot, int setSlot, bool enable)
        => WithFeature(servicesSlot, p =>
        {
            if (Fn<OutByteFn>(p, SlotIsSupported)(p, out var supported) != ADLX_OK || supported == 0)
                return false;
            return Fn<SetByteFn>(p, setSlot)(p, enable ? (byte)1 : (byte)0) == ADLX_OK;
        });

    /// <summary>
    /// Applies a profile and reports, per feature, whether the driver took it.
    ///
    /// Order is deliberate: anything conflicting with Chill is turned OFF before Chill goes on, and
    /// Chill goes off before Boost/Anti-Lag go on. AMD's driver refuses the forbidden combination
    /// rather than merging it, so applying in a careless order silently half-lands the profile.
    /// </summary>
    public IReadOnlyDictionary<string, bool> Apply(GpuProfile profile)
    {
        var result = new Dictionary<string, bool>();

        if (profile.Chill)
        {
            result["antiLag"] = SetEnabled(SlotGetAntiLag, SlotAntiLagSetEnabled, false);
            result["boost"] = SetEnabled(SlotGetBoost, SlotBoostSetEnabled, false);
            result["chill"] = SetEnabled(SlotGetChill, SlotChillSetEnabled, true);
        }
        else
        {
            result["chill"] = SetEnabled(SlotGetChill, SlotChillSetEnabled, false);
            result["antiLag"] = SetEnabled(SlotGetAntiLag, SlotAntiLagSetEnabled, profile.AntiLag);
            result["boost"] = SetEnabled(SlotGetBoost, SlotBoostSetEnabled, profile.Boost);
        }

        return result;
    }

    /// <summary>
    /// Walks the acquire chain one step at a time and reports where it stops.
    ///
    /// Exists because "unreadable" is a useless answer on its own: services, GPU enumeration and the
    /// feature interface are three separate things that can fail, and collapsing them into one word
    /// sends you guessing. Read-only.
    /// </summary>
    public IReadOnlyList<string> Diagnose()
    {
        var steps = new List<string>();
        IntPtr services = IntPtr.Zero, list = IntPtr.Zero, gpu = IntPtr.Zero, feature = IntPtr.Zero;
        try
        {
            var rc = Fn<OutPtrFn>(system, SlotSystemGet3DSettingsServices)(system, out services);
            steps.Add($"Get3DSettingsServices -> rc={rc} ptr={(services == IntPtr.Zero ? "null" : "ok")}");
            if (services == IntPtr.Zero) return steps;

            rc = Fn<OutPtrFn>(system, SlotSystemGetGPUs)(system, out list);
            steps.Add($"GetGPUs -> rc={rc} ptr={(list == IntPtr.Zero ? "null" : "ok")}");
            if (list == IntPtr.Zero) return steps;

            uint size = Fn<OutUIntNoArgFn>(list, 3)(list);          // IADLXList::Size
            uint begin = Fn<OutUIntNoArgFn>(list, SlotListBegin)(list);
            uint end = Fn<OutUIntNoArgFn>(list, 6)(list);           // IADLXList::End
            steps.Add($"GPU list -> size={size} begin={begin} end={end}");

            rc = Fn<UIntArgOutPtrFn>(list, SlotListAtGpu)(list, begin, out gpu);
            steps.Add($"list.At_GPUList({begin}) -> rc={rc} ptr={(gpu == IntPtr.Zero ? "null" : "ok")}");
            if (gpu == IntPtr.Zero) return steps;

            rc = Fn<GpuArgOutPtrFn>(services, SlotGetAntiLag)(services, gpu, out feature);
            steps.Add($"GetAntiLag -> rc={rc} ptr={(feature == IntPtr.Zero ? "null" : "ok")}");
            if (feature == IntPtr.Zero) return steps;

            rc = Fn<OutByteFn>(feature, SlotIsSupported)(feature, out var supported);
            steps.Add($"AntiLag.IsSupported -> rc={rc} supported={supported}");
        }
        catch (Exception e)
        {
            steps.Add($"threw: {e.GetType().Name}: {e.Message}");
        }
        finally
        {
            ReleaseCom(feature);
            ReleaseCom(gpu);
            ReleaseCom(list);
            ReleaseCom(services);
        }
        return steps;
    }

    /// <summary>A real frame-rate cap, from the driver (FRTC) — distinct from the auto-TDP FPS target
    /// the Power page steers. `fps` of null disables it.</summary>
    public bool SetFrameRateCap(int? fps) => SetFrameRateCapDetailed(fps).Ok;

    /// <summary>
    /// Applies the cap and reports WHICH call failed and with what ADLX_RESULT.
    ///
    /// The boolean-only version told us "NOT applied" and nothing else, which is a dead end: SetFPS
    /// and SetEnabled fail for entirely different reasons, and the result code is the difference
    /// between a next step and a shrug. Same reasoning as Diagnose() above.
    /// </summary>
    public (bool Ok, string Detail) SetFrameRateCapDetailed(int? fps)
        => WithFeature(SlotGetFrameRateTargetControl, p =>
        {
            if (Fn<OutByteFn>(p, SlotIsSupported)(p, out var supported) != ADLX_OK || supported == 0)
                return (false, "FrameRateTargetControl reports unsupported on this GPU.");

            if (fps is not int target)
            {
                var offRc = Fn<SetByteFn>(p, SlotFrtcSetEnabled)(p, 0);
                return (offRc == ADLX_OK, offRc == ADLX_OK ? "Cap disabled." : $"SetEnabled(false) -> rc={offRc}.");
            }

            // Enable BEFORE setting the value. The intuitive order — value first, so enabling never
            // briefly applies a stale cap — is what the driver refuses: measured on this device,
            // SetFPS on a disabled FRTC returns ADLX_FAIL (rc=3). The feature has to be on before its
            // value can be written.
            //
            // The cost is real and accepted: enabling applies whatever cap was there previously for
            // the moment before the new one lands. On a handheld that can be a visible frame-rate
            // lurch, and it is the price of the only order the driver accepts.
            var enRc = Fn<SetByteFn>(p, SlotFrtcSetEnabled)(p, 1);
            if (enRc != ADLX_OK) return (false, $"SetEnabled(true) -> rc={enRc}.");

            var setRc = Fn<SetIntFn>(p, SlotFrtcSetFps)(p, target);
            return (setRc == ADLX_OK, setRc == ADLX_OK ? $"Cap set to {target} FPS." : $"Enabled ok, SetFPS({target}) -> rc={setRc}.");
        });
}
