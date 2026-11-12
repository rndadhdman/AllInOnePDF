namespace AllInOnePdf.UI.Services.Shared;

/// <summary>How file paths compare on this OS: case-insensitive on Windows, case-sensitive elsewhere.</summary>
public static class FilePaths
{
    public static StringComparison Comparison { get; } =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static StringComparer Comparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static bool AreEqual(string? a, string? b) => string.Equals(a, b, Comparison);
}
