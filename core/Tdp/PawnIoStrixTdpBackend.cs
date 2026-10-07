// GPD Forge — verified Strix Point SMU TDP backend via the signed PawnIO module. GPL-3.0-or-later.
// This backend is deliberately narrow: only code name 31 + PM table 0x5D0009 are accepted.
// No external process and no unbounded mailbox wait are used.
using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using LibreHardwareMonitor.Hardware;

namespace GpdForge.Tdp;

/// <summary>Small injectable surface for named PawnIO ioctl calls.</summary>
public interface ISmuTransport : IDisposable
{
    bool Supports(string ioctlName);
    long[] Execute(string ioctlName, long[] input, int outputCount);
}

/// <summary>Injectable wrapper for the global lock shared by SMU/PCI clients.</summary>
public interface ISmuMutex : IDisposable
{
    bool TryEnter(TimeSpan timeout, CancellationToken ct);
    void Exit();
}

/// <summary>Reflection adapter to the PawnIO RyzenSMU module embedded in LibreHardwareMonitorLib.</summary>
public sealed class PawnIoSmuTransport : ISmuTransport
{
    private const string ModuleResource = "LibreHardwareMonitor.Resources.PawnIo.RyzenSMU.bin";
    private static readonly HashSet<string> SupportedIoctls = new(StringComparer.Ordinal)
    {
        "ioctl_get_code_name",
        "ioctl_resolve_pm_table",
        "ioctl_send_smu_command",
        "ioctl_read_pm_table",
        "ioctl_read_smu_register",
        "ioctl_write_smu_register",
    };

    private readonly object _pawn;
    private readonly MethodInfo _execute;
    private readonly MethodInfo? _close;
    private readonly HashSet<string> _capabilities;
    private bool _disposed;

    public PawnIoSmuTransport() : this(LoadAdapter()) { }

    internal PawnIoSmuTransport(string moduleText, object pawn, Type pawnType)
    {
        ArgumentNullException.ThrowIfNull(moduleText);
        ArgumentNullException.ThrowIfNull(pawn);
        ArgumentNullException.ThrowIfNull(pawnType);
        _capabilities = SupportedIoctls.Where(name => moduleText.Contains(name, StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        _pawn = pawn;
        _execute = pawnType.GetMethod("Execute")
            ?? throw new InvalidOperationException("PawnIo.Execute was not found.");
        _close = pawnType.GetMethod("Close");
    }

    private PawnIoSmuTransport(Adapter adapter) : this(adapter.ModuleText, adapter.Pawn, adapter.PawnType) { }

    private static Adapter LoadAdapter()
    {
        var assembly = typeof(Computer).Assembly;
        using var resource = assembly.GetManifestResourceStream(ModuleResource)
            ?? throw new InvalidOperationException($"PawnIO module resource {ModuleResource} is missing.");
        using var bytes = new MemoryStream();
        resource.CopyTo(bytes);
        var moduleText = Encoding.Latin1.GetString(bytes.ToArray());

        var pawnType = assembly.GetType("LibreHardwareMonitor.PawnIo.PawnIo")
            ?? throw new InvalidOperationException("PawnIO type was not found in LibreHardwareMonitorLib.");
        var load = pawnType.GetMethod("LoadModuleFromResource", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("PawnIo.LoadModuleFromResource was not found.");
        var pawn = InvokeUnwrapped(() => load.Invoke(null, [pawnType.Assembly, ModuleResource]))
            ?? throw new InvalidOperationException($"Failed to load PawnIO module resource '{ModuleResource}'.");
        return new Adapter(moduleText, pawn, pawnType);
    }

    public bool Supports(string ioctlName) => _capabilities.Contains(ioctlName);

    public long[] Execute(string ioctlName, long[] input, int outputCount)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(ioctlName);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentOutOfRangeException.ThrowIfNegative(outputCount);
        if (!Supports(ioctlName)) throw new NotSupportedException($"PawnIO ioctl '{ioctlName}' is not on the Strix TDP allowlist.");

        var elementType = _execute.GetParameters()[1].ParameterType.GetElementType()
            ?? throw new InvalidOperationException("PawnIo.Execute input-array element type could not be resolved.");
        var nativeInput = Array.CreateInstance(elementType, input.Length);
        for (int i = 0; i < input.Length; i++) nativeInput.SetValue(Convert.ChangeType(input[i], elementType), i);

        object? raw = InvokeUnwrapped(() => _execute.Invoke(_pawn, [ioctlName, nativeInput, outputCount]));
        if (raw is not Array nativeOutput)
        {
            if (outputCount == 0) return [];
            throw new InvalidDataException($"PawnIO ioctl '{ioctlName}' returned no output array (expected {outputCount}).");
        }
        if (nativeOutput.Length != outputCount)
            throw new InvalidDataException($"PawnIO ioctl '{ioctlName}' returned {nativeOutput.Length} value(s), expected {outputCount}.");

        var output = new long[nativeOutput.Length];
        for (int i = 0; i < nativeOutput.Length; i++) output[i] = Convert.ToInt64(nativeOutput.GetValue(i));
        return output;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_close is not null) InvokeUnwrapped(() => _close.Invoke(_pawn, null));
    }

    private static object? InvokeUnwrapped(Func<object?> invoke)
    {
        try { return invoke(); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private sealed record Adapter(string ModuleText, object Pawn, Type PawnType);
}

/// <summary>Windows global named mutex used by other SMU/PCI tooling.</summary>
public sealed class NamedSmuMutex(string name = PawnIoStrixTdpBackend.MutexName) : ISmuMutex
{
    private readonly Mutex _mutex = new(false, name);
    private bool _entered;
    private bool _disposed;

    public bool TryEnter(TimeSpan timeout, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_entered) throw new InvalidOperationException("The SMU mutex is already held by this instance.");
        try
        {
            ct.ThrowIfCancellationRequested();
            if (ct.CanBeCanceled)
            {
                int signaled = WaitHandle.WaitAny([_mutex, ct.WaitHandle], timeout);
                if (signaled == 0) _entered = true;
                else if (signaled == 1) ct.ThrowIfCancellationRequested();
            }
            else _entered = _mutex.WaitOne(timeout);
        }
        catch (AbandonedMutexException) { _entered = true; }
        return _entered;
    }

    public void Exit()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_entered) throw new SynchronizationLockException("The SMU mutex is not held.");
        _mutex.ReleaseMutex();
        _entered = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_entered) Exit();
        _mutex.Dispose();
        _disposed = true;
    }
}

