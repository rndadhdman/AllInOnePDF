using AllInOnePdf.UI.Services.Shared;

namespace AllInOnePdf.Core.Tests;

public class NumberInputMathTests
{
    [Theory]
    [InlineData(11.26, 0.5, 11.5)]
    [InlineData(11.24, 0.5, 11.0)]
    [InlineData(1.62, 0.05, 1.6)]
    [InlineData(1.63, 0.05, 1.65)]
    [InlineData(0.0123, 0.005, 0.01)]
    [InlineData(7.4, 1, 7)]
    [InlineData(-3.26, 0.25, -3.25)]
    public void Normalize_rounds_to_the_nearest_step(double value, double step, double expected)
    {
        Assert.Equal(expected, NumberInputMath.Normalize(value, -100, 100, step));
    }

    [Fact]
    public void Normalize_avoids_floating_point_noise()
    {
        // 0.1 * 3 is 0.30000000000000004 in binary floating point.
        Assert.Equal(0.3, NumberInputMath.Normalize(0.3, 0, 1, 0.1));
        Assert.Equal("0.3", NumberInputMath.Format(NumberInputMath.Normalize(0.29, 0, 1, 0.1)!.Value));
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(150, 100)]
    [InlineData(42, 42)]
    public void Normalize_clamps_to_the_range(double value, double expected)
    {
        Assert.Equal(expected, NumberInputMath.Normalize(value, 0, 100, 1));
    }

    [Fact]
    public void Normalize_with_no_step_only_clamps_and_rounds_to_four_decimals()
    {
        Assert.Equal(3.1416, NumberInputMath.Normalize(Math.PI, 0, 10, 0));
        Assert.Equal(10, NumberInputMath.Normalize(12.5, 0, 10, -1));
    }

    [Fact]
    public void Normalize_treats_a_max_below_min_as_min()
    {
        Assert.Equal(5, NumberInputMath.Normalize(9, 5, 2, 1));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Normalize_rejects_non_finite_values(double value)
    {
        Assert.Null(NumberInputMath.Normalize(value, 0, 100, 1));
    }

    [Theory]
    [InlineData("1.5", 1.5)]
    [InlineData("1,5", 1.5)]
    [InlineData(" 2 ", 2)]
    [InlineData("-0,25", -0.25)]
    [InlineData("1e2", 100)]
    [InlineData("12", 12)]
    public void TryParse_accepts_point_and_comma_decimals(string text, double expected)
    {
        Assert.True(NumberInputMath.TryParse(text, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("1.2.3")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void TryParse_rejects_empty_and_non_numeric_text(string? text)
    {
        Assert.False(NumberInputMath.TryParse(text, out _));
    }

    [Theory]
    [InlineData(0.25, "0.25")]
    [InlineData(12, "12")]
    [InlineData(-3.5, "-3.5")]
    [InlineData(1.23456789, "1.2346")]
    [InlineData(-0.00001, "0")]
    [InlineData(double.NaN, "0")]
    public void Format_writes_invariant_text(double value, string expected)
    {
        Assert.Equal(expected, NumberInputMath.Format(value));
    }

    [Fact]
    public void Format_ignores_the_current_culture()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("1.5", NumberInputMath.Format(1.5));
            Assert.True(NumberInputMath.TryParse("1.5", out var v));
            Assert.Equal(1.5, v);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Same_tolerates_rounding_noise_only()
    {
        Assert.True(NumberInputMath.Same(0.1 + 0.2, 0.3));
        Assert.False(NumberInputMath.Same(0.3, 0.3001));
    }
}
