using System.Globalization;
using AllInOnePdf.Core.Conversion;
using Microsoft.FluentUI.AspNetCore.Components;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace AllInOnePdf.UI.Services;

/// <summary>Small display helpers shared by the Convert and History pages.</summary>
public static class ConvertFormatting
{
    public static string Bytes(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? string.Create(CultureInfo.CurrentCulture, $"{bytes:N0} {units[0]}")
            : string.Create(CultureInfo.CurrentCulture, $"{value:0.#} {units[unit]}");
    }

    public static string Duration(TimeSpan elapsed) => Duration(elapsed.TotalSeconds);

    public static string Duration(double seconds) =>
        seconds switch
        {
            < 1 => string.Create(CultureInfo.CurrentCulture, $"{seconds * 1000:0} ms"),
            < 60 => string.Create(CultureInfo.CurrentCulture, $"{seconds:0.0} s"),
            _ => string.Create(CultureInfo.CurrentCulture, $"{(int)(seconds / 60)} min {seconds % 60:0} s"),
        };

    /// <summary>"Just now", "5 minutes ago", "Yesterday at 14:03", or a date.</summary>
    public static string When(DateTimeOffset timestamp)
    {
        var local = timestamp.ToLocalTime();
        var now = DateTimeOffset.Now;
        var delta = now - local;
        if (delta < TimeSpan.FromMinutes(1))
        {
            return "Just now";
        }

        if (delta < TimeSpan.FromHours(1))
        {
            var m = (int)delta.TotalMinutes;
            return m == 1 ? "1 minute ago" : string.Create(CultureInfo.CurrentCulture, $"{m} minutes ago");
        }

        if (local.Date == now.Date)
        {
            return string.Create(CultureInfo.CurrentCulture, $"Today at {local:t}");
        }

        if (local.Date == now.Date.AddDays(-1))
        {
            return string.Create(CultureInfo.CurrentCulture, $"Yesterday at {local:t}");
        }

        return local.ToString("g", CultureInfo.CurrentCulture);
    }

    /// <summary>Icon for a file based on its detected format.</summary>
    public static Icon FileIcon(FileFormat? format) =>
        format?.Id switch
        {
            "pdf" => new Icons.Regular.Size24.DocumentPdf(),
            "markdown" or "text" => new Icons.Regular.Size24.DocumentText(),
            "html" => new Icons.Regular.Size24.Globe(),
            "docx" or "rtf" => new Icons.Regular.Size24.DocumentBulletList(),
            "epub" => new Icons.Regular.Size24.BookOpen(),
            "code" or "json" => new Icons.Regular.Size24.Code(),
            "csv" or "xlsx" => new Icons.Regular.Size24.Table(),
            "pptx" => new Icons.Regular.Size24.SlideText(),
            "png" or "jpeg" or "webp" or "gif" or "bmp" or "tiff" or "image" => new Icons.Regular.Size24.Image(),
            null => new Icons.Regular.Size24.DocumentQuestionMark(),
            _ => new Icons.Regular.Size24.Document(),
        };

    /// <summary>A target's name as pickers list it: "PDF", "EPUB (preview)", "Excel workbook (coming soon)".</summary>
    public static string TargetName(IExporter target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.Status switch
        {
            ConverterStatus.Planned => $"{target.Format.DisplayName} (coming soon)",
            ConverterStatus.Preview => $"{target.Format.DisplayName} (preview)",
            _ => target.Format.DisplayName,
        };
    }

    public static string FormatName(string? formatId) =>
        formatId is null ? "Unknown" : KnownFormats.FindById(formatId)?.DisplayName ?? formatId;
}
