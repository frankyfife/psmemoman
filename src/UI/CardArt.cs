using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace MemcardManager.UI;

/// <summary>Draws the memory card artwork (program icon, panel headers, fallback icons).</summary>
public static class CardArt
{
    static GraphicsPath Rounded(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static GraphicsPath RoundedRect(RectangleF r, float rad) => Rounded(r, rad);

    /// <summary>Draws a memory card (PS2 style: dark, PS1 style: light grey) into the rectangle.</summary>
    public static void DrawCard(Graphics g, RectangleF area, bool ps2, Color? labelColor = null)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        // card has a portrait aspect ratio of ~0.78
        float h = area.Height, w = h * 0.78f;
        if (w > area.Width) { w = area.Width; h = w / 0.78f; }
        var r = new RectangleF(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
        float rad = w * 0.10f;

        Color bodyTop = ps2 ? Color.FromArgb(70, 74, 84) : Color.FromArgb(214, 216, 220);
        Color bodyBottom = ps2 ? Color.FromArgb(22, 24, 28) : Color.FromArgb(160, 163, 170);
        Color edge = ps2 ? Color.FromArgb(10, 10, 12) : Color.FromArgb(110, 112, 118);

        // shadow
        if (w >= 24)
        {
            using var sh = Rounded(new RectangleF(r.X + w * 0.04f, r.Y + h * 0.04f, w, h), rad);
            using var sb = new SolidBrush(Color.FromArgb(70, 0, 0, 0));
            g.FillPath(sb, sh);
        }

        // body with cut corner (top right) like a real card
        using (var body = new GraphicsPath())
        {
            float cut = w * 0.22f;
            body.AddArc(r.X, r.Y, rad * 2, rad * 2, 180, 90);
            body.AddLine(r.X + rad, r.Y, r.Right - cut, r.Y);
            body.AddLine(r.Right - cut, r.Y, r.Right, r.Y + cut);
            body.AddArc(r.Right - rad * 2, r.Bottom - rad * 2, rad * 2, rad * 2, 0, 90);
            body.AddArc(r.X, r.Bottom - rad * 2, rad * 2, rad * 2, 90, 90);
            body.CloseFigure();
            using var lg = new LinearGradientBrush(r, bodyTop, bodyBottom, LinearGradientMode.Vertical);
            g.FillPath(lg, body);
            using var pen = new Pen(edge, Math.Max(1f, w * 0.03f));
            g.DrawPath(pen, body);
        }

        // contacts on top
        if (w >= 20)
        {
            int n = 6;
            float cw = w * 0.07f, gap = w * 0.035f;
            float total = n * cw + (n - 1) * gap;
            float x0 = r.X + (w * 0.78f - total) / 2;
            using var cb = new SolidBrush(ps2 ? Color.FromArgb(200, 170, 80) : Color.FromArgb(120, 120, 126));
            for (int i = 0; i < n; i++) g.FillRectangle(cb, x0 + i * (cw + gap), r.Y + h * 0.04f, cw, h * 0.08f);
        }

        // label area
        var label = new RectangleF(r.X + w * 0.12f, r.Y + h * 0.24f, w * 0.76f, h * 0.48f);
        Color lc = labelColor ?? (ps2 ? Color.FromArgb(76, 141, 255) : Color.FromArgb(225, 70, 70));
        using (var lp = Rounded(label, w * 0.05f))
        {
            using var lb = new LinearGradientBrush(label, ControlPaint.Light(lc, 0.35f), ControlPaint.Dark(lc, 0.15f), LinearGradientMode.Vertical);
            g.FillPath(lb, lp);
        }
        if (w >= 28)
        {
            string txt = ps2 ? "PS2" : "PS1";
            float fs = label.Height * 0.36f;
            using var f = new Font("Segoe UI", fs, FontStyle.Bold, GraphicsUnit.Pixel);
            using var tb = new SolidBrush(Color.FromArgb(240, 255, 255, 255));
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(txt, f, tb, label, sf);
        }
        // grip lines at bottom
        if (w >= 32)
        {
            using var gp = new Pen(ps2 ? Color.FromArgb(90, 255, 255, 255) : Color.FromArgb(90, 0, 0, 0), Math.Max(1f, w * 0.02f));
            for (int i = 0; i < 3; i++)
            {
                float y = r.Y + h * (0.80f + i * 0.05f);
                g.DrawLine(gp, r.X + w * 0.25f, y, r.Right - w * 0.25f, y);
            }
        }
    }

    public static Bitmap Render(int size, bool ps2)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        float pad = size <= 16 ? 0 : size * 0.04f;
        DrawCard(g, new RectangleF(pad, pad, size - 2 * pad, size - 2 * pad), ps2);
        return bmp;
    }

    /// <summary>Writes a multi-resolution .ico (PNG compressed entries).</summary>
    public static void WriteIco(string path, params int[] sizes)
    {
        var pngs = new List<byte[]>();
        foreach (int s in sizes)
        {
            using var b = Render(s, true);
            using var ms = new MemoryStream();
            b.Save(ms, ImageFormat.Png);
            pngs.Add(ms.ToArray());
        }
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((ushort)1); w.Write((ushort)32);
            w.Write(pngs[i].Length);
            w.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var p in pngs) w.Write(p);
    }
}
