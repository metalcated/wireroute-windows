using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WireRoute.RouterOS;

public enum RouterOSClientError
{
    InvalidBaseUrl,
    InsecureTransport,
    InvalidUsername,
    InvalidPayload,
    HttpStatus,
    ResponseTooLarge,
    WriteOutcomeUncertain,
}

public class RouterOSClientException : Exception
{
    public RouterOSClientException(RouterOSClientError error, string message)
        : base(message)
    {
        Error = error;
    }

    public RouterOSClientException(RouterOSClientError error, string message, Exception innerException)
        : base(message, innerException)
    {
        Error = error;
    }

    public RouterOSClientError Error { get; }
}

public sealed class RouterOSHttpException : RouterOSClientException
{
    public RouterOSHttpException(HttpStatusCode statusCode, string? routerMessage, string? routerDetail)
        : base(
            RouterOSClientError.HttpStatus,
            BuildMessage(statusCode, routerMessage, routerDetail))
    {
        StatusCode = statusCode;
        RouterMessage = routerMessage;
        RouterDetail = routerDetail;
    }

    public HttpStatusCode StatusCode { get; }

    public string? RouterMessage { get; }

    public string? RouterDetail { get; }

    private static string BuildMessage(HttpStatusCode statusCode, string? message, string? detail)
    {
        var values = new[] { message, detail }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return values.Length == 0
            ? $"RouterOS returned HTTP {(int)statusCode}."
            : $"RouterOS returned HTTP {(int)statusCode}: {string.Join(" — ", values)}";
    }
}

public sealed class RouterOSWriteOutcomeUncertainException : RouterOSClientException
{
    public RouterOSWriteOutcomeUncertainException(Exception innerException)
        : base(
            RouterOSClientError.WriteOutcomeUncertain,
            "RouterOS did not return a verifiable completion response. The peer may already exist.",
            innerException)
    {
    }
}

public sealed record RouterOSHttpResponse(HttpStatusCode StatusCode, byte[] Body);

public interface IRouterOSHttpTransport
{
    Task<RouterOSHttpResponse> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken);
}

public sealed class RouterOSHttpTransport : IRouterOSHttpTransport, IDisposable
{
    public const int MaximumResponseLength = 1024 * 1024;

    private readonly HttpClient client;
    private readonly RouterOSCertificateValidator certificateValidator;

    public RouterOSHttpTransport(
        Uri baseUrl,
        RouterOSServerCertificate? trustedCertificate = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        var port = baseUrl.IsDefaultPort ? 443 : baseUrl.Port;
        certificateValidator = new RouterOSCertificateValidator(
            baseUrl.Host,
            port,
            trustedCertificate);
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                RemoteCertificateValidationCallback = certificateValidator.Validate,
            },
        };
        client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(15),
        };
    }

    public async Task<RouterOSHttpResponse> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is > MaximumResponseLength)
            {
                throw new RouterOSClientException(
                    RouterOSClientError.ResponseTooLarge,
                    "RouterOS returned a response larger than 1 MB.");
            }

            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var block = new byte[16 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(block, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (buffer.Length + read > MaximumResponseLength)
                {
                    throw new RouterOSClientException(
                        RouterOSClientError.ResponseTooLarge,
                        "RouterOS returned a response larger than 1 MB.");
                }

                buffer.Write(block, 0, read);
            }

            return new RouterOSHttpResponse(response.StatusCode, buffer.ToArray());
        }
        catch (HttpRequestException exception)
        {
            var certificateFailure = certificateValidator.CertificateFailure();
            if (certificateFailure is not null)
            {
                throw certificateFailure;
            }

            if (exception.HttpRequestError == HttpRequestError.SecureConnectionError)
            {
                throw new RouterOSTlsConnectionException(exception);
            }

            throw;
        }
    }

    public void Dispose() => client.Dispose();
}

