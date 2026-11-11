using System.Diagnostics;
using MemcardManager.Core;

namespace MemcardManager.UI;

/// <summary>Common base with the dark theme.</summary>
public class DarkForm : Form
{
    public DarkForm()
    {
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Font = Theme.Normal;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        AutoScaleMode = AutoScaleMode.Dpi;
        Icon = Program.AppIcon;
    }

    protected static Button MakeButton(string text, DialogResult result = DialogResult.None)
    {
        var b = new Button
        {
            Text = text, DialogResult = result, FlatStyle = FlatStyle.Flat, BackColor = Theme.Header, ForeColor = Theme.Text,
            AutoSize = true, MinimumSize = Dpi.S(new Size(96, 30)), Padding = Dpi.S(new Padding(8, 2, 8, 2)), Margin = Dpi.S(new Padding(6, 0, 0, 0))
        };
        b.FlatAppearance.BorderColor = Theme.Border;
        return b;
    }

    protected static FlowLayoutPanel ButtonRow(params Button[] buttons)
    {
        var p = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = Dpi.S(48), Padding = Dpi.S(new Padding(10, 9, 10, 9)),
            BackColor = Theme.Header
        };
        foreach (var b in buttons.Reverse()) p.Controls.Add(b);
        return p;
    }
}

public sealed class NewCardDialog : DarkForm
{
    readonly ListBox _list = new()
    {
        Dock = DockStyle.Fill, BackColor = Theme.Row, ForeColor = Theme.Text, BorderStyle = BorderStyle.None,
        IntegralHeight = false, ItemHeight = Dpi.S(30), DrawMode = DrawMode.OwnerDrawFixed
    };

    public NewCardType Selected => (NewCardType)_list.SelectedIndex;

    public NewCardDialog()
    {
        Text = L.T("Neue Memory Card erstellen", "Create new memory card");
        ClientSize = Dpi.S(new Size(620, 420));
        var hint = new Label
        {
            Dock = DockStyle.Top, Height = Dpi.S(56), Padding = Dpi.S(new Padding(12, 10, 12, 4)), ForeColor = Theme.Dim,
            Text = L.T("Kartentyp wählen. Die Karte wird fertig formatiert angelegt und direkt geöffnet.\n" +
                       "PS2: PCSX2 nutzt .ps2 (8 MB Standard), PS3 nutzt .VM2, OPL nutzt .vmc. PS1: .mcr/.mcd für Emulatoren, .VM1 für die PS3.",
                       "Choose the card type. The card is created ready formatted and opened right away.\n" +
                       "PS2: PCSX2 uses .ps2 (8 MB standard), PS3 uses .VM2, OPL uses .vmc. PS1: .mcr/.mcd for emulators, .VM1 for the PS3.")
        };
        foreach (NewCardType t in Enum.GetValues<NewCardType>()) _list.Items.Add(CardIO.Describe(t));
        _list.SelectedIndex = 0;
        _list.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using var b = new SolidBrush(sel ? Theme.SelectedFocus : e.Index % 2 == 0 ? Theme.Row : Theme.RowAlt);
            e.Graphics.FillRectangle(b, e.Bounds);
            bool ps2 = ((NewCardType)e.Index).ToString().StartsWith("Ps2");
            CardArt.DrawCard(e.Graphics, new RectangleF(e.Bounds.X + Dpi.S(6), e.Bounds.Y + Dpi.S(3), Dpi.S(22), e.Bounds.Height - Dpi.S(6)), ps2);
            TextRenderer.DrawText(e.Graphics, _list.Items[e.Index]!.ToString(), Theme.Normal,
                new Rectangle(e.Bounds.X + Dpi.S(36), e.Bounds.Y, e.Bounds.Width - Dpi.S(40), e.Bounds.Height), Theme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        };
        _list.DoubleClick += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        var host = new Panel { Dock = DockStyle.Fill, Padding = Dpi.S(new Padding(12, 0, 12, 12)) };
        host.Controls.Add(_list);
        var ok = MakeButton(L.T("Weiter …", "Next …"), DialogResult.OK);
        var cancel = MakeButton(L.T("Abbrechen", "Cancel"), DialogResult.Cancel);
        Controls.Add(host);
        Controls.Add(hint);
        Controls.Add(ButtonRow(ok, cancel));
        AcceptButton = ok;
        CancelButton = cancel;
    }
}

