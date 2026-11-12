using System.Text.Json;
using System.Text.Json.Serialization;
using AllInOnePdf.Core.Themes;
using AllInOnePdf.Publishing.Serialization;
using AllInOnePdf.Publishing.Validation;

namespace AllInOnePdf.Publishing.Model;

/// <summary>
/// A document built in the Creator: a stack of <see cref="Section"/>s laid out with a <see cref="PdfTheme"/> snapshot.
/// Plain mutable data; serialisable with <c>CoreJson.Options</c>. Repair untrusted input with
/// <see cref="PublicationValidator.Normalize"/>.
/// </summary>
public sealed class Publication
{
    /// <summary>Unique id (32 hex characters for new publications); also the package file name.</summary>
    public string Id { get; set; } = NewId();

    /// <summary>Name shown in the gallery.</summary>
    public string Name { get; set; } = "Untitled publication";

    /// <summary>Optional description shown in the gallery.</summary>
    public string? Description { get; set; }

    /// <summary>Format version of the JSON; bumped when the model changes incompatibly.</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>When the publication was first created.</summary>
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When the publication was last saved; the store sets it.</summary>
    public DateTimeOffset Modified { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Free tags for the gallery.</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>Document information written into the PDF and used by exporters.</summary>
    public PublicationMetadata Metadata { get; set; } = new();

    /// <summary>Gallery theme the snapshot came from (for the picker). May be stale or point at a deleted theme.</summary>
    public string? ThemeId { get; set; }

    /// <summary>
    /// The look of the publication: a snapshot, edited independently of the gallery theme. Page, typography, headings,
    /// colours, code, tables, quotes, lists, links, images, header, footer and callouts apply; <c>Cover</c> and
    /// <c>Toc</c> are ignored (sections and the contents module replace them).
    /// </summary>
    public PdfTheme Theme { get; set; } = BuiltInThemes.Clean();

    /// <summary>Content in reading order.</summary>
    public List<Section> Sections { get; set; } = [];

    /// <summary>
    /// The layout new sections start with and the Creator's "Structure / Free flow" switch. Changing it does not
    /// change existing sections (each keeps its own <see cref="Section.Layout"/>); the Creator offers to convert them.
    /// </summary>
    public SectionLayout DefaultLayout { get; set; } = SectionLayout.Structure;

    /// <summary>Images used by <see cref="ImageModule"/>s. Bytes live in the package, not in the JSON.</summary>
    public List<PublicationAsset> Assets { get; set; } = [];

    /// <summary>Finds an asset by id (ordinal), or null.</summary>
    public PublicationAsset? FindAsset(string? id) =>
        string.IsNullOrEmpty(id) ? null : Assets.FirstOrDefault(a => a is not null && string.Equals(a.Id, id, StringComparison.Ordinal));

    /// <summary>
    /// Deep copy through a JSON round trip. Asset bytes are not part of the JSON, so the copy shares the original's
    /// byte arrays (treat them as immutable).
    /// </summary>
    public Publication Clone()
    {
        var copy = JsonSerializer.Deserialize<Publication>(ToJson(), PublishingJson.Options)!;
        for (var i = 0; i < copy.Assets.Count && i < Assets.Count; i++)
        {
            if (copy.Assets[i] is { } target && Assets[i] is { } source)
            {
                target.Data = source.Data;
            }
        }

        return copy;
    }

    /// <summary>Serialises the publication (without asset bytes) with the shared Core JSON settings.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, PublishingJson.Options);

    /// <summary>Reads publication JSON and repairs it with <see cref="PublicationValidator.Normalize"/>. Asset bytes stay empty.</summary>
    /// <exception cref="JsonException">The JSON is malformed or empty.</exception>
    public static Publication FromJson(string json)
    {
        var publication = JsonSerializer.Deserialize<Publication>(json, PublishingJson.Options)
            ?? throw new JsonException("Publication JSON was empty.");
        return PublicationValidator.Normalize(publication);
    }

    /// <summary>A new id: <c>Guid.NewGuid().ToString("N")</c>.</summary>
    public static string NewId() => Guid.NewGuid().ToString("N");
}

/// <summary>Document information for the PDF info dictionary and the exporters.</summary>
public sealed class PublicationMetadata
{
    /// <summary>Title; falls back to <see cref="Publication.Name"/> when empty.</summary>
    public string? Title { get; set; }

    /// <summary>Optional subtitle.</summary>
    public string? Subtitle { get; set; }

    /// <summary>Authors in display order.</summary>
    public List<string> Authors { get; set; } = [];

    /// <summary>Publication date, used by the <c>{date}</c> header/footer template.</summary>
    public DateTimeOffset? Date { get; set; }

    /// <summary>BCP 47 language tag.</summary>
    public string Language { get; set; } = "en";

    /// <summary>Keywords for the PDF info dictionary.</summary>
    public List<string> Keywords { get; set; } = [];

    /// <summary>Subject / description.</summary>
    public string? Description { get; set; }
}

/// <summary>An embedded image. The bytes are stored in the package as <c>assets/&lt;id&gt;</c>.</summary>
public sealed class PublicationAsset
{
    /// <summary>32 lowercase hex characters.</summary>
    public string Id { get; set; } = Publication.NewId();

    /// <summary>Original file name, for display and export.</summary>
    public string FileName { get; set; } = "image";

    /// <summary>Media type such as <c>image/png</c>.</summary>
    public string MediaType { get; set; } = "application/octet-stream";

    /// <summary>The image bytes. Not serialised into <c>publication.json</c>; the package keeps them as a separate entry.</summary>
    [JsonIgnore]
    public byte[] Data { get; set; } = [];

    /// <summary>Intrinsic width in pixels, when known.</summary>
    public int? PixelWidth { get; set; }

    /// <summary>Intrinsic height in pixels, when known.</summary>
    public int? PixelHeight { get; set; }
}
