using AravalsStream.Platform;
using Xunit;

namespace AravalsStream.PlatformTests;
public sealed class SceneWorkspaceTests
{
    [Fact]
    public async Task RoundTripPreservesIndependentCanvasTransformsAndVisibility()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var workspace = SceneWorkspace.Create();
            workspace.Scenes[0].Sources.Add(new() { Name = "Camera", Input = new(CaptureKind.Camera, "device"), Visible = false,
                Horizontal = new() { X = 20, Width = 640 }, Vertical = new() { X = 30, Width = 500 } });
            await workspace.SaveAsync(path); var restored = await SceneWorkspace.LoadAsync(path);
            var source = Assert.Single(restored.Scenes[0].Sources); Assert.False(source.Visible);
            Assert.Equal(20, source.Horizontal.X); Assert.Equal(30, source.Vertical.X);
            Assert.Equal(500, restored.Plan(restored.Scenes[0], DesktopPlatform.Windows, true, []).Layers[0].Transform.Width);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [Fact]
    public void EmptySceneUsesBlackCanvasRatherThanAnUnrequestedTestPattern()
    {
        var workspace = SceneWorkspace.Create();
        var args = MediaArguments.Preview(workspace.Plan(workspace.Scenes[0], PlatformCapture.Current, false, []));
        Assert.Contains(args, a => a.StartsWith("color=c=black")); Assert.DoesNotContain(args, a => a.StartsWith("testsrc"));
    }
    [Fact]
    public async Task UnsupportedSchemaDoesNotOverwriteExistingWorkspace()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try { await File.WriteAllTextAsync(path, "{\"Schema\":99,\"Scenes\":[]}"); await Assert.ThrowsAsync<InvalidDataException>(() => SceneWorkspace.LoadAsync(path)); Assert.Contains("99", await File.ReadAllTextAsync(path)); }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
