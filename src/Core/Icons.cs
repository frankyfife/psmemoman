namespace MemcardManager.Core;

/// <summary>A sequence of ARGB frames (premultiplied not required) with per-frame duration.</summary>
public sealed class IconAnimation
{
    public int Size;
    public List<int[]> Frames = new();
    public double FrameSeconds = 0.1;
    public bool IsAnimated => Frames.Count > 1;
}

/// <summary>Parsed PS2 3D icon (.ico/.icn).</summary>
public sealed class Ps2IconModel
{
    public int VertexCount, Shapes;
    public float[][] Pos = Array.Empty<float[]>(); // [shape][v*3]
    public float[] Normals = Array.Empty<float>();
    public float[] Uv = Array.Empty<float>();
    public byte[] Colors = Array.Empty<byte>();
    public int[]? Texture; // 128*128 ARGB
    public uint FrameLength;
    public float AnimSpeed;

    public static Ps2IconModel? Parse(byte[] b)
    {
        try { return ParseCore(b); }
        catch { return null; }
    }

    static Ps2IconModel? ParseCore(byte[] b)
    {
        if (b.Length < 20 || Bin.U32(b, 0) != 0x010000) return null;
        int shapes = (int)Bin.U32(b, 4);
        uint texType = Bin.U32(b, 8);
        int vc = (int)Bin.U32(b, 16);
        if (shapes < 1 || shapes > 64 || vc < 3 || vc > 200000) return null;
        int per = shapes * 8 + 16;
        if (20L + (long)vc * per > b.Length) return null;
        var m = new Ps2IconModel { VertexCount = vc, Shapes = shapes };
        m.Pos = new float[shapes][];
        for (int s = 0; s < shapes; s++) m.Pos[s] = new float[vc * 3];
        m.Normals = new float[vc * 3];
        m.Uv = new float[vc * 2];
        m.Colors = new byte[vc * 4];
        const float k = 1f / 4096f;
        int o = 20;
        for (int i = 0; i < vc; i++)
        {
            for (int s = 0; s < shapes; s++)
            {
                m.Pos[s][i * 3] = Bin.S16(b, o) * k;
                m.Pos[s][i * 3 + 1] = Bin.S16(b, o + 2) * k;
                m.Pos[s][i * 3 + 2] = Bin.S16(b, o + 4) * k;
                o += 8;
            }
            m.Normals[i * 3] = Bin.S16(b, o) * k;
            m.Normals[i * 3 + 1] = Bin.S16(b, o + 2) * k;
            m.Normals[i * 3 + 2] = Bin.S16(b, o + 4) * k;
            o += 8;
            m.Uv[i * 2] = Bin.S16(b, o) * k;
            m.Uv[i * 2 + 1] = Bin.S16(b, o + 2) * k;
            o += 4;
            m.Colors[i * 4] = b[o];
            m.Colors[i * 4 + 1] = b[o + 1];
            m.Colors[i * 4 + 2] = b[o + 2];
            m.Colors[i * 4 + 3] = b[o + 3];
            o += 4;
        }
        if (o + 20 <= b.Length && Bin.U32(b, o) == 1)
        {
            m.FrameLength = Bin.U32(b, o + 4);
            m.AnimSpeed = Bin.F32(b, o + 8);
            uint frames = Bin.U32(b, o + 16);
            o += 20;
            for (int f = 0; f < frames && f < 1024; f++)
            {
                if (o + 16 > b.Length) return m;
                uint keys = Bin.U32(b, o + 4);
                o += 16;
                if (keys > 0) o += (int)(keys - 1) * 8;
            }
        }
        else return m; // geometry only
        if ((texType & 0x04) != 0 && o < b.Length)
        {
            ushort[] px = new ushort[128 * 128];
            if ((texType & 0x08) != 0)
            {
                if (o + 4 > b.Length) return m;
                int csize = (int)Bin.U32(b, o);
                o += 4;
                int end = Math.Min(b.Length, o + csize);
                int n = 0;
                while (o + 2 <= end && n < px.Length)
                {
                    ushort code = Bin.U16(b, o);
                    o += 2;
                    if ((code & 0x8000) != 0)
                    {
                        int cnt = 0x8000 - (code ^ 0x8000);
                        for (int i = 0; i < cnt && o + 2 <= end && n < px.Length; i++) { px[n++] = Bin.U16(b, o); o += 2; }
                    }
                    else if (code > 0)
                    {
                        if (o + 2 > end) break;
                        ushort v = Bin.U16(b, o);
                        o += 2;
                        for (int i = 0; i < code && n < px.Length; i++) px[n++] = v;
                    }
                }
            }
            else
            {
                for (int i = 0; i < px.Length && o + 2 <= b.Length; i++, o += 2) px[i] = Bin.U16(b, o);
            }
            var tex = new int[px.Length];
            for (int i = 0; i < px.Length; i++)
            {
                int p = px[i];
                int r = (p & 0x1F) << 3, g = ((p >> 5) & 0x1F) << 3, bl = ((p >> 10) & 0x1F) << 3;
                r |= r >> 5; g |= g >> 5; bl |= bl >> 5;
                tex[i] = unchecked((int)0xFF000000) | (r << 16) | (g << 8) | bl;
            }
            m.Texture = tex;
        }
        return m;
    }
}

