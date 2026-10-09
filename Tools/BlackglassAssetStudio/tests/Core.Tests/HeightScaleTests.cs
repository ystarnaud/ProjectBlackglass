using Blackglass.AssetPipeline;
using Xunit;

namespace Blackglass.AssetStudio.Tests;

public class HeightScaleTests
{
    [Fact]
    public void Auto_scale_is_target_over_measured()
    {
        var r = HeightScale.Compute(2.4f, 1.85f, true, 0f);
        Assert.True(r.Ok);
        Assert.Equal(ScaleSources.Auto, r.source);
        Assert.InRange(r.scale, 1.85f / 2.4f - 1e-5f, 1.85f / 2.4f + 1e-5f);
    }

    [Fact]
    public void Manual_override_wins_even_when_measurement_is_useless()
    {
        var r = HeightScale.Compute(0f, 1.85f, true, 0.5f);
        Assert.True(r.Ok);
        Assert.Equal(ScaleSources.Manual, r.source);
        Assert.Equal(0.5f, r.scale);
    }

    [Fact]
    public void Normalize_off_means_no_scaling()
    {
        var r = HeightScale.Compute(2.4f, 1.85f, false, 0f);
        Assert.True(r.Ok);
        Assert.Equal(ScaleSources.None, r.source);
        Assert.Equal(1f, r.scale);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.005f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void Unusable_measured_height_is_an_error_not_a_nan_scale(float measured)
    {
        var r = HeightScale.Compute(measured, 1.85f, true, 0f);
        Assert.False(r.Ok);
        Assert.Contains("manual", r.error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    public void Non_positive_target_is_an_error(float target)
    {
        Assert.False(HeightScale.Compute(2.4f, target, true, 0f).Ok);
    }

    [Theory]
    [InlineData(-0.5f)]
    [InlineData(float.NaN)]
    public void Invalid_manual_override_is_an_error(float manual)
    {
        Assert.False(HeightScale.Compute(2.4f, 1.85f, true, manual).Ok);
    }
}
