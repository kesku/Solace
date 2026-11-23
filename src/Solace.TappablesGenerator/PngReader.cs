using System.Buffers.Binary;
using System.IO.Compression;

namespace Solace.TappablesGenerator;

// Minimal PNG decoder for the map tiles rendered by Solace.TileRenderer, which are grey so only the first channel is needed
public static class PngReader
{
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // Returns the first channel (grey, red or the red value of the palette entry) of each pixel, row by row starting at the top left.
    // Only non-interlaced images with a bit depth of 8 are supported.
    public static byte[] ReadFirstChannel(ReadOnlySpan<byte> png, out int width, out int height)
    {
        if (!png.StartsWith(Signature))
        {
            throw new InvalidDataException("Not a PNG image");
        }

        width = 0;
        height = 0;
        int colorType = -1;
        byte[]? palette = null;
        using var compressedData = new MemoryStream();

        int pos = Signature.Length;
        while (true)
        {
            if (pos + 12 > png.Length)
            {
                throw new InvalidDataException("Unexpected end of PNG image");
            }

            int length = BinaryPrimitives.ReadInt32BigEndian(png[pos..]);
            if (length < 0 || length > png.Length - pos - 12)
            {
                throw new InvalidDataException("Invalid PNG chunk length");
            }

            ReadOnlySpan<byte> type = png.Slice(pos + 4, 4);
            ReadOnlySpan<byte> data = png.Slice(pos + 8, length);
            pos += length + 12;

            if (type.SequenceEqual("IHDR"u8))
            {
                if (data.Length < 13)
                {
                    throw new InvalidDataException("Invalid PNG header");
                }

                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                int bitDepth = data[8];
                colorType = data[9];
                int interlaceMethod = data[12];

                if (width <= 0 || height <= 0 || width > 4096 || height > 4096)
                {
                    throw new InvalidDataException($"Unsupported PNG size {width}x{height}");
                }

                if (bitDepth != 8 || interlaceMethod != 0)
                {
                    throw new InvalidDataException($"Unsupported PNG format (bit depth {bitDepth}, interlace method {interlaceMethod})");
                }
            }
            else if (type.SequenceEqual("PLTE"u8))
            {
                palette = data.ToArray();
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                compressedData.Write(data);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                break;
            }
        }

        int bytesPerPixel = colorType switch
        {
            0 => 1, // grey
            2 => 3, // RGB
            3 => 1, // palette
            4 => 2, // grey + alpha
            6 => 4, // RGBA
            _ => throw new InvalidDataException(colorType == -1 ? "PNG image has no header" : $"Unsupported PNG colour type {colorType}"),
        };

        if (colorType == 3 && palette is null)
        {
            throw new InvalidDataException("PNG image has no palette");
        }

        int stride = width * bytesPerPixel;
        byte[] rows = new byte[(stride + 1) * height];

        compressedData.Position = 0;
        using (var zlibStream = new ZLibStream(compressedData, CompressionMode.Decompress))
        {
            zlibStream.ReadExactly(rows);
        }

        byte[] result = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            int filter = rows[y * (stride + 1)];
            Span<byte> row = rows.AsSpan((y * (stride + 1)) + 1, stride);
            ReadOnlySpan<byte> previousRow = y > 0 ? rows.AsSpan(((y - 1) * (stride + 1)) + 1, stride) : [];

            Unfilter(filter, row, previousRow, bytesPerPixel);

            for (int x = 0; x < width; x++)
            {
                byte value = row[x * bytesPerPixel];
                if (colorType == 3)
                {
                    value = value * 3 < palette!.Length ? palette[value * 3] : (byte)0;
                }

                result[(y * width) + x] = value;
            }
        }

        return result;
    }

    private static void Unfilter(int filter, Span<byte> row, ReadOnlySpan<byte> previousRow, int bytesPerPixel)
    {
        for (int i = 0; i < row.Length; i++)
        {
            int left = i >= bytesPerPixel ? row[i - bytesPerPixel] : 0;
            int up = previousRow.IsEmpty ? 0 : previousRow[i];
            int upLeft = i >= bytesPerPixel && !previousRow.IsEmpty ? previousRow[i - bytesPerPixel] : 0;

            row[i] = (byte)(row[i] + filter switch
            {
                0 => 0,
                1 => left,
                2 => up,
                3 => (left + up) / 2,
                4 => Paeth(left, up, upLeft),
                _ => throw new InvalidDataException($"Invalid PNG filter type {filter}"),
            });
        }
    }

    private static int Paeth(int left, int up, int upLeft)
    {
        int p = left + up - upLeft;
        int pLeft = Math.Abs(p - left);
        int pUp = Math.Abs(p - up);
        int pUpLeft = Math.Abs(p - upLeft);

        return pLeft <= pUp && pLeft <= pUpLeft ? left : pUp <= pUpLeft ? up : upLeft;
    }
}