/// <summary>Small software rasteriser for PS2 icons and PS1 icon decoding.</summary>
public static class IconRenderer
{
    /// <summary>Seconds for one full PS2 morph cycle.</summary>
    public static double CycleSeconds(Ps2IconModel m)
    {
        if (m.Shapes <= 1) return 0;
        double speed = m.AnimSpeed > 0.01 && m.AnimSpeed < 100 ? m.AnimSpeed : 1.0;
        double frames = m.FrameLength > 0 && m.FrameLength < 10000 ? m.FrameLength : m.Shapes * 10;
        double sec = frames / 60.0 / speed;
        return Math.Clamp(sec, 0.4, 20);
    }

    public static IconAnimation BuildPs2(Ps2IconModel m, IconSys? sys, int size, int maxFrames = 48)
    {
        var anim = new IconAnimation { Size = size };
        var bounds = Bounds(m);
        if (m.Shapes <= 1)
        {
            anim.Frames.Add(RenderPs2(m, sys, 0, size, bounds));
            return anim;
        }
        double cycle = CycleSeconds(m);
        int frames = (int)Math.Clamp(Math.Round(cycle * 20), m.Shapes * 2, maxFrames);
        anim.FrameSeconds = cycle / frames;
        for (int f = 0; f < frames; f++) anim.Frames.Add(RenderPs2(m, sys, (double)f / frames * m.Shapes, size, bounds));
        return anim;
    }

    record struct Box(float MinX, float MinY, float MinZ, float MaxX, float MaxY, float MaxZ);

    static Box Bounds(Ps2IconModel m)
    {
        float x0 = float.MaxValue, y0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue, z1 = float.MinValue;
        foreach (var p in m.Pos)
            for (int i = 0; i < m.VertexCount; i++)
            {
                float x = p[i * 3], y = p[i * 3 + 1], z = p[i * 3 + 2];
                x0 = Math.Min(x0, x); x1 = Math.Max(x1, x);
                y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
                z0 = Math.Min(z0, z); z1 = Math.Max(z1, z);
            }
        return new Box(x0, y0, z0, x1, y1, z1);
    }

