using System.Text;

namespace AllInOnePdf.Core.Rendering;

/// <summary>Reads pixel dimensions from PNG, JPEG, GIF, BMP and WebP headers without decoding the image.</summary>
public static class ImageHeader
{
    /// <summary>Width and height in pixels, or (0, 0) when the format or header is not recognised.</summary>
    public static (int Width, int Height) ReadPixelSize(byte[] d)
    {
        ArgumentNullException.ThrowIfNull(d);
        try
        {
            if (d.Length > 24 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47)
            {
                return (BigEndian(d, 16), BigEndian(d, 20));
            }

            if (d.Length > 10 && d[0] == 'G' && d[1] == 'I' && d[2] == 'F')
            {
                return (d[6] | (d[7] << 8), d[8] | (d[9] << 8));
            }

            if (d.Length > 26 && d[0] == 'B' && d[1] == 'M')
            {
                return (BitConverter.ToInt32(d, 18), Math.Abs(BitConverter.ToInt32(d, 22)));
            }

            if (d.Length > 30 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P')
            {
                var chunk = Encoding.ASCII.GetString(d, 12, 4);
                return chunk switch
                {
                    "VP8X" => (1 + (d[24] | (d[25] << 8) | (d[26] << 16)), 1 + (d[27] | (d[28] << 8) | (d[29] << 16))),
                    "VP8 " => ((d[26] | (d[27] << 8)) & 0x3FFF, (d[28] | (d[29] << 8)) & 0x3FFF),
                    "VP8L" => (1 + (d[21] | ((d[22] & 0x3F) << 8)), 1 + (((d[22] & 0xC0) >> 6) | (d[23] << 2) | ((d[24] & 0x0F) << 10))),
                    _ => (0, 0),
                };
            }

            if (d.Length > 4 && d[0] == 0xFF && d[1] == 0xD8)
            {
                var i = 2;
                while (i + 9 < d.Length)
                {
                    if (d[i] != 0xFF)
                    {
                        i++;
                        continue;
                    }

                    var marker = d[i + 1];
                    if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
                    {
                        return ((d[i + 7] << 8) | d[i + 8], (d[i + 5] << 8) | d[i + 6]);
                    }

                    if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7) or 0xFF)
                    {
                        i += marker == 0xFF ? 1 : 2;
                        continue;
                    }

                    i += 2 + ((d[i + 2] << 8) | d[i + 3]);
                }
            }
        }
        catch (ArgumentException)
        {
        }

        return (0, 0);
    }

    private static int BigEndian(byte[] d, int o) => (d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3];
}
