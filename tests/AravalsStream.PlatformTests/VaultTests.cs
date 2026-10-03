using AravalsStream.Platform;
using Xunit;

namespace AravalsStream.PlatformTests;
public sealed class VaultTests
{
    [Theory]
    [InlineData("")]
    [InlineData("\0")]
    public async Task InvalidLinuxReferenceIsRejectedBeforeAnyProcess(string reference) =>
        await Assert.ThrowsAsync<ArgumentException>(() => new LinuxSecretStorage().StoreAsync(reference, "fixture"));

    [MacFact]
    public async Task NativeMacKeychainRoundTripUpdateAndDelete()
    {
        var storage = new MacKeychainStorage(); var key = "acceptance-" + Guid.NewGuid().ToString("N");
        try
        {
            await storage.StoreAsync(key, "synthetic-vault-value\nবাংলা").WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal("synthetic-vault-value\nবাংলা", await storage.GetAsync(key).WaitAsync(TimeSpan.FromSeconds(20)));
            await storage.StoreAsync(key, "updated-synthetic-value").WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal("updated-synthetic-value", await storage.GetAsync(key).WaitAsync(TimeSpan.FromSeconds(20)));
            await storage.RemoveAsync(key).WaitAsync(TimeSpan.FromSeconds(20)); Assert.Null(await storage.GetAsync(key).WaitAsync(TimeSpan.FromSeconds(20)));
        }
        finally { await storage.RemoveAsync(key).WaitAsync(TimeSpan.FromSeconds(10)); }
    }
}
public sealed class MacFactAttribute : FactAttribute
{
    public MacFactAttribute() { if (!OperatingSystem.IsMacOS()) Skip = "Native macOS Keychain acceptance runs on macOS."; }
}
