using System.IO.Compression;
using System.Text;

namespace WireRoute.Core.Profiles;

public sealed record WireGuardProfileArchiveEntry(
    string SourceName,
    string ProfileName,
    string Configuration);

public static class WireGuardProfileArchiveReader
{
    public const int MaximumEntries = 256;
    public const long MaximumConfigurationBytes = 1024 * 1024;
    public const long MaximumTotalConfigurationBytes = 16 * 1024 * 1024;

    public static IReadOnlyList<WireGuardProfileArchiveEntry> Read(
        Stream stream,
        string archiveName)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new ArgumentException("The tunnel archive stream is not readable.", nameof(stream));
        }

        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count > MaximumEntries)
        {
            throw new InvalidDataException(
                $"Tunnel archives may contain at most {MaximumEntries} entries.");
        }

        var result = new List<WireGuardProfileArchiveEntry>();
        long totalBytes = 0;
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name)
                || !Path.GetExtension(entry.Name).Equals(".conf", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (entry.Length is < 0 or > MaximumConfigurationBytes)
            {
                throw new InvalidDataException(
                    $"{entry.FullName}: Configuration files must be 1 MB or smaller.");
            }
            totalBytes = checked(totalBytes + entry.Length);
            if (totalBytes > MaximumTotalConfigurationBytes)
            {
                throw new InvalidDataException(
                    "The tunnel configurations in this archive exceed the 16 MB total limit.");
            }

            var profileName = Path.GetFileNameWithoutExtension(entry.Name).Trim();
            if (profileName.Length == 0)
            {
                throw new InvalidDataException(
                    $"{entry.FullName}: The tunnel name is empty.");
            }
            using var entryStream = entry.Open();
            using var reader = new StreamReader(
                entryStream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 4096,
                leaveOpen: false);
            var configuration = reader.ReadToEnd();
            result.Add(new WireGuardProfileArchiveEntry(
                archiveName + "/" + entry.FullName.Replace('\\', '/'),
                profileName,
                configuration));
        }
        return result;
    }
}
