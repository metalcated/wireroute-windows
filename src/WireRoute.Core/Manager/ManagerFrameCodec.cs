using System.Buffers.Binary;

namespace WireRoute.Core.Manager;

public static class ManagerFrameCodec
{
    private const int HeaderLength = sizeof(uint);

    public static async ValueTask WriteAsync<T>(
        Stream stream,
        T message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite)
        {
            throw new ArgumentException("The manager stream must be writable.", nameof(stream));
        }

        var payload = ManagerProtocolJson.Serialize(message);
        if (payload.Length == 0 || payload.Length > ManagerProtocol.MaximumFrameLength)
        {
            throw new ManagerProtocolException(
                $"The manager frame must contain 1–{ManagerProtocol.MaximumFrameLength} bytes.");
        }

        var header = new byte[HeaderLength];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<T> ReadAsync<T>(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new ArgumentException("The manager stream must be readable.", nameof(stream));
        }

        var header = new byte[HeaderLength];
        await ReadExactlyAsync(stream, header, "manager frame header", cancellationToken).ConfigureAwait(false);
        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (payloadLength == 0 || payloadLength > ManagerProtocol.MaximumFrameLength)
        {
            throw new ManagerProtocolException(
                $"The manager frame length {payloadLength} is outside the supported range.");
        }

        var payload = new byte[payloadLength];
        await ReadExactlyAsync(stream, payload, "manager frame payload", cancellationToken).ConfigureAwait(false);
        return ManagerProtocolJson.Deserialize<T>(payload);
    }

    private static async ValueTask ReadExactlyAsync(
        Stream stream,
        Memory<byte> destination,
        string description,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var bytesRead = await stream
                .ReadAsync(destination[offset..], cancellationToken)
                .ConfigureAwait(false);
            if (bytesRead == 0)
            {
                throw new EndOfStreamException($"The {description} ended unexpectedly.");
            }

            offset += bytesRead;
        }
    }
}
