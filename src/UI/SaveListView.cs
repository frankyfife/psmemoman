using System.Diagnostics;
using System.Drawing.Drawing2D;
using MemcardManager.Core;

namespace MemcardManager.UI;

/// <summary>Owner drawn, multi-select list of saves with animated icons.</summary>
public sealed class SaveListView : Control
{
    static int IconSize => Dpi.S(64);
    public static int RowHeight => Dpi.S(80);

    readonly VScrollBar _scroll = new() { Dock = DockStyle.Right, Width = Dpi.S(14) };
    List<CardEntry> _items = new();
    readonly HashSet<int> _selected = new();
    int _focus = -1, _anchor = -1, _hover = -1;
    Point _mouseDown;
    bool _dragArmed;
    static readonly Stopwatch Clock = Stopwatch.StartNew();

    public event EventHandler? SelectionChanged;
    public event EventHandler<CardEntry>? ItemActivated;
    public event EventHandler<List<CardEntry>>? DragStarting;
    public event EventHandler? DeletePressed;

    public string EmptyText { get; set; } = L.T("Keine Savegames auf dieser Karte.\nSaves hierher ziehen zum Importieren.", "No saves on this card.\nDrag saves here to import.");

    public SaveListView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Theme.Row;
        ForeColor = Theme.Text;
        Controls.Add(_scroll);
        _scroll.ValueChanged += (_, _) => Invalidate();
        IconCache.IconReady += OnIconReady;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) IconCache.IconReady -= OnIconReady;
        base.Dispose(disposing);
    }

    void OnIconReady()
    {
        if (IsHandleCreated && !IsDisposed) BeginInvoke(Invalidate);
    }

    public IReadOnlyList<CardEntry> Items => _items;

    public void SetItems(IEnumerable<CardEntry> items)
    {
        var keep = SelectedItems.Select(e => e.Name).ToHashSet();
        _items = items.ToList();
        _selected.Clear();
        for (int i = 0; i < _items.Count; i++) if (keep.Contains(_items[i].Name)) _selected.Add(i);
        _focus = Math.Min(_focus, _items.Count - 1);
        _hover = -1;
        UpdateScroll();
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public List<CardEntry> SelectedItems => _selected.OrderBy(i => i).Where(i => i < _items.Count).Select(i => _items[i]).ToList();

    public void SelectByNames(IEnumerable<string> names)
    {
        var set = names.ToHashSet();
        _selected.Clear();
        for (int i = 0; i < _items.Count; i++) if (set.Contains(_items[i].Name)) { _selected.Add(i); _focus = i; }
        if (_focus >= 0) EnsureVisible(_focus);
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SelectAll()
    {
        for (int i = 0; i < _items.Count; i++) _selected.Add(i);
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Called by the global animation timer.</summary>
    public void Tick()
    {
        if (!Visible || _items.Count == 0) return;
        int first = _scroll.Value / RowHeight, last = Math.Min(_items.Count - 1, (_scroll.Value + Height) / RowHeight);
        for (int i = first; i <= last; i++)
        {
            var ic = IconCache.Get(_items[i].Save, IconSize, out _);
            if (ic != null && ic.Animated)
            {
                Invalidate(new Rectangle(0, i * RowHeight - _scroll.Value, Dpi.S(16) + IconSize, RowHeight));
            }
        }
    }

    public static bool Animate { get; set; } = true;

    void UpdateScroll()
    {
        int total = _items.Count * RowHeight;
        int view = Math.Max(1, ClientSize.Height);
        _scroll.Visible = total > view;
        _scroll.Minimum = 0;
        _scroll.Maximum = Math.Max(0, total - 1);
        _scroll.LargeChange = view;
        _scroll.SmallChange = RowHeight / 2;
        int max = Math.Max(0, total - view);
        if (_scroll.Value > max) _scroll.Value = max;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateScroll();
    }

    int ContentWidth => ClientSize.Width - (_scroll.Visible ? _scroll.Width : 0);

    int HitTest(Point p)
    {
        if (p.X >= ContentWidth) return -1;
        int i = (p.Y + _scroll.Value) / RowHeight;
        return i >= 0 && i < _items.Count ? i : -1;
    }

    void EnsureVisible(int i)
    {
        if (i < 0) return;
        int top = i * RowHeight, bottom = top + RowHeight;
        int max = Math.Max(0, _items.Count * RowHeight - ClientSize.Height);
        if (top < _scroll.Value) _scroll.Value = Math.Clamp(top, 0, max);
        else if (bottom > _scroll.Value + ClientSize.Height) _scroll.Value = Math.Clamp(bottom - ClientSize.Height, 0, max);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Row);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        if (_items.Count == 0)
        {
            TextRenderer.DrawText(g, EmptyText, Theme.Normal, new Rectangle(0, 0, ContentWidth, ClientSize.Height), Theme.Dim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            return;
        }
        int w = ContentWidth;
        double t = Animate ? Clock.Elapsed.TotalSeconds : 0;
        int first = _scroll.Value / RowHeight, last = Math.Min(_items.Count - 1, (_scroll.Value + ClientSize.Height) / RowHeight);
        for (int i = first; i <= last; i++)
        {
            var r = new Rectangle(0, i * RowHeight - _scroll.Value, w, RowHeight);
            if (!r.IntersectsWith(e.ClipRectangle)) continue;
            DrawRow(g, _items[i], r, i, t);
        }
    }

    void DrawRow(Graphics g, CardEntry it, Rectangle r, int index, double t)
    {
        bool sel = _selected.Contains(index);
        Color bg = sel ? (Focused ? Theme.SelectedFocus : Theme.Selected) : index == _hover ? Theme.RowHover : index % 2 == 0 ? Theme.Row : Theme.RowAlt;
        using (var b = new SolidBrush(bg)) g.FillRectangle(b, r);
        if (index == _focus && Focused)
        {
            using var p = new Pen(Theme.Accent);
            g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
        }

        int isz = IconSize;
        var iconRect = new Rectangle(r.X + Dpi.S(8), r.Y + (RowHeight - isz) / 2, isz, isz);
        var ic = IconCache.Get(it.Save, isz, out bool missing);
        g.InterpolationMode = it.Platform == Platform.PS1 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
        if (ic != null)
            g.DrawImage(ic.FrameAt(t), iconRect);
        else
        {
            var state = g.Save();
            float p6 = Dpi.S(6f);
            using (var b = new SolidBrush(Color.FromArgb(28, 255, 255, 255)))
            using (var path = CardArt.RoundedRect(new RectangleF(iconRect.X + p6, iconRect.Y + p6, isz - 2 * p6, isz - 2 * p6), Dpi.S(8f)))
                g.FillPath(b, path);
            if (missing)
                CardArt.DrawCard(g, new RectangleF(iconRect.X + isz * 0.22f, iconRect.Y + isz * 0.19f, isz * 0.56f, isz * 0.62f), it.Platform == Platform.PS2, Color.FromArgb(110, 115, 125));
            g.Restore(state);
        }

        int x = iconRect.Right + Dpi.S(12);
        int badgeW = Dpi.S(62);
        int textW = Math.Max(Dpi.S(40), r.Right - x - badgeW - Dpi.S(14));
        string l1, l2 = "";
        if (it.Save.Ps2?.IconSys is { } sys)
        {
            (l1, l2) = sys.Title();
            l1 = l1.Trim(); l2 = l2.Trim();
            if (l1.Length == 0) { l1 = l2; l2 = ""; }
            if (l1.Length == 0) l1 = it.Name;
        }
        else l1 = it.Title;

        var flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        int hb = Theme.Bold.Height, hn = Theme.Normal.Height, hs = Theme.Small.Height;
        int total = hb + (l2.Length > 0 ? hn : 0) + 2 * hs;
        int y = r.Y + Math.Max(Dpi.S(3), (RowHeight - total) / 2);
        TextRenderer.DrawText(g, l1, Theme.Bold, new Rectangle(x, y, textW, hb), Theme.Text, flags);
        y += hb;
        if (l2.Length > 0)
        {
            TextRenderer.DrawText(g, l2, Theme.Normal, new Rectangle(x, y, textW, hn), Theme.Text, flags);
            y += hn;
        }
        TextRenderer.DrawText(g, it.Name, Theme.Small, new Rectangle(x, y, textW, hs), sel ? Theme.Text : Theme.Dim, flags);
        y += hs;
        string info = it.Platform == Platform.PS1
            ? L.T($"{it.Blocks} Block{(it.Blocks == 1 ? "" : "e")} · {Theme.FormatSize(it.Size)}", $"{it.Blocks} block{(it.Blocks == 1 ? "" : "s")} · {Theme.FormatSize(it.Size)}")
            : Theme.FormatSize(it.Size) + (it.Modified is { } m ? " · " + m.ToString("dd.MM.yyyy HH:mm") : "");
        Color infoColor = sel ? Theme.Text : Theme.Dim;
        if (it.Damaged)
        {
            info = L.T("✖ BESCHÄDIGT: ", "✖ DAMAGED: ") + it.Problem;
            infoColor = Color.FromArgb(255, 120, 120);
            using var red = new SolidBrush(Theme.Danger);
            g.FillRectangle(red, r.X, r.Y, Dpi.S(4), r.Height);
        }
        else if (it.Problem != null)
        {
            info = "⚠ " + it.Problem + " · " + info;
            infoColor = Theme.Warn;
        }
        TextRenderer.DrawText(g, info, Theme.Small, new Rectangle(x, y, textW, hs), infoColor, flags);

        // region badge
        var reg = it.Region;
        var br = new Rectangle(r.Right - badgeW - Dpi.S(8), r.Y + Dpi.S(10), badgeW, hs + Dpi.S(6));
        using (var bb = new SolidBrush(Theme.RegionColor(reg)))
        using (var path = CardArt.RoundedRect(br, br.Height / 2f - 0.5f))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillPath(bb, path);
            g.SmoothingMode = SmoothingMode.None;
        }
        TextRenderer.DrawText(g, Theme.RegionShort(reg), Theme.Small, br, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        using var sep = new Pen(Color.FromArgb(20, 255, 255, 255));
        g.DrawLine(sep, r.X, r.Bottom - 1, r.Right, r.Bottom - 1);
    }

    // -------------------------------------------------------------- input

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        int i = HitTest(e.Location);
        if (e.Button == MouseButtons.Left)
        {
            if (i < 0)
            {
                if ((ModifierKeys & (Keys.Control | Keys.Shift)) == 0) { _selected.Clear(); SelectionChanged?.Invoke(this, EventArgs.Empty); Invalidate(); }
                return;
            }
            if ((ModifierKeys & Keys.Control) != 0)
            {
                if (!_selected.Remove(i)) _selected.Add(i);
                _anchor = i;
            }
            else if ((ModifierKeys & Keys.Shift) != 0 && _anchor >= 0)
            {
                _selected.Clear();
                for (int k = Math.Min(_anchor, i); k <= Math.Max(_anchor, i); k++) _selected.Add(k);
            }
            else if (!_selected.Contains(i))
            {
                _selected.Clear();
                _selected.Add(i);
                _anchor = i;
            }
            _focus = i;
            _mouseDown = e.Location;
            _dragArmed = _selected.Contains(i);
            Invalidate();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (e.Button == MouseButtons.Right && i >= 0 && !_selected.Contains(i))
        {
            _selected.Clear();
            _selected.Add(i);
            _focus = _anchor = i;
            Invalidate();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && _dragArmed && (ModifierKeys & (Keys.Control | Keys.Shift)) == 0)
        {
            // plain click on an already selected item in a multi selection → select only this one
            int i = HitTest(e.Location);
            if (i >= 0 && _selected.Count > 1 && Math.Abs(e.X - _mouseDown.X) < 4 && Math.Abs(e.Y - _mouseDown.Y) < 4)
            {
                _selected.Clear();
                _selected.Add(i);
                _anchor = i;
                Invalidate();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        _dragArmed = false;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int h = HitTest(e.Location);
        if (h != _hover) { _hover = h; Invalidate(); }
        if (_dragArmed && e.Button == MouseButtons.Left)
        {
            var ds = SystemInformation.DragSize;
            if (Math.Abs(e.X - _mouseDown.X) > ds.Width || Math.Abs(e.Y - _mouseDown.Y) > ds.Height)
            {
                _dragArmed = false;
                var items = SelectedItems;
                if (items.Count > 0) DragStarting?.Invoke(this, items);
            }
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover != -1) { _hover = -1; Invalidate(); }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        int i = HitTest(e.Location);
        if (i >= 0) ItemActivated?.Invoke(this, _items[i]);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!_scroll.Visible) return;
        int max = Math.Max(0, _items.Count * RowHeight - ClientSize.Height);
        _scroll.Value = Math.Clamp(_scroll.Value - e.Delta / 120 * RowHeight, 0, max);
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_items.Count == 0) return;
        int pageRows = Math.Max(1, ClientSize.Height / RowHeight);
        int nf = _focus;
        switch (e.KeyCode)
        {
            case Keys.Up: nf = Math.Max(0, _focus - 1); break;
            case Keys.Down: nf = Math.Min(_items.Count - 1, _focus + 1); break;
            case Keys.Home: nf = 0; break;
            case Keys.End: nf = _items.Count - 1; break;
            case Keys.PageUp: nf = Math.Max(0, _focus - pageRows); break;
            case Keys.PageDown: nf = Math.Min(_items.Count - 1, _focus + pageRows); break;
            case Keys.A when e.Control: SelectAll(); return;
            case Keys.Delete: DeletePressed?.Invoke(this, EventArgs.Empty); return;
            case Keys.Enter: if (_focus >= 0) ItemActivated?.Invoke(this, _items[_focus]); return;
            default: return;
        }
        if (nf < 0) nf = 0;
        if (e.Shift && _anchor >= 0)
        {
            _selected.Clear();
            for (int k = Math.Min(_anchor, nf); k <= Math.Max(_anchor, nf); k++) _selected.Add(k);
        }
        else
        {
            _selected.Clear();
            _selected.Add(nf);
            _anchor = nf;
        }
        _focus = nf;
        EnsureVisible(nf);
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }
}
