namespace AravalsStream.Core.Settings;

public sealed record CanvasSettings
{
    public int HorizontalWidth { get; set; } = 1920;
    public int HorizontalHeight { get; set; } = 1080;
    public int VerticalWidth { get; set; } = 1080;
    public int VerticalHeight { get; set; } = 1920;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid => new[] { HorizontalWidth, HorizontalHeight, VerticalWidth, VerticalHeight }
        .All(value => value is >= 64 and <= 4096 && value % 2 == 0);
}
