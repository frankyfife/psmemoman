using System.Diagnostics;
using System.Drawing.Drawing2D;
using MemcardManager.Core;

namespace MemcardManager.UI;

public interface ICardHost
{
    AppSettings Settings { get; }
    IReadOnlyList<CardPanel> Panels { get; }
    void SetStatus(string text, bool error = false);
    void ClosePanel(CardPanel panel);
    void OpenCards(IEnumerable<string> paths);
    void Transfer(CardPanel source, CardPanel target, List<CardEntry> entries, bool move);
    void AfterChange(CardPanel panel);
}

/// <summary>Internal drag &amp; drop payload (saves dragged from a card panel).</summary>
public sealed class DragPayload
{
    public const string Format = "MemcardManager.DragPayload";
    public required CardPanel Source { get; init; }
    public required List<CardEntry> Entries { get; init; }
}

/// <summary>One memory card segment of the main window.</summary>
public sealed class CardPanel : Panel
{
    readonly ICardHost _host;
    readonly Panel _header = new() { Dock = DockStyle.Top, Height = Math.Max(Dpi.S(62), Dpi.S(18) + Theme.Title.Height + Theme.Small.Height) };
    readonly ToolStrip _tools = new() { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden, Padding = Dpi.S(new Padding(6, 1, 6, 1)), AutoSize = true };
    readonly Panel _usage = new() { Dock = DockStyle.Top, Height = Theme.Small.Height + Dpi.S(12) };
    readonly Panel _banner = new() { Dock = DockStyle.Top, Height = Theme.Normal.Height + Dpi.S(20), Visible = false };
    readonly Button _close = new() { Text = "✕", FlatStyle = FlatStyle.Flat, Size = Dpi.S(new Size(30, 28)), Anchor = AnchorStyles.Top | AnchorStyles.Right, TabStop = false };
    readonly ToolTip _tip = new();
    readonly ToolStripButton _btnImport, _btnExport, _btnDelete, _btnSave;
    readonly ToolStripDropDownButton _btnMore;
    readonly ContextMenuStrip _ctx = new();
    bool _dropHighlight;
    Rectangle _warnRect;

    public SaveListView List { get; } = new() { Dock = DockStyle.Fill };
    public MemoryCard Card { get; private set; }
    public int Slot { get; set; }

    public CardPanel(ICardHost host, MemoryCard card)
    {
        _host = host;
        Card = card;
        DoubleBuffered = true;
        BackColor = Theme.Panel;
        Padding = new Padding(2);

        // header
        _header.BackColor = Theme.Header;
        _header.Paint += PaintHeader;
        _header.MouseClick += (_, e) =>
        {
            if (_warnRect.Contains(e.Location)) CheckCard();
        };
        _close.FlatAppearance.BorderSize = 0;
        _close.ForeColor = Theme.Dim;
        _close.BackColor = Theme.Header;
        _close.Click += (_, _) => _host.ClosePanel(this);
        _tip.SetToolTip(_close, L.T("Karte schließen (Segment ausblenden)", "Close card (hide segment)"));
        _header.Controls.Add(_close);
        _header.Resize += (_, _) => _close.Location = new Point(_header.Width - _close.Width - Dpi.S(6), Dpi.S(6));

        // toolbar
        _tools.Renderer = Theme.ToolRenderer;
        _tools.BackColor = Theme.Header;
        _btnImport = new ToolStripButton(L.T("⭳ Importieren", "⭳ Import"), null, (_, _) => ImportDialog()) { ToolTipText = L.T("Savegames aus Dateien importieren (PSU, MAX, CBS, SPS, XPS, PSV, MCS, PSX, ZIP, Karten …)", "Import saves from files (PSU, MAX, CBS, SPS, XPS, PSV, MCS, PSX, ZIP, cards …)") };
        _btnExport = new ToolStripButton(L.T("⭱ Exportieren", "⭱ Export"), null, (_, _) => ExportSelected()) { ToolTipText = L.T("Ausgewählte Savegames exportieren", "Export selected saves") };
        _btnDelete = new ToolStripButton(L.T("🗑 Löschen", "🗑 Delete"), null, (_, _) => DeleteSelected()) { ToolTipText = L.T("Ausgewählte Savegames löschen (Entf)", "Delete selected saves (Del)") };
        _btnSave = new ToolStripButton(L.T("💾 Speichern", "💾 Save"), null, (_, _) => Save()) { ToolTipText = L.T("Änderungen in die Kartendatei schreiben (mit automatischem Backup)", "Write changes to the card file (with automatic backup)") };
        _btnMore = new ToolStripDropDownButton("⋯") { ToolTipText = L.T("Weitere Aktionen", "More actions"), ShowDropDownArrow = false };
        _btnMore.DropDownItems.Add(L.T("Speichern unter / konvertieren …", "Save as / convert …"), null, (_, _) => SaveAs());
        _btnMore.DropDownItems.Add(L.T("Änderungen verwerfen (neu laden)", "Discard changes (reload)"), null, (_, _) => Revert());
        _btnMore.DropDownItems.Add(new ToolStripSeparator());
        _btnMore.DropDownItems.Add(L.T("Ordner importieren …", "Import folder …"), null, (_, _) => ImportFolderDialog());
        _btnMore.DropDownItems.Add(L.T("Rettungsimport (beschädigte Dateien, ohne Gewähr) …", "Rescue import (damaged files, no guarantee) …"), null, (_, _) => RescueImportDialog());
        _btnMore.DropDownItems.Add(L.T("Alle auswählen", "Select all"), null, (_, _) => List.SelectAll());
        _btnMore.DropDownItems.Add(L.T("Alle Savegames exportieren …", "Export all saves …"), null, (_, _) => { List.SelectAll(); ExportSelected(); });
        _btnMore.DropDownItems.Add(new ToolStripSeparator());
        _btnMore.DropDownItems.Add(L.T("Karte prüfen / reparieren …", "Check / repair card …"), null, (_, _) => CheckCard());
        _btnMore.DropDownItems.Add(L.T("Gelöschte Savegames wiederherstellen …", "Restore deleted saves …"), null, (_, _) => UndeleteSaves());
        _btnMore.DropDownItems.Add(L.T("Backup zurückspielen …", "Restore backup …"), null, (_, _) => RestoreBackup());
        _btnMore.DropDownItems.Add(L.T("Im Explorer zeigen", "Show in Explorer"), null, (_, _) => ShowInExplorer());
        _btnMore.DropDownItems.Add(new ToolStripSeparator());
        _btnMore.DropDownItems.Add(L.T("Karte schließen", "Close card"), null, (_, _) => _host.ClosePanel(this));
        Theme.StyleMenu(_btnMore.DropDown);
        _tools.Items.AddRange(new ToolStripItem[] { _btnImport, _btnExport, _btnDelete, new ToolStripSeparator(), _btnSave, _btnMore });

        // usage bar
        _usage.BackColor = Theme.Panel;
        _usage.Paint += PaintUsage;

        // unsaved banner
        _banner.BackColor = Color.FromArgb(70, 58, 25);
        var bannerText = new Label
        {
            Text = L.T("● Ungespeicherte Änderungen – die Kartendatei ist noch unverändert.", "● Unsaved changes – the card file is still unchanged."), AutoSize = false, Dock = DockStyle.Fill,
            ForeColor = Theme.Warn, TextAlign = ContentAlignment.MiddleLeft, Padding = Dpi.S(new Padding(8, 0, 0, 0))
        };
        var bSave = BannerButton(L.T("Speichern", "Save"), (_, _) => Save());
        var bRevert = BannerButton(L.T("Verwerfen", "Discard"), (_, _) => Revert());
        var flow = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Padding = Dpi.S(new Padding(0, 3, 6, 0)), WrapContents = false };
        flow.Controls.Add(bSave);
        flow.Controls.Add(bRevert);
        _banner.Controls.Add(bannerText);
        _banner.Controls.Add(flow);

