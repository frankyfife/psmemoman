using System.Diagnostics;
using MemcardManager.Core;

namespace MemcardManager.UI;

public sealed class MainForm : Form, ICardHost
{
    public const int MaxCards = 4;

    readonly List<CardPanel> _panels = new();
    readonly Panel _content = new() { Dock = DockStyle.Fill, BackColor = Theme.Back, Padding = Dpi.S(new Padding(8)) };
    readonly EmptyView _empty;
    readonly MenuStrip _menu = new();
    readonly ToolStrip _toolbar = new() { GripStyle = ToolStripGripStyle.Hidden, Padding = Dpi.S(new Padding(8, 2, 8, 2)) };
    readonly StatusStrip _status = new() { SizingGrip = true };
    readonly ToolStripStatusLabel _statusText = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly ToolStripStatusLabel _statusCards = new() { TextAlign = ContentAlignment.MiddleRight };
    readonly System.Windows.Forms.Timer _anim = new() { Interval = 33 };
    readonly ToolStripMenuItem _recentMenu = new("Zuletzt geöffnet");
    readonly ToolStripMenuItem _autoSaveItem, _animateItem, _reopenItem;
    readonly ToolStripButton _tbSaveAll;

    public AppSettings Settings { get; } = AppSettings.Load();
    public IReadOnlyList<CardPanel> Panels => _panels;

