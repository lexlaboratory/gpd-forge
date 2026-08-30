// GPD Forge — hibernate policy parsing and validation. GPL-3.0-or-later.
//
// The powercfg fixtures below are the REAL output of this device (Spanish Windows), not invented
// English. A parser tested only against English text would pass here and find nothing on the machine
// it was written for — the same class of mistake HidDevices.cs already documents.
using GpdForge.Standby;
using Xunit;

namespace GpdForge.Core.Tests;

public class HibernatePolicyParseTests
{
    // Verbatim from `powercfg /a` on the Win 4, 2026-08-30.
    private const string SpanishOutput = """
Los siguientes estados de suspensión están disponibles en este sistema:
    Modo de espera (Inactivo de baja energía S0) Red conectada
    Hibernar

Los siguientes estados de suspensión no están disponibles en este sistema:
    Modo de espera (S1)
	El firmware del sistema no admite este estado de espera.
""";

    [Fact]
    public void Hibernate_is_available_on_this_device()
    {
        // Listed with no indented reason beneath it.
        Assert.True(HibernatePolicy.ParseHibernateAvailable(SpanishOutput, out var reason));
        Assert.Null(reason);
    }

    [Fact]
    public void An_unavailable_state_is_detected_by_its_indented_reason_not_by_wording()
    {
        var output = """
Los siguientes estados de suspensión no están disponibles en este sistema:
    Hibernar
	El firmware del sistema no admite este estado de espera.
""";
        Assert.False(HibernatePolicy.ParseHibernateAvailable(output, out var reason));
        // The reason is reported verbatim, in whatever language the OS speaks — inventing an English
        // translation would be putting words in powercfg's mouth.
        Assert.Contains("firmware", reason!);
    }

    [Fact]
    public void Output_that_never_mentions_hibernate_is_not_read_as_available()
    {
        Assert.False(HibernatePolicy.ParseHibernateAvailable("something else entirely", out var reason));
        Assert.Contains("did not mention", reason!);
    }
}

public class HibernateIdleValidationTests
{
    [Fact]
    public void Zero_is_valid_because_it_means_never()
        => Assert.Null(HibernatePolicy.Reject(0));

    [Fact]
    public void A_normal_timeout_is_accepted()
        => Assert.Null(HibernatePolicy.Reject(1800));

    [Fact]
    public void Negative_is_refused_and_points_at_the_right_way_to_say_never()
        => Assert.Contains("0 for 'never'", HibernatePolicy.Reject(-1)!);

    [Fact]
    public void An_implausibly_large_value_is_refused_rather_than_clamped()
    {
        // Almost always minutes typed where seconds were meant. Clamping it would silently apply
        // something the user never asked for, which is worse than refusing.
        Assert.NotNull(HibernatePolicy.Reject(100_000));
    }
}

public class IndentWidthTests
{
    [Fact]
    public void A_tab_counts_as_deeper_than_four_spaces()
    {
        // powercfg indents state names with spaces and their reasons with a tab. Counting raw
        // characters made the reason look shallower than the state, so every unavailable state read
        // as available. This is the guard for that.
        Assert.True(HibernatePolicy.IndentWidth("\tEl firmware...") > HibernatePolicy.IndentWidth("    Hibernar"));
    }

    [Fact]
    public void Indentation_stops_at_the_first_non_whitespace()
        => Assert.Equal(4, HibernatePolicy.IndentWidth("    Hibernar   trailing"));
}
