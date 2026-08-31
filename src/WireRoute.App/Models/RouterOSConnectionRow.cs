using WireRoute.Storage;

namespace WireRoute.App.Models;

public sealed class RouterOSConnectionRow
{
    public RouterOSConnectionRow()
        : this(new RouterOSStoredConnection(
            Guid.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            null))
    {
    }

    internal RouterOSConnectionRow(RouterOSStoredConnection connection)
    {
        Connection = connection;
        Id = connection.Id;
        Name = connection.Name;
        Url = connection.Url;
        DefaultInterface = connection.DefaultInterface ?? "Automatic";
    }

    public Guid Id { get; set; }

    public string Name { get; set; }

    public string Url { get; set; }

    public string DefaultInterface { get; set; }

    internal RouterOSStoredConnection Connection { get; }
}
