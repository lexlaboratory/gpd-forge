using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace GpdForge.Core.Tests;

[Collection(DaemonCollection.Name)]
public sealed class ModePresetEndpointTests(DaemonUnderTest daemon)
{
    [Fact]
    public async Task Profile_save_is_durable_and_import_rejects_unknown_modes_without_claiming_applied()
    {
        if (!daemon.Started) throw new InvalidOperationException($"Daemon did not start: {daemon.StartupLog}");

        var saved = await daemon.Client.PostAsJsonAsync("/profiles/battery", new { stapmW = 12, fastW = 15, slowW = 13, tctlC = 90 });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var firstRead = JsonDocument.Parse(await daemon.Client.GetStringAsync("/profiles"));
        Assert.Equal(12, firstRead.RootElement.GetProperty("battery").GetProperty("stapmW").GetInt32());

        var invalidImport = await daemon.Client.PostAsJsonAsync("/settings/import", new
        {
            modePresets = new { battery = new { stapmW = 10, fastW = 12, slowW = 11, tctlC = 90 }, overdrive = new { stapmW = 40, fastW = 40, slowW = 40, tctlC = 90 } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalidImport.StatusCode);
        var invalidBody = JsonDocument.Parse(await invalidImport.Content.ReadAsStringAsync());
        Assert.False(invalidBody.RootElement.TryGetProperty("applied", out _));

        var afterInvalid = JsonDocument.Parse(await daemon.Client.GetStringAsync("/profiles"));
        Assert.Equal(12, afterInvalid.RootElement.GetProperty("battery").GetProperty("stapmW").GetInt32());

        var imported = await daemon.Client.PostAsJsonAsync("/settings/import", new
        {
            modePresets = new { battery = new { stapmW = 11, fastW = 14, slowW = 12, tctlC = 91 } },
        });
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        Assert.Contains("modePresets", await imported.Content.ReadAsStringAsync());
        var afterImport = JsonDocument.Parse(await daemon.Client.GetStringAsync("/profiles"));
        Assert.Equal(11, afterImport.RootElement.GetProperty("battery").GetProperty("stapmW").GetInt32());

        var unknown = await daemon.Client.PostAsJsonAsync("/profiles/overdrive", new { stapmW = 12, fastW = 15, slowW = 13, tctlC = 90 });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        var afterUnknown = JsonDocument.Parse(await daemon.Client.GetStringAsync("/profiles"));
        Assert.False(afterUnknown.RootElement.TryGetProperty("overdrive", out _));

        // Keep the shared daemon fixture at its catalogue value for other endpoint tests.
        var reset = await daemon.Client.PostAsJsonAsync("/profiles/battery", new { stapmW = 8, fastW = 12, slowW = 10, tctlC = 90 });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
    }

    [Fact]
    public async Task Profile_save_failure_returns_503_and_leaves_the_confirmed_value_unchanged()
    {
        if (!daemon.Started) throw new InvalidOperationException($"Daemon did not start: {daemon.StartupLog}");

        var file = Path.Combine(daemon.DataDirectory, "mode-presets.json");
        var backup = file + ".backup-test";
        if (File.Exists(backup)) File.Delete(backup);
        if (File.Exists(file)) File.Move(file, backup);
        Directory.CreateDirectory(file);
        try
        {
            var response = await daemon.Client.PostAsJsonAsync("/profiles/battery", new { stapmW = 12, fastW = 15, slowW = 13, tctlC = 90 });
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Contains("Unable to save", error.RootElement.GetProperty("error").GetString());

            var readBack = JsonDocument.Parse(await daemon.Client.GetStringAsync("/profiles"));
            Assert.Equal(8, readBack.RootElement.GetProperty("battery").GetProperty("stapmW").GetInt32());

            var import = await daemon.Client.PostAsJsonAsync("/settings/import", new
            {
                modePresets = new { battery = new { stapmW = 11, fastW = 14, slowW = 12, tctlC = 91 } },
            });
            Assert.Equal(HttpStatusCode.ServiceUnavailable, import.StatusCode);
            var importError = JsonDocument.Parse(await import.Content.ReadAsStringAsync());
            Assert.Contains("Unable to save", importError.RootElement.GetProperty("error").GetString());
            Assert.False(importError.RootElement.TryGetProperty("applied", out _));
            var afterImportFailure = JsonDocument.Parse(await daemon.Client.GetStringAsync("/profiles"));
            Assert.Equal(8, afterImportFailure.RootElement.GetProperty("battery").GetProperty("stapmW").GetInt32());
        }
        finally
        {
            Directory.Delete(file);
            if (File.Exists(backup)) File.Move(backup, file);
        }
    }
}
