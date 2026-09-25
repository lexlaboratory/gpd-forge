// GPD Forge — per-mode processor power policy (plan F4, roadmap H4). GPL-3.0-or-later.
//
// The powercfg fixtures are the REAL output of this device (Spanish Windows, 2026-09-25), because the
// parser must work on the machine it ships to, not on English text nobody here reads. The executor is
// driven against a small powercfg simulator that holds per-scheme values, so "never touches another
// scheme" and "restores what was there" are asserted against state, not against a call log.
using GpdForge.Api;
using GpdForge.Broker;
using GpdForge.Power;
using GpdForge.Profiles;
using GpdForge.Tdp;
using Xunit;

namespace GpdForge.Core.Tests;

public class ProcessorPowerPolicyPlanTests
{
    [Fact]
    public void Gaming_uses_efficient_boost_and_a_performance_leaning_epp()
    {
        var p = ProcessorPowerPolicy.Plan(ModeCatalogue.Gaming)!;
        Assert.Equal(p.Ac, p.Dc);
        Assert.Equal(33, p.Ac.Epp);
        Assert.Equal(ProcessorPowerPolicy.BoostEfficientEnabled, p.Ac.BoostMode);
        Assert.Equal(100, p.Ac.MaxProcessorState);
    }

    [Fact]
    public void Windows_is_balanced() => Assert.Equal(50, ProcessorPowerPolicy.Plan(ModeCatalogue.Windows)!.Ac.Epp);

    [Fact]
    public void Battery_leans_to_energy_and_disables_boost_on_battery_only()
    {
        var p = ProcessorPowerPolicy.Plan(ModeCatalogue.Battery)!;
        Assert.Equal(80, p.Ac.Epp);
        Assert.Equal(80, p.Dc.Epp);
        Assert.Equal(ProcessorPowerPolicy.BoostDisabled, p.Dc.BoostMode);
        Assert.Equal(ProcessorPowerPolicy.BoostEfficientEnabled, p.Ac.BoostMode);
    }

    [Fact]
    public void Ai_is_sustained_rather_than_bursty()
    {
        var p = ProcessorPowerPolicy.Plan(ModeCatalogue.Ai)!;
        Assert.True(p.Ac.Epp <= 33);
        Assert.NotEqual(ProcessorPowerPolicy.BoostAggressive, p.Ac.BoostMode);
    }

    [Theory]
    [InlineData(ModeCatalogue.Standby)]
    [InlineData("no-such-mode")]
    [InlineData(null)]
    public void Standby_and_unknown_modes_leave_windows_alone(string? mode) => Assert.Null(ProcessorPowerPolicy.Plan(mode));

    [Fact]
    public void Every_usage_mode_has_a_plan()
    {
        foreach (var m in ModeCatalogue.All.Where(m => m.Id != ModeCatalogue.Standby))
            Assert.NotNull(ProcessorPowerPolicy.Plan(m.Id));
    }

    [Fact]
    public void Writes_target_the_named_scheme_by_guid_and_reactivate_it()
    {
        const string scheme = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
        var args = ProcessorPowerPolicy.BuildWriteArgs(scheme, ProcessorPowerPolicy.Plan(ModeCatalogue.Battery)!);

        Assert.Equal(7, args.Count);
        Assert.All(args, a => Assert.Contains(scheme, a));
        Assert.DoesNotContain(args, a => a.Contains("SCHEME_CURRENT") || a.Contains("PERFEPP"));
        Assert.Contains($"/setdcvalueindex {scheme} {ProcessorPowerPolicy.SubProcessor} {ProcessorPowerPolicy.PerfBoostMode} 0", args);
        Assert.Contains($"/setacvalueindex {scheme} {ProcessorPowerPolicy.SubProcessor} {ProcessorPowerPolicy.PerfEpp} 80", args);
        Assert.Equal($"/setactive {scheme}", args[^1]);
    }
}

public class PowercfgQueryParseTests
{
    // Verbatim from this device, 2026-09-25 (Alto rendimiento is active).
    internal const string ActiveScheme = "GUID de plan de energía: 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c  (Alto rendimiento)";

