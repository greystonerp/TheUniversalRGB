using System.Net;
using System.Text;
using System.Text.Json;
using UniversalRGB.Core.Updates;
using UniversalRGB.Drivers.Updates;

namespace UniversalRGB.Tests;

public class ReleaseSignatureTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "urgb-sig-" + Guid.NewGuid().ToString("N") + ".bin");

    public ReleaseSignatureTests() => File.WriteAllBytes(_file, Encoding.UTF8.GetBytes("Universal RGB release payload"));

    public void Dispose() => File.Delete(_file);

    [Fact]
    public void Signature_round_trips_and_detects_tampering_and_wrong_keys()
    {
        var (privateKey, publicKey) = ReleaseSignature.CreateKeyPair();
        var signature = ReleaseSignature.Sign(_file, privateKey);

        Assert.True(ReleaseSignature.Verify(_file, signature, publicKey));
        Assert.Equal(publicKey, ReleaseSignature.PublicKeyFromPrivate(privateKey));

        var (_, otherPublic) = ReleaseSignature.CreateKeyPair();
        Assert.False(ReleaseSignature.Verify(_file, signature, otherPublic));

        File.AppendAllText(_file, "!");
        Assert.False(ReleaseSignature.Verify(_file, signature, publicKey));
        Assert.False(ReleaseSignature.Verify(_file, "not base64 !!", publicKey));
        Assert.False(ReleaseSignature.Verify(_file, signature, ""));
    }
}

public class UpdateClientTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "urgb-upd-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static string ReleaseJson(string tag, string? digest) => $$"""
        {
          "tag_name": "{{tag}}",
          "name": "Universal RGB {{tag}}",
          "body": "Fixes and improvements.",
          "html_url": "https://github.com/owner/UniversalRGB/releases/tag/{{tag}}",
          "assets": [
            { "name": "UniversalRGB-Setup.exe", "size": 5, "browser_download_url": "https://github.com/owner/UniversalRGB/releases/download/{{tag}}/UniversalRGB-Setup.exe" {{(digest is null ? "" : $", \"digest\": \"sha256:{digest}\"")}} },
            { "name": "UniversalRGB-Setup.exe.sig", "size": 96, "browser_download_url": "https://github.com/owner/UniversalRGB/releases/download/{{tag}}/UniversalRGB-Setup.exe.sig" }
          ]
        }
        """;

    private sealed class FakeGitHub(string releaseJson, byte[] setup, string signature) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            HttpContent content = url.EndsWith("/releases/latest", StringComparison.Ordinal) ? new StringContent(releaseJson)
                : url.EndsWith(".sig", StringComparison.Ordinal) ? new StringContent(signature)
                : new ByteArrayContent(setup);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request });
        }
    }

    private (UpdateClient Client, string PublicKey) Create(string tag, byte[] setup, Func<string, string>? signer = null, string? digest = null, bool useDigest = true)
    {
        var (privateKey, publicKey) = ReleaseSignature.CreateKeyPair();
        Directory.CreateDirectory(_dir);
        var temp = Path.Combine(_dir, "payload.tmp");
        File.WriteAllBytes(temp, setup);
        var signature = signer?.Invoke(temp) ?? ReleaseSignature.Sign(temp, privateKey);
        var sha = digest ?? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(setup)).ToLowerInvariant();
        var json = ReleaseJson(tag, useDigest ? sha : null);
        return (new UpdateClient("owner/UniversalRGB", publicKey, Path.Combine(_dir, "updates"), handler: new FakeGitHub(json, setup, signature)), publicKey);
    }

    [Fact]
    public async Task Finds_newer_release_and_downloads_a_verified_setup()
    {
        var (client, _) = Create("v0.6.0", [1, 2, 3, 4, 5]);
        using (client)
        {
            var release = await client.GetLatestAsync(CancellationToken.None);
            Assert.NotNull(release);
            Assert.Equal(new Version(0, 6, 0), release.Version);
            Assert.True(UpdateClient.IsNewer(release, new Version(0, 5, 0, 0)));
            Assert.False(UpdateClient.IsNewer(release, new Version(0, 6, 0, 0)));

            var path = await client.DownloadAsync(release, null, CancellationToken.None);
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, File.ReadAllBytes(path));
            Assert.True(client.VerifyDownloaded(path));

            File.WriteAllBytes(path, [9, 9, 9]); // swapped after download
            Assert.False(client.VerifyDownloaded(path));
        }
    }

    [Fact]
    public async Task Rejects_updates_signed_with_another_key()
    {
        var (otherPrivate, _) = ReleaseSignature.CreateKeyPair();
        var (client, _) = Create("v0.6.0", [1, 2, 3], signer: file => ReleaseSignature.Sign(file, otherPrivate));
        using (client)
        {
            var release = (await client.GetLatestAsync(CancellationToken.None))!;
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(release, null, CancellationToken.None));
            Assert.Contains("not signed with the Universal RGB release key", error.Message);
            Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "updates")));
        }
    }

    [Fact]
    public async Task Rejects_downloads_that_do_not_match_githubs_digest()
    {
        var (client, _) = Create("v0.6.0", [1, 2, 3], digest: new string('a', 64));
        using (client)
        {
            var release = (await client.GetLatestAsync(CancellationToken.None))!;
            await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(release, null, CancellationToken.None));
        }
    }

    [Fact]
    public async Task Works_without_a_published_digest_but_still_requires_the_signature()
    {
        var (client, _) = Create("v1.2.3", [7, 7], useDigest: false);
        using (client)
        {
            var release = (await client.GetLatestAsync(CancellationToken.None))!;
            Assert.Null(release.SetupSha256);
            Assert.True(client.VerifyDownloaded(await client.DownloadAsync(release, null, CancellationToken.None)));
        }
    }

    [Fact]
    public async Task Releases_still_being_published_are_reported_but_not_downloaded()
    {
        using var doc = JsonDocument.Parse("""{ "tag_name": "v0.7.0", "assets": [ { "name": "UniversalRGB-Setup.exe", "browser_download_url": "https://x/y" } ] }""");
        var release = UpdateClient.Parse(doc.RootElement);
        Assert.NotNull(release);
        Assert.Equal(new Version(0, 7, 0), release.Version);
        Assert.False(release.IsComplete);

        using var client = new UpdateClient("owner/UniversalRGB", "key", _dir);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(release, null, CancellationToken.None));
        Assert.Contains("still being published", error.Message);
    }

    [Fact]
    public void Release_notes_are_summarized_for_the_popup()
    {
        var notes = "- Setup asks about startup\r\n- MSI firmware guidance\r\n\r\n---\r\nDownload **UniversalRGB-Setup.exe** and run it.";
        var summary = UpdateClient.SummarizeNotes(notes);
        Assert.Equal("•  Setup asks about startup\n•  MSI firmware guidance", summary);
    }

    [Theory]
    [InlineData("v0.6.0", 0, 6, 0)]
    [InlineData("1.2", 1, 2, 0)]
    [InlineData("v2.0.1-beta", 2, 0, 1)]
    public void Parses_release_tags(string tag, int major, int minor, int build)
    {
        Assert.True(UpdateClient.TryParseVersion(tag, out var version));
        Assert.Equal(new Version(major, minor, build), version);
    }

    [Fact]
    public async Task Unconfigured_client_refuses_to_check()
    {
        using var client = new UpdateClient("", "", _dir);
        Assert.False(client.IsConfigured);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetLatestAsync(CancellationToken.None));
    }
}
