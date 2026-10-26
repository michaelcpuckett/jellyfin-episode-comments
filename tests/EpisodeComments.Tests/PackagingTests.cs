using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using MediaBrowser.Model.Updates;
using Xunit;

namespace EpisodeComments.Tests;

public sealed class PackagingTests
{
    [Fact]
    public void CatalogManifestMatchesJellyfinSchemaAndDownloadArchive()
    {
        var root = RepositoryRoot();
        var catalog = JsonSerializer.Deserialize<PackageInfo[]>(File.ReadAllText(Path.Combine(root, "manifest.json")))!;
        var plugin = Assert.Single(catalog);
        Assert.Equal(Guid.Parse("79672a4d-f247-4370-9e41-f508bd891631"), plugin.Id);
        var version = Assert.Single(plugin.Versions);
        Assert.Equal("0.1.0.0", version.Version);
        Assert.Equal("12.0.0.0", version.TargetAbi);
        var uri = new Uri(version.SourceUrl!);
        Assert.Equal("https", uri.Scheme);
        Assert.Equal("raw.githubusercontent.com", uri.Host);
        Assert.Contains("/master/packages/", uri.AbsolutePath);
        var zip = Path.Combine(root, "packages", Path.GetFileName(uri.AbsolutePath));
        Assert.Equal(Convert.ToHexString(MD5.HashData(File.ReadAllBytes(zip))).ToLowerInvariant(), version.Checksum);
        Assert.True(DateTimeOffset.TryParse(version.Timestamp, out _));
        using var archive = ZipFile.OpenRead(zip);
        Assert.Equal(new[] { "Jellyfin.Plugin.EpisodeComments.dll", "LICENSE", "meta.json" }, archive.Entries.Select(e => e.FullName).Order().ToArray());
        Assert.Equal(File.ReadAllBytes(Path.Combine(root, "Jellyfin.Plugin.EpisodeComments.dll")).LongLength,
            archive.GetEntry("Jellyfin.Plugin.EpisodeComments.dll")!.Length);
    }

    private static string RepositoryRoot([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../.."));
}