public sealed class ExportDialog : DarkForm
{
    readonly ComboBox _format = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = Dpi.S(420), FlatStyle = FlatStyle.Flat, BackColor = Theme.Row, ForeColor = Theme.Text };
    readonly TextBox _dir = new() { Width = Dpi.S(340), BackColor = Theme.Row, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle };
    readonly CheckBox _open = new() { Text = L.T("Zielordner nach dem Export öffnen", "Open target folder after export"), AutoSize = true, Checked = true };
    readonly ExportFormat[] _formats;

    public ExportFormat Format => _formats[_format.SelectedIndex];
    public string TargetDir => _dir.Text.Trim();
    public bool OpenAfter => _open.Checked;

    public ExportDialog(Platform platform, int count, string lastDir, int lastFormat)
    {
        Text = count == 1 ? L.T("Savegame exportieren", "Export save") : L.T($"{count} Savegames exportieren", $"Export {count} saves");
        ClientSize = Dpi.S(new Size(560, 230));
        _formats = SaveFormats.FormatsFor(platform);
        foreach (var f in _formats) _format.Items.Add(SaveFormats.Describe(f));
        _format.SelectedIndex = Math.Clamp(lastFormat, 0, _formats.Length - 1);
        _dir.Text = Directory.Exists(lastDir) ? lastDir : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var browse = MakeButton(L.T("Durchsuchen …", "Browse …"));
        browse.Margin = Dpi.S(new Padding(6, 0, 0, 0));
        browse.Click += (_, _) =>
        {
            using var fb = new FolderBrowserDialog { SelectedPath = _dir.Text, Description = L.T("Zielordner für den Export", "Target folder for the export"), UseDescriptionForTitle = true };
            if (fb.ShowDialog(this) == DialogResult.OK) _dir.Text = fb.SelectedPath;
        };
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = Dpi.S(new Padding(14)), ColumnCount = 2, RowCount = 4 };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Dpi.S(90)));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.Controls.Add(new Label { Text = "Format:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        table.Controls.Add(_format, 1, 0);
        table.Controls.Add(new Label { Text = L.T("Zielordner:", "Target folder:"), AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Dpi.S(new Padding(0)) };
        row.Controls.Add(_dir);
        row.Controls.Add(browse);
        table.Controls.Add(row, 1, 1);
        table.Controls.Add(_open, 1, 2);
        table.Controls.Add(new Label
        {
            Text = L.T("Vorhandene Dateien werden nie überschrieben – es wird ein neuer Name (2), (3) … gewählt.", "Existing files are never overwritten – a new name (2), (3) … is chosen."),
            AutoSize = true, ForeColor = Theme.Dim, MaximumSize = Dpi.S(new Size(440, 0))
        }, 1, 3);
        var ok = MakeButton(L.T("Exportieren", "Export"), DialogResult.OK);
        var cancel = MakeButton(L.T("Abbrechen", "Cancel"), DialogResult.Cancel);
        Controls.Add(table);
        Controls.Add(ButtonRow(ok, cancel));
        AcceptButton = ok;
        CancelButton = cancel;
        FormClosing += (_, e) =>
        {
            if (DialogResult == DialogResult.OK && string.IsNullOrWhiteSpace(TargetDir))
            {
                MessageBox.Show(this, L.T("Bitte einen Zielordner wählen.", "Please choose a target folder."), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                e.Cancel = true;
            }
        };
    }
}

/// <summary>Details of a save with a large animated icon.</summary>
public sealed class PropertiesDialog : DarkForm
{
    readonly IconFrames? _icon;
    readonly PictureBox _pic = new() { Size = Dpi.S(new Size(160, 160)), BackColor = Theme.Back };
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 40 };
    readonly Stopwatch _sw = Stopwatch.StartNew();

    public PropertiesDialog(CardEntry entry, MemoryCard card)
    {
        Text = L.T("Eigenschaften – ", "Properties – ") + entry.Name;
        ClientSize = Dpi.S(new Size(640, 470));
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = Dpi.S(new Size(560, 400));
        _icon = IconCache.GetNow(entry.Save, Dpi.S(160));
        _pic.Paint += (_, e) =>
        {
            e.Graphics.InterpolationMode = entry.Platform == Platform.PS1
                ? System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor
                : System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            if (_icon != null) e.Graphics.DrawImage(_icon.FrameAt(_sw.Elapsed.TotalSeconds), new Rectangle(0, 0, Dpi.S(160), Dpi.S(160)));
            else CardArt.DrawCard(e.Graphics, new RectangleF(Dpi.S(30), Dpi.S(20), Dpi.S(100), Dpi.S(120)), entry.Platform == Platform.PS2);
        };
        _timer.Tick += (_, _) => _pic.Invalidate();
        if (_icon?.Animated == true) _timer.Start();

        var s = entry.Save;
        var lines = new List<(string, string)>
        {
            (L.T("Titel", "Title"), s.Title),
            ("Name", s.Name),
            (L.T("Produktcode", "Product code"), RegionUtil.ProductCode(s.Name) is { Length: > 0 } pc ? pc : "–"),
            ("Region", RegionUtil.Display(s.Region)),
            ("System", s.Platform == Platform.PS2 ? "PlayStation 2" : "PlayStation"),
            (L.T("Größe", "Size"), s.Platform == Platform.PS1
                ? L.T($"{entry.Blocks} Blöcke ({Theme.FormatSize(entry.Size)})", $"{entry.Blocks} blocks ({Theme.FormatSize(entry.Size)})")
                : L.T($"{Theme.FormatSize(entry.Size)} auf der Karte ({Theme.FormatSize(s.Size)} Daten)", $"{Theme.FormatSize(entry.Size)} on the card ({Theme.FormatSize(s.Size)} data)")),
        };
        if (s.Ps2 != null)
        {
            lines.Add((L.T("Erstellt", "Created"), s.Ps2.Created.ToLocal()?.ToString("dd.MM.yyyy HH:mm:ss") ?? "–"));
            lines.Add((L.T("Geändert", "Modified"), s.Ps2.Modified.ToLocal()?.ToString("dd.MM.yyyy HH:mm:ss") ?? "–"));
            lines.Add((L.T("Attribute", "Attributes"), $"0x{s.Ps2.Mode:X4}" + ((s.Ps2.Mode & Ps2DirEntry.DfProtected) != 0 ? L.T(" (kopiergeschützt)", " (copy protected)") : "")));
            if (s.Ps2.Warnings.Count > 0) lines.Add((L.T("Hinweise", "Notes"), string.Join(" ", s.Ps2.Warnings)));
        }
        lines.Add((L.T("Karte", "Card"), card.DisplayName));

        int keyW = lines.Max(l => TextRenderer.MeasureText(l.Item1 + ":", Theme.Normal).Width) + Dpi.S(14);
        int infoX = Dpi.S(14) + _pic.Width + Dpi.S(18);
        int valW = ClientSize.Width - infoX - keyW - Dpi.S(20);
        var info = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = Dpi.S(new Padding(0, 2, 0, 2)) };
        info.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, keyW));
        info.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        foreach (var (k, v) in lines)
        {
            info.Controls.Add(new Label { Text = k + ":", AutoSize = true, ForeColor = Theme.Dim, Margin = Dpi.S(new Padding(0, 2, 0, 2)) });
            info.Controls.Add(new Label { Text = v, AutoSize = true, MaximumSize = new Size(valW, 0), Margin = Dpi.S(new Padding(0, 2, 0, 2)), UseMnemonic = false });
        }
        info.Location = new Point(infoX, Dpi.S(12));
        int topH = Math.Max(_pic.Height + Dpi.S(28), info.PreferredSize.Height + Dpi.S(24));
        var top = new Panel { Dock = DockStyle.Top, Height = topH };
        _pic.Location = new Point(Dpi.S(14), Dpi.S(14));
        top.Controls.Add(_pic);
        top.Controls.Add(info);
        ClientSize = new Size(ClientSize.Width, topH + Dpi.S(260) + Dpi.S(48));

        var files = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, BackColor = Theme.Row, ForeColor = Theme.Text,
            BorderStyle = BorderStyle.None, HeaderStyle = ColumnHeaderStyle.Nonclickable, OwnerDraw = true
        };
        files.DrawColumnHeader += (_, e) =>
        {
            using (var b = new SolidBrush(Theme.Header)) e.Graphics.FillRectangle(b, e.Bounds);
            using (var p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, e.Bounds.Right - 1, e.Bounds.Top, e.Bounds.Right - 1, e.Bounds.Bottom);
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                        (e.Header?.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left);
            var r = e.Bounds; r.Inflate(-Dpi.S(6), 0);
            TextRenderer.DrawText(e.Graphics, e.Header?.Text, Theme.Bold, r, Theme.Dim, flags);
        };
        files.DrawItem += (_, e) => e.DrawDefault = false;
        files.DrawSubItem += (_, e) =>
        {
            using (var b = new SolidBrush(e.Item!.Selected ? Theme.Selected : e.ItemIndex % 2 == 0 ? Theme.Row : Theme.RowAlt)) e.Graphics.FillRectangle(b, e.Bounds);
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix |
                        (e.Header?.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left);
            var r = e.Bounds; r.Inflate(-Dpi.S(6), 0);
            TextRenderer.DrawText(e.Graphics, e.SubItem?.Text, Theme.Normal, r, Theme.Text, flags);
        };
        var rows = new List<string[]>();
        if (s.Ps2 != null)
            foreach (var f in s.Ps2.Files)
                rows.Add(new[] { f.Name, f.Data.Length.ToString("#,0") + " B", f.Modified.ToLocal()?.ToString("dd.MM.yyyy HH:mm") ?? "" });
        else
            rows.Add(new[] { s.Ps1!.FileName, s.Ps1.Data.Length.ToString("#,0") + " B", "" });
        string[] heads = { L.T("Datei", "File"), L.T("Größe", "Size"), L.T("Geändert", "Modified") };
        int[] widths = new int[3];
        for (int c = 0; c < 3; c++)
            widths[c] = Math.Max(TextRenderer.MeasureText(heads[c], Theme.Bold).Width, rows.Count == 0 ? 0 : rows.Max(r => TextRenderer.MeasureText(r[c], Theme.Normal).Width)) + Dpi.S(20);
        files.Columns.Add(heads[0], widths[0]);
        files.Columns.Add(heads[1], widths[1], HorizontalAlignment.Right);
        files.Columns.Add(heads[2], widths[2]);
        foreach (var r in rows) files.Items.Add(new ListViewItem(r));
        var filesHost = new Panel { Dock = DockStyle.Fill, Padding = Dpi.S(new Padding(14, 0, 14, 10)) };
        filesHost.Controls.Add(files);

        var close = MakeButton(L.T("Schließen", "Close"), DialogResult.OK);
        Controls.Add(filesHost);
        Controls.Add(top);
        Controls.Add(ButtonRow(close));
        AcceptButton = close;
        CancelButton = close;
        FormClosed += (_, _) => _timer.Dispose();
    }
}

