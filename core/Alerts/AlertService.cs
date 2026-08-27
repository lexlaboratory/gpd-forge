namespace GpdForge.Alerts;

public sealed record AlertSummary(int Unread, int UnreadInfo, int UnreadAviso, int UnreadCritica, AlertEvent? Latest);

public sealed class AlertService
{
    private readonly AlertStore store;
    public AlertService(AlertStore store) => this.store = store;
    public IReadOnlyList<AlertEvent> List(bool unreadOnly = false, int? limit = null) => store.List(unreadOnly, limit);
    public AlertEvent Publish(AlertCategory category, AlertSeverity severity, string title, string message, string? technicalData = null, string? dedupeKey = null)
        => store.Publish(category, severity, title, message, technicalData, dedupeKey);
    public bool Acknowledge(Guid id) => store.Acknowledge(id);
    public int AcknowledgeAll() => store.AcknowledgeAll();
    public bool Delete(Guid id) => store.Delete(id);
    public AlertSummary Summary()
    {
        var all = store.List();
        var unread = all.Where(x => !x.Acknowledged).ToArray();
        return new AlertSummary(unread.Length,
            unread.Count(x => x.Severity == AlertSeverity.Info),
            unread.Count(x => x.Severity == AlertSeverity.Aviso),
            unread.Count(x => x.Severity == AlertSeverity.Critica),
            all.FirstOrDefault());
    }
}
