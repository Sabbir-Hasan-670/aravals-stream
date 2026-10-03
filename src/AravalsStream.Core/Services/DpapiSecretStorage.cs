using System.IO;
using System.Security.Cryptography;

using AravalsStream.Core.Interfaces;

namespace AravalsStream.Core.Services;

public sealed class DpapiSecretStorage : ISecretStorage
{
    private readonly string _directory;
    public DpapiSecretStorage(string? directory = null) => _directory = directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AravalsStream", "secrets");
    private string PathFor(string reference)
    {
        var safeFileName = Guid.TryParse(reference, out var g)
            ? g.ToString("N")
            : Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(reference)));
        return Path.Combine(_directory, safeFileName + ".bin");
    }
    public void Set(string reference, string secret)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI requires Windows.");
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(PathFor(reference), ProtectedData.Protect(
            System.Text.Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser));
    }
    public string? Get(string reference)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI requires Windows.");
        return File.Exists(PathFor(reference))
        ? System.Text.Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(PathFor(reference)), null, DataProtectionScope.CurrentUser))
        : null;
    }
    public Task StoreAsync(string key, string secret, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); Set(key, secret); return Task.CompletedTask; }
    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(Get(key)); }
    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); Delete(key); return Task.CompletedTask; }
    public void Delete(string reference)
    {
        var path = PathFor(reference);
        if (File.Exists(path)) File.Delete(path);
    }
}



