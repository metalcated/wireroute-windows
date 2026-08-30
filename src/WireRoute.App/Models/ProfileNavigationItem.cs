namespace WireRoute.App.Models;

public sealed class ProfileNavigationItem
{
    public ProfileNavigationItem(string name, string status, string routingLabel)
    {
        Name = name;
        Status = status;
        RoutingLabel = routingLabel;
    }

    public string Name { get; set; }

    public string Status { get; set; }

    public string RoutingLabel { get; set; }
}
