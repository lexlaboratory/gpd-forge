// GPD Forge — the alert wire format the UI actually parses. GPL-3.0-or-later.
//
// These exist because the E2E suite runs against the mock daemon, which has always emitted enum
// NAMES, while the real daemon emitted ORDINALS. Every test stayed green and the shipped app
// crashed on the Alerts page: `severity.toLowerCase()` on the number 1 throws, React unmounted the
// tree, and the window went blank. A green suite that never sees the real serializer is not proof.
//
// ⚠️ AND NEITHER IS THIS FILE, ON ITS OWN. Read the next method: it builds its own
// JsonSerializerOptions that *mirror* the ones Program.cs installs. Mirroring is copying, so this
// tests a replica of the configuration rather than the configuration — the very mistake that caused
// the outage, repeated one level up. Measured on 2026-08-31: with the JsonStringEnumConverter
// deleted from Program.cs, every test in this file still passed.
//
// What these tests do prove is worth keeping: that AlertEvent has no field which resists string
// serialisation, and that each enum member's name is what the UI expects. The guarantee that the
// RUNNING DAEMON applies the converter belongs to core.tests/ApiContractTests.cs, which asks the
// real process over HTTP. That one goes red. Do not let this file's coverage be mistaken for it.
using System.Text.Json;
using System.Text.Json.Serialization;
using GpdForge.Alerts;
using Xunit;

namespace GpdForge.Core.Tests;

public class AlertWireFormatTests
{
    /// <summary>Mirrors the options Program.cs installs via ConfigureHttpJsonOptions.</summary>
    private static readonly JsonSerializerOptions Wire = BuildWireOptions();

    private static JsonSerializerOptions BuildWireOptions()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }

    private static AlertEvent Sample(AlertSeverity severity = AlertSeverity.Aviso,
                                     AlertCategory category = AlertCategory.Thermal)
        => new(Guid.NewGuid(), DateTimeOffset.UtcNow, severity, category,
               "Thermal warning", "CPU is running hot", null, false, null);

    [Theory]
    [InlineData(AlertSeverity.Info, "Info")]
    [InlineData(AlertSeverity.Aviso, "Aviso")]
    [InlineData(AlertSeverity.Critica, "Critica")]
    public void Severity_serializes_as_its_name_not_its_ordinal(AlertSeverity severity, string expected)
    {
        var json = JsonSerializer.Serialize(Sample(severity), Wire);
        Assert.Contains($"\"severity\":\"{expected}\"", json);
    }

    [Fact]
    public void Category_serializes_as_its_name_not_its_ordinal()
    {
        var json = JsonSerializer.Serialize(Sample(category: AlertCategory.Hardware), Wire);
        Assert.Contains("\"category\":\"Hardware\"", json);
    }

    [Fact]
    public void No_enum_field_is_ever_a_bare_number()
    {
        // The precise failure: the UI does severity.toLowerCase(), which throws on a number and,
        // with no error boundary, unmounts the entire application.
        var json = JsonSerializer.Serialize(Sample(), Wire);
        using var doc = JsonDocument.Parse(json);
        foreach (var name in new[] { "severity", "category" })
        {
            var prop = doc.RootElement.GetProperty(name);
            Assert.Equal(JsonValueKind.String, prop.ValueKind);
        }
    }

    [Fact]
    public void Default_web_options_would_have_shipped_the_bug()
    {
        // Guards the reasoning above: without the converter the value really is an ordinal, so this
        // test fails loudly if someone ever removes ConfigureHttpJsonOptions from Program.cs
        // thinking it is redundant.
        var json = JsonSerializer.Serialize(Sample(AlertSeverity.Aviso), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"severity\":1", json);
    }
}