    public MainForm(string[] args)
    {
        Text = "Memory Card Manager – PS1 / PS2";
        Icon = Program.AppIcon;
        BackColor = Theme.Back;
        ForeColor = Theme.Text;
        Font = Theme.Normal;
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = Dpi.S(new Size(760, 520));
        KeyPreview = true;

        // ------------------------------------------------ menu
        _menu.Renderer = Theme.ToolRenderer;
        _menu.BackColor = Theme.Header;
        _menu.ForeColor = Theme.Text;
        var file = new ToolStripMenuItem("&Datei");
        file.DropDownItems.Add(new ToolStripMenuItem("Neue Karte erstellen …", null, (_, _) => NewCard(), Keys.Control | Keys.N));
        file.DropDownItems.Add(new ToolStripMenuItem("Karte öffnen …", null, (_, _) => OpenDialog(), Keys.Control | Keys.O));
        file.DropDownItems.Add(new ToolStripMenuItem("PCSX2-Ordner-Karte öffnen …", null, (_, _) => OpenFolderDialog()));
        file.DropDownItems.Add(_recentMenu);
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(new ToolStripMenuItem("Alle Karten speichern", null, (_, _) => SaveAll(), Keys.Control | Keys.S));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(new ToolStripMenuItem("Backup-Ordner öffnen", null, (_, _) => OpenFolder(CardIO.BackupDir)));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(new ToolStripMenuItem("Beenden", null, (_, _) => Close(), Keys.Alt | Keys.F4));
        file.DropDownOpening += (_, _) => BuildRecentMenu();

        var opts = new ToolStripMenuItem("&Optionen");
        _autoSaveItem = new ToolStripMenuItem("Änderungen sofort speichern (Auto-Speichern)", null, (_, _) => { Settings.AutoSave = !Settings.AutoSave; SyncOptions(); })
        { ToolTipText = "Jede Änderung wird sofort mit Backup in die Kartendatei geschrieben." };
        _animateItem = new ToolStripMenuItem("Icons animieren", null, (_, _) => { Settings.AnimateIcons = !Settings.AnimateIcons; SyncOptions(); });
        _reopenItem = new ToolStripMenuItem("Karten beim Start wieder öffnen", null, (_, _) => { Settings.ReopenCards = !Settings.ReopenCards; SyncOptions(); });
        opts.DropDownItems.AddRange(new ToolStripItem[] { _autoSaveItem, _animateItem, _reopenItem });

        var help = new ToolStripMenuItem("&Hilfe");
        help.DropDownItems.Add("Bedienung …", null, (_, _) => ShowHelp());
        help.DropDownItems.Add("Über …", null, (_, _) => ShowAbout());

        _menu.Items.AddRange(new ToolStripItem[] { file, opts, help });
        foreach (ToolStripMenuItem m in _menu.Items) Theme.StyleMenu(m.DropDown);
        Theme.StyleMenu(_recentMenu.DropDown);

        // ------------------------------------------------ toolbar
        _toolbar.Renderer = Theme.ToolRenderer;
        _toolbar.BackColor = Theme.Header;
        _toolbar.Items.Add(new ToolStripButton("＋ Neue Karte", null, (_, _) => NewCard()) { ToolTipText = "Neue, formatierte PS1- oder PS2-Karte anlegen (Strg+N)" });
        _toolbar.Items.Add(new ToolStripButton("📂 Karte öffnen", null, (_, _) => OpenDialog()) { ToolTipText = "Memory Card öffnen (Strg+O) – oder einfach ins Fenster ziehen" });
        _toolbar.Items.Add(new ToolStripButton("🗀 Ordner-Karte", null, (_, _) => OpenFolderDialog()) { ToolTipText = "PCSX2-Ordner-Memory-Card öffnen" });
        _toolbar.Items.Add(new ToolStripSeparator());
        _tbSaveAll = new ToolStripButton("💾 Alle speichern", null, (_, _) => SaveAll()) { ToolTipText = "Alle geänderten Karten speichern (Strg+S)" };
        _toolbar.Items.Add(_tbSaveAll);
        _toolbar.Items.Add(new ToolStripSeparator());
        _toolbar.Items.Add(new ToolStripLabel("Tipp: Saves zwischen Karten ziehen = kopieren, mit Umschalt = verschieben") { ForeColor = Theme.Dim });

        // ------------------------------------------------ status
        _status.Renderer = Theme.ToolRenderer;
        _status.BackColor = Theme.Header;
        _statusText.ForeColor = Theme.Text;
        _statusCards.ForeColor = Theme.Dim;
        _status.Items.AddRange(new ToolStripItem[] { _statusText, _statusCards });

        // ------------------------------------------------ content
        _empty = new EmptyView(this) { Dock = DockStyle.Fill };
        _content.Controls.Add(_empty);
        _content.Resize += (_, _) => LayoutPanels();
        WireDrop(_content);
        WireDrop(_empty);

        Controls.Add(_content);
        Controls.Add(_toolbar);
        Controls.Add(_menu);
        Controls.Add(_status);
        MainMenuStrip = _menu;

        _anim.Tick += (_, _) =>
        {
            if (!SaveListView.Animate || WindowState == FormWindowState.Minimized) return;
            foreach (var p in _panels) p.List.Tick();
        };
        _anim.Start();

        // window placement
        if (Settings.WindowW > 400 && Settings.WindowH > 300) Size = new Size(Settings.WindowW, Settings.WindowH);
        else { var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1600, 1000); Size = new Size(Math.Min(wa.Width, Math.Max(MinimumSize.Width, wa.Width * 3 / 4)), Math.Min(wa.Height, Math.Max(MinimumSize.Height, wa.Height * 4 / 5))); }
        if (Settings.WindowX >= 0 && Settings.WindowY >= 0 && Screen.AllScreens.Any(s => s.WorkingArea.Contains(Settings.WindowX + 50, Settings.WindowY + 20)))
        {
            StartPosition = FormStartPosition.Manual;
            Location = new Point(Settings.WindowX, Settings.WindowY);
        }
        else StartPosition = FormStartPosition.CenterScreen;
        if (Settings.Maximized) WindowState = FormWindowState.Maximized;

