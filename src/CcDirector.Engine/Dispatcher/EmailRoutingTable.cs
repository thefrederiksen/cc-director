namespace CcDirector.Engine.Dispatcher;

/// <summary>
/// Lookup table mapping email addresses to the tool routes that can send from them.
/// Built by <see cref="EmailToolDiscovery"/> at startup.
/// </summary>
public sealed class EmailRoutingTable
{
    private readonly Dictionary<string, EmailRoute> _routes;

    public EmailRoutingTable(IEnumerable<EmailRoute> routes)
    {
        _routes = new Dictionary<string, EmailRoute>(StringComparer.OrdinalIgnoreCase);
        foreach (var route in routes)
        {
            _routes.TryAdd(route.EmailAddress, route);
        }
    }

    /// <summary>Find the route for a given email address (case-insensitive).</summary>
    public EmailRoute? FindRoute(string emailAddress)
    {
        return _routes.TryGetValue(emailAddress, out var route) ? route : null;
    }

    /// <summary>All discovered routes.</summary>
    public IReadOnlyCollection<EmailRoute> AllRoutes => _routes.Values;

    /// <summary>Number of routes in the table.</summary>
    public int Count => _routes.Count;
}
