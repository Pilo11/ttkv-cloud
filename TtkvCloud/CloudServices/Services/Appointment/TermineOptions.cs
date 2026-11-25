namespace CloudServices.Services.Appointment;

public sealed class TermineOptions
{
    public string? DataDirectory { get; set; }

    /// <summary>
    /// Secret path segment for the Staffelleitung (/staffelleitung/{AdminKey}).
    /// When empty, a random key is generated once and kept in the data directory.
    /// </summary>
    public string? AdminKey { get; set; }
}
