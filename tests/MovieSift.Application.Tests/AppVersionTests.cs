using MovieSift.Application.Updates;
using Xunit;

namespace MovieSift.Application.Tests;

/// <summary>Checks public stable-release ordering independently of strings and assembly revision numbers.</summary>
public sealed class AppVersionTests
{
    [Theory]
    [InlineData("1.0.0")]
    [InlineData("v1.0.0")]
    [InlineData("1.0.0+6fb2a2711745")]
    public void StableReleaseAndInformationalVersionHaveSameIdentity(string text)
    {
        Assert.True(AppVersion.TryParse(text, out var parsed));
        Assert.Equal(new AppVersion(1, 0, 0), parsed);
    }

    [Theory]
    [InlineData("1.0.0-rc.1")]
    [InlineData("01.0.0")]
    [InlineData("1.0")]
    [InlineData("1.0.0.0")]
    [InlineData("1.0.0+")]
    [InlineData("1.0.0+bad..metadata")]
    [InlineData("1.0.0 ")]
    [InlineData("1.0.2147483648")]
    public void MalformedOrPrereleaseVersionsCannotBecomeStableUpdates(string text) =>
        Assert.False(AppVersion.TryParse(text, out _));

    [Fact]
    public void NumericOrderHandlesTwoDigitComponents()
    {
        Assert.True(new AppVersion(1, 10, 0).CompareTo(new(1, 9, 99)) > 0);
        Assert.True(new AppVersion(2, 0, 0).CompareTo(new(1, 99, 99)) > 0);
        Assert.Equal(0, new AppVersion(1, 0, 0).CompareTo(new(1, 0, 0)));
    }
}
