// GPD Forge — nothing we launch in the user's session may open a console. GPL-3.0-or-later.
//
// powershell.exe and dotnet.exe are console programs. Launched directly — from a logon shortcut, a
// hotkey, or the Tauri shell — Windows draws their console before `-WindowStyle Hidden` gets a
// chance to hide it, and a console that exists for a single frame is enough to knock a fullscreen
// game out of focus (reported 2026-09-24). The fix is a host that never creates a window
// (`conhost.exe --headless`, or CREATE_NO_WINDOW). These tests keep a later edit from quietly
// reintroducing a direct launch, and keep the stray-control-byte bug that broke the hotkey
// shortcuts (a `\v` in "WindowsPowerShell\v1.0" that a tool turned into 0x0B) from coming back.
using System.Text.RegularExpressions;
using Xunit;

namespace GpdForge.Core.Tests;

public class NoConsoleWindowTests
{
    [Fact]
    public void Scripts_contain_no_stray_control_bytes()
    {
        var offenders = new List<string>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "scripts"), "*.ps1"))
        {
            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Any(c => char.IsControl(c) && c != '\t'))
                    offenders.Add($"{Path.GetFileName(path)}:{i + 1}");
            }
        }
        Assert.True(offenders.Count == 0, "Control bytes found at " + string.Join(", ", offenders));
    }

    [Fact]
    public void Installer_shortcuts_never_target_a_console_program_directly()
    {
        var installer = File.ReadAllLines(Path.Combine(RepoRoot(), "scripts", "install-gpd-forge.ps1"));
        var direct = installer
            .Select((line, i) => (line, n: i + 1))
            .Where(x => Regex.IsMatch(x.line, @"\.TargetPath\s*=\s*.*(powershell|\$dotnetPath|dotnet\.exe)",
                RegexOptions.IgnoreCase))
            .Select(x => $"line {x.n}: {x.line.Trim()}")
            .ToList();
        Assert.True(direct.Count == 0,
            "A shortcut launches a console program directly; host it with $Conhost --headless:\n" +
            string.Join("\n", direct));
    }

    [Fact]
    public void Overlay_hotkey_does_not_spawn_a_bare_powershell()
    {
        var hotkey = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "overlay-hotkey.ps1"));
        Assert.DoesNotMatch(new Regex(@"Start-Process\s+powershell", RegexOptions.IgnoreCase), hotkey);
        Assert.Contains("--headless", hotkey);
    }

    [Fact]
    public void Tauri_shell_spawns_the_daemon_without_a_console()
    {
        var main = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "src-tauri", "src", "main.rs"));
        Assert.Contains("creation_flags(CREATE_NO_WINDOW)", main);
        Assert.Contains("0x0800_0000", main);
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
