// GPD Forge — `ryzenadj --info` output for the Ryzen AI 9 HX 370 (Strix Point). GPL-3.0-or-later.
//
// NOT CAPTURED FROM THE DEVICE, and this says so on purpose. On 2026-09-24 the bundled
// `C:\Program Files\Motion Assistant\amd\ryzenadj.exe --info` was run read-only on the reference
// HX 370 from a non-elevated shell, and it refused before printing a table:
//
//     WinRing0 Err: Driver not loaded
//     Unable to get PCI Obj, check permission
//     Unable to init ryzenadj
//
// (exit 127). Reading the PM table needs the WinRing0 driver loaded, i.e. elevation, and elevating a
// test-data capture is not worth a UAC prompt on a machine that is also a daily driver. So the table
// below is rebuilt from ryzenadj's own printer (main.c, `show_info_table`): a GitHub-markdown table
// whose rows are `printf("| %-19s | %9.3f | %-18s |\n", name, value, parameter)`, after four header
// lines. The layout is ryzenadj's; the family line, PM table version and the values are
// illustrative, chosen to be what `windows` (15/20/17 W, Tctl 92) would read back. Replace this with
// a real capture (run the same command from an elevated shell) when one is taken — the tests below
// should pass unchanged, and if they do not, the parser was wrong about the real format.
//
// The plan's acceptance item for this (F0.5, "salida real de Strix Point capturada del equipo") is
// therefore OPEN, and the plan says so. To close it: from an elevated shell run the read-only
// `dotnet GpdForge.Service.dll --probe-tdp`, which prints ryzenadj's output verbatim between two
// marker lines and then the parse; paste the verbatim part here as StrixPointInfo and keep the
// synthetic variants below as extra cases.
namespace GpdForge.Core.Tests;

public static class RyzenAdjFixtures
{
    /// <summary>The refusal a non-elevated run prints, verbatim from the 2026-09-24 attempt.</summary>
    public const string NotElevated =
        "WinRing0 Err: Driver not loaded\r\n" +
        "Unable to get PCI Obj, check permission\r\n" +
        "Unable to init ryzenadj\r\n";

    /// <summary>
    /// A Strix Point table in ryzenadj's format, with Windows line endings (the process's stdout is
    /// read as-is, so a stray '\r' on every line is the normal case, not an edge case). Values that
    /// the PM table does not expose print as <c>nan</c>, which ryzenadj really does.
    /// </summary>
    public const string StrixPointInfo =
        "CPU Family: Strix Point\r\n" +
        "SMU BIOS Interface Version: 26\r\n" +
        "Version: v0.16.0 \r\n" +
        "PM Table Version: 5d0008\r\n" +
        "|        Name         |   Value   |     Parameter      |\r\n" +
        "|---------------------|-----------|--------------------|\r\n" +
        "| STAPM LIMIT         |    15.000 | stapm-limit        |\r\n" +
        "| STAPM VALUE         |     6.482 |                    |\r\n" +
        "| PPT LIMIT FAST      |    20.000 | fast-limit         |\r\n" +
        "| PPT VALUE FAST      |     8.917 |                    |\r\n" +
        "| PPT LIMIT SLOW      |    17.000 | slow-limit         |\r\n" +
        "| PPT VALUE SLOW      |     7.104 |                    |\r\n" +
        "| StapmTimeConst      |   200.000 | stapm-time         |\r\n" +
        "| SlowPPTTimeConst    |     5.000 | slow-time          |\r\n" +
        "| PPT LIMIT APU       |       nan | apu-slow-limit     |\r\n" +
        "| PPT VALUE APU       |       nan |                    |\r\n" +
        "| TDC LIMIT VDD       |    65.000 | vrm-current        |\r\n" +
        "| TDC VALUE VDD       |     9.211 |                    |\r\n" +
        "| TDC LIMIT SOC       |    20.000 | vrmsoc-current     |\r\n" +
        "| TDC VALUE SOC       |     3.305 |                    |\r\n" +
        "| EDC LIMIT VDD       |    90.000 | vrmmax-current     |\r\n" +
        "| EDC VALUE VDD       |    31.460 |                    |\r\n" +
        "| EDC LIMIT SOC       |    30.000 | vrmsocmax-current  |\r\n" +
        "| EDC VALUE SOC       |     6.117 |                    |\r\n" +
        "| THM LIMIT CORE      |    92.000 | tctl-temp          |\r\n" +
        "| THM VALUE CORE      |    51.822 |                    |\r\n" +
        "| STT LIMIT APU       |     0.000 | apu-skin-temp      |\r\n" +
        "| STT VALUE APU       |    38.250 |                    |\r\n" +
        "| STT LIMIT dGPU      |     0.000 | dgpu-skin-temp     |\r\n" +
        "| STT VALUE dGPU      |     0.000 |                    |\r\n" +
        "| CCLK Boost SETPOINT |    95.000 | power-saving /     |\r\n" +
        "| CCLK BUSY VALUE     |    12.604 | max-performance    |\r\n";

    /// <summary>
    /// A newer SMU that ryzenadj knows how to write to but whose PM table it cannot decode: the
    /// adjustments still work, the table does not print. Real ryzenadj behaviour for a table version
    /// it has no layout for, and the likeliest failure on a freshly released APU.
    /// </summary>
    public const string StrixPointNoTable =
        "CPU Family: Strix Point\r\n" +
        "SMU BIOS Interface Version: 26\r\n" +
        "Version: v0.15.0 \r\n" +
        "Unable to init power metric table: -1, this does not affect adjustments because it is optional in ryzenadj\r\n";

    /// <summary>The limits read as <c>nan</c>: the table printed, the values are not in it.</summary>
    public const string StrixPointNanLimits =
        "CPU Family: Strix Point\r\n" +
        "PM Table Version: 5d0008\r\n" +
        "|        Name         |   Value   |     Parameter      |\r\n" +
        "|---------------------|-----------|--------------------|\r\n" +
        "| STAPM LIMIT         |       nan | stapm-limit        |\r\n" +
        "| STAPM VALUE         |       nan |                    |\r\n" +
        "| PPT LIMIT FAST      |       nan | fast-limit         |\r\n";
}
