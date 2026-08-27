// GPD Forge — PWM duty conversion (pure math). GPL-3.0-or-later.
// Formulas derived from Cryolitia/gpd-fan-driver (GPL-2.0, (c) 2024 Cryolitia PukNgae), the same
// source GpdDeviceDb's register map is derived from.
//
// GPD Forge's fan curves, the manual-duty API, and the UI slider all work in a "user" 0..255 duty
// scale. The EC's native scale for a board is 1..pwmMax (register byte 0 is reserved — some boards
// treat it as off/uninitialized rather than "0% duty"), and pwmMax varies per board (184 on
// WinMax2/HX370, 127 on the 6800U board, 244 on WinMini/Duo — see GpdDeviceDb). These two pure
// functions are the ONLY place that scale conversion happens, so every caller (GpdFanController,
// FanCurve consumers, --probe-fan-set) agrees on the same mapping.

namespace GpdForge.Fan;

public static class FanMath
{
    /// <summary>
    /// User 0..255 → EC 1..pwmMax. 0 maps to 1 (the lowest EC step), 255 maps to pwmMax (full duty).
    /// <paramref name="user0to255"/> is clamped to 0..255 first, so out-of-range input never throws.
    /// </summary>
    public static int CastPwm(int user0to255, int pwmMax)
    {
        int user = Math.Clamp(user0to255, 0, 255);
        return (int)Math.Round(user * (pwmMax - 1) / 255.0, MidpointRounding.AwayFromZero) + 1;
    }

    /// <summary>
    /// EC 1..pwmMax → user 0..255. The result is clamped to 0..255: a stray/garbage EC read (e.g. 0,
    /// which this project never writes but may be read back before manual mode is ever engaged) must
    /// never produce a negative or out-of-range duty.
    /// </summary>
    public static int UncastPwm(int ec, int pwmMax)
    {
        double user = (ec - 1) * 255.0 / (pwmMax - 1);
        return Math.Clamp((int)Math.Round(user, MidpointRounding.AwayFromZero), 0, 255);
    }
}
