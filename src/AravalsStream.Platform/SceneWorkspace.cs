using System.Text.Json;
using AravalsStream.Core.Models;

namespace AravalsStream.Platform;

public sealed class NativeSceneSource
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Source";
    public CaptureInput Input { get; set; } = new(CaptureKind.Display, "");
    public bool Visible { get; set; } = true;
    public SourceTransform Horizontal { get; set; } = new();
    public SourceTransform Vertical { get; set; } = new();
    public override string ToString() => Name + (Visible ? "" : " · hidden");
}
public sealed class NativeScene
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Scene";
    public List<NativeSceneSource> Sources { get; set; } = [];
    public override string ToString() => Name;
}
public sealed class SceneWorkspace
{
    public int Schema { get; set; } = 1;
    public Guid SelectedSceneId { get; set; }
    public List<NativeScene> Scenes { get; set; } = [];
    public List<Destination> Destinations { get; set; } = [];
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static SceneWorkspace Create()
    {
        var scene = new NativeScene { Name = "Gaming" };
        return new() { SelectedSceneId = scene.Id, Scenes = [scene] };
    }
    public MediaPlan Plan(NativeScene scene, DesktopPlatform platform, bool vertical, IReadOnlyList<CaptureInput> audio)
    {
        if (!Scenes.Contains(scene)) throw new ArgumentException("Scene does not belong to this workspace.");
        var layers = scene.Sources.Select(s => new VideoLayer(s.Input, vertical ? s.Vertical : s.Horizontal, s.Visible)).ToArray();
        return new(platform, new(CaptureKind.TestVideo, ""), audio, vertical ? CanvasSize.Vertical : CanvasSize.Horizontal) { Layers = layers, RenderScene = true };
    }
    public async Task SaveAsync(string path, CancellationToken ct = default)
    {
        path = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try { await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(this, Json), ct); File.Move(temporary, path, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static async Task<SceneWorkspace> LoadAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path)) return Create();
        var result = JsonSerializer.Deserialize<SceneWorkspace>(await File.ReadAllTextAsync(path, ct), Json) ?? throw new InvalidDataException("Invalid scene workspace.");
        if (result.Schema != 1 || result.Scenes is null || result.Scenes.Count == 0 || result.Scenes.Count > 100 ||
            result.Scenes.Any(s => s is null || s.Sources is null || s.Sources.Count > 100 || s.Sources.Any(source => source is null || source.Input is null || source.Horizontal is null || source.Vertical is null)) ||
            result.Scenes.Select(s => s.Id).Distinct().Count() != result.Scenes.Count ||
            result.Destinations is null || result.Destinations.Count > 100 || result.Destinations.Any(d => d is null) ||
            result.Destinations.Select(d => d.Id).Distinct().Count() != result.Destinations.Count)
            throw new InvalidDataException("Unsupported or invalid scene workspace.");
        if (!result.Scenes.Any(s => s.Id == result.SelectedSceneId)) result.SelectedSceneId = result.Scenes[0].Id;
        return result;
    }
}