    internal const string BoostQuery = """
GUID de plan de energía: 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c  (Alto rendimiento)
  Alias de GUID: SCHEME_MIN
  GUID de subgrupo: 54533251-82be-4824-96c1-47b60b740d00  (Administración de energía del procesador)
    Alias de GUID: SUB_PROCESSOR
    GUID de configuración de energía: be337238-0d82-4146-a960-4f3749d470c7  (Modo de mejora del rendimiento del procesador)
      Alias de GUID: PERFBOOSTMODE
      Índice de configuración posible: 000
      Nombre descriptivo de configuración posible: Deshabilitado
      Índice de configuración posible: 001
      Nombre descriptivo de configuración posible: Habilitado
      Índice de configuración posible: 002
      Nombre descriptivo de configuración posible: Agresiva
      Índice de configuración posible: 003
      Nombre descriptivo de configuración posible: Eficiencia habilitada
      Índice de configuración posible: 004
      Nombre descriptivo de configuración posible: Eficiencia agresiva
      Índice de configuración posible: 005
      Nombre descriptivo de configuración posible: Agresivo en garantizado
      Índice de configuración posible: 006
      Nombre descriptivo de configuración posible: Agresivo eficiente en garantizado
    Índice de configuración de corriente alterna actual: 0x00000002
    Índice de configuración de corriente continua actual: 0x00000002
""";

    internal const string EppQuery = """
GUID de plan de energía: 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c  (Alto rendimiento)
  Alias de GUID: SCHEME_MIN
  GUID de subgrupo: 54533251-82be-4824-96c1-47b60b740d00  (Administración de energía del procesador)
    Alias de GUID: SUB_PROCESSOR
    GUID de configuración de energía: 36687f9e-e3a5-4dbf-b1dc-15eb381c6863  (Directiva de preferencia de rendimiento de energía del procesador)
      Alias de GUID: PERFEPP
      Mínima configuración posible: 0x00000000
      Máxima configuración posible: 0x00000064
      Incremento de configuración posible: 0x00000001
      Unidades de configuración posibles: %
    Índice de configuración de corriente alterna actual: 0x00000021
    Índice de configuración de corriente continua actual: 0x00000050
""";

    [Fact]
    public void Active_scheme_guid_is_read_from_localised_output() =>
        Assert.Equal("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", ProcessorPowerPolicy.ParseActiveScheme(ActiveScheme));

    [Theory]
    [InlineData("")]
    [InlineData("Parámetros no válidos. Pruebe \"/?\" para obtener ayuda")]
    public void No_guid_means_no_scheme(string output) => Assert.Null(ProcessorPowerPolicy.ParseActiveScheme(output));

    [Fact]
    public void Enum_setting_reads_current_ac_and_dc_past_the_possible_indices() =>
        Assert.Equal((2, 2), ProcessorPowerPolicy.ParseSetting(BoostQuery, ProcessorPowerPolicy.PerfBoostMode));

    [Fact]
    public void Range_setting_reads_current_values_not_the_min_max() =>
        Assert.Equal((0x21, 0x50), ProcessorPowerPolicy.ParseSetting(EppQuery, ProcessorPowerPolicy.PerfEpp));

    [Fact]
    public void Each_setting_is_read_from_its_own_block()
    {
        var both = EppQuery + "\n" + BoostQuery;
        Assert.Equal((0x21, 0x50), ProcessorPowerPolicy.ParseSetting(both, ProcessorPowerPolicy.PerfEpp));
        Assert.Equal((2, 2), ProcessorPowerPolicy.ParseSetting(both, ProcessorPowerPolicy.PerfBoostMode));
    }

    [Fact]
    public void A_missing_or_truncated_setting_is_null_not_zero()
    {
        Assert.Null(ProcessorPowerPolicy.ParseSetting(BoostQuery, ProcessorPowerPolicy.ProcThrottleMax));
        var truncated = EppQuery[..EppQuery.IndexOf("Índice de configuración de corriente continua", StringComparison.Ordinal)];
        // Only the AC line survived. The last two hex values in the block are then the increment and
        // AC — a plausible-looking (1, 33) that must not be reported as a reading.
        Assert.Null(ProcessorPowerPolicy.ParseSetting(truncated, ProcessorPowerPolicy.PerfEpp));
        Assert.Null(ProcessorPowerPolicy.ParseSetting(null, ProcessorPowerPolicy.PerfEpp));
    }
}

/// <summary>powercfg, reduced to the calls the service makes, over per-scheme state.</summary>
internal sealed class PowercfgSimulator : IProcessRunner
{
    public const string Active = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    public const string Other = "381b4222-f694-41f0-9685-ff5bb260df2e";

