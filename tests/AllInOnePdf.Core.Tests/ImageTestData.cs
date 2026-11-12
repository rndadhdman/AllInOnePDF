using System.Text;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Importers;
using SkiaSharp;

namespace AllInOnePdf.Core.Tests;

/// <summary>Generates small images in the formats the image importer reads (Skia encodes PNG/JPEG/WebP; BMP and GIF are written by hand).</summary>
internal static class ImageTestData
{
    public static SKBitmap CreateBitmap(int width, int height)
    {
        var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.SteelBlue);
        using var paint = new SKPaint { Color = SKColors.Orange };
        canvas.DrawRect(0, 0, Math.Max(1, width / 2), Math.Max(1, height / 2), paint);
        return bitmap;
    }

    public static byte[] Encode(int width, int height, SKEncodedImageFormat format, int quality = 90)
    {
        using var bitmap = CreateBitmap(width, height);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, quality);
        return data.ToArray();
    }

    /// <summary>24-bit uncompressed bottom-up BMP.</summary>
    public static byte[] CreateBmp(int width, int height)
    {
        var stride = (width * 3 + 3) & ~3;
        var pixelBytes = stride * height;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)'B');
        w.Write((byte)'M');
        w.Write(54 + pixelBytes);
        w.Write(0);
        w.Write(54);
        w.Write(40);
        w.Write(width);
        w.Write(height);
        w.Write((short)1);
        w.Write((short)24);
        w.Write(0);
        w.Write(pixelBytes);
        w.Write(2835);
        w.Write(2835);
        w.Write(0);
        w.Write(0);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                w.Write((byte)(x * 255 / Math.Max(1, width - 1)));
                w.Write((byte)128);
                w.Write((byte)(y * 255 / Math.Max(1, height - 1)));
            }

            for (var p = width * 3; p < stride; p++)
            {
                w.Write((byte)0);
            }
        }

        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Two-colour GIF; LZW stays at 3-bit codes by emitting a clear code before every pair of pixels.</summary>
    public static byte[] CreateGif(int width, int height, int frames = 1)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("GIF89a"));
        w.Write((short)width);
        w.Write((short)height);
        w.Write((byte)0x80); // global colour table, 2 entries
        w.Write((byte)0);
        w.Write((byte)0);
        w.Write([0xFF, 0x00, 0x00, 0x00, 0x00, 0xFF]); // red, blue

        for (var f = 0; f < frames; f++)
        {
            // Graphic control extension: 10 ms delay.
            w.Write([0x21, 0xF9, 0x04, 0x00, 0x01, 0x00, 0x00, 0x00]);
            w.Write((byte)0x2C);
            w.Write((short)0);
            w.Write((short)0);
            w.Write((short)width);
            w.Write((short)height);
            w.Write((byte)0);
            w.Write((byte)2); // LZW minimum code size

            var bits = new List<bool>();
            void Emit(int code)
            {
                for (var b = 0; b < 3; b++)
                {
                    bits.Add(((code >> b) & 1) == 1);
                }
            }

            var total = width * height;
            for (var i = 0; i < total; i++)
            {
                if (i % 2 == 0)
                {
                    Emit(4);
                }

                Emit(((i % width) + f) % 2);
            }

            Emit(5);
            var bytes = new byte[(bits.Count + 7) / 8];
            for (var i = 0; i < bits.Count; i++)
            {
                if (bits[i])
                {
                    bytes[i / 8] |= (byte)(1 << (i % 8));
                }
            }

            for (var offset = 0; offset < bytes.Length; offset += 255)
            {
                var n = Math.Min(255, bytes.Length - offset);
                w.Write((byte)n);
                w.Write(bytes, offset, n);
            }

            w.Write((byte)0);
        }

        w.Write((byte)0x3B);
        w.Flush();
        return ms.ToArray();
    }

    public static async Task<Document> ImportImageAsync(byte[] data, string fileName, Action<OptionValues>? configure = null)
    {
        var importer = new ImageImporter();
        var options = OptionValues.FromDefaults(importer.Options);
        configure?.Invoke(options);
        using var stream = new MemoryStream(data);
        return await importer.ImportAsync(new ImportContext { Input = stream, FileName = fileName, Options = options });
    }

    public static (int Width, int Height) Decode(byte[] data)
    {
        using var bitmap = SKBitmap.Decode(data);
        return (bitmap.Width, bitmap.Height);
    }
}