        // list
        List.SelectionChanged += (_, _) => UpdateButtons();
        List.ItemActivated += (_, e) => ShowProperties(e);
        List.DeletePressed += (_, _) => DeleteSelected();
        List.DragStarting += (_, items) => StartDrag(items);
        List.ContextMenuStrip = _ctx;
        _ctx.Opening += (_, e) => BuildContextMenu(e);
        Theme.StyleMenu(_ctx);

        Controls.Add(List);
        Controls.Add(_banner);
        Controls.Add(_usage);
        Controls.Add(_tools);
        Controls.Add(_header);

        foreach (Control c in new Control[] { this, _header, _tools, _usage, _banner, List, bannerText }) WireDrop(c);
        RefreshView();
    }

    static Button BannerButton(string text, EventHandler click)
    {
        var b = new Button
        {
            Text = text, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(95, 80, 35), ForeColor = Theme.Text, AutoSize = true,
            Height = Theme.Normal.Height + Dpi.S(12), Margin = Dpi.S(new Padding(4, 0, 0, 0)), Padding = Dpi.S(new Padding(6, 0, 6, 0))
        };
        b.FlatAppearance.BorderColor = Theme.Warn;
        b.Click += click;
        return b;
    }

    // ================================================================== painting

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var p = new Pen(_dropHighlight ? Theme.Accent : Theme.Border, _dropHighlight ? 2 : 1);
        var r = ClientRectangle;
        r.Width -= 1; r.Height -= 1;
        if (_dropHighlight) r.Inflate(-1, -1);
        e.Graphics.DrawRectangle(p, r);
    }

    void PaintHeader(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        int h = _header.Height;
        CardArt.DrawCard(g, new RectangleF(Dpi.S(10), h * 0.12f, h * 0.70f, h * 0.76f), Card.Platform == Platform.PS2);
        g.SmoothingMode = SmoothingMode.None;
        int x = Dpi.S(12) + (int)(h * 0.70f);
        int right = _header.Width - _close.Width - Dpi.S(10);
        int th = Theme.Title.Height, sh = Theme.Small.Height;
        int y = (h - th - sh - Dpi.S(2)) / 2;
        string title = $"Slot {Slot}  ·  {Card.DisplayName}" + (Card.IsModified ? "  *" : "");
        TextRenderer.DrawText(g, title, Theme.Title, new Rectangle(x, y, right - x, th), Theme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        // health badge (click → check / repair dialog)
        int errors = _issues.Count(i => i.Level == IssueLevel.Error), warns = _issues.Count(i => i.Level == IssueLevel.Warning);
        var (wt, wc) = errors > 0 ? (L.T($"✖ {errors} Fehler", $"✖ {errors} error{(errors == 1 ? "" : "s")}"), Theme.Danger)
                     : warns > 0 ? (L.T($"⚠ {warns} Warnung{(warns == 1 ? "" : "en")}", $"⚠ {warns} warning{(warns == 1 ? "" : "s")}"), Theme.Warn)
                     : ("✔ OK", Theme.Ok);
        int warnW = TextRenderer.MeasureText(wt, Theme.Small).Width + Dpi.S(14);
        _warnRect = new Rectangle(right - warnW, y + th + Dpi.S(2), warnW, sh);
        TextRenderer.DrawText(g, wt, Theme.Small, _warnRect, wc, TextFormatFlags.HorizontalCenter);
        string sub = Card.FormatName + (Card.CanWrite ? "" : L.T("  ·  NUR LESEN", "  ·  READ ONLY"));
        TextRenderer.DrawText(g, sub, Theme.Small, new Rectangle(x, y + th + Dpi.S(2), right - x - warnW, sh),
            Card.CanWrite ? Theme.Dim : Theme.Warn, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    void PaintUsage(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        long total = Math.Max(1, Card.TotalBytes), free = Card.FreeBytes, used = total - free;
        string txt = Card.Platform == Platform.PS1
            ? L.T($"{free / Ps1Card.BlockSize} von 15 Blöcken frei · {Card.Entries.Count} Saves", $"{free / Ps1Card.BlockSize} of 15 blocks free · {Card.Entries.Count} save{(Card.Entries.Count == 1 ? "" : "s")}")
            : L.T($"{free / 1024:#,0} KB frei von {total / 1024:#,0} KB · {Card.Entries.Count} Saves", $"{free / 1024:#,0} KB free of {total / 1024:#,0} KB · {Card.Entries.Count} save{(Card.Entries.Count == 1 ? "" : "s")}");
        int tw = TextRenderer.MeasureText(txt, Theme.Small).Width;
        int barW = Math.Max(Dpi.S(40), _usage.Width - tw - Dpi.S(30));
        var bar = new Rectangle(Dpi.S(10), (_usage.Height - Dpi.S(10)) / 2, barW, Dpi.S(10));
        using (var b = new SolidBrush(Theme.Back)) g.FillRectangle(b, bar);
        double frac = Math.Clamp((double)used / total, 0, 1);
        var fill = new Rectangle(bar.X, bar.Y, (int)(bar.Width * frac), bar.Height);
        using (var b = new SolidBrush(frac > 0.92 ? Theme.Danger : frac > 0.75 ? Theme.Warn : Theme.Accent)) g.FillRectangle(b, fill);
        TextRenderer.DrawText(g, txt, Theme.Small, new Rectangle(bar.Right + Dpi.S(8), 0, _usage.Width - bar.Right - Dpi.S(10), _usage.Height), Theme.Dim,
            TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
    }

    List<CardIssue> _issues = new();

    public IReadOnlyList<CardIssue> Issues => _issues;

    public void RefreshView()
    {
        try { _issues = Card.Diagnose(); }
        catch (Exception ex) { _issues = new List<CardIssue> { new(IssueLevel.Error, L.T("Prüfung fehlgeschlagen: ", "Check failed: ") + ex.Message) }; }
        List.SetItems(Card.Entries.OrderBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(e => e.Name));
        List.EmptyText = Card.CanWrite
            ? L.T("Keine Savegames auf dieser Karte.\n\nSavegames oder Karten hierher ziehen\noder über „Importieren“ hinzufügen.", "No saves on this card.\n\nDrag saves or cards here\nor add them via \"Import\".")
            : L.T("Keine Savegames auf dieser Karte.", "No saves on this card.");
        _banner.Visible = Card.IsModified;
        _header.Invalidate();
        _usage.Invalidate();
        _tip.SetToolTip(_header, Card.FilePath);
        UpdateButtons();
    }

    void UpdateButtons()
    {
        int sel = List.SelectedItems.Count;
        _btnImport.Enabled = Card.CanWrite;
        _btnExport.Enabled = sel > 0;
        _btnDelete.Enabled = sel > 0 && Card.CanWrite;
        _btnSave.Enabled = Card.IsModified && Card.CanWrite;
        _btnSave.ForeColor = Card.IsModified ? Theme.Warn : Theme.Text;
    }

    void AfterChange(IEnumerable<string>? select = null)
    {
        RefreshView();
        if (select != null) List.SelectByNames(select);
        _host.AfterChange(this);
    }

    // ================================================================== context menu

    void BuildContextMenu(System.ComponentModel.CancelEventArgs e)
    {
        _ctx.Items.Clear();
        var sel = List.SelectedItems;
        if (sel.Count == 0)
        {
            _ctx.Items.Add(L.T("Importieren …", "Import …"), null, (_, _) => ImportDialog()).Enabled = Card.CanWrite;
            _ctx.Items.Add(L.T("Alle auswählen", "Select all"), null, (_, _) => List.SelectAll());
            return;
        }
        if (sel.Count == 1) _ctx.Items.Add(L.T("Eigenschaften …", "Properties …"), null, (_, _) => ShowProperties(sel[0])).Font = new Font(Theme.Normal, FontStyle.Bold);
        _ctx.Items.Add(L.T("Exportieren …", "Export …"), null, (_, _) => ExportSelected());
        var quick = new ToolStripMenuItem(L.T("Schnell exportieren als", "Quick export as"));
        foreach (var f in SaveFormats.FormatsFor(Card.Platform))
        {
            var fmt = f;
            quick.DropDownItems.Add(SaveFormats.Describe(fmt), null, (_, _) => QuickExport(fmt));
        }
        Theme.StyleMenu(quick.DropDown);
        _ctx.Items.Add(quick);
        var others = _host.Panels.Where(p => p != this && p.Card.Platform == Card.Platform).ToList();
        if (others.Count > 0)
        {
            var copy = new ToolStripMenuItem(L.T("Kopieren nach", "Copy to"));
            var move = new ToolStripMenuItem(L.T("Verschieben nach", "Move to"));
            foreach (var o in others)
            {
                var target = o;
                copy.DropDownItems.Add($"Slot {o.Slot}: {o.Card.DisplayName}", null, (_, _) => _host.Transfer(this, target, List.SelectedItems, false)).Enabled = o.Card.CanWrite;
                move.DropDownItems.Add($"Slot {o.Slot}: {o.Card.DisplayName}", null, (_, _) => _host.Transfer(this, target, List.SelectedItems, true)).Enabled = o.Card.CanWrite && Card.CanWrite;
            }
            Theme.StyleMenu(copy.DropDown);
            Theme.StyleMenu(move.DropDown);
            _ctx.Items.Add(copy);
            _ctx.Items.Add(move);
        }
        _ctx.Items.Add(new ToolStripSeparator());
        _ctx.Items.Add(L.T("Umbenennen / Region ändern …", "Rename / change region …"), null, (_, _) => RenameEntry(sel[0])).Enabled = sel.Count == 1 && Card.CanWrite;
        _ctx.Items.Add(L.T("Löschen", "Delete"), null, (_, _) => DeleteSelected()).Enabled = Card.CanWrite;
    }

    // ================================================================== drag & drop

    void WireDrop(Control c)
    {
        c.AllowDrop = true;
        c.DragEnter += OnDragOver;
        c.DragOver += OnDragOver;
        c.DragLeave += (_, _) => SetDropHighlight(false);
        c.DragDrop += OnDragDrop;
    }

    void SetDropHighlight(bool on)
    {
        if (_dropHighlight == on) return;
        _dropHighlight = on;
        Invalidate();
    }

    void OnDragOver(object? sender, DragEventArgs e)
    {
        e.Effect = DragDropEffects.None;
        if (e.Data?.GetDataPresent(DragPayload.Format) == true)
        {
            var p = e.Data.GetData(DragPayload.Format) as DragPayload;
            if (p != null && p.Source != this && Card.CanWrite && p.Entries.Any(x => x.Platform == Card.Platform))
            {
                bool shift = (e.KeyState & 4) != 0;
                e.Effect = shift && (e.AllowedEffect & DragDropEffects.Move) != 0 && p.Source.Card.CanWrite ? DragDropEffects.Move : DragDropEffects.Copy;
            }
        }
        else if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
        {
            e.Effect = DragDropEffects.Copy;
        }
        SetDropHighlight(e.Effect != DragDropEffects.None);
    }

    void OnDragDrop(object? sender, DragEventArgs e)
    {
        SetDropHighlight(false);
        if (e.Data?.GetDataPresent(DragPayload.Format) == true)
        {
            if (e.Data.GetData(DragPayload.Format) is DragPayload p && p.Source != this)
            {
                bool move = e.Effect == DragDropEffects.Move;
                BeginInvoke(() => _host.Transfer(p.Source, this, p.Entries, move));
            }
            return;
        }
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
        {
            var pt = new Point(e.X, e.Y);
            BeginInvoke(() => HandleDroppedPaths(paths, pt)); // let Explorer finish the drop first
        }
    }

    /// <summary>Files/folders dropped from outside: cards can be opened or their saves imported, everything else is imported.</summary>
    public void HandleDroppedPaths(string[] paths, Point screen)
    {
        var cards = new List<string>();
        var others = new List<string>();
        foreach (var p in paths)
        {
            if (IsCardPath(p)) cards.Add(p);
            else others.Add(p);
        }
        if (others.Count > 0)
        {
            if (!Card.CanWrite) { MessageBox.Show(this, Card.ReadOnlyReason, L.T("Nur lesen", "Read only"), MessageBoxButtons.OK, MessageBoxIcon.Information); }
            else ImportPaths(others);
        }
        if (cards.Count == 0) return;
        var menu = new ContextMenuStrip();
        Theme.StyleMenu(menu);
        string what = cards.Count == 1 ? L.T($"„{Path.GetFileName(cards[0].TrimEnd('\\'))}“", $"\"{Path.GetFileName(cards[0].TrimEnd('\\'))}\"") : L.T($"{cards.Count} Karten", $"{cards.Count} cards");
        menu.Items.Add(new ToolStripLabel(cards.Count == 1 ? L.T($"{what} ist eine Memory Card:", $"{what} is a memory card:") : L.T($"{what} ist eine Memory Card:", $"{what} are memory cards:")) { ForeColor = Theme.Dim });
        menu.Items.Add(L.T("Als eigene Karte öffnen (neues Segment)", "Open as separate card (new segment)"), null, (_, _) => _host.OpenCards(cards));
        menu.Items.Add(L.T("Savegames daraus in diese Karte importieren", "Import its saves into this card"), null, (_, _) => ImportPaths(cards)).Enabled = Card.CanWrite;
        menu.Items.Add(L.T("Abbrechen", "Cancel"));
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(screen);
    }

    public static bool IsCardPath(string p)
    {
        try
        {
            if (Directory.Exists(p)) return Ps2FolderCard.IsFolderCard(p);
            var fi = new FileInfo(p);
            if (!fi.Exists || fi.Length < Ps1Card.CardSize || fi.Length > 256L * 1024 * 1024) return false;
            using var fs = fi.OpenRead();
            var head = new byte[Math.Min(fi.Length, 4096)];
            fs.ReadExactly(head);
            if (Ps2Card.IsPs2Image(head)) return true;
            // card-sized files with a card extension count as cards even if damaged/unformatted
            // (opening them then offers formatting or restoring a backup)
            string ext = fi.Extension.ToLowerInvariant();
            bool cardExt = ext is ".ps2" or ".vm2" or ".vmc" or ".mc2" or ".mcr" or ".mcd" or ".mc" or ".srm" or ".vm1" or ".gme" or ".vgs" or ".vmp";
            bool cardSize = fi.Length == Ps1Card.CardSize || fi.Length % 1081344 == 0 || fi.Length % 1048576 == 0;
            if (cardExt && cardSize) return true;
            if (fi.Length > 70L * 1024 * 1024) return false;
            var all = File.ReadAllBytes(p);
            return Ps1Card.TryDetect(all, out _, out _) || CardIO.DetectUnformatted(all) != null;
        }
        catch { return false; }
    }

    void StartDrag(List<CardEntry> items)
    {
        var data = new DataObject();
        data.SetData(DragPayload.Format, new DragPayload { Source = this, Entries = items });
        try
        {
            // real files for Explorer & other programs (exported to a temp folder in the default format)
            string dir = Path.Combine(Path.GetTempPath(), "MemcardManager", "drag", DateTime.Now.Ticks.ToString());
            var files = new System.Collections.Specialized.StringCollection();
            var fmt = DefaultFormat(Card.Platform);
            foreach (var it in items) files.Add(SaveFormats.ExportTo(it.Save, fmt, dir));
            data.SetFileDropList(files);
        }
        catch (Exception ex) { _host.SetStatus(L.T("Drag-Export fehlgeschlagen: ", "Drag export failed: ") + ex.Message, true); }
        _host.SetStatus(items.Count == 1
            ? L.T($"Ziehe „{items[0].Title}“ – auf eine andere Karte (Umschalt = verschieben) oder in den Explorer.", $"Dragging \"{items[0].Title}\" – onto another card (Shift = move) or into Explorer.")
            : L.T($"Ziehe {items.Count} Savegames – auf eine andere Karte (Umschalt = verschieben) oder in den Explorer.", $"Dragging {items.Count} saves – onto another card (Shift = move) or into Explorer."));
        List.DoDragDrop(data, DragDropEffects.Copy | DragDropEffects.Move);
    }

    ExportFormat DefaultFormat(Platform p)
    {
        var fmts = SaveFormats.FormatsFor(p);
        int idx = _host.Settings.LastExportFormat.TryGetValue(p.ToString(), out var i) ? i : 0;
        var f = fmts[Math.Clamp(idx, 0, fmts.Length - 1)];
        return f;
    }

    // ================================================================== import

    void ImportDialog()
    {
        if (!Card.CanWrite) return;
        using var ofd = new OpenFileDialog
        {
            Title = L.T("Savegames importieren", "Import saves"), Filter = CardIO.ImportFilter, Multiselect = true,
            InitialDirectory = Directory.Exists(_host.Settings.LastImportDir) ? _host.Settings.LastImportDir : ""
        };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;
        _host.Settings.LastImportDir = Path.GetDirectoryName(ofd.FileNames[0]) ?? "";
        ImportPaths(ofd.FileNames.ToList());
    }

    void ImportFolderDialog()
    {
        if (!Card.CanWrite) return;
        using var fb = new FolderBrowserDialog
        {
            Description = L.T("Ordner mit einem PS2-Save (icon.sys …) oder mit Savegame-Dateien wählen", "Choose a folder with a PS2 save (icon.sys …) or with save files"), UseDescriptionForTitle = true,
            SelectedPath = _host.Settings.LastImportDir
        };
        if (fb.ShowDialog(this) != DialogResult.OK) return;
        _host.Settings.LastImportDir = fb.SelectedPath;
        ImportPaths(new List<string> { fb.SelectedPath });
    }

    public void ImportPaths(List<string> paths, bool rescue = false)
    {
        var saves = new List<SaveGame>();
        var warnings = new List<string>();
        var failed = new List<string>();
        Cursor = Cursors.WaitCursor;
        try
        {
            foreach (var p in paths)
            {
                try
                {
                    var loaded = SaveFormats.LoadAny(p, warnings, rescue ? new SaveFormats.Rescue() : null);
                    if (loaded.Count == 0) warnings.Add(L.T($"{Path.GetFileName(p)}: keine Savegames gefunden.", $"{Path.GetFileName(p)}: no saves found."));
                    saves.AddRange(loaded);
                }
                catch (Exception ex)
                {
                    warnings.Add($"{Path.GetFileName(p)}: {ex.Message}");
                    if (!rescue && File.Exists(p)) failed.Add(p);
                }
            }
        }
        finally { Cursor = Cursors.Default; }

        if (failed.Count > 0 && Card.CanWrite)
        {
            var q = MessageBox.Show(this,
                L.T($"{failed.Count} Datei(en) sind beschädigt oder unvollständig:\n", $"{failed.Count} file(s) are damaged or incomplete:\n") +
                string.Join("\n", failed.Take(8).Select(f => "  • " + Path.GetFileName(f))) + (failed.Count > 8 ? "\n  …" : "") +
                L.T("\n\nRettungsimport versuchen? Dabei wird übernommen, was lesbar ist; fehlende Teile werden mit Nullen aufgefüllt.\n" +
                    "OHNE GEWÄHR: Ob das Spiel den Stand lädt, ist ungewiss. Die Saves werden rot als „Rettungsimport“ markiert.",
                    "\n\nTry a rescue import? Whatever is readable will be taken over; missing parts are filled with zeros.\n" +
                    "NO GUARANTEE: It is uncertain whether the game will load the save. The saves are marked red as \"rescue import\"."),
                L.T("Rettungsimport", "Rescue import"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (q == DialogResult.Yes)
            {
                foreach (var p in failed)
                {
                    try
                    {
                        saves.AddRange(SaveFormats.LoadAny(p, warnings, new SaveFormats.Rescue()));
                        warnings.RemoveAll(w => w.StartsWith(Path.GetFileName(p) + ":"));
                    }
                    catch (Exception ex) { warnings.Add(L.T($"{Path.GetFileName(p)}: auch der Rettungsimport ist gescheitert – {ex.Message}", $"{Path.GetFileName(p)}: the rescue import failed as well – {ex.Message}")); }
                }
            }
        }
        ImportSaves(saves, null, warnings);
    }

    void RescueImportDialog()
    {
        if (!Card.CanWrite) return;
        using var ofd = new OpenFileDialog
        {
            Title = L.T("Rettungsimport – beschädigte Savegame-Dateien (ohne Gewähr)", "Rescue import – damaged save files (no guarantee)"), Filter = CardIO.ImportFilter, Multiselect = true,
            InitialDirectory = Directory.Exists(_host.Settings.LastImportDir) ? _host.Settings.LastImportDir : ""
        };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;
        _host.Settings.LastImportDir = Path.GetDirectoryName(ofd.FileNames[0]) ?? "";
        ImportPaths(ofd.FileNames.ToList(), true);
    }

    enum Conflict { Overwrite, Skip, Cancel }

    Conflict AskConflict(SaveGame incoming, CardEntry existing, out bool forAll)
    {
        var overwrite = new TaskDialogButton(L.T("Ersetzen", "Replace"));
        var skip = new TaskDialogButton(L.T("Überspringen", "Skip"));
        var cancel = TaskDialogButton.Cancel;
        var page = new TaskDialogPage
        {
            Caption = L.T("Savegame existiert bereits", "Save already exists"),
            Heading = L.T($"„{existing.Name}“ ist bereits auf {Card.DisplayName}.", $"\"{existing.Name}\" is already on {Card.DisplayName}."),
            Text = L.T("Vorhanden:  ", "Existing:  ") + $"{existing.Title}  ({Theme.FormatSize(existing.Size)}{(existing.Modified is { } m ? ", " + m.ToString("dd.MM.yyyy HH:mm") : "")})\n" +
                   L.T("Neu:            ", "New:            ") + $"{incoming.Title}  ({Theme.FormatSize(incoming.Size)})\n\n" +
                   L.T("Beim Ersetzen wird der vorhandene Save überschrieben. Vor dem Speichern der Karte wird automatisch ein Backup der Kartendatei angelegt.",
                       "Replacing overwrites the existing save. Before the card is saved, a backup of the card file is created automatically."),
            Icon = TaskDialogIcon.Warning,
            Verification = new TaskDialogVerificationCheckBox(L.T("Für alle weiteren Konflikte übernehmen", "Apply to all further conflicts")),
            Buttons = { overwrite, skip, cancel },
            DefaultButton = skip
        };
        var res = TaskDialog.ShowDialog(this, page);
        forAll = page.Verification.Checked;
        return res == overwrite ? Conflict.Overwrite : res == skip ? Conflict.Skip : Conflict.Cancel;
    }

    /// <summary>
    /// Imports saves with conflict handling. Returns the saves that are now on this card
    /// (imported, replaced, or already identical) – used for "move".
    /// </summary>
    public List<SaveGame> ImportSaves(List<SaveGame> saves, string? sourceName = null, List<string>? warnings = null)
    {
        var done = new List<SaveGame>();
        var errors = new List<string>(warnings ?? new List<string>());
        if (!Card.CanWrite)
        {
            MessageBox.Show(this, Card.ReadOnlyReason, L.T("Nur lesen", "Read only"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            return done;
        }
        int imported = 0, replaced = 0, identical = 0, skipped = 0;
        Conflict? all = null;
        var names = new List<string>();
        foreach (var save in saves)
        {
            if (save.Platform != Card.Platform)
            {
                errors.Add(L.T($"{save.Name}: {(save.Platform == Platform.PS1 ? "PS1" : "PS2")}-Savegame passt nicht auf eine {(Card.Platform == Platform.PS1 ? "PS1" : "PS2")}-Karte.",
                               $"{save.Name}: a {(save.Platform == Platform.PS1 ? "PS1" : "PS2")} save does not fit on a {(Card.Platform == Platform.PS1 ? "PS1" : "PS2")} card."));
                continue;
            }
            try
            {
                if (save.Ps2 != null) save.Ps2.Validate(); else save.Ps1!.Validate();
            }
            catch (Exception ex) { errors.Add($"{save.Name}: {ex.Message}"); continue; }

            var existing = Card.Find(save.Name);
            if (existing != null)
            {
                if (SaveFormats.SameContent(existing.Save, save)) { identical++; done.Add(save); names.Add(save.Name); continue; }
                Conflict decision;
                if (all != null) decision = all.Value;
                else
                {
                    decision = AskConflict(save, existing, out bool fa);
                    if (fa && decision != Conflict.Cancel) all = decision;
                }
                if (decision == Conflict.Cancel) break;
                if (decision == Conflict.Skip) { skipped++; continue; }
                if (Card.SpaceNeeded(save) > Card.FreeBytes + existing.Size)
                {
                    errors.Add(L.T($"{save.Name}: nicht genug Platz zum Ersetzen.", $"{save.Name}: not enough space to replace."));
                    continue;
                }
                var old = existing.Save;
                Card.Delete(existing);
                try { Card.Import(save.Clone()); }
                catch (Exception ex)
                {
                    try { Card.Import(old); } catch { /* old data is still part of the unchanged file on disk */ }
                    errors.Add($"{save.Name}: {ex.Message}");
                    continue;
                }
                replaced++;
                done.Add(save);
                names.Add(save.Name);
                continue;
            }
            if (Card.SpaceNeeded(save) > Card.FreeBytes)
            {
                errors.Add(L.T($"{save.Name} ({save.Title}): nicht genug Platz – benötigt {Theme.FormatSize(Card.SpaceNeeded(save))}, frei {Theme.FormatSize(Card.FreeBytes)}.",
                               $"{save.Name} ({save.Title}): not enough space – needs {Theme.FormatSize(Card.SpaceNeeded(save))}, free {Theme.FormatSize(Card.FreeBytes)}."));
                continue;
            }
            try
            {
                Card.Import(save.Clone());
                imported++;
                if (save.Damaged) errors.Add(L.T($"{save.Name}: übernommen, aber beschädigt ({save.Problem}).", $"{save.Name}: imported, but damaged ({save.Problem})."));
                done.Add(save);
                names.Add(save.Name);
            }
            catch (Exception ex) { errors.Add($"{save.Name}: {ex.Message}"); }
        }
        AfterChange(names);
        var parts = new List<string>();
        if (imported > 0) parts.Add(L.T($"{imported} importiert", $"{imported} imported"));
        if (replaced > 0) parts.Add(L.T($"{replaced} ersetzt", $"{replaced} replaced"));
        if (identical > 0) parts.Add(L.T($"{identical} bereits identisch vorhanden", $"{identical} already present (identical)"));
        if (skipped > 0) parts.Add(L.T($"{skipped} übersprungen", $"{skipped} skipped"));
        if (errors.Count > 0) parts.Add(L.T($"{errors.Count} Fehler", $"{errors.Count} error(s)"));
        _host.SetStatus($"Slot {Slot}: " + (parts.Count > 0 ? string.Join(", ", parts) : L.T("nichts importiert", "nothing imported")) +
                        (Card.IsModified && !_host.Settings.AutoSave ? L.T(" – zum Übernehmen „Speichern“ klicken.", " – click \"Save\" to apply.") : ""), errors.Count > 0);
        if (errors.Count > 0)
        {
            using var dlg = new TextReportDialog(L.T("Import – Hinweise", "Import – notes"), string.Join(Environment.NewLine, errors));
            dlg.ShowDialog(this);
        }
        return done;
    }

    // ================================================================== export / delete / properties

    void ExportSelected()
    {
        var sel = List.SelectedItems;
        if (sel.Count == 0) return;
        string key = Card.Platform.ToString();
        int last = _host.Settings.LastExportFormat.TryGetValue(key, out var i) ? i : 0;
        using var dlg = new ExportDialog(Card.Platform, sel.Count, _host.Settings.LastExportDir, last);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _host.Settings.LastExportDir = dlg.TargetDir;
        _host.Settings.LastExportFormat[key] = Array.IndexOf(SaveFormats.FormatsFor(Card.Platform), dlg.Format);
        DoExport(sel, dlg.Format, dlg.TargetDir, dlg.OpenAfter);
    }

    void QuickExport(ExportFormat fmt)
    {
        var sel = List.SelectedItems;
        using var fb = new FolderBrowserDialog { Description = L.T("Zielordner für den Export", "Target folder for the export"), UseDescriptionForTitle = true, SelectedPath = _host.Settings.LastExportDir };
        if (fb.ShowDialog(this) != DialogResult.OK) return;
        _host.Settings.LastExportDir = fb.SelectedPath;
        DoExport(sel, fmt, fb.SelectedPath, false);
    }

    void DoExport(List<CardEntry> items, ExportFormat fmt, string dir, bool open)
    {
        var errors = new List<string>();
        string? lastPath = null;
        Cursor = Cursors.WaitCursor;
        int ok = 0;
        try
        {
            foreach (var it in items)
            {
                try { lastPath = SaveFormats.ExportTo(it.Save, fmt, dir); ok++; }
                catch (Exception ex) { errors.Add($"{it.Name}: {ex.Message}"); }
            }
        }
        finally { Cursor = Cursors.Default; }
        _host.SetStatus(L.T($"{ok} Savegame(s) als {SaveFormats.Describe(fmt).Split(' ')[0]} nach {dir} exportiert.", $"{ok} save(s) exported as {SaveFormats.Describe(fmt).Split(' ')[0]} to {dir}.")
                        + (errors.Count > 0 ? L.T($" {errors.Count} Fehler.", $" {errors.Count} error(s).") : ""), errors.Count > 0);
        if (errors.Count > 0)
        {
            using var dlg = new TextReportDialog(L.T("Export – Fehler", "Export – errors"), string.Join(Environment.NewLine, errors));
            dlg.ShowDialog(this);
        }
        if (open && lastPath != null)
        {
            try { Process.Start("explorer.exe", $"/select,\"{lastPath}\""); } catch { }
        }
    }

    void DeleteSelected()
    {
        var sel = List.SelectedItems;
        if (sel.Count == 0 || !Card.CanWrite) return;
        string what = sel.Count == 1 ? L.T($"„{sel[0].Title}“ ({sel[0].Name})", $"\"{sel[0].Title}\" ({sel[0].Name})") : L.T($"{sel.Count} Savegames", $"{sel.Count} saves");
        var res = MessageBox.Show(this,
            L.T($"{what} von {Card.DisplayName} löschen?\n\nDie Kartendatei wird erst beim Speichern geändert; dabei wird automatisch ein Backup angelegt.",
                $"Delete {what} from {Card.DisplayName}?\n\nThe card file is only changed when saving; a backup is created automatically."),
            L.T("Löschen", "Delete"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (res != DialogResult.Yes) return;
        DeleteEntries(sel);
    }

    public void DeleteEntries(IEnumerable<CardEntry> entries)
    {
        int n = 0;
        foreach (var e in entries.ToList())
        {
            var cur = Card.Find(e.Name);
            if (cur == null) continue;
            try { Card.Delete(cur); n++; }
            catch (Exception ex) { _host.SetStatus($"{e.Name}: {ex.Message}", true); }
        }
        AfterChange();
        _host.SetStatus(L.T($"Slot {Slot}: {n} Savegame(s) gelöscht", $"Slot {Slot}: {n} save(s) deleted") + (Card.IsModified && !_host.Settings.AutoSave ? L.T(" – zum Übernehmen „Speichern“ klicken.", " – click \"Save\" to apply.") : "."));
    }

    void RenameEntry(CardEntry entry)
    {
        using var dlg = new RenameDialog(entry, n => Card.Exists(n));
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        SaveGame renamed;
        try { renamed = entry.Save.Renamed(dlg.NewName, dlg.RenameFiles); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, L.T("Umbenennen", "Rename"), MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

        if (dlg.KeepOriginal)
        {
            if (Card.SpaceNeeded(renamed) > Card.FreeBytes)
            {
                MessageBox.Show(this, L.T("Nicht genug Platz für eine Kopie. Ohne „Original behalten“ umbenennen oder Platz schaffen.", "Not enough space for a copy. Rename without \"Keep original\" or free up space."),
                    L.T("Umbenennen", "Rename"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try { Card.Import(renamed); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, L.T("Umbenennen", "Rename"), MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        }
        else
        {
            var old = entry.Save;
            Card.Delete(entry);
            try { Card.Import(renamed); }
            catch (Exception ex)
            {
                try { Card.Import(old); } catch { /* original file on disk is untouched until saving */ }
                MessageBox.Show(this, ex.Message, L.T("Umbenennen", "Rename"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                AfterChange();
                return;
            }
        }
        AfterChange(new[] { renamed.Name });
        _host.SetStatus(L.T($"„{entry.Name}“ → „{renamed.Name}“ ({RegionUtil.Display(renamed.Region)})", $"\"{entry.Name}\" → \"{renamed.Name}\" ({RegionUtil.Display(renamed.Region)})") +
                        (dlg.KeepOriginal ? L.T(", Original behalten", ", original kept") : "") + (Card.IsModified && !_host.Settings.AutoSave ? L.T(" – zum Übernehmen „Speichern“ klicken.", " – click \"Save\" to apply.") : "."));
    }

    void ShowProperties(CardEntry e)
    {
        using var dlg = new PropertiesDialog(e, Card);
        dlg.ShowDialog(this);
    }

    void CheckCard()
    {
        RefreshView();
        var backups = Card is Ps2FolderCard ? new List<FileInfo>() : CardIO.ListBackups(Card.FilePath);
        HealthDialog.Action action;
        using (var dlg = new HealthDialog(Card, _issues, backups.Count))
        {
            dlg.ShowDialog(this);
            action = dlg.DialogResult == DialogResult.OK ? dlg.Chosen : HealthDialog.Action.None;
        }
        switch (action)
        {
            case HealthDialog.Action.Repair: RepairCard(); break;
            case HealthDialog.Action.Undelete: UndeleteSaves(); break;
            case HealthDialog.Action.RestoreBackup: RestoreBackup(); break;
        }
    }

    void RepairCard()
    {
        if (!Card.CanWrite) return;
        var r = MessageBox.Show(this,
            L.T("Alle als „reparierbar“ markierten Probleme jetzt beheben?\n\nDas passiert zuerst nur im Speicher; erst „Speichern“ schreibt die Datei (mit Backup).",
                "Fix all problems marked as \"repairable\" now?\n\nThis happens in memory first; only \"Save\" writes the file (with backup)."),
            L.T("Reparieren", "Repair"), MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (r != DialogResult.Yes) return;
        List<string> report;
        try { report = Card.Repair(); }
        catch (Exception ex) { MessageBox.Show(this, L.T("Reparatur fehlgeschlagen: ", "Repair failed: ") + ex.Message, L.T("Reparieren", "Repair"), MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        AfterChange();
        using var dlg = new TextReportDialog(L.T("Reparatur – ", "Repair – ") + Card.DisplayName,
            report.Count == 0 ? L.T("Nichts zu reparieren.", "Nothing to repair.") : string.Join(Environment.NewLine, report.Select(x => "• " + x)) +
            (Card.IsModified && !_host.Settings.AutoSave ? Environment.NewLine + Environment.NewLine + L.T("Zum Übernehmen „Speichern“ klicken.", "Click \"Save\" to apply.") : ""));
        dlg.ShowDialog(this);
    }

    void UndeleteSaves()
    {
        if (Card.DeletedSaves.Count == 0)
        {
            MessageBox.Show(this, Card.Platform == Platform.PS1
                    ? L.T("Auf dieser Karte gibt es keine gelöschten Savegames, die noch vollständig vorhanden sind.", "This card has no deleted saves that are still completely present.")
                    : L.T("Auf PS2-Karten werden gelöschte Saves vom Dateisystem freigegeben und können nicht zuverlässig wiederhergestellt werden.\n" +
                          "Nutze stattdessen „Backup zurückspielen“ (jede Änderung durch dieses Programm wird gesichert).",
                          "On PS2 cards, deleted saves are freed by the file system and cannot be restored reliably.\n" +
                          "Use \"Restore backup\" instead (every change made by this program is backed up)."),
                L.T("Wiederherstellen", "Restore"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dlg = new UndeleteDialog(Card.DeletedSaves);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        int ok = 0;
        var errors = new List<string>();
        var names = new List<string>();
        foreach (var e in dlg.Selected)
        {
            try { Card.Undelete(e); ok++; names.Add(e.Name); }
            catch (Exception ex) { errors.Add($"{e.Name}: {ex.Message}"); }
        }
        AfterChange(names);
        _host.SetStatus(L.T($"Slot {Slot}: {ok} Savegame(s) wiederhergestellt", $"Slot {Slot}: {ok} save(s) restored") + (errors.Count > 0 ? L.T($", {errors.Count} Fehler: ", $", {errors.Count} error(s): ") + string.Join("; ", errors) : "") +
                        (Card.IsModified && !_host.Settings.AutoSave ? L.T(" – zum Übernehmen „Speichern“ klicken.", " – click \"Save\" to apply.") : "."), errors.Count > 0);
    }

    void RestoreBackup()
    {
        if (Card is Ps2FolderCard)
        {
            MessageBox.Show(this, L.T("Bei Ordner-Karten liegen gelöschte Saves als Ordner im Backup-Ordner. Sie können per Drag & Drop wieder auf die Karte gezogen werden.",
                                      "With folder cards, deleted saves are kept as folders in the backup folder. They can be dragged back onto the card via drag & drop."),
                "Backup", MessageBoxButtons.OK, MessageBoxIcon.Information);
            try { Process.Start("explorer.exe", $"\"{CardIO.BackupDir}\""); } catch { }
            return;
        }
        var backups = CardIO.ListBackups(Card.FilePath);
        if (backups.Count == 0)
        {
            MessageBox.Show(this, L.T("Für diese Karte gibt es noch keine Backups (sie entstehen bei jedem Speichern durch dieses Programm).", "There are no backups for this card yet (they are created every time this program saves)."),
                "Backup", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (Card.IsModified)
        {
            var q = MessageBox.Show(this, L.T("Ungespeicherte Änderungen an dieser Karte gehen beim Zurückspielen verloren. Fortfahren?", "Unsaved changes to this card will be lost when restoring. Continue?"), "Backup",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (q != DialogResult.Yes) return;
        }
        using var dlg = new BackupDialog(Card.FilePath, backups);
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.SelectedFile == null) return;
        try
        {
            CardIO.RestoreBackup(Card.FilePath, dlg.SelectedFile);
            ReplaceCard(CardIO.Open(Card.FilePath));
            _host.SetStatus(L.T($"Slot {Slot}: Backup zurückgespielt (vorheriger Stand ebenfalls gesichert).", $"Slot {Slot}: backup restored (previous state backed up as well)."));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, L.T("Zurückspielen fehlgeschlagen:\n", "Restore failed:\n") + ex.Message + L.T("\n\nDie Kartendatei wurde nicht verändert.", "\n\nThe card file has not been changed."), "Backup",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void ShowInExplorer()
    {
        try { Process.Start("explorer.exe", $"/select,\"{Card.FilePath.TrimEnd('\\')}\""); } catch { }
    }

    // ================================================================== save / revert

    DateTime CurrentWriteTime() =>
        Directory.Exists(Card.FilePath) ? Directory.GetLastWriteTimeUtc(Card.FilePath) :
        File.Exists(Card.FilePath) ? File.GetLastWriteTimeUtc(Card.FilePath) : DateTime.MinValue;

    public bool Save(bool quiet = false)
    {
        if (!Card.IsModified) return true;
        if (!Card.CanWrite) return SaveAs();
        if (Card is not Ps2FolderCard && File.Exists(Card.FilePath) && CurrentWriteTime() != Card.LoadedWriteTimeUtc)
        {
            var r = MessageBox.Show(this,
                L.T($"{Card.DisplayName} wurde seit dem Laden außerhalb geändert (z. B. von PCSX2).\n\n" +
                    "Ja = trotzdem speichern (die externe Version landet im Backup-Ordner)\nNein = Abbrechen (Tipp: „Änderungen verwerfen“ lädt die aktuelle Datei)",
                    $"{Card.DisplayName} has been changed outside since loading (e.g. by PCSX2).\n\n" +
                    "Yes = save anyway (the external version goes into the backup folder)\nNo = cancel (tip: \"Discard changes\" loads the current file)"),
                L.T("Datei wurde verändert", "File has been changed"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (r != DialogResult.Yes) return false;
        }
        try
        {
            Cursor = Cursors.WaitCursor;
            Card.Commit(Card.FilePath);
            Card.LoadedWriteTimeUtc = CurrentWriteTime();
            RefreshView();
            _host.SetStatus(L.T($"Slot {Slot}: {Card.DisplayName} gespeichert (Backup im Ordner „Backups“).", $"Slot {Slot}: {Card.DisplayName} saved (backup in the \"Backups\" folder)."));
            return true;
        }
        catch (Exception ex)
        {
            string hint = ex is IOException or UnauthorizedAccessException
                ? L.T("\n\nIst die Datei noch in PCSX2/einem anderen Programm geöffnet? Die Originaldatei wurde NICHT verändert.", "\n\nIs the file still open in PCSX2/another program? The original file has NOT been changed.")
                : L.T("\n\nDie Originaldatei wurde NICHT verändert.", "\n\nThe original file has NOT been changed.");
            MessageBox.Show(this, L.T("Speichern fehlgeschlagen:\n", "Saving failed:\n") + ex.Message + hint, L.T("Fehler", "Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            _host.SetStatus(L.T("Speichern fehlgeschlagen: ", "Saving failed: ") + ex.Message, true);
            return false;
        }
        finally { Cursor = Cursors.Default; }
    }

    public bool SaveAs()
    {
        string filter = Card.Platform == Platform.PS2
            ? "PCSX2 (*.ps2)|*.ps2|PS3 VM2 (*.VM2)|*.VM2|" + L.T("OPL VMC ohne ECC", "OPL VMC without ECC") + " (*.vmc)|*.vmc|MemCard PRO2 / SD2PSX (*.mc2)|*.mc2|" + L.T("Rohabbild", "Raw image") + " (*.bin)|*.bin"
            : "PS1 Raw (*.mcr)|*.mcr|DuckStation (*.mcd)|*.mcd|PS3 VM1 (*.VM1)|*.VM1|DexDrive (*.gme)|*.gme|VGS (*.vgs)|*.vgs|RetroArch (*.srm)|*.srm|POPStarter (*.VMC)|*.VMC|PSP / PS Vita (*.VMP, " + L.T("signiert", "signed") + ")|*.VMP";
        using var sfd = new SaveFileDialog
        {
            Title = L.T("Karte speichern unter / konvertieren", "Save card as / convert"), Filter = filter,
            FileName = Path.GetFileNameWithoutExtension(Card.FilePath.TrimEnd('\\')) + L.T("_kopie", "_copy"),
            InitialDirectory = Path.GetDirectoryName(Card.FilePath.TrimEnd('\\')) ?? ""
        };
        if (sfd.ShowDialog(this) != DialogResult.OK) return false;
        try
        {
            CardIO.SaveAs(Card, sfd.FileName);
            var r = MessageBox.Show(this, L.T($"Gespeichert als:\n{sfd.FileName}\n\nDie neue Datei jetzt in diesem Segment öffnen?", $"Saved as:\n{sfd.FileName}\n\nOpen the new file in this segment now?"),
                L.T("Speichern unter", "Save as"), MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (r == DialogResult.Yes) ReplaceCard(CardIO.Open(sfd.FileName));
            _host.SetStatus(L.T("Gespeichert als ", "Saved as ") + sfd.FileName);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, L.T("Speichern fehlgeschlagen:\n", "Saving failed:\n") + ex.Message, L.T("Fehler", "Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    public void Revert()
    {
        if (Card.IsModified)
        {
            var r = MessageBox.Show(this, L.T("Alle ungespeicherten Änderungen an dieser Karte verwerfen und die Datei neu laden?", "Discard all unsaved changes to this card and reload the file?"), L.T("Verwerfen", "Discard"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (r != DialogResult.Yes) return;
        }
        try
        {
            ReplaceCard(CardIO.Open(Card.FilePath));
            _host.SetStatus(L.T($"Slot {Slot}: neu geladen.", $"Slot {Slot}: reloaded."));
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, L.T("Fehler", "Error"), MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    void ReplaceCard(MemoryCard c)
    {
        Card = c;
        RefreshView();
        _host.AfterChange(this);
    }

    /// <summary>Asks to save before closing. Returns false if the user cancelled.</summary>
    public bool ConfirmClose()
    {
        if (!Card.IsModified) return true;
        var r = MessageBox.Show(this, L.T($"{Card.DisplayName} hat ungespeicherte Änderungen. Speichern?", $"{Card.DisplayName} has unsaved changes. Save?"), L.T("Karte schließen", "Close card"),
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (r == DialogResult.Cancel) return false;
        if (r == DialogResult.Yes) return Save();
        return true;
    }
}
