using System.Buffers;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using AravalsStream.Core.Versioning;

namespace AravalsStream.App.Services;

public sealed record AvailableAppUpdate(string Version, Uri InstallerUrl, Uri ChecksumsUrl);

public sealed class AppUpdateService : IDisposable
{
    private const string Repository = "Sabbir-Hasan-670/aravals-stream";
    private const long MaxInstallerBytes = 1_000_000_000;
    private readonly HttpClient _http;

    public AppUpdateService(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("AravalsStream-Updater/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<AvailableAppUpdate?> CheckAsync(string currentVersion, bool includeDevelopment,
        CancellationToken cancellationToken = default)
    {
        if (!ReleaseVersion.TryParse(currentVersion, out var current)) return null;
        using var response = await _http.GetAsync(
            $"https://api.github.com/repos/{Repository}/releases?per_page=30",
            HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
        AvailableAppUpdate? best = null;
        ReleaseVersion bestVersion = current;
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean()) continue;
            if (!ReleaseVersion.TryParse(release.GetProperty("tag_name").GetString(), out var candidate) ||
                candidate.CompareTo(bestVersion) <= 0 ||
                (release.GetProperty("prerelease").GetBoolean() && !includeDevelopment)) continue;
            var name = $"AravalsStream-Setup-{release.GetProperty("tag_name").GetString()!.TrimStart('v', 'V')}.exe";
            Uri? installer = null, checksums = null;
            foreach (var asset in release.GetProperty("assets").EnumerateArray())
            {
                var assetName = asset.GetProperty("name").GetString();
                var url = asset.GetProperty("browser_download_url").GetString();
                if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || !IsTrustedAsset(parsed)) continue;
                if (assetName == name) installer = parsed;
                else if (assetName == "SHA256SUMS.txt") checksums = parsed;
            }
            if (installer is null || checksums is null) continue;
            bestVersion = candidate;
            best = new AvailableAppUpdate(release.GetProperty("tag_name").GetString()!.TrimStart('v', 'V'), installer, checksums);
        }
        return best;
    }

    public async Task<string> DownloadVerifiedInstallerAsync(AvailableAppUpdate update,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!IsTrustedAsset(update.InstallerUrl) || !IsTrustedAsset(update.ChecksumsUrl))
            throw new InvalidOperationException("Update asset location is not trusted.");
        var fileName = Path.GetFileName(update.InstallerUrl.AbsolutePath);
        if (fileName != $"AravalsStream-Setup-{update.Version}.exe")
            throw new InvalidOperationException("Update installer name does not match its version.");
        var sums = await _http.GetStringAsync(update.ChecksumsUrl, cancellationToken).ConfigureAwait(false);
        var expected = FindChecksum(sums, fileName) ??
            throw new InvalidOperationException("Release checksum for the Windows installer is missing.");

        var directory = Path.Combine(Path.GetTempPath(), "AravalsStream", "Updates", update.Version);
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, fileName);
        var partial = destination + ".part";
        try
        {
            using var response = await _http.GetAsync(update.InstallerUrl,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            if (total > MaxInstallerBytes) throw new InvalidOperationException("Update installer is unexpectedly large.");
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(65536);
                try
                {
                    long received = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        received += read;
                        if (received > MaxInstallerBytes) throw new InvalidOperationException("Update installer is unexpectedly large.");
                        await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        hash.AppendData(buffer, 0, read);
                        if (total > 0) progress?.Report(Math.Clamp((double)received / total.Value, 0, 1));
                    }
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            var actual = Convert.ToHexString(hash.GetHashAndReset());
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Update checksum verification failed.");
            File.Move(partial, destination, true);
            progress?.Report(1);
            return destination;
        }
        catch
        {
            if (File.Exists(partial)) File.Delete(partial);
            throw;
        }
    }

    public static string? FindChecksum(string contents, string fileName)
    {
        foreach (var line in contents.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length < 67 || trimmed[64..].TrimStart(' ', '*') != fileName) continue;
            var hash = trimmed[..64];
            if (hash.All(Uri.IsHexDigit)) return hash;
        }
        return null;
    }

    private static bool IsTrustedAsset(Uri uri) => uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.StartsWith($"/{Repository}/releases/download/", StringComparison.OrdinalIgnoreCase);

    public void Dispose() => _http.Dispose();
}
