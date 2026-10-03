using AravalsStream.Core.Services;
using Xunit;

namespace AravalsStream.Tests;

public sealed class SecretStorageTests
{
    [Fact]
    public async Task DpapiStorage_IsolatesAndDeletesSecrets()
    {
        var directory = Path.Combine(Path.GetTempPath(), "aravals-secrets-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new DpapiSecretStorage(directory);
            var a = Guid.NewGuid().ToString();
            var b = Guid.NewGuid().ToString();
            await storage.StoreAsync(a, "fake-test-secret-a");
            await storage.StoreAsync(b, "fake-test-secret-b");
            Assert.Equal("fake-test-secret-a", await storage.GetAsync(a));
            Assert.Equal("fake-test-secret-b", await storage.GetAsync(b));
            foreach (var file in Directory.GetFiles(directory))
                Assert.DoesNotContain("fake-test-secret", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(file)));
            await storage.RemoveAsync(a);
            Assert.Null(await storage.GetAsync(a));
            Assert.Equal("fake-test-secret-b", await storage.GetAsync(b));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