/// <summary>Rename a save / change its product code and region.</summary>
public sealed class RenameDialog : DarkForm
{
    readonly TextBox _name = new() { BackColor = Theme.Row, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 11f) };
    readonly CheckBox _files = new() { Text = L.T("Dateien im Save mit gleichem Namen ebenfalls umbenennen (empfohlen)", "Also rename files in the save with the same name (recommended)"), AutoSize = true, Checked = true };
    readonly CheckBox _keep = new() { Text = L.T("Original behalten (als Kopie unter neuem Namen anlegen)", "Keep original (create as a copy under the new name)"), AutoSize = true, Checked = true };
    readonly Label _info = new() { AutoSize = true, ForeColor = Theme.Dim };
    readonly int _max;

    public string NewName => _name.Text.Trim();
    public bool RenameFiles => _files.Checked;
    public bool KeepOriginal => _keep.Checked;

    public RenameDialog(CardEntry entry, Func<string, bool> exists)
    {
        Text = L.T("Umbenennen / Region ändern – ", "Rename / change region – ") + entry.Name;
        _max = entry.Platform == Platform.PS2 ? 31 : 20;
        ClientSize = Dpi.S(new Size(640, 330));
        _name.Text = entry.Name;
        _name.MaxLength = _max;
        _name.Width = Dpi.S(420);

        var regions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Dpi.S(new Padding(0, 6, 0, 6)) };
        foreach (var (label, ch) in new[] { ("PAL (BE)", 'E'), ("USA (BA)", 'A'), ("Japan (BI)", 'I'), ("Korea (BK)", 'K'), (L.T("Asien (BH)", "Asia (BH)"), 'H') })
        {
            var b = MakeButton(label);
            b.MinimumSize = Dpi.S(new Size(70, 28));
            b.Click += (_, _) => _name.Text = SaveGame.WithRegionPrefix(_name.Text, ch);
            regions.Controls.Add(b);
        }

        var hint = new Label
        {
            AutoSize = true, ForeColor = Theme.Dim, MaximumSize = new Size(Dpi.S(600), 0),
            Text = L.T("Ein Spiel findet nur Saves mit seinem eigenen Produktcode (z. B. BESLES-53703 = PAL, BASLUS-… = USA). " +
                       "Für eine andere Regionsversion Präfix UND Nummer anpassen (die Nummer der anderen Version steht z. B. auf der Disc/Hülle). " +
                       "Ob der Spielstand dort funktioniert, hängt vom Spiel ab (Sprache, Prüfsummen, 50/60 Hz).",
                       "A game only finds saves with its own product code (e.g. BESLES-53703 = PAL, BASLUS-… = USA). " +
                       "For another regional version, adjust the prefix AND the number (the number of the other version is e.g. on the disc/case). " +
                       "Whether the save works there depends on the game (language, checksums, 50/60 Hz).")
        };
        if (entry.Platform == Platform.PS1) _files.Visible = false;

        var table = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = Dpi.S(new Padding(14)), ColumnCount = 1, AutoSize = true };
        table.Controls.Add(new Label { Text = L.T($"Neuer Name (max. {_max} Zeichen):", $"New name (max. {_max} characters):"), AutoSize = true });
        table.Controls.Add(_name);
        table.Controls.Add(regions);
        table.Controls.Add(_files);
        table.Controls.Add(_keep);
        table.Controls.Add(_info);
        table.Controls.Add(hint);

        _name.TextChanged += (_, _) => UpdateInfo(entry, exists);
        UpdateInfo(entry, exists);

        var ok = MakeButton(L.T("Übernehmen", "Apply"), DialogResult.OK);
        var cancel = MakeButton(L.T("Abbrechen", "Cancel"), DialogResult.Cancel);
        Controls.Add(table);
        Controls.Add(ButtonRow(ok, cancel));
        AcceptButton = ok;
        CancelButton = cancel;
        FormClosing += (_, e) =>
        {
            if (DialogResult != DialogResult.OK) return;
            string err = Validate(entry, exists);
            if (err.Length > 0)
            {
                MessageBox.Show(this, err, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                e.Cancel = true;
            }
        };
    }

    string Validate(CardEntry entry, Func<string, bool> exists)
    {
        string n = NewName;
        if (n.Length == 0) return L.T("Bitte einen Namen eingeben.", "Please enter a name.");
        if (n.Length > _max) return L.T($"Der Name darf höchstens {_max} Zeichen haben.", $"The name may have at most {_max} characters.");
        if (n == entry.Name) return L.T("Der Name ist unverändert.", "The name is unchanged.");
        if (n.Contains('/') || n.Any(c => c < 32 || c > 126)) return L.T("Nur ASCII-Zeichen ohne '/' sind erlaubt.", "Only ASCII characters without '/' are allowed.");
        if (exists(n)) return L.T($"„{n}“ existiert bereits auf der Karte.", $"\"{n}\" already exists on the card.");
        return "";
    }

    void UpdateInfo(CardEntry entry, Func<string, bool> exists)
    {
        string err = Validate(entry, exists);
        var r = RegionUtil.FromName(NewName);
        _info.ForeColor = err.Length == 0 ? Theme.Ok : Theme.Dim;
        _info.Text = (err.Length == 0 ? "✓ " : "") + $"Region: {RegionUtil.Display(r)}" +
                     (RegionUtil.ProductCode(NewName) is { Length: > 0 } pc ? L.T($" · Produktcode: {pc}", $" · Product code: {pc}") : "") +
                     (err.Length > 0 && NewName != entry.Name ? "  –  " + err : "");
    }
}

