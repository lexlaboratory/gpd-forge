namespace GpdForge.Alerts;

public enum AlertSeverity { Info, Aviso, Critica }
public enum AlertCategory { Thermal, Hardware, Service, Configuration, System }

/// <summary>
/// One alert as the UI sees it. A continuous phenomenon (the CPU sitting on the throttle threshold
/// for minutes) is a SINGLE event that keeps being re-observed, so <see cref="TimestampUtc"/> is the
/// first occurrence, <see cref="LastSeenUtc"/> the most recent one and <see cref="Count"/> how many
/// times it has been published. <see cref="Message"/> and <see cref="TechnicalData"/> always hold the
/// latest reading — the alert stays current instead of freezing on the first sample.
/// </summary>
public sealed record AlertEvent(
    Guid Id,
    DateTimeOffset TimestampUtc,
    AlertSeverity Severity,
    AlertCategory Category,
    string Title,
    string Message,
    string? TechnicalData,
    bool Acknowledged,
    string? DedupeKey,
    int Count = 1,
    DateTimeOffset LastSeenUtc = default);

public interface IAlertClock { DateTimeOffset UtcNow { get; } }
public sealed class SystemAlertClock : IAlertClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }
