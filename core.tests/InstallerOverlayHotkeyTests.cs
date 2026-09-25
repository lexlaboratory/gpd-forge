// GPD Forge — the installer binds the overlay to a WinControls-mapped Home button. GPL-3.0-or-later.
//
// The GPD Win 4 has no Home button. The supported path is to map a back paddle (L4/R4) or Menu to a
// single unused key with GPD's own WinControls and have the resident listener catch that key
// (docs/overlay-home-button.md). Until F7 the installer could only make the listener resident on its
// default Ctrl+Alt+Home, so a paddle mapped to F24 did nothing unless the user hand-built a startup
// shortcut. -OverlayHotkey closes that. These tests run the installer's own argument builder and its
// own validation pattern, because a chord reaches a .lnk command line and an elevated relaunch.
using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit;

namespace GpdForge.Core.Tests;

public class InstallerOverlayHotkeyTests
{
    private static string Installer() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "install-gpd-forge.ps1"));

    private static string Pattern()
    {
        var m = Regex.Match(Installer(),
            @"\[ValidatePattern\('(?<p>[^']+)'\)\]\s*\[string\]\$OverlayHotkey\s*=\s*'Ctrl\+Alt\+Home'");
        Assert.True(m.Success, "-OverlayHotkey must be a validated string defaulting to Ctrl+Alt+Home.");
        return m.Groups["p"].Value;
    }

    [Theory]
    [InlineData("F24", true)]
    [InlineData("Ctrl+Alt+Home", true)]
    [InlineData("ctrl+shift+F13", true)]
    [InlineData("Win+F23", true)]
    [InlineData("", false)]
    [InlineData("F24 -Url http://evil", false)]
    [InlineData("F24\" & calc", false)]
    [InlineData("Ctrl+", false)]
    [InlineData("Hyper+F24", false)]
    public void The_chord_is_validated_before_it_reaches_a_command_line(string chord, bool ok) =>
        // PowerShell's ValidatePattern is case-insensitive.
        Assert.Equal(ok, Regex.IsMatch(chord, Pattern(), RegexOptions.IgnoreCase));

    [Fact]
    public void The_chord_becomes_the_listeners_modifiers_and_key()
    {
        var fn = Regex.Match(Installer(), @"function Get-OverlayHotkeyArgs\b.*?\n\}", RegexOptions.Singleline);
        Assert.True(fn.Success, "Get-OverlayHotkeyArgs was not found in the installer.");

        var output = RunPowerShell(fn.Value +
            "; Get-OverlayHotkeyArgs 'F24'; Get-OverlayHotkeyArgs 'Ctrl+Alt+Home'; Get-OverlayHotkeyArgs 'Shift+F13'");

        // "None" rather than an empty string: an empty quoted argument does not survive a .lnk
        // command line reliably, and overlay-hotkey.ps1 ignores an unknown modifier token.
        Assert.Equal(
            ["-Modifiers None -Key F24", "-Modifiers Ctrl,Alt -Key Home", "-Modifiers Shift -Key F13"],
            output);
    }

    [Fact]
    public void The_overlay_shortcut_carries_the_chord_and_the_relaunch_keeps_it()
    {
        var src = Installer();
        Assert.Matches(@"Script = 'overlay-hotkey\.ps1';[^\n]*Args = \(Get-OverlayHotkeyArgs \$OverlayHotkey\)", src);
        Assert.Contains("$l.Arguments = Get-HeadlessScriptArgs $hk.Script $hk.Args", src);
        // The self-elevating relaunch used to forward switches only, so the elevated half would
        // quietly install the default chord instead of the one asked for.
        Assert.Matches(@"\$kv\.Value -is \[string\]", src);
    }

    private static string[] RunPowerShell(string command)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-Command", command }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, stderr);
        return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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
