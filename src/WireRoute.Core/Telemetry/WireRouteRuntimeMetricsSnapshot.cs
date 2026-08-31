using System.Text.Json.Serialization;

namespace WireRoute.Core.Telemetry;

public sealed record WireRouteRuntimeMetricsSnapshot(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("receivedBytes")] ulong ReceivedBytes,
    [property: JsonPropertyName("sentBytes")] ulong SentBytes,
    [property: JsonPropertyName("lastHandshakeFileTime")] ulong LastHandshakeFileTime);
