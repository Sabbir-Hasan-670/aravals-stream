using SharpDX.DXGI;

namespace AravalsStream.Capture.Display;

// DXGI reports this process's video-memory usage and budget on each adapter.
public sealed record GpuMemorySample(string Adapter, long DedicatedBytes, long BudgetBytes, long UsedBytes);

public static class GpuMemoryProbe
{
    public static IReadOnlyList<GpuMemorySample> Sample()
    {
        var result = new List<GpuMemorySample>();
        try
        {
            using var factory = new Factory1();
            foreach (var adapter in factory.Adapters1)
            {
                using (adapter)
                {
                    try
                    {
                        using var adapter3 = adapter.QueryInterface<Adapter3>();
                        var memory = adapter3.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local);
                        result.Add(new GpuMemorySample(adapter.Description1.Description,
                            (long)adapter.Description1.DedicatedVideoMemory,
                            (long)memory.Budget, (long)memory.CurrentUsage));
                    }
                    catch (Exception) { /* Older adapters may not expose IDXGIAdapter3. */ }
                }
            }
        }
        catch (Exception) { /* DXGI unavailable: diagnostics show N/A. */ }
        return result;
    }
}
