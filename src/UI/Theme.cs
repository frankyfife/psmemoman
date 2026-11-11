using System.Text.Json;
using MemcardManager.Core;

namespace MemcardManager.UI;

/// <summary>DPI scaling of hard coded pixel sizes (fonts scale by themselves).</summary>
public static class Dpi
{
    public static float F { get; set; } = 1f;
    public static int S(int v) => (int)Math.Round(v * F);
    public static float S(float v) => v * F;
    public static Size S(Size s) => new(S(s.Width), S(s.Height));
    public static Point S(Point p) => new(S(p.X), S(p.Y));
    public static Padding S(Padding p) => new(S(p.Left), S(p.Top), S(p.Right), S(p.Bottom));
}

public static class Theme
{
    public static readonly Color Back = Color.FromArgb(24, 26, 31);
    public static readonly Color Panel = Color.FromArgb(34, 37, 44);
    public static readonly Color Header = Color.FromArgb(43, 47, 56);
    public static readonly Color Row = Color.FromArgb(34, 37, 44);
    public static readonly Color RowAlt = Color.FromArgb(38, 41, 49);
    public static readonly Color RowHover = Color.FromArgb(48, 53, 64);
    public static readonly Color Selected = Color.FromArgb(44, 74, 130);
    public static readonly Color SelectedFocus = Color.FromArgb(52, 92, 165);
    public static readonly Color Border = Color.FromArgb(58, 63, 75);
    public static readonly Color Text = Color.FromArgb(232, 235, 241);
    public static readonly Color Dim = Color.FromArgb(150, 158, 172);
    public static readonly Color Accent = Color.FromArgb(76, 141, 255);
    public static readonly Color Warn = Color.FromArgb(230, 170, 50);
    public static readonly Color Danger = Color.FromArgb(225, 85, 85);
    public static readonly Color Ok = Color.FromArgb(70, 190, 115);

    public static readonly Font Normal = new("Segoe UI", 9f);
    public static readonly Font Bold = new("Segoe UI Semibold", 9.5f);
    public static readonly Font Title = new("Segoe UI Semibold", 11f);
    public static readonly Font Small = new("Segoe UI", 8f);
    public static readonly Font Big = new("Segoe UI Light", 16f);

    public static Color RegionColor(Region r) => r switch
    {
        Region.Europa => Color.FromArgb(60, 120, 220),
        Region.USA => Color.FromArgb(200, 70, 70),
        Region.Japan => Color.FromArgb(60, 160, 100),
        Region.Korea => Color.FromArgb(150, 90, 200),
        Region.Asien or Region.China => Color.FromArgb(200, 140, 40),
        _ => Color.FromArgb(100, 105, 115)
    };

    public static string RegionShort(Region r) => r switch
    {
        Region.Europa => "PAL",
        Region.USA => "NTSC-U",
        Region.Japan => "NTSC-J",
        Region.Korea => "KOR",
        Region.Asien => "ASIA",
        Region.China => "CHN",
        _ => "?"
    };

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:#,0.#} KB";
        return $"{bytes / (1024.0 * 1024):#,0.0#} MB";
    }

    sealed class Colors : ProfessionalColorTable
    {
        public override Color MenuStripGradientBegin => Header;
        public override Color MenuStripGradientEnd => Header;
        public override Color ToolStripGradientBegin => Header;
        public override Color ToolStripGradientMiddle => Header;
        public override Color ToolStripGradientEnd => Header;
        public override Color ToolStripBorder => Header;
        public override Color ToolStripDropDownBackground => Panel;
        public override Color ImageMarginGradientBegin => Panel;
        public override Color ImageMarginGradientMiddle => Panel;
        public override Color ImageMarginGradientEnd => Panel;
        public override Color MenuItemSelected => RowHover;
        public override Color MenuItemSelectedGradientBegin => RowHover;
        public override Color MenuItemSelectedGradientEnd => RowHover;
        public override Color MenuItemBorder => Border;
        public override Color MenuBorder => Border;
        public override Color MenuItemPressedGradientBegin => Panel;
        public override Color MenuItemPressedGradientEnd => Panel;
        public override Color ButtonSelectedHighlight => RowHover;
        public override Color ButtonSelectedGradientBegin => RowHover;
        public override Color ButtonSelectedGradientEnd => RowHover;
        public override Color ButtonPressedGradientBegin => Selected;
        public override Color ButtonPressedGradientEnd => Selected;
        public override Color ButtonSelectedBorder => Border;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
        public override Color StatusStripGradientBegin => Header;
        public override Color StatusStripGradientEnd => Header;
        public override Color CheckBackground => Selected;
        public override Color CheckSelectedBackground => SelectedFocus;
    }

    public sealed class Renderer : ToolStripProfessionalRenderer
    {
        public Renderer() : base(new Colors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Text : Color.FromArgb(95, 100, 112);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Text;
            base.OnRenderArrow(e);
        }
    }

    public static readonly Renderer ToolRenderer = new();

    public static void StyleMenu(ToolStripDropDown dd)
    {
        dd.Renderer = ToolRenderer;
        dd.BackColor = Panel;
        dd.ForeColor = Text;
    }
}

public sealed class AppSettings
{
    public List<string> OpenCards { get; set; } = new();
    public List<string> RecentCards { get; set; } = new();
    public bool AutoSave { get; set; }
    public bool AnimateIcons { get; set; } = true;
    public bool ReopenCards { get; set; } = true;
    public string Language { get; set; } = "";
    public string LastExportDir { get; set; } = "";
    public string LastImportDir { get; set; } = "";
    public string LastCardDir { get; set; } = "";
    public Dictionary<string, int> LastExportFormat { get; set; } = new();
    public int WindowX { get; set; } = -1;
    public int WindowY { get; set; } = -1;
    public int WindowW { get; set; }
    public int WindowH { get; set; }
    public bool Maximized { get; set; }

    static string PathFile => Path.Combine(AppContext.BaseDirectory, "MemcardManager.settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(PathFile)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(PathFile)) ?? new();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try { File.WriteAllText(PathFile, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
        catch { }
    }

    public void AddRecent(string path)
    {
        RecentCards.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentCards.Insert(0, path);
        if (RecentCards.Count > 12) RecentCards.RemoveRange(12, RecentCards.Count - 12);
    }
}
