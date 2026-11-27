using CloudServices.Services.Appointment;

namespace CloudServices.Pages.Appointment;

/// <summary>Public list of Spielpläne; each opens <see cref="TargetPage"/> (Terminplanung or Termineingabe).</summary>
public sealed record PlanList(IReadOnlyList<TermineTenantSummary> Plans, string TargetPage);
