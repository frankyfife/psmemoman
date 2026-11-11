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
            if (Card.Warnings.Count > 0 && _warnRect.Contains(e.Location)) ShowWarnings();
        };
        _close.FlatAppearance.BorderSize = 0;
        _close.ForeColor = Theme.Dim;
        _close.BackColor = Theme.Header;
        _close.Click += (_, _) => _host.ClosePanel(this);
        _tip.SetToolTip(_close, "Karte schließen (Segment ausblenden)");
        _header.Controls.Add(_close);
        _header.Resize += (_, _) => _close.Location = new Point(_header.Width - _close.Width - Dpi.S(6), Dpi.S(6));

        // toolbar
        _tools.Renderer = Theme.ToolRenderer;
        _tools.BackColor = Theme.Header;
        _btnImport = new ToolStripButton("⭳ Importieren", null, (_, _) => ImportDialog()) { ToolTipText = "Savegames aus Dateien importieren (PSU, MAX, CBS, SPS, XPS, PSV, MCS, PSX, ZIP, Karten …)" };
        _btnExport = new ToolStripButton("⭱ Exportieren", null, (_, _) => ExportSelected()) { ToolTipText = "Ausgewählte Savegames exportieren" };
        _btnDelete = new ToolStripButton("🗑 Löschen", null, (_, _) => DeleteSelected()) { ToolTipText = "Ausgewählte Savegames löschen (Entf)" };
        _btnSave = new ToolStripButton("💾 Speichern", null, (_, _) => Save()) { ToolTipText = "Änderungen in die Kartendatei schreiben (mit automatischem Backup)" };
        _btnMore = new ToolStripDropDownButton("⋯") { ToolTipText = "Weitere Aktionen", ShowDropDownArrow = false };
        _btnMore.DropDownItems.Add("Speichern unter / konvertieren …", null, (_, _) => SaveAs());
        _btnMore.DropDownItems.Add("Änderungen verwerfen (neu laden)", null, (_, _) => Revert());
        _btnMore.DropDownItems.Add(new ToolStripSeparator());
        _btnMore.DropDownItems.Add("Ordner importieren …", null, (_, _) => ImportFolderDialog());
        _btnMore.DropDownItems.Add("Alle auswählen", null, (_, _) => List.SelectAll());
        _btnMore.DropDownItems.Add("Alle Savegames exportieren …", null, (_, _) => { List.SelectAll(); ExportSelected(); });
        _btnMore.DropDownItems.Add(new ToolStripSeparator());
        _btnMore.DropDownItems.Add("Karte prüfen", null, (_, _) => CheckCard());
        _btnMore.DropDownItems.Add("Im Explorer zeigen", null, (_, _) => ShowInExplorer());
        _btnMore.DropDownItems.Add(new ToolStripSeparator());
        _btnMore.DropDownItems.Add("Karte schließen", null, (_, _) => _host.ClosePanel(this));
        Theme.StyleMenu(_btnMore.DropDown);
        _tools.Items.AddRange(new ToolStripItem[] { _btnImport, _btnExport, _btnDelete, new ToolStripSeparator(), _btnSave, _btnMore });

        // usage bar
        _usage.BackColor = Theme.Panel;
        _usage.Paint += PaintUsage;

        // unsaved banner
        _banner.BackColor = Color.FromArgb(70, 58, 25);
        var bannerText = new Label
        {
            Text = "● Ungespeicherte Änderungen – die Kartendatei ist noch unverändert.", AutoSize = false, Dock = DockStyle.Fill,
            ForeColor = Theme.Warn, TextAlign = ContentAlignment.MiddleLeft, Padding = Dpi.S(new Padding(8, 0, 0, 0))
        };
        var bSave = BannerButton("Speichern", (_, _) => Save());
        var bRevert = BannerButton("Verwerfen", (_, _) => Revert());
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
        int warnW = 0;
        if (Card.Warnings.Count > 0)
        {
            string wt = "⚠ " + Card.Warnings.Count;
            warnW = TextRenderer.MeasureText(wt, Theme.Small).Width + Dpi.S(8);
            _warnRect = new Rectangle(right - warnW, y + th + Dpi.S(2), warnW, sh);
            TextRenderer.DrawText(g, wt, Theme.Small, _warnRect, Theme.Warn, TextFormatFlags.HorizontalCenter);
        }
        string sub = Card.FormatName + (Card.CanWrite ? "" : "  ·  NUR LESEN");
        TextRenderer.DrawText(g, sub, Theme.Small, new Rectangle(x, y + th + Dpi.S(2), right - x - warnW, sh),
            Card.CanWrite ? Theme.Dim : Theme.Warn, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    void PaintUsage(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        long total = Math.Max(1, Card.TotalBytes), free = Card.FreeBytes, used = total - free;
        string txt = Card.Platform == Platform.PS1
            ? $"{free / Ps1Card.BlockSize} von 15 Blöcken frei · {Card.Entries.Count} Saves"
            : $"{free / 1024:#,0} KB frei von {total / 1024:#,0} KB · {Card.Entries.Count} Saves";
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

    public void RefreshView()
    {
        List.SetItems(Card.Entries.OrderBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(e => e.Name));
        List.EmptyText = Card.CanWrite
            ? "Keine Savegames auf dieser Karte.\n\nSavegames oder Karten hierher ziehen\noder über „Importieren“ hinzufügen."
            : "Keine Savegames auf dieser Karte.";
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
            _ctx.Items.Add("Importieren …", null, (_, _) => ImportDialog()).Enabled = Card.CanWrite;
            _ctx.Items.Add("Alle auswählen", null, (_, _) => List.SelectAll());
            return;
        }
        if (sel.Count == 1) _ctx.Items.Add("Eigenschaften …", null, (_, _) => ShowProperties(sel[0])).Font = new Font(Theme.Normal, FontStyle.Bold);
        _ctx.Items.Add("Exportieren …", null, (_, _) => ExportSelected());
        var quick = new ToolStripMenuItem("Schnell exportieren als");
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
            var copy = new ToolStripMenuItem("Kopieren nach");
            var move = new ToolStripMenuItem("Verschieben nach");
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
        _ctx.Items.Add("Umbenennen / Region ändern …", null, (_, _) => RenameEntry(sel[0])).Enabled = sel.Count == 1 && Card.CanWrite;
        _ctx.Items.Add("Löschen", null, (_, _) => DeleteSelected()).Enabled = Card.CanWrite;
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
            if (!Card.CanWrite) { MessageBox.Show(this, Card.ReadOnlyReason, "Nur lesen", MessageBoxButtons.OK, MessageBoxIcon.Information); }
            else ImportPaths(others);
        }
        if (cards.Count == 0) return;
        var menu = new ContextMenuStrip();
        Theme.StyleMenu(menu);
        string what = cards.Count == 1 ? $"„{Path.GetFileName(cards[0].TrimEnd('\\'))}“" : $"{cards.Count} Karten";
        menu.Items.Add(new ToolStripLabel($"{what} ist eine Memory Card:") { ForeColor = Theme.Dim });
        menu.Items.Add("Als eigene Karte öffnen (neues Segment)", null, (_, _) => _host.OpenCards(cards));
        menu.Items.Add("Savegames daraus in diese Karte importieren", null, (_, _) => ImportPaths(cards)).Enabled = Card.CanWrite;
        menu.Items.Add("Abbrechen");
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
            if (fi.Length > 4 * 1024 * 1024) return false;
            var all = File.ReadAllBytes(p);
            return Ps1Card.TryDetect(all, out _, out _);
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
        catch (Exception ex) { _host.SetStatus("Drag-Export fehlgeschlagen: " + ex.Message, true); }
        _host.SetStatus(items.Count == 1
            ? $"Ziehe „{items[0].Title}“ – auf eine andere Karte (Umschalt = verschieben) oder in den Explorer."
            : $"Ziehe {items.Count} Savegames – auf eine andere Karte (Umschalt = verschieben) oder in den Explorer.");
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
            Title = "Savegames importieren", Filter = CardIO.ImportFilter, Multiselect = true,
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
            Description = "Ordner mit einem PS2-Save (icon.sys …) oder mit Savegame-Dateien wählen", UseDescriptionForTitle = true,
            SelectedPath = _host.Settings.LastImportDir
        };
        if (fb.ShowDialog(this) != DialogResult.OK) return;
        _host.Settings.LastImportDir = fb.SelectedPath;
        ImportPaths(new List<string> { fb.SelectedPath });
    }

    public void ImportPaths(List<string> paths)
    {
        var saves = new List<SaveGame>();
        var warnings = new List<string>();
        Cursor = Cursors.WaitCursor;
        try
        {
            foreach (var p in paths)
            {
                try
                {
                    var loaded = SaveFormats.LoadAny(p, warnings);
                    if (loaded.Count == 0) warnings.Add($"{Path.GetFileName(p)}: keine Savegames gefunden.");
                    saves.AddRange(loaded);
                }
                catch (Exception ex) { warnings.Add($"{Path.GetFileName(p)}: {ex.Message}"); }
            }
        }
        finally { Cursor = Cursors.Default; }
        ImportSaves(saves, null, warnings);
    }

    enum Conflict { Overwrite, Skip, Cancel }

    Conflict AskConflict(SaveGame incoming, CardEntry existing, out bool forAll)
    {
        var overwrite = new TaskDialogButton("Ersetzen");
        var skip = new TaskDialogButton("Überspringen");
        var cancel = TaskDialogButton.Cancel;
        var page = new TaskDialogPage
        {
            Caption = "Savegame existiert bereits",
            Heading = $"„{existing.Name}“ ist bereits auf {Card.DisplayName}.",
            Text = $"Vorhanden:  {existing.Title}  ({Theme.FormatSize(existing.Size)}{(existing.Modified is { } m ? ", " + m.ToString("dd.MM.yyyy HH:mm") : "")})\n" +
                   $"Neu:            {incoming.Title}  ({Theme.FormatSize(incoming.Size)})\n\n" +
                   "Beim Ersetzen wird der vorhandene Save überschrieben. Vor dem Speichern der Karte wird automatisch ein Backup der Kartendatei angelegt.",
            Icon = TaskDialogIcon.Warning,
            Verification = new TaskDialogVerificationCheckBox("Für alle weiteren Konflikte übernehmen"),
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
            MessageBox.Show(this, Card.ReadOnlyReason, "Nur lesen", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return done;
        }
        int imported = 0, replaced = 0, identical = 0, skipped = 0;
        Conflict? all = null;
        var names = new List<string>();
        foreach (var save in saves)
        {
            if (save.Platform != Card.Platform)
            {
                errors.Add($"{save.Name}: {(save.Platform == Platform.PS1 ? "PS1" : "PS2")}-Savegame passt nicht auf eine {(Card.Platform == Platform.PS1 ? "PS1" : "PS2")}-Karte.");
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
                    errors.Add($"{save.Name}: nicht genug Platz zum Ersetzen.");
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
                errors.Add($"{save.Name} ({save.Title}): nicht genug Platz – benötigt {Theme.FormatSize(Card.SpaceNeeded(save))}, frei {Theme.FormatSize(Card.FreeBytes)}.");
                continue;
            }
            try
            {
                Card.Import(save.Clone());
                imported++;
                done.Add(save);
                names.Add(save.Name);
            }
            catch (Exception ex) { errors.Add($"{save.Name}: {ex.Message}"); }
        }
        AfterChange(names);
        var parts = new List<string>();
        if (imported > 0) parts.Add($"{imported} importiert");
        if (replaced > 0) parts.Add($"{replaced} ersetzt");
        if (identical > 0) parts.Add($"{identical} bereits identisch vorhanden");
        if (skipped > 0) parts.Add($"{skipped} übersprungen");
        if (errors.Count > 0) parts.Add($"{errors.Count} Fehler");
        _host.SetStatus($"Slot {Slot}: " + (parts.Count > 0 ? string.Join(", ", parts) : "nichts importiert") +
                        (Card.IsModified && !_host.Settings.AutoSave ? " – zum Übernehmen „Speichern“ klicken." : ""), errors.Count > 0);
        if (errors.Count > 0)
        {
            using var dlg = new TextReportDialog("Import – Hinweise", string.Join(Environment.NewLine, errors));
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
        using var fb = new FolderBrowserDialog { Description = "Zielordner für den Export", UseDescriptionForTitle = true, SelectedPath = _host.Settings.LastExportDir };
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
        _host.SetStatus($"{ok} Savegame(s) als {SaveFormats.Describe(fmt).Split(' ')[0]} nach {dir} exportiert." + (errors.Count > 0 ? $" {errors.Count} Fehler." : ""), errors.Count > 0);
        if (errors.Count > 0)
        {
            using var dlg = new TextReportDialog("Export – Fehler", string.Join(Environment.NewLine, errors));
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
        string what = sel.Count == 1 ? $"„{sel[0].Title}“ ({sel[0].Name})" : $"{sel.Count} Savegames";
        var res = MessageBox.Show(this,
            $"{what} von {Card.DisplayName} löschen?\n\nDie Kartendatei wird erst beim Speichern geändert; dabei wird automatisch ein Backup angelegt.",
            "Löschen", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
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
        _host.SetStatus($"Slot {Slot}: {n} Savegame(s) gelöscht" + (Card.IsModified && !_host.Settings.AutoSave ? " – zum Übernehmen „Speichern“ klicken." : "."));
    }

    void RenameEntry(CardEntry entry)
    {
        using var dlg = new RenameDialog(entry, n => Card.Exists(n));
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        SaveGame renamed;
        try { renamed = entry.Save.Renamed(dlg.NewName, dlg.RenameFiles); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Umbenennen", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

        if (dlg.KeepOriginal)
        {
            if (Card.SpaceNeeded(renamed) > Card.FreeBytes)
            {
                MessageBox.Show(this, "Nicht genug Platz für eine Kopie. Ohne „Original behalten“ umbenennen oder Platz schaffen.",
                    "Umbenennen", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try { Card.Import(renamed); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Umbenennen", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        }
        else
        {
            var old = entry.Save;
            Card.Delete(entry);
            try { Card.Import(renamed); }
            catch (Exception ex)
            {
                try { Card.Import(old); } catch { /* original file on disk is untouched until saving */ }
                MessageBox.Show(this, ex.Message, "Umbenennen", MessageBoxButtons.OK, MessageBoxIcon.Error);
                AfterChange();
                return;
            }
        }
        AfterChange(new[] { renamed.Name });
        _host.SetStatus($"„{entry.Name}“ → „{renamed.Name}“ ({RegionUtil.Display(renamed.Region)})" +
                        (dlg.KeepOriginal ? ", Original behalten" : "") + (Card.IsModified && !_host.Settings.AutoSave ? " – zum Übernehmen „Speichern“ klicken." : "."));
    }

    void ShowProperties(CardEntry e)
    {
        using var dlg = new PropertiesDialog(e, Card);
        dlg.ShowDialog(this);
    }

    void ShowWarnings()
    {
        using var dlg = new TextReportDialog("Hinweise zur Karte " + Card.DisplayName, string.Join(Environment.NewLine, Card.Warnings));
        dlg.ShowDialog(this);
    }

    void CheckCard()
    {
        var lines = new List<string> { Card.FormatName, Card.FilePath, "" };
        if (Card is Ps2Card p2)
        {
            var msgs = p2.Check();
            lines.Add(msgs.Count == 0 ? "Keine Fehler im Dateisystem gefunden." : "Gefundene Probleme:");
            lines.AddRange(msgs.Select(m => "  • " + m));
            if (msgs.Any(m => m.Contains("verlorene")))
                lines.Add("\nVerlorene Cluster sind harmlos (Platz, der keinem Save gehört). Sie werden nicht verändert.");
        }
        else lines.Add("Struktur gelesen, keine Fehler.");
        if (Card.Warnings.Count > 0) { lines.Add(""); lines.Add("Hinweise beim Laden:"); lines.AddRange(Card.Warnings.Select(w => "  • " + w)); }
        using var dlg = new TextReportDialog("Kartenprüfung – " + Card.DisplayName, string.Join(Environment.NewLine, lines));
        dlg.ShowDialog(this);
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
                $"{Card.DisplayName} wurde seit dem Laden außerhalb geändert (z. B. von PCSX2).\n\n" +
                "Ja = trotzdem speichern (die externe Version landet im Backup-Ordner)\nNein = Abbrechen (Tipp: „Änderungen verwerfen“ lädt die aktuelle Datei)",
                "Datei wurde verändert", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (r != DialogResult.Yes) return false;
        }
        try
        {
            Cursor = Cursors.WaitCursor;
            Card.Commit(Card.FilePath);
            Card.LoadedWriteTimeUtc = CurrentWriteTime();
            RefreshView();
            _host.SetStatus($"Slot {Slot}: {Card.DisplayName} gespeichert (Backup im Ordner „Backups“).");
            return true;
        }
        catch (Exception ex)
        {
            string hint = ex is IOException or UnauthorizedAccessException
                ? "\n\nIst die Datei noch in PCSX2/einem anderen Programm geöffnet? Die Originaldatei wurde NICHT verändert."
                : "\n\nDie Originaldatei wurde NICHT verändert.";
            MessageBox.Show(this, "Speichern fehlgeschlagen:\n" + ex.Message + hint, "Fehler", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _host.SetStatus("Speichern fehlgeschlagen: " + ex.Message, true);
            return false;
        }
        finally { Cursor = Cursors.Default; }
    }

    public bool SaveAs()
    {
        string filter = Card.Platform == Platform.PS2
            ? "PCSX2 (*.ps2)|*.ps2|PS3 VM2 (*.VM2)|*.VM2|OPL VMC ohne ECC (*.vmc)|*.vmc|MemCard PRO2 / SD2PSX (*.mc2)|*.mc2|Rohabbild (*.bin)|*.bin"
            : "PS1 Raw (*.mcr)|*.mcr|DuckStation (*.mcd)|*.mcd|PS3 VM1 (*.VM1)|*.VM1|DexDrive (*.gme)|*.gme|VGS (*.vgs)|*.vgs|RetroArch (*.srm)|*.srm|POPStarter (*.VMC)|*.VMC|PSP / PS Vita (*.VMP, signiert)|*.VMP";
        using var sfd = new SaveFileDialog
        {
            Title = "Karte speichern unter / konvertieren", Filter = filter,
            FileName = Path.GetFileNameWithoutExtension(Card.FilePath.TrimEnd('\\')) + "_kopie",
            InitialDirectory = Path.GetDirectoryName(Card.FilePath.TrimEnd('\\')) ?? ""
        };
        if (sfd.ShowDialog(this) != DialogResult.OK) return false;
        try
        {
            CardIO.SaveAs(Card, sfd.FileName);
            var r = MessageBox.Show(this, $"Gespeichert als:\n{sfd.FileName}\n\nDie neue Datei jetzt in diesem Segment öffnen?",
                "Speichern unter", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (r == DialogResult.Yes) ReplaceCard(CardIO.Open(sfd.FileName));
            _host.SetStatus("Gespeichert als " + sfd.FileName);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Speichern fehlgeschlagen:\n" + ex.Message, "Fehler", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    public void Revert()
    {
        if (Card.IsModified)
        {
            var r = MessageBox.Show(this, "Alle ungespeicherten Änderungen an dieser Karte verwerfen und die Datei neu laden?", "Verwerfen",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (r != DialogResult.Yes) return;
        }
        try
        {
            ReplaceCard(CardIO.Open(Card.FilePath));
            _host.SetStatus($"Slot {Slot}: neu geladen.");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Fehler", MessageBoxButtons.OK, MessageBoxIcon.Error); }
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
        var r = MessageBox.Show(this, $"{Card.DisplayName} hat ungespeicherte Änderungen. Speichern?", "Karte schließen",
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (r == DialogResult.Cancel) return false;
        if (r == DialogResult.Yes) return Save();
        return true;
    }
}
