// GPD Forge — an opt-out install must not start an agent that still drives Radeon settings. GPL-3.0-or-later.
//
// Since F0 the installer starts the session agent unconditionally (the service needs it to see which
// app is in front). The agent inherits the INSTALLER's environment, and GpuAgentLoop takes the ADLX
// path (Anti-Lag, Chill, Boost, frame caps) when GPDFORGE_ENABLE_GPU_PROFILES is "1". An elevated
// PowerShell opened after an opt-in install carries that 1 from the Machine scope; reinstalling
// without -EnableGpuProfiles cleared only the Machine value, so the agent it spawned kept applying
// Radeon profiles until logoff (audit round 1, 2026-09-25). These tests keep both scopes cleared.
using System.Text.RegularExpressions;
using Xunit;

namespace GpdForge.Core.Tests;

public class InstallerGpuGateTests
{
    private const string Gate = "GPDFORGE_ENABLE_GPU_PROFILES";

    [Fact]
    public void Opt_out_clears_the_gate_from_the_installers_own_process_before_starting_the_agent()
    {
        var lines = File.ReadAllLines(Path.Combine(RepoRoot(), "scripts", "install-gpd-forge.ps1"));
        var optIn = IndexOf(lines, l => l.Contains($"SetEnvironmentVariable('{Gate}', '1', 'Machine')"));
        var agentStart = IndexOf(lines, l => Regex.IsMatch(l, @"^\s*Start-Process\b.*--gpu-agent"));
        Assert.True(optIn >= 0, "The opt-in branch that sets the Machine-scope gate was not found.");
        Assert.True(agentStart > optIn, "The agent start was not found after the gate is decided.");

        var between = lines[optIn..agentStart];
        Assert.Contains(between, l => Regex.IsMatch(l,
            $@"^\s*Remove-Item\s+Env:{Gate}\b.*-ErrorAction\s+SilentlyContinue", RegexOptions.IgnoreCase));
    }

    private static int IndexOf(string[] lines, Func<string, bool> match)
    {
        for (var i = 0; i < lines.Length; i++) if (match(lines[i])) return i;
        return -1;
    }

    /// <summary>Anchored on Directory.Build.props, like ModeCatalogueTests.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            $"Could not find the repository root above {AppContext.BaseDirectory}.");
    }
}
