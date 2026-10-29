using Fala.Helpers;
using Xunit;

namespace Fala.Tests;

public class TimeTextTests
{
    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(65, "1:05")]
    [InlineData(3599, "59:59")]
    [InlineData(3723, "1:02:03")]
    [InlineData(-5, "0:00")]
    public void Format(double seconds, string expected)
        => Assert.Equal(expected, TimeText.Format(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData("12:34", 754)]
    [InlineData("1:02:03", 3723)]
    [InlineData("90", 90)]
    [InlineData(" 5:00 ", 300)]
    public void TryParse_Valid(string raw, double expectedSeconds)
    {
        Assert.True(TimeText.TryParse(raw, out var value));
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    public void TryParse_Invalid(string? raw)
        => Assert.False(TimeText.TryParse(raw, out _));
}
