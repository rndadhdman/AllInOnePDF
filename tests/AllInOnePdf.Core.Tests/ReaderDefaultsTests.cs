using AllInOnePdf.UI.Services;

namespace AllInOnePdf.Core.Tests;

/// <summary>R2: adjustments in the Reader last for the session and never overwrite the defaults set in Settings.</summary>
public class ReaderDefaultsTests
{
    private static UserPreferences Defaults() => new()
    {
        ReaderFont = "Inter",
        ReaderJustify = true,
        ReaderFontSizePx = 20,
        ReaderTheme = ReaderTheme.Sepia,
        ReaderPaletteChosen = true,
    };

    [Fact]
    public void Reader_starts_from_the_settings_defaults()
    {
        var session = new ReaderSession();
        var shown = session.ResolveSettings(Defaults());
        Assert.Equal("Inter", shown.Font);
        Assert.True(shown.Justify);
        Assert.True(session.ResolvePaletteChosen(Defaults()));
    }

    [Fact]
    public void Adjustments_stay_in_the_session_and_leave_the_preferences_alone()
    {
        var preferences = Defaults();
        var before = preferences.Clone();
        var session = new ReaderSession();

        var adjusted = session.ResolveSettings(preferences) with { Font = "Literata", Justify = false, FontSizePx = 24, Palette = ReaderTheme.Night };
        session.Adjust(adjusted, paletteChosen: true);

        Assert.Equal("Literata", session.ResolveSettings(preferences).Font);
        Assert.Equal(24, session.ResolveSettings(preferences).FontSizePx);
        Assert.Equal(before.ReaderFont, preferences.ReaderFont);
        Assert.Equal(before.ReaderJustify, preferences.ReaderJustify);
        Assert.Equal(before.ReaderFontSizePx, preferences.ReaderFontSizePx);
        Assert.Equal(before.ReaderTheme, preferences.ReaderTheme);
    }

    [Fact]
    public void Reset_to_defaults_means_the_users_settings_not_factory_values()
    {
        var defaults = ReaderViewSettings.FromPreferences(Defaults());
        var adjusted = defaults with { Font = "JetBrains Mono", Justify = false, FontSizePx = 26, LineHeight = 1.4, Palette = ReaderTheme.Night };

        var reset = ReaderViewSettings.WithTypographyOf(adjusted, defaults);

        Assert.Equal("Inter", reset.Font);
        Assert.True(reset.Justify);
        Assert.Equal(20, reset.FontSizePx);
        Assert.Equal(defaults.LineHeight, reset.LineHeight);
        Assert.Equal(ReaderTheme.Night, reset.Palette); // the palette has its own buttons and stays
    }

    [Fact]
    public void Clearing_adjustments_shows_the_defaults_again()
    {
        var session = new ReaderSession();
        session.Adjust(new ReaderViewSettings { Font = "System" }, paletteChosen: false);
        session.ClearAdjustments();
        Assert.Equal("Inter", session.ResolveSettings(Defaults()).Font);
        Assert.True(session.ResolvePaletteChosen(Defaults()));
    }
}
