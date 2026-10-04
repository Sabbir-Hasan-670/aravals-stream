using System.Net;
using System.Net.Http;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AravalsStream.App.Services;
using Xunit;

namespace AravalsStream.DesktopTests;

public sealed class AppUpdateServiceTests
{
    private const string Base = "https://github.com/Sabbir-Hasan-670/aravals-stream/releases/download/v9.9.9-beta/";
    private const string FileName = "AravalsStream-Setup-9.9.9-beta.exe";

    [Fact]
    public async Task CheckHonorsChannelAndDownloadVerifiesChecksum()
    {
        var bytes = Encoding.UTF8.GetBytes("test installer bytes");
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var json = $$"""
            [{"tag_name":"v9.9.9-beta","draft":false,"prerelease":true,"assets":[
              {"name":"{{FileName}}","browser_download_url":"{{Base}}{{FileName}}"},
              {"name":"SHA256SUMS.txt","browser_download_url":"{{Base}}SHA256SUMS.txt"}]}]
            """;
        using var client = new HttpClient(new StubHandler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("api.github.com")) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
            if (url.EndsWith("SHA256SUMS.txt")) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{hash}  {FileName}\n") };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }));
        using var updates = new AppUpdateService(client);
        Assert.Null(await updates.CheckAsync("1.0.0", false));
        var available = await updates.CheckAsync("1.0.0", true);
        Assert.NotNull(available);
        var path = await updates.DownloadVerifiedInstallerAsync(available);
        try { Assert.Equal(bytes, await File.ReadAllBytesAsync(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ChecksumRequiresExactInstallerName()
    {
        var hash = new string('a', 64);
        Assert.Null(AppUpdateService.FindChecksum($"{hash}  wrong.exe", FileName));
        Assert.Equal(hash, AppUpdateService.FindChecksum($"{hash}  {FileName}", FileName));
    }

    [Fact]
    public async Task RejectsTamperedInstaller()
    {
        const string version = "9.9.8-beta";
        var name = $"AravalsStream-Setup-{version}.exe";
        var baseUrl = $"https://github.com/Sabbir-Hasan-670/aravals-stream/releases/download/v{version}/";
        using var client = new HttpClient(new StubHandler(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS.txt")
                    ? new StringContent($"{new string('0', 64)}  {name}\n")
                    : new ByteArrayContent(Encoding.UTF8.GetBytes("tampered"))
            }));
        using var updates = new AppUpdateService(client);
        await Assert.ThrowsAsync<InvalidOperationException>(() => updates.DownloadVerifiedInstallerAsync(
            new AvailableAppUpdate(version, new Uri(baseUrl + name), new Uri(baseUrl + "SHA256SUMS.txt"))));
        Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), "AravalsStream", "Updates", version, name)));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
