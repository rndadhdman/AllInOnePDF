using System.Text.Json;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Publishing.Model;

namespace AllInOnePdf.Publishing.Tests;

public class ModelTests
{
    [Fact]
    public void Publication_round_trips_through_json_with_every_module_type()
    {
        var original = Samples.EveryModule();
        var json = original.ToJson();
        var back = Publication.FromJson(json);

        var expected = original.Sections.SelectMany(s => s.Rows).SelectMany(r => r.Columns).SelectMany(c => c.Modules).ToList();
        var actual = back.Sections.SelectMany(s => s.Rows).SelectMany(r => r.Columns).SelectMany(c => c.Modules).ToList();
        Assert.Equal(expected.Select(m => m.GetType()), actual.Select(m => m.GetType()));
        Assert.Equal(expected.Select(m => m.Id), actual.Select(m => m.Id));
        Assert.Equal(12, actual.Select(m => m.GetType()).Distinct().Count());

        var callout = Assert.IsType<CalloutModule>(actual.Single(m => m is CalloutModule));
        Assert.Equal(CalloutKind.Warning, callout.Kind);
        var table = Assert.IsType<TableModule>(actual.Single(m => m is TableModule));
        Assert.Equal("€3", table.Cells[1][1]);
        Assert.Equal(ColumnAlignment.Right, table.Alignments[1]);
        Assert.Equal(json, back.ToJson());
    }

    [Fact]
    public void Json_uses_lowercase_discriminators_and_omits_asset_bytes()
    {
        var json = Samples.EveryModule().ToJson();
        using var doc = JsonDocument.Parse(json);
        var types = doc.RootElement.GetProperty("sections")[0].GetProperty("rows")[0].GetProperty("columns")
            .EnumerateArray().SelectMany(c => c.GetProperty("modules").EnumerateArray()).Select(m => m.GetProperty("type").GetString()).ToList();
        Assert.Equal(["heading", "text", "image", "divider", "spacer", "quote", "callout", "code", "table", "button", "contents", "pagebreak"], types);

        var asset = doc.RootElement.GetProperty("assets")[0];
        Assert.False(asset.TryGetProperty("data", out _));
        Assert.Equal("image/png", asset.GetProperty("mediaType").GetString());
    }

    [Fact]
    public void Serialises_with_core_json_options_too()
    {
        var original = Samples.EveryModule();
        var json = JsonSerializer.Serialize(original, CoreJson.Options);
        Assert.Equal(original.ToJson(), json);
        var back = JsonSerializer.Deserialize<Publication>(json, CoreJson.Options)!;
        Assert.IsType<HeadingModule>(back.Sections[0].Rows[0].Columns[0].Modules[0]);
    }

    [Fact]
    public void Type_discriminator_may_come_after_other_properties()
    {
        const string json = """
            { "name": "Hand made", "sections": [ { "rows": [ { "columns": [ { "modules": [
                { "id": "m1", "text": "Hi", "level": 2, "type": "heading" } ] } ] } ] } ] }
            """;
        var p = Publication.FromJson(json);
        var heading = Assert.IsType<HeadingModule>(p.Sections[0].Rows[0].Columns[0].Modules[0]);
        Assert.Equal(2, heading.Level);
        Assert.Equal("m1", heading.Id);
    }

    [Fact]
    public void Clone_is_deep_and_keeps_asset_bytes()
    {
        var original = Samples.EveryModule();
        var copy = original.Clone();

        Assert.Equal(original.ToJson(), copy.ToJson());
        Assert.Equal(Samples.FakePng, copy.Assets[0].Data);
        copy.Sections[0].Rows[0].Columns[0].Modules.Clear();
        copy.Theme.Colors.Accent = "#FF0000";
        Assert.NotEmpty(original.Sections[0].Rows[0].Columns[0].Modules);
        Assert.NotEqual("#FF0000", original.Theme.Colors.Accent);
    }

    [Fact]
    public void Defaults_match_the_specification()
    {
        var p = new Publication();
        Assert.Equal("Untitled publication", p.Name);
        Assert.Equal(1, p.SchemaVersion);
        Assert.Equal(32, p.Id.Length);
        Assert.Equal("en", p.Metadata.Language);

        var row = new Row();
        Assert.Equal([1.0], row.Fractions);
        Assert.Single(row.Columns);
        Assert.Equal(16, row.GutterPt);
        Assert.True(row.KeepTogether);
        Assert.Equal(VerticalAlign.Top, row.Align);

        Assert.Equal(1, new ModuleDesign().FontScale);
        Assert.Equal("Heading", new HeadingModule().Text);
        Assert.Equal(100, new ImageModule().WidthPercent);
        Assert.Equal(BlockAlign.Center, new ImageModule().Align);
        Assert.Equal(0.75, new DividerModule().ThicknessPt);
        Assert.Equal(24, new SpacerModule().HeightPt);
        Assert.Equal("Learn more", new ButtonModule().Text);
        Assert.Equal(4, new ButtonModule().CornerRadiusPt);
        Assert.Equal(2, new ContentsModule().Depth);
        Assert.True(new ContentsModule().ShowPageNumbers);
        Assert.True(new TableModule().HeaderRow);

        var s = Spacing.All(5);
        Assert.Equal([5.0, 5, 5, 5], [s.TopPt, s.RightPt, s.BottomPt, s.LeftPt]);
        var v = Spacing.Vertical(7);
        Assert.Equal([7.0, 0, 7, 0], [v.TopPt, v.RightPt, v.BottomPt, v.LeftPt]);
    }

    [Fact]
    public void Column_layout_presets_sum_to_one()
    {
        Assert.Equal(13, ColumnLayouts.Presets.Count);
        foreach (var preset in ColumnLayouts.Presets)
        {
            Assert.Equal(1, preset.Fractions.Sum(), 9);
        }

        Assert.Equal("⅓ ⅔", ColumnLayouts.Match([1.0 / 3, 2.0 / 3])?.Name);
        Assert.Null(ColumnLayouts.Match([0.1, 0.9]));
    }

    [Fact]
    public void Apply_keeps_modules_when_columns_are_removed()
    {
        var a = new TextModule { Markdown = "a" };
        var b = new TextModule { Markdown = "b" };
        var c = new TextModule { Markdown = "c" };
        var row = new Row
        {
            Fractions = [1.0 / 3, 1.0 / 3, 1.0 / 3],
            Columns = [new Column { Modules = [a] }, new Column { Modules = [b] }, new Column { Modules = [c] }],
        };
        var firstId = row.Columns[0].Id;

        ColumnLayouts.Apply(row, [1]);

        var only = Assert.Single(row.Columns);
        Assert.Equal(firstId, only.Id);
        Assert.Equal([a, b, c], only.Modules);
        Assert.Equal([1.0], row.Fractions);
    }

    [Fact]
    public void Apply_adds_empty_columns_and_scales_fractions()
    {
        var a = new TextModule();
        var row = new Row { Columns = [new Column { Modules = [a] }] };

        ColumnLayouts.Apply(row, [1, 2, 1]);

        Assert.Equal(3, row.Columns.Count);
        Assert.Equal([a], row.Columns[0].Modules);
        Assert.Empty(row.Columns[1].Modules);
        Assert.Empty(row.Columns[2].Modules);
        Assert.Equal([0.25, 0.5, 0.25], row.Fractions);
        Assert.Throws<ArgumentException>(() => ColumnLayouts.Apply(row, []));
        Assert.Throws<ArgumentException>(() => ColumnLayouts.Apply(row, [0.5, -1]));
    }
}
