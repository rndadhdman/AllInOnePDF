using System.IO.Compression;
using System.Text;
using AllInOnePdf.Publishing.Model;
using AllInOnePdf.Publishing.Serialization;

namespace AllInOnePdf.Publishing.Tests;

public class PackageTests
{
    private static readonly byte[] Thumbnail = [0x89, 0x50, 0x4E, 0x47, 9, 9, 9];

    [Fact]
    public void Write_and_read_round_trip_assets_and_thumbnail()
    {
        var original = Samples.EveryModule();
        var bytes = PublicationPackage.Write(original, Thumbnail);

        using var stream = new MemoryStream(bytes);
        var (publication, thumbnail) = PublicationPackage.Read(stream);

        Assert.Equal(Thumbnail, thumbnail);
        var asset = Assert.Single(publication.Assets);
        Assert.Equal(original.Assets[0].Id, asset.Id);
        Assert.Equal(Samples.FakePng, asset.Data);
        Assert.Equal(640, asset.PixelWidth);
        Assert.Equal(original.ToJson(), publication.ToJson());

        var image = publication.Sections[0].Rows[0].Columns[0].Modules.OfType<ImageModule>().Single();
        Assert.Equal(asset.Id, image.AssetId);
    }

    [Fact]
    public void Package_holds_the_documented_entries()
    {
        var original = Samples.EveryModule();
        using var zip = new ZipArchive(new MemoryStream(PublicationPackage.Write(original)), ZipArchiveMode.Read);
        Assert.Equal(["publication.json", "assets/" + original.Assets[0].Id], zip.Entries.Select(e => e.FullName));
    }

    [Fact]
    public void Write_does_not_change_the_argument()
    {
        var original = Samples.EveryModule();
        original.Sections[0].Rows[0].GutterPt = 999;
        PublicationPackage.Write(original);
        Assert.Equal(999, original.Sections[0].Rows[0].GutterPt);
    }

    [Theory]
    [InlineData("evil.txt")]
    [InlineData("../publication.json")]
    [InlineData("assets/not-an-id")]
    [InlineData("assets/ABCDEF0123456789ABCDEF0123456789")]
    [InlineData("assets/sub/abcdef0123456789abcdef0123456789")]
    public void Read_rejects_unknown_entry_names(string entryName)
    {
        var bytes = PublicationPackage.Write(Samples.EveryModule());
        using var buffer = new MemoryStream();
        buffer.Write(bytes);
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Update, leaveOpen: true))
        {
            using var s = zip.CreateEntry(entryName).Open();
            s.Write("x"u8);
        }

        buffer.Position = 0;
        var ex = Assert.Throws<InvalidDataException>(() => PublicationPackage.Read(buffer));
        Assert.Contains(entryName, ex.Message);
    }

    [Fact]
    public void Read_rejects_packages_without_json_or_not_zip()
    {
        using (var empty = new MemoryStream())
        {
            using (new ZipArchive(empty, ZipArchiveMode.Create, leaveOpen: true))
            {
            }

            empty.Position = 0;
            Assert.Throws<InvalidDataException>(() => PublicationPackage.Read(empty));
        }

        using var junk = new MemoryStream(Encoding.UTF8.GetBytes("not a zip at all"));
        Assert.Throws<InvalidDataException>(() => PublicationPackage.Read(junk));
    }

    [Fact]
    public void Read_rejects_malformed_json()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var s = zip.CreateEntry("publication.json").Open();
            s.Write("{ \"sections\": ["u8);
        }

        buffer.Position = 0;
        Assert.Throws<InvalidDataException>(() => PublicationPackage.Read(buffer));
    }

    [Fact]
    public void Assets_without_an_entry_are_dropped_and_references_cleared()
    {
        var original = Samples.EveryModule();
        var bytes = PublicationPackage.Write(original);
        using var buffer = new MemoryStream();
        buffer.Write(bytes);
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Update, leaveOpen: true))
        {
            zip.GetEntry("assets/" + original.Assets[0].Id)!.Delete();
        }

        buffer.Position = 0;
        var (publication, thumbnail) = PublicationPackage.Read(buffer);
        Assert.Null(thumbnail);
        Assert.Empty(publication.Assets);
        Assert.Null(publication.Sections[0].Rows[0].Columns[0].Modules.OfType<ImageModule>().Single().AssetId);
    }

    [Fact]
    public void Read_accepts_non_seekable_streams()
    {
        var bytes = PublicationPackage.Write(Samples.EveryModule(), Thumbnail);
        using var stream = new ForwardOnlyStream(bytes);
        var (publication, thumbnail) = PublicationPackage.Read(stream);
        Assert.Single(publication.Assets);
        Assert.Equal(Thumbnail, thumbnail);
    }

    private sealed class ForwardOnlyStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
