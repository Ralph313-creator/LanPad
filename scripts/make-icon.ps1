# Draws the LanPad app icon (gamepad + LAN signal on a blue/violet tile) and writes src\LanPad\LanPad.ico
# with 16, 20, 24, 32, 40, 48, 64, 128 and 256 px images. Run it again only if you change the design.
param(
    [string]$Out = (Join-Path $PSScriptRoot "..\src\LanPad\LanPad.ico"),
    [string]$Preview = ""
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing @"
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

public static class LanPadIcon
{
    static GraphicsPath Round(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath();
        float d = r * 2;
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // Everything is drawn on a 256 x 256 design grid and scaled. Small sizes drop the signal arcs
    // and use a bigger gamepad so the icon stays readable at 16 px.
    public static Bitmap Draw(int size)
    {
        bool small = size <= 24;
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            g.ScaleTransform(size / 256f, size / 256f);

            float inset = small ? 4 : 12;
            using (var tile = Round(inset, inset, 256 - 2 * inset, 256 - 2 * inset, small ? 44 : 52))
            using (var bg = new LinearGradientBrush(new PointF(0, 0), new PointF(256, 256),
                       Color.FromArgb(37, 99, 235), Color.FromArgb(124, 58, 237)))
            {
                g.FillPath(bg, tile);
                if (!small)
                    using (var shine = new LinearGradientBrush(new PointF(0, inset - 1), new PointF(0, 141),
                               Color.FromArgb(60, 255, 255, 255), Color.FromArgb(0, 255, 255, 255)))
                    {
                        g.SetClip(tile);
                        g.FillRectangle(shine, inset, inset, 256 - 2 * inset, 140 - inset);
                        g.ResetClip();
                    }

                // gamepad body: rounded bar + two grips
                float k = small ? 1.18f : 1f;           // scale of the pad around its centre
                float cy = small ? 132 : 152;           // vertical centre of the pad
                var st = g.Save();
                g.TranslateTransform(128, cy);
                g.ScaleTransform(k, k);
                using (var body = new GraphicsPath { FillMode = FillMode.Winding })
                {
                    body.AddPath(Round(-92, -40, 184, 74, 37), false);
                    body.AddEllipse(-96, -14, 66, 66);
                    body.AddEllipse(30, -14, 66, 66);
                    g.FillPath(Brushes.White, body);
                }
                // cut-outs use the tile gradient, mapped back to tile coordinates
                using (var cut = new LinearGradientBrush(new PointF(0, 0), new PointF(256, 256),
                           Color.FromArgb(37, 99, 235), Color.FromArgb(124, 58, 237)))
                {
                    cut.ScaleTransform(1 / k, 1 / k, MatrixOrder.Prepend);
                    cut.TranslateTransform(-128, -cy, MatrixOrder.Prepend);
                    // D-pad
                    g.FillPath(cut, Round(-62, -24, 14, 40, 3));
                    g.FillPath(cut, Round(-75, -11, 40, 14, 3));
                    // face buttons
                    float bx = 55, by = -4, o = 15, r = small ? 9 : 8;
                    g.FillEllipse(cut, bx - r, by - o - r, 2 * r, 2 * r);
                    g.FillEllipse(cut, bx - r, by + o - r, 2 * r, 2 * r);
                    g.FillEllipse(cut, bx - o - r, by - r, 2 * r, 2 * r);
                    g.FillEllipse(cut, bx + o - r, by - r, 2 * r, 2 * r);
                }
                g.Restore(st);

                // LAN signal arcs above the pad
                if (!small)
                    using (var pen = new Pen(Color.White, 13) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    {
                        g.FillEllipse(Brushes.White, 128 - 10, 96 - 10, 20, 20);
                        g.DrawArc(pen, 128 - 34, 96 - 34, 68, 68, 225, 90);
                        g.DrawArc(pen, 128 - 60, 96 - 60, 120, 120, 228, 84);
                    }
            }
        }
        return bmp;
    }

    // ICO with 32-bit DIB images (PNG for 256 px), readable by Explorer and System.Drawing.Icon.
    public static void WriteIco(string path, int[] sizes)
    {
        var images = new byte[sizes.Length][];
        for (int i = 0; i < sizes.Length; i++)
            using (var bmp = Draw(sizes[i]))
                images[i] = sizes[i] >= 256 ? Png(bmp) : Dib(bmp);

        using (var fs = new FileStream(path, FileMode.Create))
        using (var w = new BinaryWriter(fs))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32);
                w.Write(images[i].Length); w.Write(offset);
                offset += images[i].Length;
            }
            foreach (var img in images) w.Write(img);
        }
    }

    static byte[] Png(Bitmap bmp)
    {
        using (var ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); return ms.ToArray(); }
    }

    static byte[] Dib(Bitmap bmp)
    {
        int n = bmp.Width;
        int maskRow = ((n + 31) / 32) * 4;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            w.Write(40); w.Write(n); w.Write(n * 2); w.Write((short)1); w.Write((short)32);
            w.Write(0); w.Write(n * n * 4 + maskRow * n); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            for (int y = n - 1; y >= 0; y--)
                for (int x = 0; x < n; x++)
                {
                    Color c = bmp.GetPixel(x, y);
                    w.Write(c.B); w.Write(c.G); w.Write(c.R); w.Write(c.A);
                }
            for (int y = n - 1; y >= 0; y--)
            {
                var row = new byte[maskRow];
                for (int x = 0; x < n; x++)
                    if (bmp.GetPixel(x, y).A == 0) row[x / 8] |= (byte)(0x80 >> (x % 8));
                w.Write(row);
            }
            return ms.ToArray();
        }
    }
}
"@

$Out = [IO.Path]::GetFullPath($Out)
[LanPadIcon]::WriteIco($Out, [int[]](16, 20, 24, 32, 40, 48, 64, 128, 256))
"icon: $Out"
if ($Preview) {
    $sheet = New-Object System.Drawing.Bitmap 560, 280
    $g = [System.Drawing.Graphics]::FromImage($sheet)
    $g.Clear([System.Drawing.Color]::FromArgb(32, 32, 36))
    $g.DrawImage([LanPadIcon]::Draw(256), 8, 12)
    $g.DrawImage([LanPadIcon]::Draw(128), 280, 12)
    $x = 280
    foreach ($s in 64, 48, 32, 24, 16) { $g.DrawImage([LanPadIcon]::Draw($s), $x, 160); $x += $s + 10 }
    $g.Dispose(); $sheet.Save($Preview, [System.Drawing.Imaging.ImageFormat]::Png); "preview: $Preview"
}
