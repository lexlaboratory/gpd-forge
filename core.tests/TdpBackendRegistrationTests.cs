using GpdForge.Tdp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GpdForge.Core.Tests;

public sealed class TdpBackendRegistrationTests
{
    [Fact]
    public void Hardware_gate_closed_registers_stub_even_when_PawnIO_is_requested()
    {
        var services = new ServiceCollection();
        TdpBackendRegistration.Register(services, hardwareEnabled: false, "pawnio-strix", null);
        using var provider = services.BuildServiceProvider();

        var backend = provider.GetRequiredService<ITdpBackend>();
        Assert.IsType<StubTdpBackend>(backend);
        Assert.Equal("stub", TdpBackendRegistration.Name(backend));
    }

    [Fact]
    public void Explicit_PawnIO_selection_registers_strix_backend_without_opening_hardware()
    {
        var services = new ServiceCollection();
        TdpBackendRegistration.Register(services, hardwareEnabled: true, "PaWnIo-StRiX", null);
        using var provider = services.BuildServiceProvider();

        var backend = provider.GetRequiredService<ITdpBackend>();
        Assert.IsType<PawnIoStrixTdpBackend>(backend);
        Assert.Equal("pawnio-strix", TdpBackendRegistration.Name(backend));
    }

    [Fact]
    public void Other_hardware_profiles_preserve_RyzenAdj_backend_and_runner()
    {
        var services = new ServiceCollection();
        TdpBackendRegistration.Register(services, hardwareEnabled: true, "unknown", "ryzenadj-test.exe");
        using var provider = services.BuildServiceProvider();

        var backend = provider.GetRequiredService<ITdpBackend>();
        Assert.IsType<RyzenAdjBackend>(backend);
        Assert.IsType<SystemProcessRunner>(provider.GetRequiredService<IProcessRunner>());
        Assert.Equal("ryzenadj", TdpBackendRegistration.Name(backend));
    }
}
