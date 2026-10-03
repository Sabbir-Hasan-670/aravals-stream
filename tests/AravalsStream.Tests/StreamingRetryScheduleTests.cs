using AravalsStream.Core.Services;
using Xunit;

namespace AravalsStream.Tests;

public sealed class StreamingRetryScheduleTests
{
    [Fact]
    public void ExponentialThenCappedDelays()
    {
        var seconds = Enumerable.Range(0, 8).Select(i => RetrySchedule.GetDelay(i).TotalSeconds).ToArray();
        Assert.Equal(new double[] { 1, 2, 5, 10, 15, 30, 30, 30 }, seconds);
    }
}