/// <summary>
/// Narrow Strix Point TDP backend. It validates chipset/table/capabilities and readback before the
/// first TDP-setting write. Applying a profile uses MP1 mailbox 0x14/0x15/0x16/0x19 in that order.
/// </summary>
public sealed class PawnIoStrixTdpBackend : ITdpBackend, IDisposable
{
    public const string MutexName = "Global\\Access_PCI";
    public const int SupportedCodeName = 31;
    public const long SupportedPmTableVersion = 0x5D0009;
    public const long PsmuRefreshTableCommand = 0x65;
    public const long Mp1MessageRegister = 0x3B10928;
    public const long Mp1ResponseRegister = 0x3B10978;
    public const long Mp1ArgumentBase = 0x3B10998;
    public const int TableFloatCount = 64;
    public const int TableQwordCount = TableFloatCount / 2;

    private static readonly string[] RequiredIoctls =
    [
        "ioctl_get_code_name", "ioctl_resolve_pm_table", "ioctl_send_smu_command",
        "ioctl_read_pm_table", "ioctl_read_smu_register", "ioctl_write_smu_register",
    ];

    private readonly object _transportGate = new();
    private readonly Func<ISmuTransport> _transportFactory;
    private readonly Func<ISmuMutex> _mutexFactory;
    private readonly TimeSpan _mutexTimeout;
    private readonly TimeSpan _responseTimeout;
    private ISmuTransport? _transport;
    private bool _disposed;

    public PawnIoStrixTdpBackend(
        Func<ISmuTransport>? transportFactory = null,
        Func<ISmuMutex>? mutexFactory = null,
        TimeSpan? mutexTimeout = null,
        TimeSpan? responseTimeout = null)
    {
        _transportFactory = transportFactory ?? (() => new PawnIoSmuTransport());
        _mutexFactory = mutexFactory ?? (() => new NamedSmuMutex());
        _mutexTimeout = mutexTimeout ?? TimeSpan.FromSeconds(5);
        _responseTimeout = responseTimeout ?? TimeSpan.FromMilliseconds(100);
        if (_mutexTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(mutexTimeout));
        if (_responseTimeout <= TimeSpan.Zero || _responseTimeout > TimeSpan.FromMilliseconds(100))
            throw new ArgumentOutOfRangeException(nameof(responseTimeout), "SMU response timeout must be in (0, 100 ms].");
    }

