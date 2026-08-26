// GPD Forge — keyboard backlight advisory (not yet controllable). GPL-3.0-or-later.
//
// The Win 4's keyboard backlight is EC/Fn-hotkey controlled — the same access path whose control
// writes are already blocked on this board's firmware (see core/Fan/GpdFanReader.cs and its
// --probe-ec notes in Program.cs). There is no verified, safe write path from user mode today, so
// GPD Forge does not attempt a blind EC write here: this control stays advisory-only rather than
// silently doing nothing while claiming success.
namespace GpdForge.Display;

public static class KeyboardBacklightAdvisor
{
    public const string Advisory =
        "Keyboard backlight is controlled by the embedded controller (the same EC path already " +
        "blocked on this board's firmware) or the Fn hotkey directly. GPD Forge has no verified " +
        "write path for it yet, so this stays read-only/advisory.";
}

/// <summary>Response shape both GET and POST /display/keyboard-backlight share.</summary>
public readonly record struct KeyboardBacklightStatus(bool Controllable, bool Applied, string Advisory);

/// <summary>No hardware access at all — see <see cref="KeyboardBacklightAdvisor"/>. A tiny class
/// (rather than a bare constant) so the endpoint wiring matches the shape of the other Display
/// services and stays trivially unit-testable.</summary>
public sealed class KeyboardBacklightService
{
    public KeyboardBacklightStatus Get() => new(Controllable: false, Applied: false, KeyboardBacklightAdvisor.Advisory);
    public KeyboardBacklightStatus Set() => new(Controllable: false, Applied: false, KeyboardBacklightAdvisor.Advisory);
}