public sealed class RouterOSClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        MaxDepth = 32,
        PropertyNameCaseInsensitive = false,
        RespectNullableAnnotations = true,
    };

    private readonly Uri restBaseUrl;
    private readonly AuthenticationHeaderValue authorizationHeader;
    private readonly IRouterOSHttpTransport transport;

    public RouterOSClient(
        Uri baseUrl,
        RouterOSCredentials credentials,
        IRouterOSHttpTransport transport)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(credentials);
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));

        if (!baseUrl.IsAbsoluteUri
            || string.IsNullOrWhiteSpace(baseUrl.Host)
            || !string.IsNullOrEmpty(baseUrl.UserInfo)
            || !string.IsNullOrEmpty(baseUrl.Query)
            || !string.IsNullOrEmpty(baseUrl.Fragment))
        {
            throw new RouterOSClientException(
                RouterOSClientError.InvalidBaseUrl,
                "Enter a complete RouterOS address without credentials, a query, or a fragment.");
        }

        if (!baseUrl.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new RouterOSClientException(
                RouterOSClientError.InsecureTransport,
                "RouterOS connections require HTTPS.");
        }

        if (string.IsNullOrEmpty(credentials.Username) || credentials.Username.Contains(':', StringComparison.Ordinal))
        {
            throw new RouterOSClientException(
                RouterOSClientError.InvalidUsername,
                "Enter a RouterOS username without a colon.");
        }

        var normalizedPath = baseUrl.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        restBaseUrl = normalizedPath switch
        {
            [] => new UriBuilder(baseUrl) { Path = "/rest/" }.Uri,
            [var rest] when rest.Equals("rest", StringComparison.OrdinalIgnoreCase) =>
                new UriBuilder(baseUrl) { Path = "/rest/" }.Uri,
            _ => throw new RouterOSClientException(
                RouterOSClientError.InvalidBaseUrl,
                "The RouterOS address path must be empty or /rest."),
        };

        authorizationHeader = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credentials.Username}:{credentials.Password}")));
    }

    public Task<IReadOnlyList<RouterOSWireGuardInterface>> GetWireGuardInterfacesAsync(
        CancellationToken cancellationToken = default) =>
        GetAsync<RouterOSWireGuardInterface>(["interface", "wireguard"], cancellationToken);

    public Task<IReadOnlyList<RouterOSWireGuardPeer>> GetWireGuardPeersAsync(
        CancellationToken cancellationToken = default) =>
        GetAsync<RouterOSWireGuardPeer>(["interface", "wireguard", "peers"], cancellationToken);

    public Task<IReadOnlyList<RouterOSIpAddress>> GetIpAddressesAsync(
        CancellationToken cancellationToken = default) =>
        GetAsync<RouterOSIpAddress>(["ip", "address"], cancellationToken);

    public async Task<RouterOSWireGuardPeer> CreateWireGuardPeerAsync(
        RouterOSPeerCreation peer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peer);
        using var request = CreateRequest(
            HttpMethod.Put,
            ["interface", "wireguard", "peers"],
            JsonSerializer.SerializeToUtf8Bytes(peer.RequestPayload, JsonOptions));
        try
        {
            var response = await transport.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return DecodeResponse<RouterOSWireGuardPeer>(response);
        }
        catch (RouterOSHttpException exception) when (
            (int)exception.StatusCode is >= 400 and < 500
            && exception.StatusCode != HttpStatusCode.RequestTimeout)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new RouterOSWriteOutcomeUncertainException(exception);
        }
    }

    public async Task<RouterOSWireGuardPeer> ReplaceWireGuardPeerPublicKeyAsync(
        RouterOSWireGuardPeer peer,
        string publicKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peer);
        if (!RouterOSPeerCreation.IsWireGuardKey(publicKey))
        {
            throw RouterOSProvisioningErrors.Create(RouterOSProvisioningError.InvalidKey);
        }

        using var request = CreateRequest(
            HttpMethod.Patch,
            ["interface", "wireguard", "peers", peer.Id],
            JsonSerializer.SerializeToUtf8Bytes(
                new RouterOSPeerPublicKeyUpdateRequest(publicKey),
                JsonOptions));
        try
        {
            var response = await transport.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return DecodeResponse<RouterOSWireGuardPeer>(response);
        }
        catch (RouterOSHttpException exception) when (
            (int)exception.StatusCode is >= 400 and < 500
            && exception.StatusCode != HttpStatusCode.RequestTimeout)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new RouterOSWriteOutcomeUncertainException(exception);
        }
    }

    private async Task<IReadOnlyList<T>> GetAsync<T>(
        IReadOnlyList<string> pathComponents,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, pathComponents);
        var response = await transport.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return DecodeResponse<T[]>(response);
    }

    private HttpRequestMessage CreateRequest(
        HttpMethod method,
        IReadOnlyList<string> pathComponents,
        byte[]? body = null)
    {
        var relativePath = string.Join('/', pathComponents.Select(Uri.EscapeDataString));
        var request = new HttpRequestMessage(method, new Uri(restBaseUrl, relativePath));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = authorizationHeader;
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        return request;
    }

    private static T DecodeResponse<T>(RouterOSHttpResponse response)
    {
        if ((int)response.StatusCode is < 200 or >= 300)
        {
            var routerError = TryDeserialize<RouterOSErrorResponse>(response.Body);
            throw new RouterOSHttpException(
                response.StatusCode,
                routerError?.Message,
                routerError?.Detail);
        }

        return TryDeserialize<T>(response.Body)
            ?? throw new RouterOSClientException(
                RouterOSClientError.InvalidPayload,
                "RouterOS returned an invalid response.");
    }

    private static T? TryDeserialize<T>(byte[] body)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body, JsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private sealed record RouterOSErrorResponse(
        [property: JsonPropertyName("message")] string? Message,
        [property: JsonPropertyName("detail")] string? Detail);

    private sealed record RouterOSPeerPublicKeyUpdateRequest(
        [property: JsonPropertyName("public-key")] string PublicKey);
}