    // scheme -> setting -> (ac, dc)
    public readonly Dictionary<string, Dictionary<string, (int Ac, int Dc)>> Schemes = new()
    {
        [Active] = Fresh(),
        [Other] = Fresh(),
    };
    public readonly List<string> Calls = [];
    public string ActiveScheme = Active;
    /// <summary>When true, value writes are accepted and silently dropped (a policy-managed machine).</summary>
    public bool IgnoreWrites;

    private static Dictionary<string, (int, int)> Fresh() => new()
    {
        [ProcessorPowerPolicy.PerfEpp] = (0, 0),
        [ProcessorPowerPolicy.PerfBoostMode] = (2, 2),
        [ProcessorPowerPolicy.ProcThrottleMax] = (100, 100),
    };

    public Task<string> RunAsync(string exePath, string arguments, CancellationToken ct)
    {
        Assert.Equal("powercfg.exe", exePath);
        Calls.Add(arguments);
        var a = arguments.Split(' ');
        switch (a[0])
        {
            case "/getactivescheme":
                return Task.FromResult($"GUID de plan de energía: {ActiveScheme}  (Alto rendimiento)");
            case "/q":
                var (ac, dc) = Schemes[a[1]][a[3]];
                return Task.FromResult(
                    $"GUID de plan de energía: {a[1]}\n  GUID de subgrupo: {a[2]}\n    GUID de configuración de energía: {a[3]}\n" +
                    $"    Índice de configuración de corriente alterna actual: 0x{ac:x8}\n" +
                    $"    Índice de configuración de corriente continua actual: 0x{dc:x8}\n");
            case "/setacvalueindex" or "/setdcvalueindex":
                if (!IgnoreWrites)
                {
                    var cur = Schemes[a[1]][a[3]];
                    var v = int.Parse(a[4]);
                    Schemes[a[1]][a[3]] = a[0] == "/setacvalueindex" ? (v, cur.Dc) : (cur.Ac, v);
                }
                return Task.FromResult("");
            case "/setactive":
                ActiveScheme = a[1];
                return Task.FromResult("");
            default:
                throw new InvalidOperationException($"unexpected powercfg call: {arguments}");
        }
    }
}

