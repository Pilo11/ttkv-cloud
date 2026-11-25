namespace CloudServices.Services.Appointment;

/// <summary>Key figures of a Spielplan for overview lists and headers.</summary>
public sealed class TermineTenantSummary
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public int ClubCount { get; init; }

    public int MatchCount { get; init; }

    public int ScheduledCount { get; init; }

    public int OpenOfferCount { get; init; }

    public int DateCount { get; init; }

    public string? FirstDate { get; init; }

    public string? LastDate { get; init; }

    public int OpenCount => Math.Max(0, MatchCount - ScheduledCount);

    public int ScheduledPercent =>
        MatchCount == 0 ? 0 : (int)Math.Round(100.0 * ScheduledCount / MatchCount);

    public int BlockedCount { get; init; }

    public static TermineTenantSummary From(TermineTenantDocument tenant, DateTimeOffset createdAt = default)
    {
        var dates = tenant.Dates.Order(StringComparer.Ordinal).ToList();
        var hasRange = TermineDates.TryGetRange(tenant, out var from, out var to);
        var rangeDays = hasRange ? to.DayNumber - from.DayNumber + 1 : dates.Count;
        return new TermineTenantSummary
        {
            Id = tenant.Id,
            Name = tenant.Name,
            CreatedAt = createdAt,
            ClubCount = tenant.Clubs.Count,
            MatchCount = tenant.Matches.Count,
            ScheduledCount = tenant.Matches.Count(match => TermineRules.IsMatchScheduled(tenant, match.Id)),
            OpenOfferCount = tenant.Slots.Count(slot => string.IsNullOrEmpty(slot.GuestClubId)),
            DateCount = dates.Count,
            BlockedCount = Math.Max(0, rangeDays - dates.Count),
            FirstDate = hasRange ? TermineDates.ToIso(from) : null,
            LastDate = hasRange ? TermineDates.ToIso(to) : null
        };
    }

    public static async Task<IReadOnlyList<TermineTenantSummary>> LoadAllAsync(
        TermineStore store,
        CancellationToken cancellationToken = default)
    {
        var entries = await store.ListAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<TermineTenantSummary>(entries.Count);
        foreach (var entry in entries)
        {
            var tenant = await store.GetByIdAsync(entry.Id, cancellationToken).ConfigureAwait(false);
            result.Add(tenant is null
                ? new TermineTenantSummary { Id = entry.Id, Name = entry.Name, CreatedAt = entry.CreatedAt }
                : From(tenant, entry.CreatedAt));
        }

        return result;
    }
}