/// <summary>Result of the card check with repair / restore actions.</summary>
public sealed class HealthDialog : DarkForm
{
    public enum Action { None, Repair, Undelete, RestoreBackup }
    public Action Chosen { get; private set; }

    public HealthDialog(MemoryCard card, List<CardIssue> issues, int backupCount)
    {
        Text = L.T("Kartenprüfung – ", "Card check – ") + card.DisplayName;
        ClientSize = Dpi.S(new Size(720, 440));
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = Dpi.S(new Size(560, 320));

        int errors = issues.Count(i => i.Level == IssueLevel.Error), warns = issues.Count(i => i.Level == IssueLevel.Warning);
        var head = new Label
        {
            Dock = DockStyle.Top, AutoSize = false, Height = Theme.Title.Height + Dpi.S(24), Padding = Dpi.S(new Padding(14, 10, 14, 0)),
            Font = Theme.Title,
            ForeColor = errors > 0 ? Theme.Danger : warns > 0 ? Theme.Warn : Theme.Ok,
            Text = errors > 0 ? L.T($"✖  {errors} Fehler, {warns} Warnung{(warns == 1 ? "" : "en")} gefunden", $"✖  {errors} error{(errors == 1 ? "" : "s")}, {warns} warning{(warns == 1 ? "" : "s")} found")
                 : warns > 0 ? L.T($"⚠  {warns} Warnung{(warns == 1 ? "" : "en")} gefunden", $"⚠  {warns} warning{(warns == 1 ? "" : "s")} found")
                 : issues.Count > 0 ? L.T("✔  Karte in Ordnung (nur Hinweise)", "✔  Card OK (notes only)") : L.T("✔  Karte in Ordnung – keine Probleme gefunden", "✔  Card OK – no problems found")
        };
        var list = new ListBox
        {
            Dock = DockStyle.Fill, BackColor = Theme.Row, ForeColor = Theme.Text, BorderStyle = BorderStyle.None,
            DrawMode = DrawMode.OwnerDrawVariable, IntegralHeight = false, HorizontalScrollbar = false
        };
        foreach (var i in issues) list.Items.Add(i);
        if (issues.Count == 0) list.Items.Add(new CardIssue(IssueLevel.Info, L.T("Keine Auffälligkeiten.", "Nothing unusual.")));
        list.MeasureItem += (_, e) =>
        {
            var it = (CardIssue)list.Items[e.Index];
            string t = it.Text + (it.Repairable ? L.T("   [reparierbar]", "   [repairable]") : "");
            var sz = TextRenderer.MeasureText(t, Theme.Normal, new Size(list.ClientSize.Width - Dpi.S(56), 0), TextFormatFlags.WordBreak);
            e.ItemHeight = Math.Min(255, sz.Height + Dpi.S(12));
        };
        list.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            var it = (CardIssue)list.Items[e.Index];
            using (var b = new SolidBrush(e.Index % 2 == 0 ? Theme.Row : Theme.RowAlt)) e.Graphics.FillRectangle(b, e.Bounds);
            var (sym, col) = it.Level switch
            {
                IssueLevel.Error => ("✖", Theme.Danger),
                IssueLevel.Warning => ("⚠", Theme.Warn),
                _ => ("ℹ", Theme.Accent)
            };
            TextRenderer.DrawText(e.Graphics, sym, Theme.Bold, new Rectangle(e.Bounds.X + Dpi.S(8), e.Bounds.Y + Dpi.S(5), Dpi.S(24), Dpi.S(24)), col);
            string txt = it.Text + (it.Repairable ? L.T("   [reparierbar]", "   [repairable]") : "");
            TextRenderer.DrawText(e.Graphics, txt, Theme.Normal,
                new Rectangle(e.Bounds.X + Dpi.S(36), e.Bounds.Y + Dpi.S(6), e.Bounds.Width - Dpi.S(44), e.Bounds.Height - Dpi.S(6)),
                Theme.Text, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        };
        var host = new Panel { Dock = DockStyle.Fill, Padding = Dpi.S(new Padding(14, 4, 14, 10)) };
        host.Controls.Add(list);

        var buttons = new List<Button>();
        if (issues.Any(i => i.Repairable) && card.CanWrite)
        {
            var b = MakeButton(L.T("Reparieren …", "Repair …"));
            b.Click += (_, _) => { Chosen = Action.Repair; DialogResult = DialogResult.OK; };
            buttons.Add(b);
        }
        if (card.DeletedSaves.Count > 0 && card.CanWrite)
        {
            var b = MakeButton(L.T($"Gelöschte wiederherstellen ({card.DeletedSaves.Count}) …", $"Restore deleted ({card.DeletedSaves.Count}) …"));
            b.Click += (_, _) => { Chosen = Action.Undelete; DialogResult = DialogResult.OK; };
            buttons.Add(b);
        }
        if (backupCount > 0)
        {
            var b = MakeButton(L.T($"Backup zurückspielen ({backupCount}) …", $"Restore backup ({backupCount}) …"));
            b.Click += (_, _) => { Chosen = Action.RestoreBackup; DialogResult = DialogResult.OK; };
            buttons.Add(b);
        }
        var close = MakeButton(L.T("Schließen", "Close"), DialogResult.Cancel);
        buttons.Add(close);
        var note = new Label
        {
            Dock = DockStyle.Bottom, AutoSize = false, Height = Theme.Small.Height * 2 + Dpi.S(10), ForeColor = Theme.Dim, Font = Theme.Small,
            Padding = Dpi.S(new Padding(14, 2, 14, 2)),
            Text = L.T("Reparaturen ändern nur die Kopie im Speicher. Erst „Speichern“ schreibt die Datei – vorher wird automatisch ein Backup angelegt. " +
                       "Beschädigte Saves lassen sich über Exportieren retten (fehlende Teile sind mit Nullen gefüllt).",
                       "Repairs only change the copy in memory. Only \"Save\" writes the file – a backup is created automatically beforehand. " +
                       "Damaged saves can be rescued via Export (missing parts are filled with zeros).")
        };
        Controls.Add(host);
        Controls.Add(note);
        Controls.Add(head);
        Controls.Add(ButtonRow(buttons.ToArray()));
        CancelButton = close;
    }
}

