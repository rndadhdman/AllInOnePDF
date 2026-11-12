using System.Globalization;
using System.Text;

namespace AllInOnePdf.UI.Services.Shared;

/// <summary>
/// Builds the plain-text block that Settings › About › Copy diagnostics puts on the clipboard: version, runtime, host,
/// data folder and the start of the newest crash log. Nothing in it leaves the machine unless the user pastes it.
/// </summary>
public static class DiagnosticsReport
{
    /// <summary>Folder under the data folder where the desktop host writes <c>crash-*.log</c> files.</summary>
    public const string LogsFolderName = "logs";

    /// <summary>How many lines of the newest crash log are included.</summary>
    public const int CrashLogLines = 40;

    /// <summary>Returns the newest <c>crash-*.log</c> in <c>&lt;data&gt;/logs</c>, or null when there is none.</summary>
    public static FileInfo? FindNewestCrashLog(string dataDirectory)
    {
        try
        {
            var logs = new DirectoryInfo(Path.Combine(dataDirectory, LogsFolderName));
            return logs.Exists
                ? logs.EnumerateFiles("crash-*.log").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Builds the report.</summary>
    /// <param name="host">Desktop or web.</param>
    /// <param name="dataDirectory">The data folder in use.</param>
    /// <param name="isPortable">Whether portable mode is on.</param>
    /// <param name="defaultThemeId">The default PDF theme id, when known.</param>
    public static string Build(HostKind host, string dataDirectory, bool isPortable, string? defaultThemeId)
    {
        ArgumentNullException.ThrowIfNull(dataDirectory);
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"{BuildInfo.ProductName} diagnostics");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Version:       {BuildInfo.DisplayVersion}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Build:         {BuildInfo.InformationalVersion}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Runtime:       {BuildInfo.Runtime}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"OS:            {BuildInfo.OperatingSystem}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Host:          {HostLabel(host)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Data folder:   {dataDirectory}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Portable:      {(isPortable ? "yes" : "no")}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Default theme: {defaultThemeId ?? "unknown"}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Generated:     {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");

        var log = FindNewestCrashLog(dataDirectory);
        if (log is null)
        {
            sb.AppendLine("Crash log:     none");
            return sb.ToString();
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"Crash log:     {log.Name} ({log.LastWriteTime:yyyy-MM-dd HH:mm:ss})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"--- first {CrashLogLines} lines ---");
        try
        {
            foreach (var line in File.ReadLines(log.FullName).Take(CrashLogLines))
            {
                sb.AppendLine(line);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"(the log could not be read: {ex.Message})");
        }

        sb.AppendLine("--- end ---");
        return sb.ToString();
    }

    /// <summary>"Desktop app" or "Web app (browser)".</summary>
    public static string HostLabel(HostKind host) => host == HostKind.Desktop ? "Desktop app" : "Web app (browser)";
}
