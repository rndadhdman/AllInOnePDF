using System.Text.Json;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Publishing.Model;
using AllInOnePdf.Publishing.Serialization;
using AllInOnePdf.Publishing.Validation;

namespace AllInOnePdf.Publishing.Tests;

public class ValidatorTests
{
    private static Publication Messy()
    {
        var image = new ImageModule { Id = "dup", AssetId = "missing", WidthPercent = 400, CornerRadiusPt = -3, MaxHeightPt = -1 };
        var heading = new HeadingModule { Id = "dup", Level = 9, Text = null! };
        var spacer = new SpacerModule { HeightPt = 5000, Design = new ModuleDesign { FontScale = 10, TextColor = "red", Margin = Spacing.All(999), Padding = null! } };
        var divider = new DividerModule { WidthPercent = 1, ThicknessPt = double.NaN, Color = "#12345G" };
        var table = new TableModule { Cells = [["a", "b", "c"], ["d"], null!], Alignments = [ColumnAlignment.Right, (ColumnAlignment)42, ColumnAlignment.Center, ColumnAlignment.Left] };
        var contents = new ContentsModule { Depth = 0, Title = null! };
        var button = new ButtonModule { Url = "  ", Color = "#abc", CornerRadiusPt = 1000, Style = (ButtonStyle)7 };

        var row = new Row
        {
            Id = "dup",
            Fractions = [0.01, 10, double.NaN],
            GutterPt = 500,
            Align = (VerticalAlign)99,
            Design = null!,
            Columns = [new Column { Modules = [image, heading, null!, spacer] }, null!, new Column { Modules = [divider, table, contents, button] }],
        };

        return new Publication
        {
            Id = "../evil",
            Name = "  ",
            Theme = null!,
            Metadata = null!,
            Tags = ["  ", "x"],
            Sections = [new Section { Rows = [row, null!, new Row { Fractions = [0.5, 0.5], Columns = [] }] }, null!],
            Assets = [new PublicationAsset { Id = "ABCDEF0123456789ABCDEF0123456789", FileName = "", MediaType = "", PixelWidth = -4 }, null!],
        };
    }

    [Fact]
    public void Normalize_clamps_and_repairs()
    {
        var p = PublicationValidator.Normalize(Messy());

        Assert.True(PublicationValidator.IsSafeId(p.Id));
        Assert.Equal("Untitled publication", p.Name);
        Assert.NotNull(p.Theme);
        Assert.NotNull(p.Metadata);
        Assert.Equal(["x"], p.Tags);
        Assert.Single(p.Sections);
        Assert.Equal(2, p.Sections[0].Rows.Count);

        var row = p.Sections[0].Rows[0];
        Assert.Equal(2, row.Columns.Count);
        Assert.Equal(2, row.Fractions.Count);
        Assert.Equal(1, row.Fractions.Sum(), 9);
        Assert.All(row.Fractions, f => Assert.True(f >= PublicationValidator.MinFraction - 1e-12));
        Assert.Equal(100, row.GutterPt);
        Assert.Equal(VerticalAlign.Top, row.Align);
        Assert.NotNull(row.Design);

        var empty = p.Sections[0].Rows[1];
        Assert.Equal(2, empty.Columns.Count);

        var modules = row.Columns.SelectMany(c => c.Modules).ToList();
        Assert.Equal(7, modules.Count);
        var ids = p.Sections.SelectMany(s => s.Rows).SelectMany(r => r.Columns.Select(c => c.Id).Prepend(r.Id))
            .Concat(modules.Select(m => m.Id)).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());

        var image = modules.OfType<ImageModule>().Single();
        Assert.Null(image.AssetId);
        Assert.Equal(100, image.WidthPercent);
        Assert.Equal(0, image.CornerRadiusPt);
        Assert.Null(image.MaxHeightPt);

        var heading = modules.OfType<HeadingModule>().Single();
        Assert.Equal(6, heading.Level);
        Assert.Equal("", heading.Text);

        var spacer = modules.OfType<SpacerModule>().Single();
        Assert.Equal(600, spacer.HeightPt);
        Assert.Equal(3, spacer.Design.FontScale);
        Assert.Null(spacer.Design.TextColor);
        Assert.Equal(200, spacer.Design.Margin.TopPt);
        Assert.NotNull(spacer.Design.Padding);

        var divider = modules.OfType<DividerModule>().Single();
        Assert.Equal(5, divider.WidthPercent);
        Assert.Equal(0.75, divider.ThicknessPt);
        Assert.Null(divider.Color);

        var table = modules.OfType<TableModule>().Single();
        Assert.Equal(2, table.Cells.Count);
        Assert.Equal(["d", "", ""], table.Cells[1]);
        Assert.Equal([ColumnAlignment.Right, ColumnAlignment.Default, ColumnAlignment.Center], table.Alignments);

        var contents = modules.OfType<ContentsModule>().Single();
        Assert.Equal(1, contents.Depth);
        Assert.Equal("Contents", contents.Title);

        var button = modules.OfType<ButtonModule>().Single();
        Assert.Null(button.Url);
        Assert.Equal("#abc", button.Color);
        Assert.Equal(100, button.CornerRadiusPt);
        Assert.Equal(ButtonStyle.Filled, button.Style);

        var asset = Assert.Single(p.Assets);
        Assert.Equal("abcdef0123456789abcdef0123456789", asset.Id);
        Assert.Equal("image", asset.FileName);
        Assert.Null(asset.PixelWidth);
    }

    [Fact]
    public void Normalize_is_idempotent()
    {
        var p = PublicationValidator.Normalize(Messy());
        var once = p.ToJson();
        var twice = PublicationValidator.Normalize(p).ToJson();
        Assert.Equal(once, twice);

        var sample = PublicationValidator.Normalize(Samples.EveryModule());
        Assert.Equal(sample.ToJson(), PublicationValidator.Normalize(sample.Clone()).ToJson());
    }

    [Fact]
    public void Fractions_below_the_minimum_are_lifted_and_stay_put()
    {
        var fractions = PublicationValidator.NormalizeFractions([0.001, 1, 1, 0.002], 4);
        Assert.Equal(1, fractions.Sum(), 9);
        Assert.Equal(PublicationValidator.MinFraction, fractions[0], 9);
        Assert.Equal(PublicationValidator.MinFraction, fractions[3], 9);
        Assert.Equal(0.45, fractions[1], 9);
        Assert.Same(fractions, PublicationValidator.NormalizeFractions(fractions, 4));
    }

    [Fact]
    public void Asset_references_follow_repaired_asset_ids()
    {
        var asset = new PublicationAsset { Id = "ABCDEF0123456789ABCDEF0123456789", Data = Samples.FakePng };
        var image = new ImageModule { AssetId = asset.Id };
        var p = new Publication { Assets = [asset], Sections = [new Section { Rows = [new Row { Columns = [new Column { Modules = [image] }] }] }] };

        PublicationValidator.Normalize(p);

        Assert.Equal("abcdef0123456789abcdef0123456789", image.AssetId);
    }

    [Fact]
    public void Null_lists_from_json_are_filled()
    {
        const string json = """{ "sections": [ { "rows": null, "design": null } ], "assets": null, "tags": null, "metadata": { "authors": null } }""";
        var p = PublicationValidator.Normalize(JsonSerializer.Deserialize<Publication>(json, PublishingJson.Options)!);
        Assert.Empty(p.Sections[0].Rows);
        Assert.NotNull(p.Sections[0].Design);
        Assert.Empty(p.Assets);
        Assert.Empty(p.Metadata.Authors);
    }
}