/// <summary>Choose a backup of a card file to restore.</summary>
public sealed class BackupDialog : DarkForm
{
    readonly ListView _list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, BackColor = Theme.Row, ForeColor = Theme.Text,
        BorderStyle = BorderStyle.None, HeaderStyle = ColumnHeaderStyle.Nonclickable
    };

    public string? SelectedFile => _list.SelectedItems.Count == 1 ? (string)_list.SelectedItems[0].Tag! : null;

    public BackupDialog(string cardPath, List<FileInfo> backups)
    {
        Text = L.T("Backup zurückspielen – ", "Restore backup – ") + Path.GetFileName(cardPath.TrimEnd('\\'));
        ClientSize = Dpi.S(new Size(700, 420));
        FormBorderStyle = FormBorderStyle.Sizable;
        _list.Columns.Add(L.T("Backup vom", "Backup from"), Dpi.S(170));
        _list.Columns.Add(L.T("Inhalt", "Content"), Dpi.S(380));
        _list.Columns.Add(L.T("Größe", "Size"), Dpi.S(100), HorizontalAlignment.Right);
        foreach (var f in backups)
        {
            string when = f.Name;
            var m = System.Text.RegularExpressions.Regex.Match(f.Name, @"\.(\d{8})_(\d{6})_\d{3}\.bak$");
            if (m.Success && DateTime.TryParseExact(m.Groups[1].Value + m.Groups[2].Value, "yyyyMMddHHmmss", null,
                    System.Globalization.DateTimeStyles.None, out var dt)) when = dt.ToString("dd.MM.yyyy HH:mm:ss");
            string content;
            try
            {
                var c = CardIO.TryOpenBytes(File.ReadAllBytes(f.FullName), f.FullName);
                content = c == null ? L.T("nicht lesbar / unformatiert", "not readable / unformatted") : L.T($"{c.Entries.Count} Saves: ", $"{c.Entries.Count} saves: ") + string.Join(", ", c.Entries.Take(6).Select(e => e.Title)) + (c.Entries.Count > 6 ? " …" : "");
            }
            catch (Exception ex) { content = L.T("Fehler: ", "Error: ") + ex.Message; }
            _list.Items.Add(new ListViewItem(new[] { when, content, Theme.FormatSize(f.Length) }) { Tag = f.FullName });
        }
        if (_list.Items.Count > 0) _list.Items[0].Selected = true;
        var hint = new Label
        {
            Dock = DockStyle.Top, AutoSize = false, Height = Theme.Normal.Height * 2 + Dpi.S(16), Padding = Dpi.S(new Padding(14, 8, 14, 0)), ForeColor = Theme.Dim,
            Text = L.T("Das gewählte Backup ersetzt die Kartendatei. Der aktuelle Stand wird vorher selbst als Backup gesichert – nichts geht verloren.",
                       "The chosen backup replaces the card file. The current state is backed up itself beforehand – nothing is lost.")
        };
        var host = new Panel { Dock = DockStyle.Fill, Padding = Dpi.S(new Padding(14, 4, 14, 10)) };
        host.Controls.Add(_list);
        var ok = MakeButton(L.T("Zurückspielen", "Restore"), DialogResult.OK);
        var cancel = MakeButton(L.T("Abbrechen", "Cancel"), DialogResult.Cancel);
        _list.DoubleClick += (_, _) => { if (SelectedFile != null) { DialogResult = DialogResult.OK; Close(); } };
        Controls.Add(host);
        Controls.Add(hint);
        Controls.Add(ButtonRow(ok, cancel));
        AcceptButton = ok;
        CancelButton = cancel;
    }
}

