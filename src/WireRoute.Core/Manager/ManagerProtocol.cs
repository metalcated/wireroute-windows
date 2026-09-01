using System.Text.Json;
using System.Text.Json.Serialization;
using WireRoute.Core.Routing;

namespace WireRoute.Core.Manager;

public static class ManagerProtocol
{
    public const string Name = "wireroute-manager";
    public const int CurrentVersion = 1;
    public const int MaximumFrameLength = 1024 * 1024;
}

public static class ManagerMethods
{
    public const string Hello = "hello";
    public const string ListProfiles = "profiles.list";
    public const string GetProfile = "profiles.get";
    public const string ImportProfile = "profiles.import";
    public const string GetTunnelState = "tunnel.state";
    public const string StartTunnel = "tunnel.start";
    public const string StopTunnel = "tunnel.stop";
    public const string QuitManager = "manager.quit";
}

public static class ManagerEvents
{
    public const string ProfilesChanged = "profiles.changed";
    public const string TunnelStateChanged = "tunnel.stateChanged";
    public const string ManagerStopping = "manager.stopping";
}

public sealed record ManagerRequest(
    int Version,
    long RequestId,
    string Method,
    JsonElement? Parameters)
{
    public static ManagerRequest Create<T>(long requestId, string method, T parameters) => new(
        ManagerProtocol.CurrentVersion,
        requestId,
        method,
        ManagerProtocolJson.ToElement(parameters));
}

public sealed record ManagerResponse(
    int Version,
    long RequestId,
    JsonElement? Result,
    ManagerError? Error)
{
    public static ManagerResponse Success<T>(long requestId, T result) => new(
        ManagerProtocol.CurrentVersion,
        requestId,
        ManagerProtocolJson.ToElement(result),
        null);

    public static ManagerResponse Failure(long requestId, string code, string message) => new(
        ManagerProtocol.CurrentVersion,
        requestId,
        null,
        new ManagerError(code, message));

    public T GetRequiredResult<T>()
    {
        if (Error is not null && Result is not null)
        {
            throw new ManagerProtocolException("The manager response contained both a result and an error.");
        }

        if (Error is not null)
        {
            throw new ManagerRemoteException(Error.Code, Error.Message);
        }

        if (Result is null)
        {
            throw new ManagerProtocolException("The manager response did not contain a result.");
        }

        return ManagerProtocolJson.FromElement<T>(Result.Value);
    }
}

public sealed record ManagerError(string Code, string Message);

public sealed record ManagerEvent(
    int Version,
    long Sequence,
    string Event,
    JsonElement? Payload)
{
    public T GetRequiredPayload<T>()
    {
        if (Payload is null)
        {
            throw new ManagerProtocolException("The manager event did not contain a payload.");
        }

        return ManagerProtocolJson.FromElement<T>(Payload.Value);
    }
}

public sealed record ManagerHelloRequest(
    string Protocol,
    int MinimumVersion,
    int MaximumVersion,
    string ClientVersion,
    string Architecture)
{
    public static ManagerHelloRequest Create(string clientVersion, string architecture) => new(
        ManagerProtocol.Name,
        ManagerProtocol.CurrentVersion,
        ManagerProtocol.CurrentVersion,
        clientVersion,
        architecture);
}

public sealed record ManagerHelloResponse(
    string Protocol,
    int SelectedVersion,
    string ManagerVersion,
    ManagerCapabilities Capabilities);

public sealed record ManagerCapabilities(
    bool CanListProfiles,
    bool CanReadProfileDetails,
    bool CanReadTunnelState,
    bool CanImportProfiles,
    bool CanStartTunnels,
    bool CanStopTunnels,
    bool CanQuitManager = false);

public sealed record ManagerEmpty;

public enum ManagerTunnelState
{
    Unknown,
    Stopped,
    Starting,
    Started,
    Stopping,
}

public sealed record ManagerListProfilesRequest;

public sealed record ManagerListProfilesResponse(IReadOnlyList<ManagerProfileSummary> Profiles);

public sealed record ManagerProfileSummary(
    string Name,
    string DisplayName,
    ManagerTunnelState State,
    TunnelRouteMode DetectedRouteMode,
    string? InterfacePublicKey = null);

public sealed record ManagerGetProfileRequest(string Name);

public sealed record ManagerGetTunnelStateRequest(string Name);

public sealed record ManagerGetTunnelStateResponse(string Name, ManagerTunnelState State);

public sealed record ManagerImportProfileRequest(
    string DisplayName,
    string WgQuickConfiguration);

public sealed record ManagerImportProfileResponse(ManagerProfileSummary Profile);

public sealed record ManagerTunnelCommandRequest(string Name);

public sealed record ManagerTunnelCommandResponse(string Name, ManagerTunnelState State);

public sealed record ManagerQuitRequest(bool StopTunnels);

public sealed record ManagerQuitResponse(bool AlreadyQuit);

public sealed record ManagerProfileDetail(
    string Name,
    string DisplayName,
    IReadOnlyList<string> InterfaceAddresses,
    IReadOnlyList<ManagerDnsServer> DnsServers,
    IReadOnlyList<string> DnsSearchDomains,
    IReadOnlyList<ManagerPeerDetail> Peers,
    TunnelRouteMode DetectedRouteMode,
    bool HasHooks);

public sealed record ManagerDnsServer(string Address, DnsServerRoute Route);

public sealed record ManagerPeerDetail(
    string PublicKey,
    bool HasPresharedKey,
    IReadOnlyList<string> AllowedIps,
    string? Endpoint,
    ushort? PersistentKeepalive);

public sealed record ManagerProfilesChangedEvent(IReadOnlyList<string> ProfileNames);

public sealed record ManagerTunnelStateChangedEvent(string Name, ManagerTunnelState State, string? ErrorCode);

public sealed record ManagerStoppingEvent(string Reason);

public static class ManagerProtocolJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static T Deserialize<T>(ReadOnlySpan<byte> json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options)
                ?? throw new ManagerProtocolException("The manager message was empty.");
        }
        catch (JsonException exception)
        {
            throw new ManagerProtocolException("The manager message was not valid protocol JSON.", exception);
        }
    }

    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    public static T FromElement<T>(JsonElement element)
    {
        try
        {
            return element.Deserialize<T>(Options)
                ?? throw new ManagerProtocolException("The manager payload was empty.");
        }
        catch (JsonException exception)
        {
            throw new ManagerProtocolException("The manager payload did not match the protocol contract.", exception);
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            MaxDepth = 32,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            RespectNullableAnnotations = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

public class ManagerProtocolException : IOException
{
    public ManagerProtocolException(string message)
        : base(message)
    {
    }

    public ManagerProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class ManagerRemoteException : ManagerProtocolException
{
    public ManagerRemoteException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
