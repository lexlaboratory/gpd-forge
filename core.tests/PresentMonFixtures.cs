// GPD Forge — PresentMon CSV fixtures for the parser and feed tests. GPL-3.0-or-later.
using System.Globalization;

namespace GpdForge.Core.Tests;

internal static class PresentMonFixtures
{
    /// <summary>
    /// Verbatim header of the PresentMon the installer ships (2.5.1, default metrics, no time flags),
    /// captured on the GPD Win 4 on 2026-09-24 with a 3 s non-elevated run. Note what it does NOT
    /// have: no "FrameTime" and no "CPUStartTime" — the frame interval is "MsBetweenPresents" and the
    /// row times are "TimeInMs" / "CPUStartTimeInMs", both in milliseconds.
    /// </summary>
    public const string Header251 =
        "Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing," +
        "PresentMode,TimeInMs,MsBetweenSimulationStart,MsBetweenPresents,MsBetweenDisplayChange," +
        "MsInPresentAPI,MsRenderPresentLatency,MsUntilDisplayed,CPUStartTimeInMs,MsBetweenAppStart," +
        "MsCPUBusy,MsCPUWait,MsGPULatency,MsGPUTime,MsGPUBusy,MsGPUWait,MsAnimationError,AnimationTime," +
        "MsFlipDelay,MsAllInputToPhotonLatency,MsClickToPhotonLatency";

    /// <summary>
    /// Rows from the same capture, verbatim. They show the three things the old parser got wrong:
    /// "NA" in columns it does not need, rows that arrive out of time order (the webview rows count
    /// DOWN from 566 to 501 ms), and a zero MsBetweenPresents (a repeated present, not a frame).
    /// </summary>
    public static readonly string[] Rows251 =
    [
        "Orca.exe,7644,0x1C0CA006180,DXGI,0,0,0,Composed: Flip,165.1775,NA,82.70060000000001,83.38170000000001,0.60340000000000,2.73210000000000,15.1785,82.9437,82.8372,82.2338,0.6034,82.2818,2.6841,2.6143,0.0698,NA,82.9437,NA,NA,NA",
        "<unknown>,2236,0x0,Other,-1,0,0,Hardware: Legacy Flip,168.9328,NA,82.98860000000001,83.38170000000001,0.00000000000000,0.28630000000000,11.4232,85.9442,82.9886,82.9886,0.0000,0.2111,83.0638,0.6626,82.4012,NA,85.9442,NA,NA,NA",
        "Orca.exe,7644,0x1C0CA006180,DXGI,0,0,0,Composed: Flip,250.3881,NA,85.21060000000000,83.21020000000000,0.41090000000000,2.22440000000000,13.1781,165.7809,85.0181,84.6072,0.4109,84.5307,2.3009,2.2648,0.0361,-0.3730,165.7809,NA,NA,NA",
        "Orca.exe,7644,0x1C0CA006180,DXGI,0,0,0,Composed: Flip,266.5803,NA,16.19220000000000,16.71100000000000,0.41480000000000,2.50650000000000,13.6969,250.7990,16.1961,15.7813,0.4148,15.7810,2.5068,2.2154,0.2914,68.3071,250.7990,NA,NA,NA",
        "Orca.exe,7644,0x1C0CA006180,DXGI,0,0,0,Composed: Flip,283.4760,NA,16.89570000000000,16.77180000000000,0.65370000000000,1.35820000000000,13.5730,266.9951,17.1346,16.4809,0.6537,16.5239,1.3152,1.2895,0.0257,-0.5757,266.9951,NA,NA,NA",
        "msedgewebview2.exe,18896,0x1D09E4CCAE0,DXGI,0,0,0,Composed: Flip,566.3335,NA,0.00000000000000,NA,1.11390000000000,1.19490000000000,NA,584.0555,1.1139,0.0000,1.1139,0.0000,0.5664,0.5311,0.0353,NA,NA,NA,NA,NA",
        "msedgewebview2.exe,18896,0x1D09E4CCAE0,DXGI,0,0,0,Composed: Flip,551.2604,NA,0.00000000000000,NA,1.60460000000000,1.49330000000000,NA,567.4474,1.6046,0.0000,1.6046,0.0000,1.3461,0.8010,0.5451,NA,NA,NA,NA,NA",
    ];

    /// <summary>PresentMon 1.x: the interval is "MsBetweenPresents", the row time "TimeInSeconds".</summary>
    public const string Header1x =
        "Application,ProcessID,SwapChainAddress,Runtime,SyncInterval,PresentFlags,Dropped," +
        "TimeInSeconds,MsBetweenPresents,MsBetweenDisplayChange,MsInPresentAPI,MsUntilRenderComplete,MsUntilDisplayed";

    /// <summary>
    /// The other 2.x layout (the binary carries both): "CPUStartTime" plus a "FrameTime" interval.
    /// MsBetweenPresents is added here so the NA fallback has somewhere to fall back to.
    /// </summary>
    public const string HeaderV2FrameTime =
        "Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing," +
        "PresentMode,CPUStartTime,FrameTime,MsBetweenPresents,CPUBusy,CPUWait";

    /// <summary>
    /// A synthetic 2.5.1-shaped row: only the columns the feed reads carry real values, the rest are
    /// the same "NA"s the real capture has.
    /// </summary>
    public static string Row251(string app, double timeMs, double frameMs, int pid = 4242) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{app},{pid},0x1,DXGI,0,0,0,Hardware: Independent Flip,{timeMs:F4},NA,{frameMs:F4},NA,0.5,NA,NA,{timeMs:F4},NA,NA,NA,NA,NA,NA,NA,NA,NA,NA,NA,NA");

    /// <summary>
    /// <paramref name="seconds"/> of frames at <paramref name="fps"/> for one app, as 2.5.1 rows with
    /// capture-relative times starting at <paramref name="startMs"/>.
    /// </summary>
    public static IEnumerable<(double TimeMs, string Line)> Stream(string app, double fps, double seconds, double startMs = 0)
    {
        double frameMs = 1000.0 / fps;
        int count = (int)Math.Round(seconds * fps);
        for (int i = 1; i <= count; i++)
        {
            double t = startMs + i * frameMs;
            yield return (t, Row251(app, t, frameMs));
        }
    }

    /// <summary>Merges per-app streams into the time order PresentMon (mostly) emits them in.</summary>
    public static IEnumerable<(double TimeMs, string Line)> Interleave(params IEnumerable<(double TimeMs, string Line)>[] streams) =>
        streams.SelectMany(s => s).OrderBy(r => r.TimeMs);
}
