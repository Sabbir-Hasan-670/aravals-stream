using AravalsStream.Core.Versioning;
using Xunit;

namespace AravalsStream.Tests;

public sealed class ReleaseVersionTests
{
    [Theory]
    [InlineData("1.0.1-beta", "1.0.1", -1)]
    [InlineData("v1.0.0", "1.0.1-beta", -1)]
    [InlineData("1.0.1-beta.2", "1.0.1-beta.10", -1)]
    [InlineData("1.0.2-beta", "1.0.1", 1)]
    public void OrdersReleaseChannelsAndPatchVersions(string left, string right, int sign)
    {
        Assert.True(ReleaseVersion.TryParse(left, out var a));
        Assert.True(ReleaseVersion.TryParse(right, out var b));
        Assert.Equal(sign, Math.Sign(a.CompareTo(b)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.0")]
    [InlineData("1.0.1/evil")]
    public void RejectsInvalidVersions(string value) => Assert.False(ReleaseVersion.TryParse(value, out _));
}
