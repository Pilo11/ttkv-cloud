namespace CloudServices.Services.Appointment;

public sealed class TermineTenantIndexEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class TermineIndexDocument
{
    public List<TermineTenantIndexEntry> Tenants { get; set; } = [];
}
