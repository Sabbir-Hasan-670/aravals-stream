using AravalsStream.Core.Interfaces;
using AravalsStream.Core.Services;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace AravalsStream.Platform;

public static class PlatformSecretStorage
{
    public static ISecretStorage Create() => OperatingSystem.IsWindows() ? new DpapiSecretStorage() :
        OperatingSystem.IsMacOS() ? new MacKeychainStorage() : new LinuxSecretStorage();
    internal static void Validate(string reference, string? secret = null)
    {
        if (string.IsNullOrWhiteSpace(reference) || reference.Length > 512 || reference.Contains('\0')) throw new ArgumentException("Invalid credential reference.");
        if (secret?.Length > 512 * 1024) throw new ArgumentException("Credential is too large.");
    }
}

public sealed class LinuxSecretStorage : ISecretStorage
{
    public async Task StoreAsync(string key, string secret, CancellationToken cancellationToken = default)
    {
        PlatformSecretStorage.Validate(key, secret);
        var result = await Run(["store", "--label=Aravals Stream", "application", "com.aravals.stream", "reference", key], Convert.ToBase64String(Encoding.UTF8.GetBytes(secret)), cancellationToken);
        if (result.ExitCode != 0) throw Unavailable();
    }
    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        PlatformSecretStorage.Validate(key);
        var result = await Run(["lookup", "application", "com.aravals.stream", "reference", key], null, cancellationToken);
        if (result.ExitCode == 1 && string.IsNullOrWhiteSpace(result.Error)) return null;
        if (result.ExitCode != 0) throw Unavailable();
        try
        {
            var bytes = Convert.FromBase64String(result.Output.Trim());
            try { return Encoding.UTF8.GetString(bytes); } finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (FormatException) { throw new IOException("The stored credential has an unsupported format."); }
    }
    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        PlatformSecretStorage.Validate(key);
        var result = await Run(["clear", "application", "com.aravals.stream", "reference", key], null, cancellationToken);
        if (result.ExitCode != 0 && !(result.ExitCode == 1 && string.IsNullOrWhiteSpace(result.Error))) throw Unavailable();
    }
    private static IOException Unavailable() => new("Linux Secret Service is unavailable or locked. Install libsecret-tools and unlock your desktop keyring; plaintext storage is not used.");
    private static async Task<(int ExitCode, string Output, string Error)> Run(string[] args, string? secret, CancellationToken ct)
    {
        var start = new ProcessStartInfo("secret-tool") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        Process process;
        try { process = Process.Start(start) ?? throw Unavailable(); }
        catch (System.ComponentModel.Win32Exception) { throw Unavailable(); }
        using (process)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var error = process.StandardError.ReadToEndAsync(deadline.Token);
            try
            {
                // Password data is sent on stdin, never in a command argument or environment value.
                if (secret is not null) await process.StandardInput.WriteAsync(secret.AsMemory(), deadline.Token);
                process.StandardInput.Close();
                await process.WaitForExitAsync(deadline.Token);
                return (process.ExitCode, await output, await error);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
                ct.ThrowIfCancellationRequested(); throw Unavailable();
            }
        }
    }
}
