namespace WireRoute.Core.Manager;

public sealed class ManagerProtocolClient : IAsyncDisposable
{
    private readonly Stream responseReader;
    private readonly Stream requestWriter;
    private readonly Stream eventReader;
    private readonly bool leaveOpen;
    private readonly SemaphoreSlim requestLock = new(1, 1);
    private long nextRequestId;
    private long lastEventSequence;
    private int helloStarted;
    private int protocolFaulted;
    private bool helloCompleted;
    private bool disposed;

    public ManagerProtocolClient(
        Stream responseReader,
        Stream requestWriter,
        Stream eventReader,
        bool leaveOpen = false)
    {
        this.responseReader = responseReader ?? throw new ArgumentNullException(nameof(responseReader));
        this.requestWriter = requestWriter ?? throw new ArgumentNullException(nameof(requestWriter));
        this.eventReader = eventReader ?? throw new ArgumentNullException(nameof(eventReader));
        this.leaveOpen = leaveOpen;
    }

    public async ValueTask<ManagerHelloResponse> HelloAsync(
        string clientVersion,
        string architecture,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Interlocked.Exchange(ref helloStarted, 1) != 0)
        {
            throw new ManagerProtocolException("The manager hello request has already been attempted.");
        }

        var response = await SendRequestAsync<ManagerHelloRequest, ManagerHelloResponse>(
            ManagerMethods.Hello,
            ManagerHelloRequest.Create(clientVersion, architecture),
            cancellationToken).ConfigureAwait(false);
        if (!response.Protocol.Equals(ManagerProtocol.Name, StringComparison.Ordinal)
            || response.SelectedVersion != ManagerProtocol.CurrentVersion)
        {
            throw new ManagerProtocolException("The manager selected an incompatible protocol.");
        }

        helloCompleted = true;
        return response;
    }

    public async ValueTask<TResponse> RequestAsync<TRequest, TResponse>(
        string method,
        TRequest parameters,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!Volatile.Read(ref helloCompleted))
        {
            throw new ManagerProtocolException("The manager hello request must complete before other methods.");
        }

        if (string.Equals(method, ManagerMethods.Hello, StringComparison.Ordinal))
        {
            throw new ManagerProtocolException("Use HelloAsync to negotiate the manager protocol.");
        }

        return await SendRequestAsync<TRequest, TResponse>(method, parameters, cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<TResponse> SendRequestAsync<TRequest, TResponse>(
        string method,
        TRequest parameters,
        CancellationToken cancellationToken)
    {
        ThrowIfFaulted();
        if (string.IsNullOrWhiteSpace(method))
        {
            throw new ArgumentException("A manager method is required.", nameof(method));
        }

        await requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfFaulted();
            var requestId = Interlocked.Increment(ref nextRequestId);
            var request = ManagerRequest.Create(requestId, method, parameters);
            await ManagerFrameCodec.WriteAsync(requestWriter, request, cancellationToken).ConfigureAwait(false);
            var response = await ManagerFrameCodec
                .ReadAsync<ManagerResponse>(responseReader, cancellationToken)
                .ConfigureAwait(false);

            ValidateVersion(response.Version);
            if (response.RequestId != requestId)
            {
                throw new ManagerProtocolException(
                    $"Manager response {response.RequestId} did not match request {requestId}.");
            }

            return response.GetRequiredResult<TResponse>();
        }
        catch (ManagerRemoteException)
        {
            throw;
        }
        catch
        {
            Interlocked.Exchange(ref protocolFaulted, 1);
            throw;
        }
        finally
        {
            requestLock.Release();
        }
    }

    public async ValueTask<ManagerEvent> ReadEventAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ThrowIfFaulted();
        try
        {
            var managerEvent = await ManagerFrameCodec
                .ReadAsync<ManagerEvent>(eventReader, cancellationToken)
                .ConfigureAwait(false);
            ValidateVersion(managerEvent.Version);
            if (managerEvent.Sequence <= lastEventSequence)
            {
                throw new ManagerProtocolException(
                    $"Manager event sequence {managerEvent.Sequence} followed {lastEventSequence}.");
            }

            lastEventSequence = managerEvent.Sequence;
            return managerEvent;
        }
        catch
        {
            Interlocked.Exchange(ref protocolFaulted, 1);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        requestLock.Dispose();
        if (!leaveOpen)
        {
            await responseReader.DisposeAsync().ConfigureAwait(false);
            await requestWriter.DisposeAsync().ConfigureAwait(false);
            await eventReader.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static void ValidateVersion(int version)
    {
        if (version != ManagerProtocol.CurrentVersion)
        {
            throw new ManagerProtocolException(
                $"Manager protocol version {version} is not supported by this client.");
        }
    }

    private void ThrowIfFaulted()
    {
        if (Volatile.Read(ref protocolFaulted) != 0)
        {
            throw new ManagerProtocolException("The manager protocol connection is faulted and cannot be reused.");
        }
    }
}
