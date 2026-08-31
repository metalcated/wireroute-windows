using System.IO.Compression;
using System.Text;
using WireRoute.Core.Profiles;

namespace WireRoute.Core.Tests;

[TestClass]
public sealed class WireGuardProfileArchiveReaderTests
{
    [TestMethod]
    public void ReadsNestedConfigurationsAndIgnoresOtherFiles()
    {
        using var stream = CreateArchive(
            ("Laptop.conf", "[Interface]\nPrivateKey = one"),
            ("nested/Phone.CONF", "[Interface]\nPrivateKey = two"),
            ("notes.txt", "not a tunnel"));

        var entries = WireGuardProfileArchiveReader.Read(stream, "profiles.zip");

        Assert.AreEqual(2, entries.Count);
        Assert.AreEqual("Laptop", entries[0].ProfileName);
        Assert.AreEqual("profiles.zip/Laptop.conf", entries[0].SourceName);
        Assert.AreEqual("Phone", entries[1].ProfileName);
        Assert.AreEqual("profiles.zip/nested/Phone.CONF", entries[1].SourceName);
    }

    [TestMethod]
    public void RejectsArchivesWithTooManyEntries()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var index = 0; index <= WireGuardProfileArchiveReader.MaximumEntries; index++)
            {
                _ = archive.CreateEntry($"ignored-{index}.txt");
            }
        }
        stream.Position = 0;

        Assert.ThrowsExactly<InvalidDataException>(() =>
            WireGuardProfileArchiveReader.Read(stream, "oversized.zip"));
    }

    [TestMethod]
    public void RejectsInvalidUtf8Configuration()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("bad.conf");
            using var destination = entry.Open();
            destination.Write([0xC3, 0x28]);
        }
        stream.Position = 0;

        Assert.ThrowsExactly<DecoderFallbackException>(() =>
            WireGuardProfileArchiveReader.Read(stream, "invalid.zip"));
    }

    private static MemoryStream CreateArchive(params (string Name, string Content)[] entries)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var item in entries)
            {
                var entry = archive.CreateEntry(item.Name);
                using var writer = new StreamWriter(
                    entry.Open(),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.Write(item.Content);
            }
        }
        stream.Position = 0;
        return stream;
    }
}
