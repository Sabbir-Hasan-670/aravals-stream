namespace AravalsStream.Capture.Display;

public sealed record DisplayInfo(string Id, string Name, int Width, int Height, bool IsPrimary, int AdapterIndex, int OutputIndex)
{
    public string Label => $"{Name}  ·  {Width} × {Height}{(IsPrimary ? "  ·  Primary" : "")}";
}

public sealed class DisplayFrame(int width, int height, int stride, byte[] pixels, int missedFrames = 0) : IDisposable
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public int Stride { get; } = stride;
    public byte[] Pixels { get; } = pixels;
    public int MissedFrames { get; } = Math.Max(0, missedFrames);
    private int _returned;
    // The receiving frame consumer owns this pooled buffer and must dispose it.
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _returned, 1) == 0)
            System.Buffers.ArrayPool<byte>.Shared.Return(Pixels);
    }
}
