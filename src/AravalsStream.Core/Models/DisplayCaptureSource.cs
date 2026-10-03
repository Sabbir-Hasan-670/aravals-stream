namespace AravalsStream.Core.Models;

// Resource identity is separate from each scene item and its canvas transforms.
public sealed class DisplayCaptureSource
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string DisplayId { get; set; } = "";
    public string Name { get; set; } = "Display Capture";
}
