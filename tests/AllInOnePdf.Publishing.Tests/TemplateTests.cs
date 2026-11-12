using AllInOnePdf.Core.Themes;
using AllInOnePdf.Publishing.Content;
using AllInOnePdf.Publishing.Model;
using AllInOnePdf.Publishing.Templates;
using AllInOnePdf.Publishing.Validation;

namespace AllInOnePdf.Publishing.Tests;

public class TemplateTests
{
    [Fact]
    public void Templates_have_unique_ids_and_pass_validation_unchanged()
    {
        var all = BuiltInTemplates.All();
        Assert.Equal(["blank", "newsletter", "flyer", "report", "menu", "resume", "invoice", "poster"], all.Select(t => t.Id));
        Assert.Equal(all.Count, all.Select(t => t.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(all.Count, all.Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (var template in all)
        {
            Assert.False(string.IsNullOrWhiteSpace(template.Description), template.Id);
            Assert.NotEmpty(template.Tags);

            var publication = template.Create();
            Assert.Equal(template.Name, publication.Name);
            Assert.NotEmpty(publication.Sections);
            Assert.True(publication.Sections.SelectMany(s => s.Rows).All(r => r.Columns.Count == r.Fractions.Count), template.Id);
            Assert.NotNull(publication.ThemeId);
            Assert.NotNull(BuiltInThemes.Find(publication.ThemeId!));

            var modules = publication.Sections.SelectMany(PublicationFlattener.Modules).ToList();
            if (template.Id != BuiltInTemplates.BlankId)
            {
                Assert.NotEmpty(modules);
                Assert.NotEmpty(PublicationFlattener.ToDocument(publication).Blocks);
            }

            var before = publication.ToJson();
            var after = PublicationValidator.Normalize(publication.Clone()).ToJson();
            Assert.True(before == after, $"{template.Id} is changed by PublicationValidator.Normalize");
        }
    }

    [Fact]
    public void Blank_is_one_empty_column()
    {
        var blank = BuiltInTemplates.Find("BLANK")!.Create();
        var section = Assert.Single(blank.Sections);
        var row = Assert.Single(section.Rows);
        Assert.Empty(Assert.Single(row.Columns).Modules);
        Assert.Equal("clean", blank.ThemeId);
    }

    [Fact]
    public void Create_returns_fresh_ids_each_time()
    {
        var a = BuiltInTemplates.Find("newsletter")!.Create();
        var b = BuiltInTemplates.Find("newsletter")!.Create();
        Assert.NotEqual(a.Id, b.Id);
        Assert.NotEqual(a.Sections[0].Id, b.Sections[0].Id);
    }

    [Fact]
    public void Templates_use_the_specified_themes_and_layouts()
    {
        Assert.Equal("swiss", BuiltInTemplates.Find("flyer")!.Create().ThemeId);
        Assert.Equal("newsprint", BuiltInTemplates.Find("menu")!.Create().ThemeId);
        Assert.Equal("compact", BuiltInTemplates.Find("resume")!.Create().ThemeId);
        Assert.Equal("technical", BuiltInTemplates.Find("invoice")!.Create().ThemeId);

        var report = BuiltInTemplates.Find("report")!.Create();
        Assert.Equal("academic", report.ThemeId);
        var modules = report.Sections.SelectMany(s => s.Rows).SelectMany(r => r.Columns).SelectMany(c => c.Modules).ToList();
        Assert.Single(modules.OfType<ContentsModule>());
        Assert.Contains(modules.OfType<TableModule>(), _ => true);
        Assert.Contains(modules.OfType<CalloutModule>(), c => c.Kind == Core.Documents.CalloutKind.Note);

        var resume = BuiltInTemplates.Find("resume")!.Create();
        Assert.Equal("⅓ ⅔", ColumnLayouts.Match(resume.Sections[0].Rows[0].Fractions)?.Name);

        Assert.Null(BuiltInTemplates.Find("nope"));
    }
}