    /// <summary>Renders one frame; phase in [0, shapes) blends shape floor(phase) with the next one.</summary>
    static int[] RenderPs2(Ps2IconModel m, IconSys? sys, double phase, int size, Box bx)
    {
        const int ss = 3; // supersampling
        int w = size * ss;
        var color = new int[w * w];
        var depth = new float[w * w];
        Array.Fill(depth, float.MaxValue);

        int s0 = (int)Math.Floor(phase) % m.Shapes, s1 = (s0 + 1) % m.Shapes;
        float t = (float)(phase - Math.Floor(phase));
        var a = m.Pos[s0];
        var b = m.Pos[s1];

        float cx = (bx.MinX + bx.MaxX) / 2, cy = (bx.MinY + bx.MaxY) / 2, cz = (bx.MinZ + bx.MaxZ) / 2;
        // rotate slightly so the 3D form is visible (like the PS2 browser)
        double yaw = -0.35, pitch = 0.18;
        float cyw = (float)Math.Cos(yaw), syw = (float)Math.Sin(yaw), cp = (float)Math.Cos(pitch), sp = (float)Math.Sin(pitch);

        int vc = m.VertexCount;
        var sx = new float[vc];
        var sy = new float[vc];
        var sz = new float[vc];
        var rx = new float[vc];
        var ry = new float[vc];
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        for (int i = 0; i < vc; i++)
        {
            float x = a[i * 3] + (b[i * 3] - a[i * 3]) * t - cx;
            float y = a[i * 3 + 1] + (b[i * 3 + 1] - a[i * 3 + 1]) * t - cy;
            float z = a[i * 3 + 2] + (b[i * 3 + 2] - a[i * 3 + 2]) * t - cz;
            // yaw around Y
            float x2 = x * cyw + z * syw;
            float z2 = -x * syw + z * cyw;
            // pitch around X
            float y3 = y * cp - z2 * sp;
            float z3 = y * sp + z2 * cp;
            rx[i] = x2; ry[i] = y3; sz[i] = z3;
        }
        // fit using all shapes' bounding sphere so the icon does not "pump" while animating
        float ext = Math.Max(bx.MaxX - bx.MinX, Math.Max(bx.MaxY - bx.MinY, bx.MaxZ - bx.MinZ));
        if (ext <= 0) ext = 1;
        float scale = w * 0.80f / ext;
        for (int i = 0; i < vc; i++)
        {
            sx[i] = w / 2f + rx[i] * scale;
            sy[i] = w / 2f + ry[i] * scale;
            minX = Math.Min(minX, sx[i]); maxX = Math.Max(maxX, sx[i]);
            minY = Math.Min(minY, sy[i]); maxY = Math.Max(maxY, sy[i]);
        }

        // lighting
        float ambR = 0.55f, ambG = 0.55f, ambB = 0.55f;
        var lights = new List<(float x, float y, float z, float r, float g, float b)>();
        if (sys != null)
        {
            float ar = sys.Ambient[0], ag = sys.Ambient[1], ab = sys.Ambient[2];
            if (ar + ag + ab > 0.05f && ar <= 2 && ag <= 2 && ab <= 2) { ambR = ar; ambG = ag; ambB = ab; }
            for (int l = 0; l < 3; l++)
            {
                var d = sys.LightDir[l];
                var c = sys.LightColor[l];
                float len = MathF.Sqrt(d[0] * d[0] + d[1] * d[1] + d[2] * d[2]);
                if (len < 1e-4f || c[0] + c[1] + c[2] < 0.01f || c[0] > 4 || c[1] > 4 || c[2] > 4) continue;
                lights.Add((d[0] / len, d[1] / len, d[2] / len, c[0], c[1], c[2]));
            }
        }
        if (lights.Count == 0) { lights.Add((0.4f, 0.4f, -0.8f, 0.5f, 0.5f, 0.5f)); }

        var tex = m.Texture;
        for (int tri = 0; tri + 2 < vc; tri += 3)
        {
            int i0 = tri, i1 = tri + 1, i2 = tri + 2;
            float x0 = sx[i0], y0 = sy[i0], x1 = sx[i1], y1 = sy[i1], x2 = sx[i2], y2 = sy[i2];
            float area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
            if (MathF.Abs(area) < 1e-6f) continue;
            int bx0 = Math.Max(0, (int)MathF.Floor(MathF.Min(x0, MathF.Min(x1, x2))));
            int bx1 = Math.Min(w - 1, (int)MathF.Ceiling(MathF.Max(x0, MathF.Max(x1, x2))));
            int by0 = Math.Max(0, (int)MathF.Floor(MathF.Min(y0, MathF.Min(y1, y2))));
            int by1 = Math.Min(w - 1, (int)MathF.Ceiling(MathF.Max(y0, MathF.Max(y1, y2))));
            if (bx0 > bx1 || by0 > by1) continue;

            // per-vertex lighting (Gouraud)
            Span<float> lr = stackalloc float[3], lg = stackalloc float[3], lb = stackalloc float[3];
            int[] idx = { i0, i1, i2 };
            for (int k = 0; k < 3; k++)
            {
                int v = idx[k];
                float nx = m.Normals[v * 3], ny = m.Normals[v * 3 + 1], nz = m.Normals[v * 3 + 2];
                float nl = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                if (nl > 1e-4f) { nx /= nl; ny /= nl; nz /= nl; }
                float r = ambR, g = ambG, bb = ambB;
                foreach (var L in lights)
                {
                    float dot = MathF.Abs(nx * L.x + ny * L.y + nz * L.z);
                    r += L.r * dot; g += L.g * dot; bb += L.b * dot;
                }
                float vr = m.Colors[v * 4] / 128f, vg = m.Colors[v * 4 + 1] / 128f, vb = m.Colors[v * 4 + 2] / 128f;
                if (vr + vg + vb < 0.05f) { vr = vg = vb = 1f; }
                lr[k] = Math.Clamp(r, 0.35f, 1.25f) * Math.Min(vr, 2f);
                lg[k] = Math.Clamp(g, 0.35f, 1.25f) * Math.Min(vg, 2f);
                lb[k] = Math.Clamp(bb, 0.35f, 1.25f) * Math.Min(vb, 2f);
            }
            float u0 = m.Uv[i0 * 2], v0 = m.Uv[i0 * 2 + 1], u1 = m.Uv[i1 * 2], v1 = m.Uv[i1 * 2 + 1], u2 = m.Uv[i2 * 2], v2 = m.Uv[i2 * 2 + 1];
            float z0 = sz[i0], z1 = sz[i1], z2 = sz[i2];
            float inv = 1f / area;
            for (int py = by0; py <= by1; py++)
            {
                float fy = py + 0.5f;
                for (int px = bx0; px <= bx1; px++)
                {
                    float fx = px + 0.5f;
                    float w0 = ((x1 - fx) * (y2 - fy) - (x2 - fx) * (y1 - fy)) * inv;
                    float w1 = ((x2 - fx) * (y0 - fy) - (x0 - fx) * (y2 - fy)) * inv;
                    float w2 = 1f - w0 - w1;
                    if (w0 < -1e-5f || w1 < -1e-5f || w2 < -1e-5f) continue;
                    float z = w0 * z0 + w1 * z1 + w2 * z2;
                    int pi = py * w + px;
                    if (z >= depth[pi]) continue;
                    depth[pi] = z;
                    float r = w0 * lr[0] + w1 * lr[1] + w2 * lr[2];
                    float g = w0 * lg[0] + w1 * lg[1] + w2 * lg[2];
                    float bb = w0 * lb[0] + w1 * lb[1] + w2 * lb[2];
                    int tr = 200, tg = 200, tb = 200;
                    if (tex != null)
                    {
                        float u = w0 * u0 + w1 * u1 + w2 * u2;
                        float vv = w0 * v0 + w1 * v1 + w2 * v2;
                        // texture rows are stored top-down (row 0 = v 0); bilinear, wrapping
                        float fu = u * 128f - 0.5f, fv = vv * 128f - 0.5f;
                        int tx0 = (int)MathF.Floor(fu), ty0 = (int)MathF.Floor(fv);
                        float ax = fu - tx0, ay = fv - ty0;
                        int c00 = tex[(ty0 & 127) * 128 + (tx0 & 127)], c10 = tex[(ty0 & 127) * 128 + ((tx0 + 1) & 127)];
                        int c01 = tex[((ty0 + 1) & 127) * 128 + (tx0 & 127)], c11 = tex[((ty0 + 1) & 127) * 128 + ((tx0 + 1) & 127)];
                        tr = Bilerp(c00 >> 16, c10 >> 16, c01 >> 16, c11 >> 16, ax, ay);
                        tg = Bilerp(c00 >> 8, c10 >> 8, c01 >> 8, c11 >> 8, ax, ay);
                        tb = Bilerp(c00, c10, c01, c11, ax, ay);
                    }
                    int R = Math.Clamp((int)(tr * r), 0, 255), G = Math.Clamp((int)(tg * g), 0, 255), B = Math.Clamp((int)(tb * bb), 0, 255);
                    color[pi] = unchecked((int)0xFF000000) | (R << 16) | (G << 8) | B;
                }
            }
        }
        // downsample with alpha
        var res = new int[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int sa = 0, sr = 0, sg = 0, sb = 0;
                for (int j = 0; j < ss; j++)
                    for (int i = 0; i < ss; i++)
                    {
                        int c = color[(y * ss + j) * w + x * ss + i];
                        int al = (c >> 24) & 0xFF;
                        if (al == 0) continue;
                        sa += al; sr += (c >> 16) & 0xFF; sg += (c >> 8) & 0xFF; sb += c & 0xFF;
                    }
                int n = ss * ss;
                int cnt = sa / 255;
                if (cnt == 0) continue;
                res[y * size + x] = ((sa / n) << 24) | ((sr / cnt) << 16) | ((sg / cnt) << 8) | (sb / cnt);
            }
        return res;
    }

