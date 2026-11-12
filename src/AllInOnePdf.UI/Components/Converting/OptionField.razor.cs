using System.Globalization;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Fonts;
using AllInOnePdf.Core.Themes;
using AllInOnePdf.UI.Services;
using AllInOnePdf.UI.Services.Shared;
using Microsoft.AspNetCore.Components;

namespace AllInOnePdf.UI.Components.Converting;

/// <summary>Renders the control for a single <see cref="OptionDefinition"/> and writes changes into an <see cref="OptionValues"/> bag.</summary>
public partial class OptionField
{
    private IReadOnlyList<PdfTheme>? _themes;
    private string? _defaultThemeId;

    [Parameter, EditorRequired] public OptionDefinition Definition { get; set; } = default!;

    [Parameter, EditorRequired] public OptionValues Values { get; set; } = default!;

    /// <summary>Raised after a value was written, with the option key.</summary>
    [Parameter] public EventCallback<string> OnChanged { get; set; }

    [Parameter] public bool Disabled { get; set; }

    /// <summary>Optional pre-loaded theme list (saves each Theme field loading it separately).</summary>
    [Parameter] public IReadOnlyList<PdfTheme>? Themes { get; set; }

    /// <summary>Prefix for element ids so several panels can coexist on one page.</summary>
    [Parameter] public string IdPrefix { get; set; } = "opt";

    [Inject] private IThemeStore ThemeStore { get; set; } = default!;

    private string InputId => $"{IdPrefix}-{Definition.Key}";

    private string HelpId => $"{IdPrefix}-{Definition.Key}-help";

    private string? Error => ConvertOptionRules.Validate(Definition, Values);

    private bool HasRange => Definition.Min is not null && Definition.Max is not null;

    /// <summary>Step of an integer option's slider and arrow keys (whole numbers, at least 1).</summary>
    private double IntStep => Math.Max(1, Math.Round(Definition.Step ?? 1));

    private double DoubleStep => Definition.Step is { } s && s > 0 ? s : 0.1;

    private string InvalidText => Error is null ? "false" : "true";

    private object? Raw => Values.Contains(Definition.Key) ? Values.Get(Definition.Key) : Definition.DefaultValue;

    private bool BoolValue => Values.GetBool(Definition.Key, Definition.DefaultValue is bool b && b);

    private int IntValue => Values.Contains(Definition.Key) ? Values.GetInt(Definition.Key) : System.Convert.ToInt32(Definition.DefaultValue ?? 0, CultureInfo.InvariantCulture);

    private double DoubleValue => Values.Contains(Definition.Key) ? Values.GetDouble(Definition.Key) : System.Convert.ToDouble(Definition.DefaultValue ?? 0d, CultureInfo.InvariantCulture);

    private string? StringValue => Values.Contains(Definition.Key)
        ? Values.GetString(Definition.Key)
        : System.Convert.ToString(Definition.DefaultValue, CultureInfo.InvariantCulture);

    // <input type=color> only accepts #rrggbb.
    private string SwatchValue => ConvertOptionRules.IsValidHexColor(StringValue) ? HexColor.ToColorInputValue(StringValue) : "#000000";

    /// <summary>Theme option value: empty while the file follows the default theme (the key is unset).</summary>
    private string ThemeValue => Values.Contains(Definition.Key) ? Values.GetString(Definition.Key) ?? string.Empty : string.Empty;

    /// <summary>Theme shown for the "Default theme" choice (the store's default at the time the drawer opened).</summary>
    private PdfTheme? DefaultTheme => _defaultThemeId is null ? null : _themes?.FirstOrDefault(t => t.Id == _defaultThemeId);

    private IEnumerable<BundledFontFamily> FontChoices
    {
        get
        {
            var current = StringValue;
            foreach (var family in BundledFonts.Families)
            {
                yield return family;
            }

            if (!string.IsNullOrWhiteSpace(current) && !BundledFonts.IsBundled(current))
            {
                yield return new BundledFontFamily(current, FontClass.Sans, "System font: output may look different on other computers.", "");
            }
        }
    }

    private BundledFontFamily? SelectedFont => FontChoices.FirstOrDefault(f => string.Equals(f.Name, StringValue, StringComparison.OrdinalIgnoreCase));

    private PdfTheme? SelectedTheme => string.IsNullOrEmpty(ThemeValue) ? DefaultTheme : _themes?.FirstOrDefault(t => t.Id == ThemeValue);

    protected override async Task OnParametersSetAsync()
    {
        if (Definition.Type != OptionType.Theme)
        {
            return;
        }

        if (Themes is not null)
        {
            _themes = Themes;
        }
        else if (_themes is null)
        {
            _themes = await ThemeStore.GetAllAsync();
        }

        _defaultThemeId ??= await ThemeStore.GetDefaultThemeIdAsync();
    }

    private async Task SetAsync(object? value)
    {
        if (Equals(Raw, value))
        {
            return;
        }

        if (value is string s && string.IsNullOrEmpty(s) && Definition.Type is OptionType.Text or OptionType.MultilineText or OptionType.PageRange)
        {
            value = null;
        }

        Values.Set(Definition.Key, value);
        await OnChanged.InvokeAsync(Definition.Key);
    }

    /// <summary>A number from the slider or the number box, rounded to whole steps and kept inside the option's range.</summary>
    private Task SetIntAsync(double value) =>
        NumberInputMath.Normalize(value, Definition.Min ?? int.MinValue, Definition.Max ?? int.MaxValue, IntStep) is { } v
            ? SetAsync((int)v)
            : Task.CompletedTask;

    /// <summary>A number from the slider or the number box, rounded to the option's step and kept inside its range.</summary>
    private Task SetDoubleAsync(double value) =>
        NumberInputMath.Normalize(value, Definition.Min ?? double.MinValue, Definition.Max ?? double.MaxValue, DoubleStep) is { } v
            ? SetAsync(v)
            : Task.CompletedTask;

    /// <summary>Picking a theme makes it explicit for this file; "Default theme" removes the value again.</summary>
    private async Task SetThemeAsync(string? themeId)
    {
        if (string.Equals(themeId ?? string.Empty, ThemeValue, StringComparison.Ordinal))
        {
            return;
        }

        if (string.IsNullOrEmpty(themeId))
        {
            Values.Remove(Definition.Key);
        }
        else
        {
            Values.Set(Definition.Key, themeId);
        }

        await OnChanged.InvokeAsync(Definition.Key);
    }

    /// <summary>A tiny "Aa" chip in the theme's page colour, text colour, accent and body font. Values are validated
    /// before they reach the style attribute because user themes are untrusted JSON.</summary>
    private static string ThemeChipStyle(PdfTheme theme)
    {
        static string Safe(string? hex, string fallback) => ConvertOptionRules.IsValidHexColor(hex) ? hex! : fallback;
        var font = theme.Typography.BodyFont;
        var safeFont = !string.IsNullOrWhiteSpace(font) && font.All(c => char.IsLetterOrDigit(c) || c == ' ' || c == '-') ? font : "Inter";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"background:{Safe(theme.Page.Background, "#FFFFFF")};color:{Safe(theme.Colors.Text, "#1F2937")};border-left-color:{Safe(theme.Colors.Accent, "#0F6CBD")};font-family:'{safeFont}',var(--aio-font-ui)");
    }
}
