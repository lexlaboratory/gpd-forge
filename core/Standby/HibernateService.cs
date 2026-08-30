// GPD Forge — reading and setting the hibernate-instead-of-idle policy. GPL-3.0-or-later.
//
// See HibernatePolicy for why this control exists and why reads come from the registry. This class
// is the side-effecting half: it finds the active scheme, reports the current timeouts, and applies
// changes through powercfg.
//
// Applying through powercfg rather than writing the registry directly is deliberate. The registry
// value is where the setting is STORED; powercfg is what makes the power manager adopt it. Writing
// the key alone produces a machine that shows the new number and behaves like the old one — the
// exact class of lie this project keeps removing.
using GpdForge.Tdp;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace GpdForge.Standby;

public sealed class HibernateService(IProcessRunner runner, ILogger<HibernateService>? logger = null)
{
    /// <summary>
    /// The active power scheme's GUID, read from the registry. Null when it cannot be determined,
    /// which makes every timeout below null too — better than reporting another scheme's numbers as
    /// if they governed this machine.
    /// </summary>
    public static string? ActiveSchemeGuid()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes");
            return key?.GetValue("ActivePowerScheme") as string;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task<HibernatePolicyState> ReadAsync(CancellationToken ct)
    {
        var available = false;
        string? unavailable = "powercfg could not be run.";
        try
        {
            var output = await runner.RunAsync("powercfg.exe", "/a", ct);
            available = HibernatePolicy.ParseHibernateAvailable(output, out unavailable);
        }
        catch (Exception e)
        {
            logger?.LogDebug(e, "powercfg /a failed.");
            unavailable = $"powercfg /a could not be run: {e.Message}";
        }

        var scheme = ActiveSchemeGuid();
        if (scheme is null)
            return new HibernatePolicyState(available, unavailable ?? "The active power scheme could not be read.",
                new IdleTimeouts(null, null), new IdleTimeouts(null, null));

        var (standbyAc, standbyDc) = HibernatePolicy.ReadSetting(scheme, HibernatePolicy.StandbyIdleSetting);
        var (hibAc, hibDc) = HibernatePolicy.ReadSetting(scheme, HibernatePolicy.HibernateIdleSetting);

        return new HibernatePolicyState(
            available,
            available ? null : unavailable,
            new IdleTimeouts(standbyAc, hibAc),
            new IdleTimeouts(standbyDc, hibDc));
    }

    /// <summary>
    /// Sets how long the machine idles before hibernating, on battery and/or on AC.
    /// Returns what actually held, re-read afterwards rather than echoed back.
    /// </summary>
    public async Task<(bool Applied, string Detail, HibernatePolicyState State)> SetHibernateIdleAsync(
        int? dcSeconds, int? acSeconds, CancellationToken ct)
    {
        var state = await ReadAsync(ct);
        if (!state.HibernateAvailable)
            return (false, state.Unavailable ?? "Hibernate is not available on this system.", state);

        try
        {
            if (dcSeconds is int dc)
                await runner.RunAsync("powercfg.exe",
                    $"/setdcvalueindex SCHEME_CURRENT SUB_SLEEP {HibernatePolicy.HibernateIdleSetting} {dc}", ct);

            if (acSeconds is int ac)
                await runner.RunAsync("powercfg.exe",
                    $"/setacvalueindex SCHEME_CURRENT SUB_SLEEP {HibernatePolicy.HibernateIdleSetting} {ac}", ct);

            // Without this the scheme is edited but not re-activated, and the power manager keeps
            // running the previous values — a setting that reads as changed and behaves as it was.
            await runner.RunAsync("powercfg.exe", "/setactive SCHEME_CURRENT", ct);
        }
        catch (Exception e)
        {
            logger?.LogWarning(e, "Applying the hibernate idle timeout failed.");
            return (false, $"powercfg refused the change: {e.Message}", state);
        }

        // Read back rather than trusting the write. Elevation, policy or a managed scheme can all
        // make powercfg exit quietly without changing anything.
        var after = await ReadAsync(ct);
        var wanted = dcSeconds ?? acSeconds;
        var got = dcSeconds is not null ? after.OnBattery.HibernateIdleSeconds : after.OnAc.HibernateIdleSeconds;

        return got == wanted
            ? (true, $"Hibernate idle timeout is now {got} s and was read back.", after)
            : (false, $"powercfg reported no error but the value reads {(got?.ToString() ?? "unknown")}, not {wanted}. "
                      + "This usually means the service lacks the rights to change the active scheme.", after);
    }
}
