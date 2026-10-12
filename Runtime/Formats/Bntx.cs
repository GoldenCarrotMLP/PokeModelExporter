using System;
using System.IO;
using System.Text;

namespace PokeModelExporter.Formats
{
    // Nintendo Switch texture container (BNTX). Reads the first texture's top mip level and
    // returns RGBA8. Layout (little endian):
    //   0x00 "BNTX" header; 0x20 "NX  " header with 0x28 = offset of the texture info pointer array
    //   BRTI (texture info): 0x12 tile mode, 0x1C format, 0x24 width, 0x28 height,
    //        0x34 texture layout (low 3 bits = log2 block height in GOBs), 0x70 = offset of the
    //        mip offset array (absolute file offsets)
    // Pixel data is in the Tegra X1 "block linear" layout: 64-byte x 8-row GOBs stacked
    // blockHeight GOBs tall. Format codes: high byte = layout, low byte = type (1 UNORM, 6 SRGB).
    public static class Bntx
    {
        public sealed class Texture
        {
            public int Width, Height;
            public uint Format;
            public byte[] Rgba;
        }

        public static Texture Read(byte[] file, bool normalMap = false)
        {
            if (Encoding.ASCII.GetString(file, 0, 4) != "BNTX") throw new InvalidDataException("Not a BNTX file");
            long infoPtrs = BitConverter.ToInt64(file, 0x28);
            int brti = (int)BitConverter.ToInt64(file, (int)infoPtrs);
            if (Encoding.ASCII.GetString(file, brti, 4) != "BRTI") throw new InvalidDataException("Missing BRTI");

            int tileMode = BitConverter.ToUInt16(file, brti + 0x12);
            uint format = BitConverter.ToUInt32(file, brti + 0x1C);
            int width = BitConverter.ToInt32(file, brti + 0x24), height = BitConverter.ToInt32(file, brti + 0x28);
            int blockHeightLog2 = BitConverter.ToInt32(file, brti + 0x34) & 7;
            long mipPtrs = BitConverter.ToInt64(file, brti + 0x70);
            int data = (int)BitConverter.ToInt64(file, (int)mipPtrs);

            int layout = (int)(format >> 8);
            BcDecoder.Format? bc = layout switch
            {
                0x1A => BcDecoder.Format.BC1,
                0x1B => BcDecoder.Format.BC2,
                0x1C => BcDecoder.Format.BC3,
                0x1D => BcDecoder.Format.BC4,
                0x1E => BcDecoder.Format.BC5,
                0x20 => BcDecoder.Format.BC7,
                _ => (BcDecoder.Format?)null,
            };

            int blockW = bc != null ? 4 : 1, bytesPerBlock = bc != null ? BcDecoder.BlockBytes(bc.Value) : BytesPerPixel(layout);
            int wBlocks = (width + blockW - 1) / blockW, hBlocks = (height + blockW - 1) / blockW;
            var linear = tileMode == 1
                ? Slice(file, data, wBlocks * hBlocks * bytesPerBlock) // already linear (pitch)
                : Deswizzle(file, data, wBlocks, hBlocks, bytesPerBlock, 1 << blockHeightLog2);

            byte[] rgba;
            if (bc != null) rgba = BcDecoder.Decode(linear, width, height, bc.Value, normalMap);
            else rgba = Uncompressed(linear, width, height, layout);
            return new Texture { Width = width, Height = height, Format = format, Rgba = rgba };
        }

        static int BytesPerPixel(int layout) => layout switch
        {
            0x02 => 1, // R8
            0x09 => 2, // R8G8
            0x0B => 4, // R8G8B8A8
            0x0C => 4, // B8G8R8A8
            _ => throw new NotSupportedException($"BNTX format layout 0x{layout:X2}"),
        };

        static byte[] Uncompressed(byte[] src, int w, int h, int layout)
        {
            var dst = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++)
            {
                switch (layout)
                {
                    case 0x02: dst[i * 4] = dst[i * 4 + 1] = dst[i * 4 + 2] = src[i]; dst[i * 4 + 3] = 255; break;
                    case 0x09: dst[i * 4] = src[i * 2]; dst[i * 4 + 1] = src[i * 2 + 1]; dst[i * 4 + 3] = 255; break;
                    case 0x0B: Buffer.BlockCopy(src, i * 4, dst, i * 4, 4); break;
                    case 0x0C: dst[i * 4] = src[i * 4 + 2]; dst[i * 4 + 1] = src[i * 4 + 1]; dst[i * 4 + 2] = src[i * 4]; dst[i * 4 + 3] = src[i * 4 + 3]; break;
                }
            }
            return dst;
        }

        // Tegra X1 block linear -> linear rows of "elements" (pixels, or 4x4 blocks for BCn).
        static byte[] Deswizzle(byte[] src, int offset, int width, int height, int bpp, int blockHeight)
        {
            int widthBytes = width * bpp;
            int gobsWide = (widthBytes + 63) / 64;
            int blockRows = 8 * blockHeight;
            var dst = new byte[width * height * bpp];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < widthBytes; x++)
                {
                    long gob = (long)(y / blockRows) * 512 * blockHeight * gobsWide
                               + (x / 64) * 512L * blockHeight
                               + ((y % blockRows) / 8) * 512L;
                    int gx = x % 64, gy = y % 8;
                    int inGob = (gx / 32) * 256 + (gy / 2) * 64 + ((gx % 32) / 16) * 32 + (gy % 2) * 16 + (gx % 16);
                    long at = offset + gob + inGob;
                    if (at < src.Length) dst[y * widthBytes + x] = src[at];
                }
            }
            return dst;
        }

        static byte[] Slice(byte[] b, int offset, int length)
        {
            var s = new byte[Math.Min(length, b.Length - offset)];
            Buffer.BlockCopy(b, offset, s, 0, s.Length);
            return s;
        }
    }
}