public sealed class PowerPolicyServiceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("forge-power-").FullName;
    private readonly PowercfgSimulator _sim = new();
    private readonly HardwareAuditLog _audit = new();
    private string Record => Path.Combine(_dir, "power-policy-originals.json");

    private PowerPolicyService Service() => new(_sim, Record, _audit);

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } }

    [Fact]
    public async Task Applies_the_mode_to_the_active_scheme_and_verifies_by_readback()
    {
        var svc = Service();
        var r = await svc.ApplyAsync(ModeCatalogue.Gaming, CancellationToken.None);

        Assert.True(r!.Verified);
        Assert.Equal(PowercfgSimulator.Active, r.Scheme);
        Assert.Equal((33, 33), _sim.Schemes[PowercfgSimulator.Active][ProcessorPowerPolicy.PerfEpp]);
        Assert.Equal((3, 3), _sim.Schemes[PowercfgSimulator.Active][ProcessorPowerPolicy.PerfBoostMode]);
        Assert.Contains($"/setactive {PowercfgSimulator.Active}", _sim.Calls);
        Assert.Contains(_audit.Recent(), w => w.Subsystem == "power-policy" && w.Operation == "apply gaming" && w.Verified == true);
        Assert.Equal(r, svc.LastApply);
    }

    [Fact]
    public async Task Never_reads_or_writes_another_scheme()
    {
        await Service().ApplyAsync(ModeCatalogue.Battery, CancellationToken.None);

        Assert.Equal((0, 0), _sim.Schemes[PowercfgSimulator.Other][ProcessorPowerPolicy.PerfEpp]);
        Assert.DoesNotContain(_sim.Calls, c => c.Contains(PowercfgSimulator.Other) || c.Contains("SCHEME_CURRENT"));
        Assert.Equal(PowercfgSimulator.Active, _sim.ActiveScheme);
    }

    [Fact]
    public async Task Originals_are_captured_once_before_the_first_write()
    {
        var svc = Service();
        await svc.ApplyAsync(ModeCatalogue.Gaming, CancellationToken.None);
        await svc.ApplyAsync(ModeCatalogue.Battery, CancellationToken.None);

        Assert.True(svc.OriginalsCaptured);
        var json = File.ReadAllText(Record);
        // Still the pre-Forge values (EPP 0, boost aggressive), not gaming's 33/efficient.
        Assert.Contains("\"Epp\": 0", json);
        Assert.Contains("\"BoostMode\": 2", json);
        Assert.DoesNotContain("\"Epp\": 33", json);
    }

    [Fact]
    public async Task A_write_that_does_not_stick_is_reported_unverified()
    {
        _sim.IgnoreWrites = true;
        var r = await Service().ApplyAsync(ModeCatalogue.Gaming, CancellationToken.None);

        Assert.False(r!.Verified);
        Assert.Contains("read back", r.Detail);
        Assert.Contains(_audit.Recent(), w => w.Operation == "apply gaming" && w.Verified == false);
    }

    [Fact]
    public async Task Standby_writes_nothing()
    {
        Assert.Null(await Service().ApplyAsync(ModeCatalogue.Standby, CancellationToken.None));
        Assert.Empty(_sim.Calls);
    }

    [Fact]
    public async Task An_unreadable_record_blocks_the_write_instead_of_being_replaced()
    {
        File.WriteAllText(Record, "{ not json");
        var r = await Service().ApplyAsync(ModeCatalogue.Gaming, CancellationToken.None);

        Assert.False(r!.Verified);
        Assert.Equal((0, 0), _sim.Schemes[PowercfgSimulator.Active][ProcessorPowerPolicy.PerfEpp]);
        Assert.Equal("{ not json", File.ReadAllText(Record));
    }

    [Fact]
    public async Task Restore_puts_the_originals_back_and_deletes_the_record()
    {
        var svc = Service();
        await svc.ApplyAsync(ModeCatalogue.Battery, CancellationToken.None);

        var (restored, _) = await svc.RestoreAsync(CancellationToken.None);

        Assert.True(restored);
        Assert.Equal((0, 0), _sim.Schemes[PowercfgSimulator.Active][ProcessorPowerPolicy.PerfEpp]);
        Assert.Equal((2, 2), _sim.Schemes[PowercfgSimulator.Active][ProcessorPowerPolicy.PerfBoostMode]);
        Assert.False(File.Exists(Record));
        Assert.Contains(_audit.Recent(), w => w.Operation == "restore" && w.Verified == true);
    }

    [Fact]
    public async Task Restore_does_not_switch_the_plan_when_the_user_moved_to_another_scheme()
    {
        var svc = Service();
        await svc.ApplyAsync(ModeCatalogue.Gaming, CancellationToken.None);
        _sim.ActiveScheme = PowercfgSimulator.Other;
        _sim.Calls.Clear();

        var (restored, _) = await svc.RestoreAsync(CancellationToken.None);

        Assert.True(restored);
        Assert.Equal(PowercfgSimulator.Other, _sim.ActiveScheme);
        Assert.DoesNotContain(_sim.Calls, c => c.StartsWith("/setactive"));
        Assert.Equal((0, 0), _sim.Schemes[PowercfgSimulator.Active][ProcessorPowerPolicy.PerfEpp]);
    }

    [Fact]
    public async Task Restore_without_a_record_writes_nothing()
    {
        var (restored, detail) = await Service().RestoreAsync(CancellationToken.None);
        Assert.False(restored);
        Assert.Contains("nothing to restore", detail);
        Assert.Empty(_sim.Calls);
    }

    [Fact]
    public async Task Read_reports_the_active_scheme_and_its_policy()
    {
        var read = await Service().ReadAsync(CancellationToken.None);
        Assert.Equal(PowercfgSimulator.Active, read.Scheme);
        Assert.Equal(new ProcessorSettings(0, 2, 100), read.Current!.Ac);
    }

    [Fact]
    public async Task Worker_applies_once_per_mode_change()
    {
        var mode = new ModeState { Active = ModeCatalogue.Gaming };
        var worker = new PowerPolicyWorker(Service(), mode);

        await worker.TickAsync(CancellationToken.None);
        var afterFirst = _sim.Calls.Count;
        await worker.TickAsync(CancellationToken.None);
        Assert.Equal(afterFirst, _sim.Calls.Count);

        mode.Active = ModeCatalogue.Battery;
        await worker.TickAsync(CancellationToken.None);
        Assert.Equal((80, 80), _sim.Schemes[PowercfgSimulator.Active][ProcessorPowerPolicy.PerfEpp]);
    }
}
