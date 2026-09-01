// GPD Forge — parsing the powercfg battery report. GPL-3.0-or-later.
//
// The fixture is the REAL report this device produced on 2026-09-01, trimmed. A fixture written by
// hand from the documentation would agree with the parser and disagree with powercfg — which is how
// the standby parser shipped a bug that only appeared on a Spanish-language machine.
using GpdForge.Battery;
using Xunit;

namespace GpdForge.Core.Tests;

public class PowercfgDesignCapacityTests
{
    /// <summary>Real output, including the namespace that a naive XPath would trip over.</summary>
    private const string RealReport = """
        <?xml version="1.0" encoding="utf-8"?>
        <BatteryReport xmlns="http://schemas.microsoft.com/battery/2012">
          <Batteries>
            <Battery>
              <Id>SR Real Battery</Id>
              <Manufacturer>Standard</Manufacturer>
              <Chemistry>LION</Chemistry>
              <DesignCapacity>43890</DesignCapacity>
              <FullChargeCapacity>40009</FullChargeCapacity>
              <CycleCount>0</CycleCount>
            </Battery>
          </Batteries>
          <RuntimeEstimates>
            <DesignCapacity>
              <Capacity>43890</Capacity>
              <ActiveRuntime>PT2H31M38S</ActiveRuntime>
            </DesignCapacity>
          </RuntimeEstimates>
        </BatteryReport>
        """;

    [Fact]
    public void Reads_the_design_capacity_from_a_real_report()
    {
        Assert.Equal(43890, PowercfgDesignCapacitySource.ParseDesignCapacity(RealReport));
    }

    [Fact]
    public void Is_not_confused_by_the_RuntimeEstimates_section()
    {
        // <RuntimeEstimates> contains its own <DesignCapacity> element holding runtime projections,
        // not a capacity in mWh. A parser that searched for the first element by that name anywhere
        // in the document would pick up the wrong node — here it happens to nest <Capacity> and would
        // yield null, which reads as "no battery" on a machine that has one.
        Assert.Equal(43890, PowercfgDesignCapacitySource.ParseDesignCapacity(RealReport));
    }

    [Fact]
    public void Ignores_the_schema_namespace()
    {
        // Matching on local name means a schema version bump does not silently return null for every
        // machine on earth. Same report, different namespace.
        var future = RealReport.Replace(
            "http://schemas.microsoft.com/battery/2012",
            "http://schemas.microsoft.com/battery/2031");
        Assert.Equal(43890, PowercfgDesignCapacitySource.ParseDesignCapacity(future));
    }

    [Fact]
    public void Skips_a_battery_that_reports_no_usable_capacity()
    {
        // A docked or secondary pack shows up as another <Battery>. Taking the first unconditionally
        // would report the dock's zero as the machine's design capacity, making health null.
        var withDock = RealReport.Replace("<Batteries>", """
            <Batteries>
                <Battery>
                  <Id>Dock</Id>
                  <DesignCapacity>0</DesignCapacity>
                  <FullChargeCapacity>0</FullChargeCapacity>
                </Battery>
            """);
        Assert.Equal(43890, PowercfgDesignCapacitySource.ParseDesignCapacity(withDock));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_input_yields_null_rather_than_throwing(string xml)
    {
        Assert.Null(PowercfgDesignCapacitySource.ParseDesignCapacity(xml));
    }

    [Fact]
    public void A_report_with_no_battery_yields_null()
    {
        const string desktop = """
            <?xml version="1.0" encoding="utf-8"?>
            <BatteryReport xmlns="http://schemas.microsoft.com/battery/2012">
              <Batteries />
            </BatteryReport>
            """;
        Assert.Null(PowercfgDesignCapacitySource.ParseDesignCapacity(desktop));
    }

    [Fact]
    public void A_non_numeric_capacity_yields_null_rather_than_zero()
    {
        var broken = RealReport.Replace("<DesignCapacity>43890</DesignCapacity>",
                                        "<DesignCapacity>n/a</DesignCapacity>");
        // Null, not 0: zero would divide into a health figure of 0 %, announcing a dead battery
        // because a field failed to parse.
        Assert.Null(PowercfgDesignCapacitySource.ParseDesignCapacity(broken));
    }
}
