namespace AravalsStream.Core.Composition;

public sealed class FrameSnapshotBus
{
    private byte[]? _latest;
    public byte[]? Latest => Volatile.Read(ref _latest);
    public void Publish(byte[] completedFrame) => Volatile.Write(ref _latest, completedFrame);
}
