using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace WireRoute.App.Interop;

internal sealed class DnsOverHttpsProxy : IAsyncDisposable
{
    private const int DnsPort = 53;
    private const int MaximumConcurrentQueries = 32;
    private readonly SemaphoreSlim stateGate = new(1, 1);
    private readonly SemaphoreSlim queryGate = new(MaximumConcurrentQueries, MaximumConcurrentQueries);
    private CancellationTokenSource? lifetime;
    private UdpClient? udp;
    private TcpListener? tcp;
    private HttpClient? client;
    private Task? udpLoop;
    private Task? tcpLoop;
    private string? profileName;

    public bool IsRunningFor(string name) =>
        lifetime is not null
        && profileName?.Equals(name, StringComparison.OrdinalIgnoreCase) == true;

    public async Task StartAsync(
        string name,
        Uri resolver,
        IReadOnlyList<IPAddress> bootstrapAddresses,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(resolver);
        if (resolver.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("Encrypted DNS requires an HTTPS resolver.", nameof(resolver));
        }
        if (bootstrapAddresses.Count == 0)
        {
            throw new ArgumentException(
                "Encrypted DNS requires at least one bootstrap address.",
                nameof(bootstrapAddresses));
        }

        await stateGate.WaitAsync(cancellationToken);
        try
        {
            if (lifetime is not null)
            {
                if (IsRunningFor(name))
                {
                    return;
                }
                throw new InvalidOperationException(
                    "Disconnect the other encrypted-DNS tunnel before activating this one.");
            }

            var addresses = bootstrapAddresses.Distinct().ToArray();
            var nextAddress = -1;
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.None,
                ConnectCallback = async (context, token) =>
                {
                    var address = addresses[
                        (int)((uint)Interlocked.Increment(ref nextAddress) % addresses.Length)];
                    var socket = new Socket(
                        address.AddressFamily,
                        SocketType.Stream,
                        ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(
                            new IPEndPoint(address, context.DnsEndPoint.Port),
                            token);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                },
                EnableMultipleHttp2Connections = true,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            };
            var httpClient = new HttpClient(handler)
            {
                DefaultRequestVersion = HttpVersion.Version20,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
                Timeout = TimeSpan.FromSeconds(12),
            };

            var udpServer = new UdpClient(AddressFamily.InterNetwork);
            var tcpServer = new TcpListener(IPAddress.Loopback, DnsPort);
            try
            {
                udpServer.Client.ExclusiveAddressUse = true;
                udpServer.Client.Bind(new IPEndPoint(IPAddress.Loopback, DnsPort));
                tcpServer.Server.ExclusiveAddressUse = true;
                tcpServer.Start();
            }
            catch
            {
                udpServer.Dispose();
                tcpServer.Stop();
                httpClient.Dispose();
                throw new InvalidOperationException(
                    "WireRoute could not reserve 127.0.0.1:53 for encrypted DNS. "
                    + "Close any other local DNS proxy and try again.");
            }

            var cancellation = new CancellationTokenSource();
            profileName = name;
            client = httpClient;
            udp = udpServer;
            tcp = tcpServer;
            lifetime = cancellation;
            udpLoop = RunUdpAsync(udpServer, resolver, cancellation.Token);
            tcpLoop = RunTcpAsync(tcpServer, resolver, cancellation.Token);
        }
        finally
        {
            stateGate.Release();
        }
    }

    public async Task StopAsync(string name)
    {
        await stateGate.WaitAsync();
        try
        {
            if (lifetime is null || !IsRunningFor(name))
            {
                return;
            }

            lifetime.Cancel();
            udp?.Dispose();
            tcp?.Stop();
            var loops = new[] { udpLoop, tcpLoop }.Where(task => task is not null).Cast<Task>();
            try
            {
                await Task.WhenAll(loops);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }

            client?.Dispose();
            lifetime.Dispose();
            lifetime = null;
            udp = null;
            tcp = null;
            client = null;
            udpLoop = null;
            tcpLoop = null;
            profileName = null;
        }
        finally
        {
            stateGate.Release();
        }
    }

    private async Task RunUdpAsync(
        UdpClient server,
        Uri resolver,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult query;
            try
            {
                query = await server.ReceiveAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            _ = ForwardUdpAsync(server, resolver, query, cancellationToken);
        }
    }

    private async Task ForwardUdpAsync(
        UdpClient server,
        Uri resolver,
        UdpReceiveResult query,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await ResolveAsync(resolver, query.Buffer, cancellationToken);
            _ = await server.SendAsync(response, query.RemoteEndPoint, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch
        {
            // DNS clients retry; WireRoute records tunnel lifecycle errors separately.
        }
    }

    private async Task RunTcpAsync(
        TcpListener server,
        Uri resolver,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient connection;
            try
            {
                connection = await server.AcceptTcpClientAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            _ = HandleTcpAsync(connection, resolver, cancellationToken);
        }
    }

    private async Task HandleTcpAsync(
        TcpClient connection,
        Uri resolver,
        CancellationToken cancellationToken)
    {
        using (connection)
        {
            try
            {
                var stream = connection.GetStream();
                var lengthBytes = new byte[2];
                while (await ReadExactAsync(stream, lengthBytes, cancellationToken))
                {
                    var length = lengthBytes[0] << 8 | lengthBytes[1];
                    if (length < 12)
                    {
                        return;
                    }
                    var query = new byte[length];
                    if (!await ReadExactAsync(stream, query, cancellationToken))
                    {
                        return;
                    }
                    var response = await ResolveAsync(resolver, query, cancellationToken);
                    if (response.Length > ushort.MaxValue)
                    {
                        return;
                    }
                    lengthBytes[0] = (byte)(response.Length >> 8);
                    lengthBytes[1] = (byte)response.Length;
                    await stream.WriteAsync(lengthBytes, cancellationToken);
                    await stream.WriteAsync(response, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
            catch (HttpRequestException)
            {
            }
        }
    }

    private async Task<byte[]> ResolveAsync(
        Uri resolver,
        byte[] query,
        CancellationToken cancellationToken)
    {
        if (query.Length is < 12 or > ushort.MaxValue)
        {
            throw new InvalidDataException("The DNS query length is invalid.");
        }
        await queryGate.WaitAsync(cancellationToken);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, resolver)
            {
                Content = new ByteArrayContent(query),
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-message"));
            using var response = await client!.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentType?.MediaType is not "application/dns-message")
            {
                throw new InvalidDataException("The resolver returned an invalid DNS media type.");
            }
            if (response.Content.Headers.ContentLength is > ushort.MaxValue)
            {
                throw new InvalidDataException("The resolver returned an oversized DNS message.");
            }
            var message = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            return message.Length <= ushort.MaxValue
                ? message
                : throw new InvalidDataException("The resolver returned an oversized DNS message.");
        }
        finally
        {
            queryGate.Release();
        }
    }

    private static async Task<bool> ReadExactAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(offset),
                cancellationToken);
            if (read == 0)
            {
                return false;
            }
            offset += read;
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        var name = profileName;
        if (name is not null)
        {
            await StopAsync(name);
        }
    }
}
