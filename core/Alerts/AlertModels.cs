namespace GpdForge.Alerts;

public enum AlertSeverity { Info, Aviso, Critica }
public enum AlertCategory { Thermal, Hardware, Service, Configuration, System }

public sealed record AlertEvent(
    Guid Id,
    DateTimeOffset TimestampUtc,
    AlertSeverity Severity,
    AlertCategory Category,
    string Title,
    string Message,
    string? TechnicalData,
    bool Acknowledged,
    string? DedupeKey);

public interface IAlertClock { DateTimeOffset UtcNow { get; } }
public sealed class SystemAlertClock : IAlertClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }
