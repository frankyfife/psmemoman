using System.Collections.Concurrent;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using MemcardManager.Core;

namespace MemcardManager.UI;

/// <summary>Rendered icon frames as bitmaps.</summary>
public sealed class IconFrames
{
    public Bitmap[] Frames = Array.Empty<Bitmap>();
    public double FrameSeconds = 0.1;
    public bool Animated => Frames.Length > 1;

    public Bitmap FrameAt(double seconds) =>
        Frames.Length == 1 ? Frames[0] : Frames[(int)(seconds / FrameSeconds) % Frames.Length];
}

/// <summary>Renders save icons in the background and caches them by content.</summary>
public static class IconCache
{
    static readonly ConcurrentDictionary<string, IconFrames?> Cache = new();
    static readonly ConcurrentDictionary<string, bool> Pending = new();
    static readonly SemaphoreSlim Gate = new(Math.Max(1, Environment.ProcessorCount - 1));

    public static event Action? IconReady;

    static string Key(SaveGame g, int size)
    {
        uint crc;
        if (g.Ps1 != null) crc = Crc32.Compute(g.Ps1.Data.AsSpan(0, Math.Min(0x200, g.Ps1.Data.Length)));
        else
        {
            var s = g.Ps2!;
            var sys = s.Find("icon.sys");
            crc = sys != null ? Crc32.Compute(sys.Data) : 0;
            var ic = s.IconSys;
            var f = ic != null ? s.Find(ic.IconNormal) : null;
            if (f != null) crc = Crc32.Compute(f.Data, crc);
        }
        return $"{size}|{g.Platform}|{g.Name}|{crc:X8}";
    }

    /// <summary>Returns the icon if ready; otherwise schedules rendering and returns null.</summary>
    public static IconFrames? Get(SaveGame g, int size, out bool missing)
    {
        string key = Key(g, size);
        if (Cache.TryGetValue(key, out var f)) { missing = f == null; return f; }
        missing = false;
        if (Pending.TryAdd(key, true))
        {
            Task.Run(async () =>
            {
                await Gate.WaitAsync();
                try
                {
                    IconAnimation? anim = null;
                    try { anim = IconRenderer.Build(g, size); } catch { }
                    Cache[key] = anim == null ? null : ToFrames(anim);
                }
                finally
                {
                    Gate.Release();
                    Pending.TryRemove(key, out _);
                    IconReady?.Invoke();
                }
            });
        }
        return null;
    }

    /// <summary>Synchronous variant (used by the properties dialog for large icons).</summary>
    public static IconFrames? GetNow(SaveGame g, int size)
    {
        string key = Key(g, size);
        if (Cache.TryGetValue(key, out var f)) return f;
        IconAnimation? anim = null;
        try { anim = IconRenderer.Build(g, size); } catch { }
        var res = anim == null ? null : ToFrames(anim);
        Cache[key] = res;
        return res;
    }

    static IconFrames ToFrames(IconAnimation a)
    {
        var res = new IconFrames { FrameSeconds = a.FrameSeconds, Frames = new Bitmap[a.Frames.Count] };
        for (int i = 0; i < a.Frames.Count; i++)
        {
            var bmp = new Bitmap(a.Size, a.Size, PixelFormat.Format32bppArgb);
            var bd = bmp.LockBits(new Rectangle(0, 0, a.Size, a.Size), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                var src = a.Frames[i];
                for (int y = 0; y < a.Size; y++) Marshal.Copy(src, y * a.Size, bd.Scan0 + y * bd.Stride, a.Size);
            }
            finally { bmp.UnlockBits(bd); }
            res.Frames[i] = bmp;
        }
        return res;
    }
}
