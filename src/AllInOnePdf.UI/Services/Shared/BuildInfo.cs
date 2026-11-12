using System.Reflection;
using System.Runtime.InteropServices;

namespace AllInOnePdf.UI.Services.Shared;

/// <summary>
/// Version and runtime facts about the running app, for the About card, diagnostics and anything else that shows them.
/// Read from the entry assembly (the desktop or web host), falling back to the UI assembly.
/// </summary>
public static class BuildInfo
{
    /// <summary>The product name shown to users.</summary>
    public const string ProductName = "AllInOne PDF";

    /// <summary>Where releases are published.</summary>
    public const string ReleasesUrl = "https://github.com/rndadhdman/AllInOnePDF/releases";

    /// <summary>Where problems are reported.</summary>
    public const string IssuesUrl = "https://github.com/rndadhdman/AllInOnePDF/issues";

    /// <summary>The source repository.</summary>
    public const string RepositoryUrl = "https://github.com/rndadhdman/AllInOnePDF";

    /// <summary>The author's website.</summary>
    public const string WebsiteUrl = "https://therandomadmin.com";

    private static readonly Lazy<(string Raw, string Version, string? Commit)> Parsed = new(Parse);

    /// <summary>The raw <see cref="AssemblyInformationalVersionAttribute"/>, for example <c>0.1.0+25e50f5c…</c>.</summary>
    public static string InformationalVersion => Parsed.Value.Raw;

    /// <summary>The version without build metadata, for example <c>0.1.0</c>.</summary>
    public static string Version => Parsed.Value.Version;

    /// <summary>The short (7 character) commit hash when the build recorded one, otherwise null.</summary>
    public static string? Commit => Parsed.Value.Commit;

    /// <summary>The version as shown to users: <c>0.1.0 (25e50f5)</c>, or <c>0.1.0</c> without a commit.</summary>
    public static string DisplayVersion => Commit is null ? Version : $"{Version} ({Commit})";

    /// <summary>The .NET runtime, for example <c>.NET 10.0.14</c>.</summary>
    public static string Runtime => RuntimeInformation.FrameworkDescription;

    /// <summary>The operating system and process architecture, for example <c>Microsoft Windows 10.0.26300 (X64)</c>.</summary>
    public static string OperatingSystem => $"{RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture})";

    /// <summary>Splits an informational version into the version and a short commit hash (exposed for tests).</summary>
    public static (string Version, string? Commit) Split(string informationalVersion)
    {
        ArgumentNullException.ThrowIfNull(informationalVersion);
        var plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        if (plus < 0)
        {
            return (informationalVersion, null);
        }

        var version = informationalVersion[..plus];
        var metadata = informationalVersion[(plus + 1)..];

        // SourceLink appends the full commit hash; other build metadata may be dot-separated.
        var hash = metadata.Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(IsHex);
        return (version, hash is null ? null : hash[..Math.Min(7, hash.Length)]);
    }

    private static bool IsHex(string value) => value.Length >= 7 && value.All(Uri.IsHexDigit);

    private static (string Raw, string Version, string? Commit) Parse()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(BuildInfo).Assembly;
        var raw = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                  ?? assembly.GetName().Version?.ToString()
                  ?? "unknown";
        var (version, commit) = Split(raw);
        return (raw, version, commit);
    }
}