    static int Bilerp(int a, int b, int c, int d, float ax, float ay)
    {
        a &= 0xFF; b &= 0xFF; c &= 0xFF; d &= 0xFF;
        float top = a + (b - a) * ax, bot = c + (d - c) * ax;
        return (int)(top + (bot - top) * ay + 0.5f);
    }

    /// <summary>Decodes the 16x16 4-bit PS1 icon frames (1-3) scaled to the requested size.</summary>
    public static IconAnimation? BuildPs1(Ps1Save s, int size)
    {
        var d = s.Data;
        if (d.Length < 0x200 || d[0] != 'S' || d[1] != 'C') return null;
        int flag = d[2];
        int frames = flag switch { 0x11 => 1, 0x12 => 2, 0x13 => 3, _ => 1 };
        var pal = new int[16];
        for (int i = 0; i < 16; i++)
        {
            int c = Bin.U16(d, 0x60 + i * 2);
            int r = (c & 0x1F) << 3, g = ((c >> 5) & 0x1F) << 3, b = ((c >> 10) & 0x1F) << 3;
            r |= r >> 5; g |= g >> 5; b |= b >> 5;
            pal[i] = c == 0 ? 0 : unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
        }
        var anim = new IconAnimation { Size = size, FrameSeconds = frames == 3 ? 0.18 : 0.27 };
        for (int f = 0; f < frames; f++)
        {
            int baseOff = 0x80 + f * 0x80;
            var img = new int[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int ix = x * 16 / size, iy = y * 16 / size;
                    int bi = baseOff + iy * 8 + ix / 2;
                    int nib = (ix & 1) == 0 ? d[bi] & 0x0F : d[bi] >> 4;
                    img[y * size + x] = pal[nib];
                }
            anim.Frames.Add(img);
        }
        return anim;
    }

    /// <summary>Builds the icon animation for any save (null if the save has no usable icon).</summary>
    public static IconAnimation? Build(SaveGame g, int size)
    {
        if (g.Ps1 != null) return BuildPs1(g.Ps1, size);
        var s = g.Ps2!;
        var sys = s.IconSys;
        if (sys == null) return null;
        var f = s.Find(sys.IconNormal) ?? s.Find(sys.IconCopy) ?? s.Find(sys.IconDelete)
                ?? s.Files.FirstOrDefault(x => x.Name.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) || x.Name.EndsWith(".icn", StringComparison.OrdinalIgnoreCase));
        if (f == null) return null;
        var m = Ps2IconModel.Parse(f.Data);
        if (m == null) return null;
        return BuildPs2(m, sys, size);
    }
}
