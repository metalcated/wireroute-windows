using System.Text.Json;
using System.Text.Json.Serialization;

namespace WireRoute.RouterOS;

public sealed record RouterOSCredentials(string Username, string Password)
{
    public override string ToString() => $"RouterOSCredentials(username: {Username}, password: <redacted>)";
}

[JsonConverter(typeof(RouterOSWireGuardInterfaceConverter))]
public sealed record RouterOSWireGuardInterface(
    string Id,
    string Name,
    int? Mtu,
    int? ListenPort,
    string PublicKey,
    bool IsDisabled,
    bool IsRunning);

[JsonConverter(typeof(RouterOSWireGuardPeerConverter))]
public sealed record RouterOSWireGuardPeer(
    string Id,
    string InterfaceName,
    string? Name,
    string? Comment,
    string PublicKey,
    IReadOnlyList<string> AllowedAddresses,
    string? EndpointAddress,
    int? EndpointPort,
    string? CurrentEndpointAddress,
    int? CurrentEndpointPort,
    string? PersistentKeepalive,
    string? LastHandshake,
    ulong? ReceivedBytes,
    ulong? TransmittedBytes,
    bool IsDisabled,
    bool IsDynamic,
    bool IsResponder);

[JsonConverter(typeof(RouterOSIpAddressConverter))]
public sealed record RouterOSIpAddress(
    string Id,
    string Address,
    string? Network,
    string InterfaceName,
    string? ActualInterfaceName,
    bool IsDisabled,
    bool IsDynamic,
    bool IsInvalid);

internal static class RouterOSJson
{
    public static string RequiredString(JsonElement root, string propertyName) =>
        String(root, propertyName)
        ?? throw new JsonException($"RouterOS did not return {propertyName}.");

    public static string? String(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property)
            || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => null,
        };
    }

    public static int? Integer(JsonElement root, string propertyName) =>
        int.TryParse(String(root, propertyName), out var value) ? value : null;

    public static ulong? UnsignedInteger(JsonElement root, string propertyName) =>
        ulong.TryParse(String(root, propertyName), out var value) ? value : null;

    public static bool? Boolean(JsonElement root, string propertyName) =>
        String(root, propertyName)?.ToLowerInvariant() switch
        {
            "true" or "yes" or "1" => true,
            "false" or "no" or "0" => false,
            _ => null,
        };

    public static IReadOnlyList<string> List(JsonElement root, string propertyName) =>
        String(root, propertyName)?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        ?? [];
}

internal sealed class RouterOSWireGuardInterfaceConverter : JsonConverter<RouterOSWireGuardInterface>
{
    public override RouterOSWireGuardInterface Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        return new RouterOSWireGuardInterface(
            RouterOSJson.RequiredString(root, ".id"),
            RouterOSJson.RequiredString(root, "name"),
            RouterOSJson.Integer(root, "mtu"),
            RouterOSJson.Integer(root, "listen-port"),
            RouterOSJson.RequiredString(root, "public-key"),
            RouterOSJson.Boolean(root, "disabled") ?? false,
            RouterOSJson.Boolean(root, "running") ?? false);
    }

    public override void Write(
        Utf8JsonWriter writer,
        RouterOSWireGuardInterface value,
        JsonSerializerOptions options) =>
        throw new NotSupportedException("RouterOS discovery models are read-only.");
}

internal sealed class RouterOSWireGuardPeerConverter : JsonConverter<RouterOSWireGuardPeer>
{
    public override RouterOSWireGuardPeer Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        return new RouterOSWireGuardPeer(
            RouterOSJson.RequiredString(root, ".id"),
            RouterOSJson.RequiredString(root, "interface"),
            RouterOSJson.String(root, "name"),
            RouterOSJson.String(root, "comment"),
            RouterOSJson.RequiredString(root, "public-key"),
            RouterOSJson.List(root, "allowed-address"),
            RouterOSJson.String(root, "endpoint-address"),
            RouterOSJson.Integer(root, "endpoint-port"),
            RouterOSJson.String(root, "current-endpoint-address"),
            RouterOSJson.Integer(root, "current-endpoint-port"),
            RouterOSJson.String(root, "persistent-keepalive"),
            RouterOSJson.String(root, "last-handshake"),
            RouterOSJson.UnsignedInteger(root, "rx"),
            RouterOSJson.UnsignedInteger(root, "tx"),
            RouterOSJson.Boolean(root, "disabled") ?? false,
            RouterOSJson.Boolean(root, "dynamic") ?? false,
            RouterOSJson.Boolean(root, "responder") ?? false);
    }

    public override void Write(Utf8JsonWriter writer, RouterOSWireGuardPeer value, JsonSerializerOptions options) =>
        throw new NotSupportedException("RouterOS discovery models are read-only.");
}

internal sealed class RouterOSIpAddressConverter : JsonConverter<RouterOSIpAddress>
{
    public override RouterOSIpAddress Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        return new RouterOSIpAddress(
            RouterOSJson.RequiredString(root, ".id"),
            RouterOSJson.RequiredString(root, "address"),
            RouterOSJson.String(root, "network"),
            RouterOSJson.RequiredString(root, "interface"),
            RouterOSJson.String(root, "actual-interface"),
            RouterOSJson.Boolean(root, "disabled") ?? false,
            RouterOSJson.Boolean(root, "dynamic") ?? false,
            RouterOSJson.Boolean(root, "invalid") ?? false);
    }

    public override void Write(Utf8JsonWriter writer, RouterOSIpAddress value, JsonSerializerOptions options) =>
        throw new NotSupportedException("RouterOS discovery models are read-only.");
}
