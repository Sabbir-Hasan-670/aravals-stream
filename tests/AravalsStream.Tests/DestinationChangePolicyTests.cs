using AravalsStream.Core.Models;
using Xunit;

namespace AravalsStream.Tests;

public sealed class DestinationChangePolicyTests
{
    [Fact]
    public void NameChangeDoesNotRestartButBitrateChangeDoes()
    {
        var before = new Destination();
        var renamed = before.Copy(); renamed.Name = "Renamed";
        Assert.False(DestinationChangePolicy.RequiresRestart(before, renamed));
        var bitrateChanged = before.Copy(); bitrateChanged.VideoBitrateKbps = 8000;
        Assert.True(DestinationChangePolicy.RequiresRestart(before, bitrateChanged));
    }
}
