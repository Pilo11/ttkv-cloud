namespace CloudServices.Services.Appointment;

/// <summary>
/// Route constraint "staffelkey": a wrong key matches no route at all, so every
/// request (GET or POST) to an unknown key gets the same plain 404.
/// </summary>
public sealed class StaffelleitungKeyConstraint : IRouteConstraint
{
    public const string Name = "staffelkey";

    private readonly StaffelleitungAccess _access;

    public StaffelleitungKeyConstraint(StaffelleitungAccess access)
    {
        _access = access;
    }

    public bool Match(
        HttpContext? httpContext,
        IRouter? route,
        string routeKey,
        RouteValueDictionary values,
        RouteDirection routeDirection)
    {
        return values.TryGetValue(routeKey, out var value)
            && _access.IsValid(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
    }
}