        SyncOptions();
        Shown += (_, _) =>
        {
            var toOpen = new List<string>();
            if (Settings.ReopenCards) toOpen.AddRange(Settings.OpenCards.Where(p => File.Exists(p) || Directory.Exists(p)));
            toOpen.AddRange(args.Where(a => File.Exists(a) || Directory.Exists(a)));
            var cards = toOpen.Where(CardPanel.IsCardPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (cards.Count > 0) OpenCards(cards);
            SetStatus(_panels.Count == 0 ? "Bereit – Memory Card ins Fenster ziehen oder öffnen." : $"{_panels.Count} Karte(n) geladen.");
            CleanupTemp();
        };
        UpdateChrome();
    }

    // ================================================================== ICardHost

    public void SetStatus(string text, bool error = false)
    {
        _statusText.Text = text;
        _statusText.ForeColor = error ? Theme.Danger : Theme.Text;
    }

    public void ClosePanel(CardPanel panel)
    {
        if (!panel.ConfirmClose()) return;
        _panels.Remove(panel);
        _content.Controls.Remove(panel);
        panel.Dispose();
        Renumber();
        LayoutPanels();
        UpdateChrome();
        SetStatus($"Karte geschlossen – {_panels.Count} Karte(n) geladen.");
    }

    public void OpenCards(IEnumerable<string> paths)
    {
        var errors = new List<string>();
        foreach (var raw in paths)
        {
            string path = Path.GetFullPath(raw.TrimEnd('\\', '/'));
            var already = _panels.FirstOrDefault(p => string.Equals(Path.GetFullPath(p.Card.FilePath.TrimEnd('\\', '/')), path, StringComparison.OrdinalIgnoreCase));
            if (already != null)
            {
                already.List.Focus();
                SetStatus($"{Path.GetFileName(path)} ist bereits in Slot {already.Slot} geöffnet.");
                continue;
            }
            if (_panels.Count >= MaxCards)
            {
                errors.Add($"{Path.GetFileName(path)}: Es können maximal {MaxCards} Karten gleichzeitig geöffnet sein. Bitte zuerst eine Karte schließen (✕).");
                continue;
            }
            try
            {
                var card = CardIO.Open(path);
                var panel = new CardPanel(this, card);
                _panels.Add(panel);
                _content.Controls.Add(panel);
                Settings.AddRecent(path);
                Settings.LastCardDir = Path.GetDirectoryName(path) ?? "";
                SetStatus($"{card.DisplayName} geöffnet: {card.Entries.Count} Savegames.");
            }
            catch (Exception ex) { errors.Add($"{Path.GetFileName(path)}: {ex.Message}"); }
        }
        Renumber();
        LayoutPanels();
        UpdateChrome();
        if (errors.Count > 0)
            MessageBox.Show(this, string.Join("\n\n", errors), "Karte öffnen", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    public void Transfer(CardPanel source, CardPanel target, List<CardEntry> entries, bool move)
    {
        if (source == target) return;
        if (move && !source.Card.CanWrite) move = false;
        var saves = entries.Select(e => e.Save).ToList();
        var done = target.ImportSaves(saves, source.Card.DisplayName);
        if (move && done.Count > 0)
        {
            var names = done.Select(d => d.Name).ToHashSet();
            source.DeleteEntries(source.Card.Entries.Where(e => names.Contains(e.Name)));
            SetStatus($"{done.Count} Savegame(s) von Slot {source.Slot} nach Slot {target.Slot} verschoben" +
                      (Settings.AutoSave ? "." : " – zum Übernehmen beide Karten speichern."));
        }
    }

    public void AfterChange(CardPanel panel)
    {
        if (Settings.AutoSave && panel.Card.IsModified && panel.Card.CanWrite) panel.Save(true);
        UpdateChrome();
    }

    // ================================================================== layout

    void Renumber()
    {
        for (int i = 0; i < _panels.Count; i++)
        {
            _panels[i].Slot = i + 1;
            _panels[i].RefreshView();
        }
    }

    void LayoutPanels()
    {
        _empty.Visible = _panels.Count == 0;
        if (_panels.Count == 0) return;
        var area = _content.DisplayRectangle;
        int gap = Dpi.S(8);
        var rects = new List<Rectangle>();
        int n = _panels.Count;
        int w = area.Width, h = area.Height;
        if (n == 1) rects.Add(area);
        else if (n == 2)
        {
            if (w >= h * 1.1)
            {
                int cw = (w - gap) / 2;
                rects.Add(new Rectangle(area.X, area.Y, cw, h));
                rects.Add(new Rectangle(area.X + cw + gap, area.Y, w - cw - gap, h));
            }
            else
            {
                int rh = (h - gap) / 2;
                rects.Add(new Rectangle(area.X, area.Y, w, rh));
                rects.Add(new Rectangle(area.X, area.Y + rh + gap, w, h - rh - gap));
            }
        }
        else if (n == 3 && w >= Dpi.S(1500))
        {
            int cw = (w - 2 * gap) / 3;
            for (int i = 0; i < 3; i++) rects.Add(new Rectangle(area.X + i * (cw + gap), area.Y, i == 2 ? w - 2 * (cw + gap) : cw, h));
        }
        else
        {
            int cw = (w - gap) / 2, rh = (h - gap) / 2;
            rects.Add(new Rectangle(area.X, area.Y, cw, rh));
            rects.Add(new Rectangle(area.X + cw + gap, area.Y, w - cw - gap, rh));
            if (n == 3) rects.Add(new Rectangle(area.X, area.Y + rh + gap, w, h - rh - gap));
            else
            {
                rects.Add(new Rectangle(area.X, area.Y + rh + gap, cw, h - rh - gap));
                rects.Add(new Rectangle(area.X + cw + gap, area.Y + rh + gap, w - cw - gap, h - rh - gap));
            }
        }
        _content.SuspendLayout();
        for (int i = 0; i < n; i++) _panels[i].Bounds = rects[i];
        _content.ResumeLayout();
    }

    void UpdateChrome()
    {
        int modified = _panels.Count(p => p.Card.IsModified);
        _tbSaveAll.Enabled = modified > 0;
        _statusCards.Text = $"{_panels.Count}/{MaxCards} Karten" + (modified > 0 ? $" · {modified} ungespeichert" : "");
        Text = "Memory Card Manager – PS1 / PS2" + (modified > 0 ? " *" : "");
    }

    void SyncOptions()
    {
        _autoSaveItem.Checked = Settings.AutoSave;
        _animateItem.Checked = Settings.AnimateIcons;
        _reopenItem.Checked = Settings.ReopenCards;
        SaveListView.Animate = Settings.AnimateIcons;
        foreach (var p in _panels) p.List.Invalidate();
        if (Settings.AutoSave) foreach (var p in _panels.Where(p => p.Card.IsModified && p.Card.CanWrite)) p.Save(true);
        UpdateChrome();
    }

    // ================================================================== drag & drop onto the window

    void WireDrop(Control c)
    {
        c.AllowDrop = true;
        c.DragEnter += OnDragOver;
        c.DragOver += OnDragOver;
        c.DragDrop += (_, e) =>
        {
            if (e.Data?.GetDataPresent(DragPayload.Format) == true) return;
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths)
                BeginInvoke(() => HandleWindowDrop(paths));
        };
    }

    void OnDragOver(object? sender, DragEventArgs e)
    {
        e.Effect = e.Data?.GetDataPresent(DragPayload.Format) != true && e.Data?.GetDataPresent(DataFormats.FileDrop) == true
            ? DragDropEffects.Copy : DragDropEffects.None;
    }

    void HandleWindowDrop(string[] paths)
    {
        var cards = paths.Where(CardPanel.IsCardPath).ToList();
        var others = paths.Except(cards).ToList();
        if (cards.Count > 0) OpenCards(cards);
        if (others.Count == 0) return;
        if (_panels.Count == 1 && _panels[0].Card.CanWrite)
        {
            _panels[0].ImportPaths(others);
            return;
        }
        MessageBox.Show(this,
            _panels.Count == 0
                ? "Das sind Savegame-Dateien, keine Memory Cards.\nÖffne oder erstelle zuerst eine Karte und ziehe die Saves dann auf diese Karte."
                : "Zum Importieren die Savegames direkt auf die gewünschte Karte (Segment) ziehen.",
            "Importieren", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ================================================================== commands

    string DefaultCardDir()
    {
        if (Directory.Exists(Settings.LastCardDir)) return Settings.LastCardDir;
        // the program normally lives in ...\memcards\Files → default to ...\memcards
        var parent = Directory.GetParent(AppContext.BaseDirectory.TrimEnd('\\'));
        return parent?.FullName ?? "";
    }

    void OpenDialog()
    {
        using var ofd = new OpenFileDialog
        {
            Title = "Memory Card öffnen", Filter = CardIO.OpenFilter, Multiselect = true, InitialDirectory = DefaultCardDir()
        };
        if (ofd.ShowDialog(this) == DialogResult.OK) OpenCards(ofd.FileNames);
    }

    void OpenFolderDialog()
    {
        using var fb = new FolderBrowserDialog
        {
            Description = "PCSX2-Ordner-Memory-Card wählen (Ordner mit _pcsx2_superblock)", UseDescriptionForTitle = true,
            SelectedPath = DefaultCardDir()
        };
        if (fb.ShowDialog(this) == DialogResult.OK) OpenCards(new[] { fb.SelectedPath });
    }

    void NewCard()
    {
        if (_panels.Count >= MaxCards)
        {
            MessageBox.Show(this, $"Es sind bereits {MaxCards} Karten geöffnet. Bitte zuerst eine schließen.", "Neue Karte", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dlg = new NewCardDialog();
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var type = dlg.Selected;
        var (ext, filter) = CardIO.NewCardFile(type);
        using var sfd = new SaveFileDialog
        {
            Title = "Neue Karte speichern als", Filter = filter, DefaultExt = ext.TrimStart('.'), InitialDirectory = DefaultCardDir(),
            FileName = SuggestNewName(ext), OverwritePrompt = true
        };
        if (sfd.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var bytes = CardIO.CreateNew(type);
            CardIO.SafeWrite(sfd.FileName, bytes, b =>
            {
                if (CardIO.TryOpenBytes(b, sfd.FileName) == null) throw new InvalidDataException("Neue Karte konnte nicht verifiziert werden.");
            });
            OpenCards(new[] { sfd.FileName });
            SetStatus($"Neue Karte erstellt: {sfd.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Karte konnte nicht erstellt werden:\n" + ex.Message, "Fehler", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    string SuggestNewName(string ext)
    {
        string dir = DefaultCardDir();
        if (ext.Equals(".VMP", StringComparison.OrdinalIgnoreCase) && !File.Exists(Path.Combine(dir, "SCEVMC0.VMP"))) return "SCEVMC0.VMP";
        for (int i = 1; i < 100; i++)
        {
            string n = $"Mcd{i:000}{ext}";
            if (!File.Exists(Path.Combine(dir, n))) return n;
        }
        return "Neue_Karte" + ext;
    }

    void SaveAll()
    {
        int ok = 0;
        foreach (var p in _panels.Where(p => p.Card.IsModified))
            if (p.Save()) ok++;
        UpdateChrome();
        if (ok > 0) SetStatus($"{ok} Karte(n) gespeichert.");
    }

    void BuildRecentMenu()
    {
        _recentMenu.DropDownItems.Clear();
        foreach (var r in Settings.RecentCards.Where(p => File.Exists(p) || Directory.Exists(p)))
        {
            var path = r;
            _recentMenu.DropDownItems.Add(path, null, (_, _) => OpenCards(new[] { path }));
        }
        if (_recentMenu.DropDownItems.Count == 0) _recentMenu.DropDownItems.Add(new ToolStripMenuItem("(leer)") { Enabled = false });
    }

    static void OpenFolder(string dir)
    {
        try { Process.Start("explorer.exe", $"\"{dir}\""); } catch { }
    }

    void ShowHelp()
    {
        string text = """
            MEMORY CARD MANAGER – BEDIENUNG

            Karten
              • Bis zu 4 Karten gleichzeitig. Jede geladene Karte bekommt ein eigenes Segment,
                leere Segmente werden nicht angezeigt. ✕ schließt eine Karte.
              • Öffnen: Karte(n) ins Fenster ziehen, Datei > Karte öffnen, oder Doppelklick auf
                die Datei (wenn mit dem Programm verknüpft).
              • Neue Karte: PS2 (PCSX2 .ps2 8–64 MB, PS3 .VM2, OPL .vmc / MemCard PRO2 .mc2)
                oder PS1 (.mcr/.mcd/.srm, PS3 .VM1, DexDrive .gme, VGS .vgs).
              • „Speichern unter“ konvertiert Karten (z. B. PCSX2 .ps2 ⇄ PS3 .VM2 ⇄ OPL .vmc,
                PS1 .mcr ⇄ .gme ⇄ .VM1).

            Drag & Drop
              • Save auf eine andere Karte ziehen = kopieren, mit Umschalt = verschieben.
              • Save in den Explorer ziehen = Export (Format wie zuletzt beim Export gewählt).
              • Dateien aus dem Explorer auf eine Karte ziehen = importieren
                (PSU, MAX, CBS, SPS, XPS, PSV, ZIP, Save-Ordner, MCS, PSX/MCB/MCX/PDA, RAW).
              • Eine komplette Karte auf ein Segment ziehen: öffnen oder alle Saves übernehmen.

            Sicherheit
              • Alle Änderungen passieren zuerst nur im Speicher (gelber Balken).
                Erst „Speichern“ schreibt die Datei – vorher wird das neue Abbild geprüft,
                in eine Temp-Datei geschrieben, zurückgelesen und erst dann ersetzt.
              • Vor jedem Überschreiben landet die alte Datei im Ordner „Backups“.
              • Exporte überschreiben nie vorhandene Dateien.
              • Bei Ordner-Karten werden gelöschte Saves in den Backup-Ordner verschoben.
              • Tipp: Karte in PCSX2 vorher auswerfen bzw. PCSX2 schließen.

            Tasten
              Strg+N neue Karte · Strg+O öffnen · Strg+S alle speichern · Entf löschen
              Strg+A alle auswählen · Enter Eigenschaften
            """;
        using var dlg = new TextReportDialog("Bedienung", text);
        dlg.ShowDialog(this);
    }

    void ShowAbout()
    {
        MessageBox.Show(this,
            "Memory Card Manager für PS1 & PS2 – Version " + Application.ProductVersion.Split('+')[0] + "\n\n" +
            "PS2: PCSX2 (.ps2, Datei & Ordner), PS3 (.VM2), OPL (.vmc), MemCard PRO2/SD2PSX (.mc2), Rohabbild (.bin), 8–64 MB, mit/ohne ECC\n" +
            "PS1: .mcr .mcd .mc .srm .gme .vgs .mem .vmp (lesen) .VM1 .ddf .mci .psm …\n" +
            "Saves: PSU, MAX, CBS, SPS/XPS, PSV, ZIP, Ordner, MCS, PSX/MCB/MCX/PDA, RAW\n\n" +
            "Formatwissen u. a. nach mymc (Ross Ridge, Public Domain) / dart_mymc, LZARI nach Haruhiko Okumura.",
            "Über", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    static void CleanupTemp()
    {
        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "MemcardManager", "drag");
            if (!Directory.Exists(dir)) return;
            foreach (var d in Directory.GetDirectories(dir))
                if (Directory.GetCreationTimeUtc(d) < DateTime.UtcNow.AddHours(-12)) Directory.Delete(d, true);
        }
        catch { }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        var modified = _panels.Where(p => p.Card.IsModified).ToList();
        if (modified.Count > 0)
        {
            var r = MessageBox.Show(this,
                "Ungespeicherte Änderungen in:\n" + string.Join("\n", modified.Select(p => $"  Slot {p.Slot}: {p.Card.DisplayName}")) +
                "\n\nJetzt speichern?", "Beenden", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) { e.Cancel = true; return; }
            if (r == DialogResult.Yes)
                foreach (var p in modified)
                    if (!p.Save()) { e.Cancel = true; return; }
        }
        Settings.OpenCards = _panels.Select(p => p.Card.FilePath).ToList();
        Settings.Maximized = WindowState == FormWindowState.Maximized;
        var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        Settings.WindowX = b.X; Settings.WindowY = b.Y; Settings.WindowW = b.Width; Settings.WindowH = b.Height;
        Settings.Save();
        base.OnFormClosing(e);
    }

    /// <summary>Shown when no card is loaded: a big drop target.</summary>
    sealed class EmptyView : Control
    {
        readonly MainForm _owner;
        readonly Button _open, _new;

        public EmptyView(MainForm owner)
        {
            _owner = owner;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Back;
            _open = Make("📂  Karte öffnen …", (_, _) => _owner.OpenDialog());
            _new = Make("＋  Neue Karte erstellen …", (_, _) => _owner.NewCard());
            Controls.Add(_open);
            Controls.Add(_new);
        }

        static Button Make(string t, EventHandler click)
        {
            var b = new Button { Text = t, FlatStyle = FlatStyle.Flat, BackColor = Theme.Header, ForeColor = Theme.Text, Size = Dpi.S(new Size(240, 44)), Font = Theme.Bold };
            b.FlatAppearance.BorderColor = Theme.Accent;
            b.Click += click;
            return b;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            int cx = Width / 2, y = Height / 2 + Dpi.S(96);
            _open.Location = new Point(cx - _open.Width - Dpi.S(8), y);
            _new.Location = new Point(cx + Dpi.S(8), y);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var r = ClientRectangle;
            r.Inflate(-Dpi.S(24), -Dpi.S(24));
            using (var pen = new Pen(Theme.Border, 2) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash })
                g.DrawRectangle(pen, r);
            int cx = Width / 2, cy = Height / 2;
            CardArt.DrawCard(g, new RectangleF(cx - Dpi.S(110), cy - Dpi.S(150), Dpi.S(100), Dpi.S(120)), true);
            CardArt.DrawCard(g, new RectangleF(cx + Dpi.S(10), cy - Dpi.S(150), Dpi.S(100), Dpi.S(120)), false);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
            TextRenderer.DrawText(g, "Noch keine Memory Card geladen", Theme.Big, new Rectangle(0, cy - Dpi.S(20), Width, Theme.Big.Height + Dpi.S(4)), Theme.Text, TextFormatFlags.HorizontalCenter);
            TextRenderer.DrawText(g, "Karten hierher ziehen (.ps2 .VM2 .vmc .mc2 .mcr .mcd .gme .VM1 … oder PCSX2-Ordnerkarten) – bis zu 4 gleichzeitig",
                Theme.Normal, new Rectangle(0, cy + Dpi.S(26), Width, Theme.Normal.Height + Dpi.S(4)), Theme.Dim, TextFormatFlags.HorizontalCenter);
            TextRenderer.DrawText(g, "Savegames zwischen Karten ziehen = kopieren · mit Umschalt = verschieben · in den Explorer ziehen = exportieren",
                Theme.Small, new Rectangle(0, cy + Dpi.S(54), Width, Theme.Small.Height + Dpi.S(4)), Theme.Dim, TextFormatFlags.HorizontalCenter);
        }
    }
}