/// <summary>Pick deleted saves to restore.</summary>
public sealed class UndeleteDialog : DarkForm
{
    readonly CheckedListBox _list = new() { Dock = DockStyle.Fill, BackColor = Theme.Row, ForeColor = Theme.Text, BorderStyle = BorderStyle.None, CheckOnClick = true };
    readonly IReadOnlyList<CardEntry> _entries;

    public List<CardEntry> Selected => _list.CheckedIndices.Cast<int>().Select(i => _entries[i]).ToList();

    public UndeleteDialog(IReadOnlyList<CardEntry> deleted)
    {
        _entries = deleted;
        Text = L.T("Gelöschte Savegames wiederherstellen", "Restore deleted saves");
        ClientSize = Dpi.S(new Size(560, 340));
        foreach (var e in deleted) _list.Items.Add(L.T($"{e.Title}   ({e.Name}, {e.Blocks} Block{(e.Blocks == 1 ? "" : "e")})", $"{e.Title}   ({e.Name}, {e.Blocks} block{(e.Blocks == 1 ? "" : "s")})"), true);
        var hint = new Label
        {
            Dock = DockStyle.Top, AutoSize = false, Height = Theme.Normal.Height * 2 + Dpi.S(16), Padding = Dpi.S(new Padding(14, 8, 14, 0)), ForeColor = Theme.Dim,
            Text = L.T("Diese Saves wurden gelöscht, ihre Blöcke sind aber noch unverändert vorhanden.", "These saves were deleted, but their blocks are still present unchanged.")
        };
        var host = new Panel { Dock = DockStyle.Fill, Padding = Dpi.S(new Padding(14, 4, 14, 10)) };
        host.Controls.Add(_list);
        var ok = MakeButton(L.T("Wiederherstellen", "Restore"), DialogResult.OK);
        var cancel = MakeButton(L.T("Abbrechen", "Cancel"), DialogResult.Cancel);
        Controls.Add(host);
        Controls.Add(hint);
        Controls.Add(ButtonRow(ok, cancel));
        AcceptButton = ok;
        CancelButton = cancel;
    }
}

