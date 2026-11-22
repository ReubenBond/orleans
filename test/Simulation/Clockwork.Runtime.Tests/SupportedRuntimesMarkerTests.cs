using Orleans.Testing.Simulation.Runtime;

namespace Orleans.Testing.Simulation.Runtime.Tests;

public sealed class SupportedRuntimesMarkerTests
{
    private static readonly Version Net11 = new(11, 0);
    private static readonly Version Net12 = new(12, 0);

    [Theory]
    [InlineData("11.0", "11.0", "[11.0,11.0]")]
    [InlineData("11.0", null, "[11.0,]")]
    [InlineData(null, "12.0", "[,12.0]")]
    [InlineData(null, null, "[,]")]
    public void FormatRoundTrips(string? minimum, string? maximum, string expected)
    {
        Version? low = minimum is null ? null : Version.Parse(minimum);
        Version? high = maximum is null ? null : Version.Parse(maximum);

        string marker = SupportedRuntimesMarker.Format(low, high);

        Assert.Equal(expected, marker);
        Assert.True(SupportedRuntimesMarker.TryParse(marker, out Version? parsedLow, out Version? parsedHigh));
        Assert.Equal(low, parsedLow);
        Assert.Equal(high, parsedHigh);
    }

    [Theory]
    [InlineData("")]
    [InlineData("11.0")]
    [InlineData("[11.0]")]
    [InlineData("[11.0,11.0,12.0]")]
    [InlineData("[11.0.1,]")]
    [InlineData("[12.0,11.0]")]
    [InlineData("[eleven,]")]
    [InlineData("[ 11.0,]")]
    [InlineData("[011.0,]")]
    public void TryParseRejectsMalformedValues(string value) =>
        Assert.False(SupportedRuntimesMarker.TryParse(value, out _, out _));

    [Theory]
    [InlineData(null)]
    [InlineData("[11.0,11.0]")]
    [InlineData("[10.0,]")]
    [InlineData("[,11.0]")]
    [InlineData("[,]")]
    public void RuntimeInRangePasses(string? marker) =>
        SupportedRuntimesMarker.Verify("Fx.App", marker, Net11);

    [Theory]
    [InlineData("[11.0,11.0]", "11.0")]
    [InlineData("[10.0,11.0]", "10.0 to 11.0")]
    [InlineData("[,11.0]", "11.0 or earlier")]
    [InlineData("[13.0,]", "13.0 or later")]
    public void RuntimeOutsideRangeFails(string marker, string supported)
    {
        Assert.True(SupportedRuntimesMarker.TryParse(marker, out Version? minimum, out Version? maximum));

        UnsupportedRuntimeException exception = Assert.Throws<UnsupportedRuntimeException>(
            () => SupportedRuntimesMarker.Verify("Fx.App", marker, Net12));

        Assert.Equal("Fx.App", exception.AssemblyName);
        Assert.Equal(minimum, exception.MinimumRuntime);
        Assert.Equal(maximum, exception.MaximumRuntime);
        Assert.Equal(Net12, exception.Runtime);
        Assert.Contains($"for .NET {supported}, but this process runs .NET 12.0", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryRangeRoundTripsAndAdmitsExactlyItsRuntimes()
    {
        Version?[] bounds = [null, .. Enumerable.Range(9, 4).Select(major => new Version(major, 0))];
        Version[] probes = [.. Enumerable.Range(8, 6).Select(major => new Version(major, 0))];
        foreach (Version? minimum in bounds)
        {
            foreach (Version? maximum in bounds.Where(maximum => minimum is null || maximum is null || minimum <= maximum))
            {
                string marker = SupportedRuntimesMarker.Format(minimum, maximum);
                Assert.True(SupportedRuntimesMarker.TryParse(marker, out Version? parsedMinimum, out Version? parsedMaximum));
                Assert.Equal((minimum, maximum), (parsedMinimum, parsedMaximum));

                foreach (Version probe in probes)
                {
                    bool supported = (minimum is null || probe >= minimum) && (maximum is null || probe <= maximum);
                    Exception? failure = Record.Exception(() => SupportedRuntimesMarker.Verify("Fx.App", marker, probe));
                    Assert.True(supported == failure is null, $"{marker} at {probe}");
                }
            }
        }
    }

    [Fact]
    public void UnreadableMarkerFails()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => SupportedRuntimesMarker.Verify("Fx.App", "11.0", Net11));

        Assert.Contains("unreadable value '11.0'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatRejectsPatchVersions() =>
        Assert.Throws<ArgumentException>(() => SupportedRuntimesMarker.Format(new Version(11, 0, 1), null));
}
