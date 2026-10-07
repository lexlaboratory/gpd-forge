using GpdForge.Tdp;
using System.Reflection;
using Xunit;

namespace GpdForge.Core.Tests;

public sealed class PawnIoStrixTdpBackendTests
{
    private static readonly TdpProfile Requested = new(StapmW: 18, FastW: 24, SlowW: 22, TctlC: 100);
    private const string WriteRegister = "ioctl_write_smu_register";
    private const long MessageRegister = 0x3B10928;
    private const long ResponseRegister = 0x3B10978;
    private const long ArgumentBase = 0x3B10998;

    [Fact]
    public async Task Read_unpacks_StrixPoint_PM_table_values_from_64_float_words()
    {
        var transport = new FakeSmuTransport();
        using var backend = Build(transport);

        var observed = await backend.ReadAsync(CancellationToken.None);

        Assert.Equal(18, observed.StapmW);
        Assert.Equal(24, observed.PptW);
        Assert.Equal(22, observed.PptSlowW);
        Assert.Equal(100, observed.TctlC);
        var refresh = Assert.Single(transport.Calls, c => c.Name == "ioctl_send_smu_command");
        Assert.Equal(new long[] { 0x65, 0, 0, 0, 0, 0, 0 }, refresh.Input);
        Assert.Equal(6, refresh.OutCount);
        Assert.DoesNotContain(transport.Calls, c => c.Name == "ioctl_update_pm_table");
    }

    [Fact]
    public async Task Apply_writes_MP1_STAPM_fast_slow_then_Tctl_in_mW_and_Celsius()
    {
        var transport = new FakeSmuTransport();
        using var backend = Build(transport);

        await backend.ApplyAsync(Requested, CancellationToken.None);

        var messages = transport.Calls
            .Where(c => c.Name == WriteRegister && c.Input.Length == 2 && c.Input[0] == MessageRegister)
            .Select(c => c.Input[1])
            .ToArray();
        Assert.Equal(new long[] { 0x14, 0x15, 0x16, 0x19 }, messages);

        var args = new List<long>();
        foreach (var call in transport.Calls.Where(c => c.Name == WriteRegister && c.Input.Length == 2))
        {
            if (call.Input[0] == ArgumentBase) args.Add(call.Input[1]);
        }
        Assert.Equal(new long[] { 18_000, 24_000, 22_000, 100 }, args);
    }

    [Fact]
    public async Task Unsupported_code_name_is_rejected_before_any_write()
    {
        var transport = new FakeSmuTransport { CodeName = 30 };
        using var backend = Build(transport);

        await Assert.ThrowsAsync<NotSupportedException>(() => backend.ApplyAsync(Requested, CancellationToken.None));

        Assert.DoesNotContain(transport.Calls, IsRegisterWrite);
        Assert.DoesNotContain(transport.Calls, c => c.Name == "ioctl_send_smu_command");
    }

    [Fact]
    public async Task Unsupported_PM_table_version_is_rejected_before_any_write()
    {
        var transport = new FakeSmuTransport { TableVersion = 0x5D0008 };
        using var backend = Build(transport);

        await Assert.ThrowsAsync<NotSupportedException>(() => backend.ApplyAsync(Requested, CancellationToken.None));

        Assert.DoesNotContain(transport.Calls, IsRegisterWrite);
        Assert.DoesNotContain(transport.Calls, c => c.Name == "ioctl_send_smu_command");
    }

    [Fact]
    public async Task Missing_write_register_capability_is_rejected_before_hardware_access()
    {
        var transport = new FakeSmuTransport { SupportsWriteRegister = false };
        using var backend = Build(transport);

        await Assert.ThrowsAsync<NotSupportedException>(() => backend.ApplyAsync(Requested, CancellationToken.None));

        Assert.Empty(transport.Calls);
    }

    [Fact]
    public async Task All_zero_PM_table_is_unknown_and_never_applies_zero_or_requested_limits()
    {
        var transport = new FakeSmuTransport { AllZeroTable = true };
        using var backend = Build(transport);

        await Assert.ThrowsAsync<InvalidDataException>(() => backend.ApplyAsync(Requested, CancellationToken.None));

        Assert.DoesNotContain(transport.Calls, IsRegisterWrite);
    }

    [Fact]
    public async Task Tiny_positive_limit_that_would_round_to_zero_is_unknown()
    {
        var transport = new FakeSmuTransport { StapmReadback = 0.5f };
        using var backend = Build(transport);
        await Assert.ThrowsAsync<InvalidDataException>(() => backend.ReadAsync(CancellationToken.None));
        Assert.DoesNotContain(transport.Calls, IsRegisterWrite);
    }