public sealed class AboutDialog : DarkForm
{
    public AboutDialog()
    {
        Text = L.T("Über PSMemoMan", "About PSMemoMan");
        ClientSize = Dpi.S(new Size(620, 420));
        var art = new Panel { Location = Dpi.S(new Point(20, 20)), Size = Dpi.S(new Size(110, 140)) };
        art.Paint += (_, e) => CardArt.DrawCard(e.Graphics, new RectangleF(0, 0, art.Width, art.Height), true);

        int x = Dpi.S(150);
        var title = new Label { Text = "PSMemoMan", Font = new Font("Segoe UI Semibold", 20f), AutoSize = true, Location = new Point(x, Dpi.S(18)) };
        var ver = new Label
        {
            Text = L.T("Version ", "Version ") + MainForm.Version + "  ·  " + L.T("Memory Card Manager für PlayStation & PlayStation 2", "Memory card manager for PlayStation & PlayStation 2"),
            AutoSize = true, ForeColor = Theme.Dim, Location = new Point(x, Dpi.S(62))
        };
        var author = new Label { Text = L.T("Autor: ", "Author: ") + MainForm.Author, AutoSize = true, Location = new Point(x, Dpi.S(96)) };
        var link = new LinkLabel
        {
            Text = MainForm.GitHubUrl, AutoSize = true, Location = new Point(x, Dpi.S(122)),
            LinkColor = Theme.Accent, ActiveLinkColor = Theme.Text, VisitedLinkColor = Theme.Accent
        };
        link.LinkClicked += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(MainForm.GitHubUrl) { UseShellExecute = true }); } catch { }
        };
        var lic = new Label { Text = L.T("Lizenz: GNU GPL v3.0", "License: GNU GPL v3.0"), AutoSize = true, Location = new Point(x, Dpi.S(148)) };
        var info = new Label
        {
            Location = new Point(Dpi.S(20), Dpi.S(185)), Size = Dpi.S(new Size(580, 170)), ForeColor = Theme.Dim,
            Text = L.T(
                "PS2: PCSX2 (.ps2, Datei & Ordner), LRPS2, PS3 (.VM2), OPL (.vmc), MemCard PRO2/SD2PSX (.mc2), .bin – 8–64 MB, mit/ohne ECC\n" +
                "PS1: .mcr .mcd .mc .srm .gme .vgs .mem .VMP (PSP/Vita) .VM1 (PS3) .VMC (POPStarter) …\n" +
                "Saves: PSU, MAX, CBS, SPS/XPS, PSV, ZIP, Ordner, MCS, PSX/MCB/MCX/PDA, RAW\n\n" +
                "Dank an: mymc (Ross Ridge, Public Domain) und dart_mymc (BAD-AL), MemcardRex (Shendo, GPL-3), LZARI (Haruhiko Okumura).\n" +
                "PlayStation ist eine Marke von Sony Interactive Entertainment. Dieses Projekt steht in keiner Verbindung zu Sony.",
                "PS2: PCSX2 (.ps2, file & folder), LRPS2, PS3 (.VM2), OPL (.vmc), MemCard PRO2/SD2PSX (.mc2), .bin – 8–64 MB, with/without ECC\n" +
                "PS1: .mcr .mcd .mc .srm .gme .vgs .mem .VMP (PSP/Vita) .VM1 (PS3) .VMC (POPStarter) …\n" +
                "Saves: PSU, MAX, CBS, SPS/XPS, PSV, ZIP, folders, MCS, PSX/MCB/MCX/PDA, RAW\n\n" +
                "Thanks to: mymc (Ross Ridge, public domain) and dart_mymc (BAD-AL), MemcardRex (Shendo, GPL-3), LZARI (Haruhiko Okumura).\n" +
                "PlayStation is a trademark of Sony Interactive Entertainment. This project is not affiliated with Sony.")
        };
        var ok = MakeButton("OK", DialogResult.OK);
        Controls.AddRange(new Control[] { art, title, ver, author, link, lic, info });
        Controls.Add(ButtonRow(ok));
        AcceptButton = ok;
        CancelButton = ok;
    }
}

public sealed class TextReportDialog : DarkForm
{
    public TextReportDialog(string title, string text)
    {
        Text = title;
        ClientSize = Dpi.S(new Size(640, 420));
        FormBorderStyle = FormBorderStyle.Sizable;
        var tb = new TextBox
        {
            Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both, WordWrap = false,
            BackColor = Theme.Row, ForeColor = Theme.Text, BorderStyle = BorderStyle.None, Font = new Font("Consolas", 9.5f), Text = text
        };
        var host = new Panel { Dock = DockStyle.Fill, Padding = Dpi.S(new Padding(12)) };
        host.Controls.Add(tb);
        var ok = MakeButton("OK", DialogResult.OK);
        Controls.Add(host);
        Controls.Add(ButtonRow(ok));
        AcceptButton = ok;
    }
}