    public Task ApplyAsync(TdpProfile profile, CancellationToken ct)
    {
        ValidateProfile(profile);
        return Task.Run(() => WithMutex(ct, transport =>
        {
            _ = ReadValidatedTable(transport, ct);
            ct.ThrowIfCancellationRequested();
            ApplyMp1Command(transport, 0x14, checked(profile.StapmW * 1000), ct);
            ApplyMp1Command(transport, 0x15, checked(profile.FastW * 1000), ct);
            ApplyMp1Command(transport, 0x16, checked(profile.SlowW * 1000), ct);
            ApplyMp1Command(transport, 0x19, profile.TctlC, ct);
            return true;
        }), ct);
    }

    public Task<TdpReadout> ReadAsync(CancellationToken ct) => Task.Run(
        () => WithMutex(ct, transport => ReadValidatedTable(transport, ct)), ct);

    public void Dispose()
    {
        ISmuTransport? transport;
        lock (_transportGate)
        {
            if (_disposed) return;
            _disposed = true;
            transport = _transport;
            _transport = null;
        }
        transport?.Dispose();
    }

    private T WithMutex<T>(CancellationToken ct, Func<ISmuTransport, T> action)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        using var mutex = _mutexFactory();
        if (!mutex.TryEnter(_mutexTimeout, ct)) throw new TimeoutException($"Timed out after {_mutexTimeout.TotalSeconds:0.###} s waiting for {MutexName}.");
        try
        {
            ct.ThrowIfCancellationRequested();
            var transport = GetTransport();
            ValidateCapabilities(transport);
            return action(transport);
        }
        finally { mutex.Exit(); }
    }

    private ISmuTransport GetTransport()
    {
        lock (_transportGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _transport ??= _transportFactory();
        }
    }

    private static void ValidateCapabilities(ISmuTransport transport)
    {
        string? missing = RequiredIoctls.FirstOrDefault(name => !transport.Supports(name));
        if (missing is not null) throw new NotSupportedException($"PawnIO module does not advertise required ioctl '{missing}'.");
    }

    private static TdpReadout ReadValidatedTable(ISmuTransport transport, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var code = ExecuteExact(transport, "ioctl_get_code_name", [], 1);
        if (code[0] != SupportedCodeName)
            throw new NotSupportedException($"Unsupported CPU code name {code[0]}; Strix Point code name {SupportedCodeName} is required.");

        ct.ThrowIfCancellationRequested();
        var table = ExecuteExact(transport, "ioctl_resolve_pm_table", [], 2);
        if (table[0] != SupportedPmTableVersion)
            throw new NotSupportedException($"Unsupported PM table version 0x{table[0]:X}; expected 0x{SupportedPmTableVersion:X}.");
        if (table[1] == 0) throw new InvalidDataException("PawnIO resolved a zero PM table base.");

        ct.ThrowIfCancellationRequested();
        _ = ExecuteExact(transport, "ioctl_send_smu_command", [PsmuRefreshTableCommand, 0, 0, 0, 0, 0, 0], 6);
        ct.ThrowIfCancellationRequested();
        var packedWords = ExecuteExact(transport, "ioctl_read_pm_table", [], TableQwordCount);
        var floats = UnpackTableFloats(packedWords);
        var readout = new TdpReadout(
            ToPositiveWhole(floats[0], "STAPM"),
            ToPositiveWhole(floats[2], "fast PPT"),
            ToPositiveWhole(floats[4], "slow PPT"),
            ToTemperature(floats[16]));
        return readout;
    }

    private static float[] UnpackTableFloats(long[] packedWords)
    {
        if (packedWords.Length != TableQwordCount)
            throw new InvalidDataException($"PM table returned {packedWords.Length} qword(s), expected {TableQwordCount}.");
        var values = new float[TableFloatCount];
        for (int word = 0; word < packedWords.Length; word++)
        {
            ulong bits = unchecked((ulong)packedWords[word]);
            values[word * 2] = BitConverter.UInt32BitsToSingle((uint)bits);
            values[word * 2 + 1] = BitConverter.UInt32BitsToSingle((uint)(bits >> 32));
        }
        return values;
    }

    private static int ToPositiveWhole(float value, string field)
    {
        if (!float.IsFinite(value) || value < 1 || value > 100)
            throw new InvalidDataException($"PM table {field} readback is outside the supported 1-100 W range ({value}).");
        int rounded = checked((int)Math.Round(value, MidpointRounding.AwayFromZero));
        if (rounded < 1 || Math.Abs(value - rounded) > 0.25f)
            throw new InvalidDataException($"PM table {field} readback is not a plausible whole-watt value ({value}).");
        return rounded;
    }

    private static int ToTemperature(float value)
    {
        if (!float.IsFinite(value) || value < 60 || value > 110)
            throw new InvalidDataException($"PM table Tctl readback is invalid ({value}).");
        return checked((int)Math.Round(value, MidpointRounding.AwayFromZero));
    }

    private void ApplyMp1Command(ISmuTransport transport, long command, int value, CancellationToken ct)
    {
        var wait = Stopwatch.StartNew();
        long response = 0;
        do
        {
            ct.ThrowIfCancellationRequested();
            response = ReadRegister(transport, Mp1ResponseRegister);
            if (response != 0) break;
            Thread.Sleep(1);
        } while (wait.Elapsed < _responseTimeout);
        if (response == 0) throw new TimeoutException($"MP1 mailbox was busy before command 0x{command:X}; no command was sent.");

        ct.ThrowIfCancellationRequested();
        // From clearing the old response through writing the message register, setup is an atomic
        // mailbox transaction. Cancellation here would leave response=0 with no message to complete.
        WriteRegister(transport, Mp1ResponseRegister, 0);
        for (int index = 0; index < 6; index++)
            WriteRegister(transport, Mp1ArgumentBase + index * 4, index == 0 ? value : 0);
        WriteRegister(transport, Mp1MessageRegister, command);

        // Once sent, wait for this command to finish within the fixed bound even if cancellation
        // arrives. Leaving an in-flight SMU command unobserved risks starting the next mailbox write
        // against an old or cleared response. Cancellation takes effect between commands.
        wait.Restart();
        do
        {
            response = ReadRegister(transport, Mp1ResponseRegister);
            if (response != 0) break;
            Thread.Sleep(1);
        } while (wait.Elapsed < _responseTimeout);
        if (response == 0) throw new TimeoutException($"MP1 command 0x{command:X} did not respond within {_responseTimeout.TotalMilliseconds:0} ms.");
        if (response != 1) throw new InvalidOperationException($"MP1 command 0x{command:X} returned failure response 0x{response:X}.");
        ct.ThrowIfCancellationRequested();
    }

    private static long ReadRegister(ISmuTransport transport, long register) =>
        ExecuteExact(transport, "ioctl_read_smu_register", [register], 1)[0];

    private static void WriteRegister(ISmuTransport transport, long register, long value)
    {
        _ = ExecuteExact(transport, "ioctl_write_smu_register", [register, value], 0);
    }

    private static long[] ExecuteExact(ISmuTransport transport, string ioctl, long[] input, int outputCount)
    {
        var output = transport.Execute(ioctl, input, outputCount);
        if (output.Length != outputCount)
            throw new InvalidDataException($"PawnIO ioctl '{ioctl}' returned {output.Length} value(s), expected {outputCount}.");
        return output;
    }

    private static void ValidateProfile(TdpProfile profile)
    {
        ValidatePower(profile.StapmW, 40, nameof(profile.StapmW));
        ValidatePower(profile.FastW, 45, nameof(profile.FastW));
        ValidatePower(profile.SlowW, 45, nameof(profile.SlowW));
        if (profile.TctlC is < 60 or > 100)
            throw new ArgumentOutOfRangeException(nameof(profile), profile.TctlC, "Tctl must be between 60 and 100 °C.");
    }

    private static void ValidatePower(int watts, int maximum, string field)
    {
        if (watts < 1 || watts > maximum)
            throw new ArgumentOutOfRangeException(field, watts, $"{field} must be between 1 and {maximum} W.");
    }
}

