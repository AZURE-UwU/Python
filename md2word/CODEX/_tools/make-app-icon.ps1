# Generates the application icon (rounded office-blue tile, white "M", plus a
# down arrow when the size allows it) and writes a multi-size .ico file.
# All drawing/packing lives in the C# block below so the script stays ASCII-only.
param(
    [string]$IcoPath = ".\MarkdownToWord\Assets\app.ico",
    [string]$PreviewDir = ".\_output\icon-preview"
)

$ErrorActionPreference = 'Stop'

$code = @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;

public static class AppIconMaker
{
    private static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

    public static void Run(string icoPath, string previewDir)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(icoPath)));
        Directory.CreateDirectory(previewDir);

        var images = new List<byte[]>();
        foreach (var size in Sizes)
        {
            using (var bmp = Draw(size))
            {
                images.Add(size >= 64 ? ToPng(bmp) : ToDib(bmp));
                if (size == 256 || size == 48 || size == 32 || size == 16)
                {
                    bmp.Save(Path.Combine(previewDir, "preview-" + size + ".png"), ImageFormat.Png);
                }
            }
        }

        using (var stream = new FileStream(icoPath, FileMode.Create, FileAccess.Write))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write((short)0);              // reserved
            writer.Write((short)1);              // type: icon
            writer.Write((short)Sizes.Length);

            var offset = 6 + 16 * Sizes.Length;
            for (var i = 0; i < Sizes.Length; i++)
            {
                writer.Write((byte)(Sizes[i] >= 256 ? 0 : Sizes[i]));
                writer.Write((byte)(Sizes[i] >= 256 ? 0 : Sizes[i]));
                writer.Write((byte)0);           // palette
                writer.Write((byte)0);           // reserved
                writer.Write((short)1);          // planes
                writer.Write((short)32);         // bpp
                writer.Write(images[i].Length);
                writer.Write(offset);
                offset += images[i].Length;
            }

            foreach (var image in images)
            {
                writer.Write(image);
            }
        }

        Console.WriteLine("icon written: " + Path.GetFullPath(icoPath) + " (" + new FileInfo(icoPath).Length + " bytes, " + Sizes.Length + " sizes)");
        Console.WriteLine("previews: " + Path.GetFullPath(previewDir));
    }

    private static Bitmap Draw(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Color.Transparent);

            var pad = size * 0.045f;
            var rect = new RectangleF(pad, pad, size - 2 * pad, size - 2 * pad);
            using (var path = RoundedPath(rect, size * 0.22f))
            using (var brush = new LinearGradientBrush(rect, Color.FromArgb(55, 106, 176), Color.FromArgb(21, 60, 112), 45f))
            {
                g.FillPath(brush, path);
            }

            var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };

            var fontSize = size >= 32 ? size * 0.46f : size * 0.60f;
            using (var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                var textRect = size >= 32
                    ? new RectangleF(0, size * 0.06f, size, size * 0.56f)
                    : new RectangleF(0, 0, size, size);
                textRect.Offset(0, size * 0.02f);
                g.DrawString("M", font, Brushes.White, textRect, format);
            }

            format.Dispose();

            if (size >= 32)
            {
                var cx = size / 2f;
                var stemW = size * 0.11f;
                var stemH = size * 0.12f;
                var stemTop = size * 0.63f;
                g.FillRectangle(Brushes.White, cx - stemW / 2, stemTop, stemW, stemH);

                var headW = size * 0.30f;
                var headTop = stemTop + stemH - size * 0.01f;
                g.FillPolygon(Brushes.White, new[]
                {
                    new PointF(cx - headW / 2, headTop),
                    new PointF(cx + headW / 2, headTop),
                    new PointF(cx, size * 0.88f),
                });
            }
        }

        return bmp;
    }

    private static GraphicsPath RoundedPath(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static byte[] ToPng(Bitmap bmp)
    {
        using (var ms = new MemoryStream())
        {
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
    }

    private static byte[] ToDib(Bitmap bmp)
    {
        var size = bmp.Width;
        var xor = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            var srcY = size - 1 - y;      // DIB rows are stored bottom-up
            for (var x = 0; x < size; x++)
            {
                var c = bmp.GetPixel(x, srcY);
                var i = (y * size + x) * 4;
                xor[i] = c.B;
                xor[i + 1] = c.G;
                xor[i + 2] = c.R;
                xor[i + 3] = c.A;
            }
        }

        var maskRow = ((size + 31) / 32) * 4;
        var mask = new byte[maskRow * size];

        var header = new byte[40];
        BitConverter.GetBytes(40).CopyTo(header, 0);
        BitConverter.GetBytes(size).CopyTo(header, 4);
        BitConverter.GetBytes(size * 2).CopyTo(header, 8);
        BitConverter.GetBytes((short)1).CopyTo(header, 12);
        BitConverter.GetBytes((short)32).CopyTo(header, 14);
        BitConverter.GetBytes(xor.Length + mask.Length).CopyTo(header, 20);

        var data = new byte[header.Length + xor.Length + mask.Length];
        header.CopyTo(data, 0);
        xor.CopyTo(data, header.Length);
        mask.CopyTo(data, header.Length + xor.Length);
        return data;
    }
}
'@

Add-Type -TypeDefinition $code -Language CSharp -ReferencedAssemblies System.Drawing, System.Drawing.Primitives, System.Runtime
[AppIconMaker]::Run($IcoPath, $PreviewDir)
