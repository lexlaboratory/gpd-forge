using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GpdForge.Tdp;

/// <summary>Registers the TDP backend behind the daemon's general hardware write gate.</summary>
public static class TdpBackendRegistration
{
    public static void Register(IServiceCollection services, bool hardwareEnabled, string? requestedBackend, string? ryzenAdjPath)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!hardwareEnabled)
        {
            services.AddSingleton<ITdpBackend, StubTdpBackend>();
            return;
        }

        if (string.Equals(requestedBackend, "pawnio-strix", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<ITdpBackend, PawnIoStrixTdpBackend>();
            return;
        }

        string exePath = ryzenAdjPath ?? @"C:\Program Files\Motion Assistant\amd\ryzenadj.exe";
        services.AddSingleton<IProcessRunner, SystemProcessRunner>();
        services.AddSingleton<ITdpBackend>(sp =>
            new RyzenAdjBackend(sp.GetRequiredService<IProcessRunner>(), exePath,
                sp.GetService<ILogger<RyzenAdjBackend>>()));
    }

    public static string Name(ITdpBackend backend) => backend switch
    {
        StubTdpBackend => "stub",
        PawnIoStrixTdpBackend => "pawnio-strix",
        _ => "ryzenadj",
    };
}
