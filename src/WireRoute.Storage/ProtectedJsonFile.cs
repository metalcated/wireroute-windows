using System.ComponentModel;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WireRoute.Storage;

public sealed class WireRouteStorageException : IOException
{
    public WireRouteStorageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

[SupportedOSPlatform("windows")]
internal sealed class ProtectedJsonFile<T>
{
    private const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 32,
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectNullableAnnotations = true,
    };

    private readonly string path;
    private readonly Func<T> emptyValue;
    private readonly SemaphoreSlim gate = new(1, 1);

    public ProtectedJsonFile(string path, Func<T> emptyValue)
    {
        this.path = Path.GetFullPath(path);
        this.emptyValue = emptyValue;
    }

    public async Task<T> ReadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<T> UpdateAsync(
        Func<T, T> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            var updated = update(current);
            await WriteUnlockedAsync(updated, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<T> ReadUnlockedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return emptyValue();
        }

        byte[]? plaintext = null;
        try
        {
            var ciphertext = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            plaintext = WindowsDpapi.Unprotect(ciphertext);
            var document = JsonSerializer.Deserialize<StoredDocument<T>>(plaintext, JsonOptions)
                ?? throw new JsonException("The protected store was empty.");
            if (document.Version != CurrentVersion || document.Value is null)
            {
                throw new JsonException("The protected store version is not supported.");
            }

            return document.Value;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or CryptographicException or JsonException or Win32Exception)
        {
            throw new WireRouteStorageException("Protected WireRoute settings could not be read.", exception);
        }
        finally
        {
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    private async Task WriteUnlockedAsync(T value, CancellationToken cancellationToken)
    {
        byte[]? plaintext = null;
        try
        {
            plaintext = JsonSerializer.SerializeToUtf8Bytes(
                new StoredDocument<T>(CurrentVersion, value),
                JsonOptions);
            var ciphertext = WindowsDpapi.Protect(plaintext);
            var directory = Path.GetDirectoryName(path)
                ?? throw new IOException("The protected store has no parent directory.");
            Directory.CreateDirectory(directory);
            var temporaryPath = Path.Combine(
                directory,
                $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(ciphertext, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or CryptographicException or JsonException or Win32Exception)
        {
            throw new WireRouteStorageException("Protected WireRoute settings could not be saved.", exception);
        }
        finally
        {
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    private sealed record StoredDocument<TValue>(int Version, TValue Value);
}
