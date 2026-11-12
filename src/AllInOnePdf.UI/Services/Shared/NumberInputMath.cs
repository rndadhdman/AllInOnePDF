using System.Globalization;

namespace AllInOnePdf.UI.Services.Shared;

/// <summary>
/// Parsing, rounding, clamping and formatting for the shared native numeric inputs
/// (<c>Components/Shared/RangeInput</c> and <c>Components/Shared/NumberInput</c>). Values travel as invariant-culture
/// text between the browser and the circuit, so the round trip is exact.
/// </summary>
public static class NumberInputMath
{
    /// <summary>
    /// Rounds <paramref name="value"/> to the nearest multiple of <paramref name="step"/> (4 decimals) and clamps it to
    /// [<paramref name="min"/>, <paramref name="max"/>]. Null for NaN or infinity. A step of 0 or less only clamps; a
    /// <paramref name="max"/> below <paramref name="min"/> is treated as <paramref name="min"/>.
    /// </summary>
    public static double? Normalize(double value, double min, double max, double step)
    {
        if (!double.IsFinite(value))
        {
            return null;
        }

        value = RoundToStep(value, step);
        if (max < min)
        {
            max = min;
        }

        return Math.Clamp(value, min, max);
    }

    /// <summary>Rounds to the nearest multiple of <paramref name="step"/> (4 decimals); a step of 0 or less only rounds to 4 decimals.</summary>
    public static double RoundToStep(double value, double step)
    {
        if (!double.IsFinite(value))
        {
            return value;
        }

        return step > 0 && double.IsFinite(step)
            ? Math.Round(Math.Round(value / step) * step, 4)
            : Math.Round(value, 4);
    }

    /// <summary>Reads a number typed by the user ("1.5", "1,5", " 2 "); false for empty or non-numeric text.</summary>
    public static bool TryParse(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var t = text.Trim().Replace(',', '.');
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    /// <summary>Invariant text for an input's value attribute ("0.25", "12", "-3.5").</summary>
    public static string Format(double value)
    {
        var rounded = double.IsFinite(value) ? Math.Round(value, 4) : 0;
        return rounded == 0 ? "0" : rounded.ToString("0.####", CultureInfo.InvariantCulture);
    }

    /// <summary>True when two values are the same for an input (closer than 1e-9).</summary>
    public static bool Same(double a, double b) => Math.Abs(a - b) < 1e-9;
}