    [Fact]
    public async Task Implausible_board_power_readback_is_rejected()
    {
        var transport = new FakeSmuTransport { FastReadback = 101f };
        using var backend = Build(transport);
        await Assert.ThrowsAsync<InvalidDataException>(() => backend.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Implausible_temperature_readback_is_rejected()
    {
        var transport = new FakeSmuTransport { TctlReadback = 111f };
        using var backend = Build(transport);
        await Assert.ThrowsAsync<InvalidDataException>(() => backend.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_after_first_MP1_command_prevents_later_command_writes()
    {
        using var cts = new CancellationTokenSource();
        var transport = new FakeSmuTransport { OnMessage = command => { if (command == 0x14) cts.Cancel(); } };
        using var backend = Build(transport);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backend.ApplyAsync(Requested, cts.Token));
        var commands = transport.Calls.Where(c => c.Name == WriteRegister && c.Input[0] == MessageRegister).Select(c => c.Input[1]);
        Assert.Equal(new long[] { 0x14 }, commands);
    }

    [Fact]
    public async Task Cancellation_during_mailbox_setup_finishes_the_in_flight_command_atomically()
    {
        using var cts = new CancellationTokenSource();
        var transport = new FakeSmuTransport { OnResponseReset = () => cts.Cancel() };
        using var backend = Build(transport);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backend.ApplyAsync(Requested, cts.Token));

        var writes = transport.Calls.Where(IsRegisterWrite).ToArray();
        Assert.Equal(8, writes.Length); // response reset, six argument registers, message send
        Assert.Equal(ResponseRegister, writes[0].Input[0]);
        Assert.Equal(MessageRegister, writes[^1].Input[0]);
        Assert.Equal(0x14, writes[^1].Input[1]);
    }

    [Fact]
    public async Task Profile_accepts_existing_mode_ceiling_for_fast_and_slow_PPT()
    {
        var transport = new FakeSmuTransport();
        using var backend = Build(transport);

        await backend.ApplyAsync(new TdpProfile(40, 45, 45, 100), CancellationToken.None);

        Assert.Equal(new long[] { 0x14, 0x15, 0x16, 0x19 }, transport.Calls
            .Where(c => c.Name == WriteRegister && c.Input.Length == 2 && c.Input[0] == MessageRegister)
            .Select(c => c.Input[1]));
    }

    [Theory]
    [InlineData(0, 20, 20, 90)]
    [InlineData(41, 40, 40, 90)]
    [InlineData(20, 46, 40, 90)]
    [InlineData(20, 40, 46, 90)]
    [InlineData(20, 20, 20, 59)]
    [InlineData(20, 20, 20, 101)]
    public async Task Out_of_range_profiles_are_rejected_before_transport_creation(int stapm, int fast, int slow, int tctl)
    {
        var factory = new RecordingTransportFactory(new FakeSmuTransport());
        using var backend = Build(factory);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => backend.ApplyAsync(new TdpProfile(stapm, fast, slow, tctl), CancellationToken.None));

        Assert.Equal(0, factory.CreateCount);
    }

    [Fact]
    public async Task Busy_global_mutex_prevents_all_hardware_calls()
    {
        var transport = new FakeSmuTransport();
        var mutex = new FakeSmuMutex { Acquired = false };
        using var backend = Build(transport, () => mutex);

        await Assert.ThrowsAsync<TimeoutException>(() => backend.ApplyAsync(Requested, CancellationToken.None));

        Assert.Empty(transport.Calls);
        Assert.False(mutex.Released);
        Assert.True(mutex.Disposed);
    }

    [Fact]
    public async Task Non_success_SMU_response_fails_apply_instead_of_reporting_success()
    {
        var transport = new FakeSmuTransport { CommandResponse = 0xFC };
        using var backend = Build(transport, responseTimeout: TimeSpan.FromMilliseconds(20));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => backend.ApplyAsync(Requested, CancellationToken.None));

        Assert.Contains("response", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(transport.Calls, c => c.Name == WriteRegister && c.Input[0] == MessageRegister);
    }

    [Fact]
    public async Task Missing_SMU_response_times_out_within_the_bounded_window()
    {
        var transport = new FakeSmuTransport { CommandResponse = 0 };
        using var backend = Build(transport, responseTimeout: TimeSpan.FromMilliseconds(30));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(() => backend.ApplyAsync(Requested, CancellationToken.None));

        Assert.InRange(watch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(2));
        Assert.Single(transport.Calls, c => c.Name == WriteRegister && c.Input[0] == MessageRegister);
    }

    [Fact]
    public async Task Busy_mailbox_times_out_without_clearing_response_or_sending_a_command()
    {
        var transport = new FakeSmuTransport { InitialResponse = 0 };
        using var backend = Build(transport, responseTimeout: TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAsync<TimeoutException>(() => backend.ApplyAsync(Requested, CancellationToken.None));

        Assert.DoesNotContain(transport.Calls, IsRegisterWrite);
    }

    [Fact]
    public async Task Dispose_closes_the_lazy_transport_once()
    {
        var transport = new FakeSmuTransport();
        var backend = Build(transport);
        await backend.ReadAsync(CancellationToken.None);
        backend.Dispose();
        Assert.True(transport.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => backend.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Successful_commands_release_mutex_even_when_followup_read_fails()
    {
        var transport = new FakeSmuTransport { ThrowOnTableRead = true };
        var mutex = new FakeSmuMutex();
        using var backend = Build(transport, () => mutex);

        await Assert.ThrowsAsync<InvalidOperationException>(() => backend.ReadAsync(CancellationToken.None));

        Assert.True(mutex.Released);
        Assert.True(mutex.Disposed);
    }

    [Fact]
    public void PawnIO_reflection_transport_converts_values_checks_capabilities_and_disposes_once()
    {
        var pawn = new FakePawnApi { Result = [7, 11] };
        using var transport = CreateTransport(pawn, WriteRegister);

        Assert.True(transport.Supports(WriteRegister));
        Assert.False(transport.Supports("ioctl_not_allowlisted"));
        Assert.Equal(new long[] { 7, 11 }, transport.Execute(WriteRegister, [1, 2], 2));
        Assert.Equal(new long[] { 1, 2 }, pawn.LastInput);
        Assert.Throws<NotSupportedException>(() => transport.Execute("ioctl_not_allowlisted", [], 0));
        transport.Dispose();
        transport.Dispose();
        Assert.Equal(1, pawn.CloseCount);
        Assert.Throws<ObjectDisposedException>(() => transport.Execute(WriteRegister, [], 0));
    }

    [Fact]
    public void PawnIO_reflection_transport_rejects_wrong_output_and_unwraps_invoke_errors()
    {
        var wrongLength = CreateTransport(new FakePawnApi { Result = [3] }, "ioctl_read_smu_register");
        Assert.Throws<InvalidDataException>(() => wrongLength.Execute("ioctl_read_smu_register", [], 2));
        wrongLength.Dispose();

        var throwing = CreateTransport(new FakePawnApi { Throws = true }, "ioctl_read_smu_register");
        var error = Assert.Throws<InvalidOperationException>(() => throwing.Execute("ioctl_read_smu_register", [], 1));
        Assert.Equal("fake PawnIO failure", error.Message);
        throwing.Dispose();
    }

    [Fact]
    public void PawnIO_reflection_transport_allows_null_output_for_write_only_calls()
    {
        var transport = CreateTransport(new FakePawnApi { Result = null }, WriteRegister);
        Assert.Empty(transport.Execute(WriteRegister, [1, 2], 0));
        transport.Dispose();
    }

    [Fact]
    public void Named_mutex_supports_cancellable_and_uncancellable_acquisition_and_disposal()
    {
        var mutex = new NamedSmuMutex($"GpdForge.Core.Tests.{Guid.NewGuid():N}");
        Assert.True(mutex.TryEnter(TimeSpan.FromMilliseconds(100), CancellationToken.None));
        Assert.Throws<InvalidOperationException>(() => mutex.TryEnter(TimeSpan.Zero, CancellationToken.None));
        mutex.Exit();

        using var cts = new CancellationTokenSource();
        Assert.True(mutex.TryEnter(TimeSpan.FromMilliseconds(100), cts.Token));
        mutex.Dispose(); // releases the held mutex before disposing its handle
        Assert.Throws<ObjectDisposedException>(() => mutex.TryEnter(TimeSpan.Zero, CancellationToken.None));
    }

    private static bool IsRegisterWrite(RecordedCall call) => call.Name == WriteRegister;

    private static PawnIoSmuTransport CreateTransport(FakePawnApi pawn, params string[] capabilities) =>
        new(string.Join('\n', capabilities), pawn, typeof(FakePawnApi));

    private static PawnIoStrixTdpBackend Build(
        FakeSmuTransport transport,
        Func<ISmuMutex>? mutexFactory = null,
        TimeSpan? responseTimeout = null) =>
        Build(new RecordingTransportFactory(transport), mutexFactory, responseTimeout);

    private static PawnIoStrixTdpBackend Build(
        RecordingTransportFactory factory,
        Func<ISmuMutex>? mutexFactory = null,
        TimeSpan? responseTimeout = null) =>
        new(factory.Create, mutexFactory ?? (() => new FakeSmuMutex()), responseTimeout: responseTimeout ?? TimeSpan.FromMilliseconds(100));

    private sealed record RecordedCall(string Name, long[] Input, int OutCount);

    private sealed class RecordingTransportFactory(FakeSmuTransport transport)
    {
        public int CreateCount { get; private set; }
        public ISmuTransport Create() { CreateCount++; return transport; }
    }

    private sealed class FakeSmuTransport : ISmuTransport
    {
        public int CodeName { get; init; } = 31;
        public long TableVersion { get; init; } = 0x5D0009;
        public uint TableBase { get; init; } = 0x74100000;
        public bool AllZeroTable { get; init; }
        public float StapmReadback { get; init; } = 18f;
        public float FastReadback { get; init; } = 24.000002f;
        public float SlowReadback { get; init; } = 22f;
        public float TctlReadback { get; init; } = 100f;
        public Action<long>? OnMessage { get; init; }
        public Action? OnResponseReset { get; init; }
        public bool SupportsWriteRegister { get; init; } = true;
        public long CommandResponse { get; init; } = 1;
        public bool ThrowOnTableRead { get; init; }
        public long InitialResponse { get; init; } = 1;
        public bool Disposed { get; private set; }
        private long _response = 1;
        private bool _responseInitialised;
        public List<RecordedCall> Calls { get; } = [];

        public bool Supports(string ioctlName) => ioctlName != WriteRegister || SupportsWriteRegister;

        public long[] Execute(string ioctlName, long[] input, int outputCount)
        {
            Calls.Add(new RecordedCall(ioctlName, [.. input], outputCount));
            return ioctlName switch
            {
                "ioctl_get_code_name" => [CodeName],
                "ioctl_get_smu_version" => [0x5D0000],
                "ioctl_resolve_pm_table" => [TableVersion, TableBase],
                "ioctl_send_smu_command" => [.. new long[outputCount]],
                "ioctl_read_pm_table" when ThrowOnTableRead => throw new InvalidOperationException("synthetic table read failure"),
                "ioctl_read_pm_table" => PackTable(),
                "ioctl_read_smu_register" => [input[0] == ResponseRegister ? ReadResponse() : 0],
                WriteRegister => Write(input),
                _ => throw new NotSupportedException(ioctlName),
            };
        }

        private long[] PackTable()
        {
            var words = new long[32]; // 64 IEEE-754 singles packed two per qword.
            if (AllZeroTable) return words;
            PutFloat(words, 0, StapmReadback);
            PutFloat(words, 2, FastReadback);
            PutFloat(words, 4, SlowReadback);
            PutFloat(words, 16, TctlReadback);
            return words;
        }

        private static void PutFloat(long[] words, int floatIndex, float value)
        {
            var bits = BitConverter.SingleToUInt32Bits(value);
            int wordIndex = floatIndex / 2;
            int shift = (floatIndex % 2) * 32;
            words[wordIndex] |= (long)bits << shift;
        }

        private long[] Write(long[] input)
        {
            if (input[0] == ResponseRegister) { _response = input[1]; if (input[1] == 0) OnResponseReset?.Invoke(); }
            if (input[0] == MessageRegister) { _response = CommandResponse; OnMessage?.Invoke(input[1]); }
            return [];
        }

        private long ReadResponse()
        {
            if (!_responseInitialised)
            {
                _response = InitialResponse;
                _responseInitialised = true;
            }
            return _response;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeSmuMutex : ISmuMutex
    {
        public bool Acquired { get; init; } = true;
        public bool Released { get; private set; }
        public bool Disposed { get; private set; }
        public bool TryEnter(TimeSpan timeout, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Acquired; }
        public void Exit() => Released = true;
        public void Dispose() => Disposed = true;
    }

    private sealed class FakePawnApi
    {
        public long[]? Result { get; init; }
        public bool Throws { get; init; }
        public long[]? LastInput { get; private set; }
        public int CloseCount { get; private set; }

        public long[]? Execute(string ioctlName, long[] input, int outputCount)
        {
            LastInput = input;
            if (Throws) throw new InvalidOperationException("fake PawnIO failure");
            return Result;
        }

        public void Close() => CloseCount++;
    }
}

