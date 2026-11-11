using System.Drawing;
using System.Drawing.Imaging;
using MemcardManager.Core;
L.Init("de"); // the checks below compare German texts
if (args.Length == 2 && args[0] == "--verify")
{
    var d = File.ReadAllBytes(args[1]);
    bool vmp = d.Length > 4 && d[1] == 'P' && d[2] == 'M' && d[3] == 'V';
    bool ok = vmp ? PsSign.VerifyVmp(d) : PsSign.VerifyPsv(d);
    Console.WriteLine($"{Path.GetFileName(args[1])}: {(vmp ? "VMP" : "PSV")} Signatur {(ok ? "GÜLTIG" : "UNGÜLTIG")}");
    if (vmp)
    {
        var card = new Ps1Card(d, args[1], "vmp");
        foreach (var e in card.Entries) Console.WriteLine($"   {e.Name}  {e.Title}  {e.Blocks} Bl.");
        // re-sign the unchanged card and check the result verifies too
        var re = PsSign.MakeVmp(card.RawData);
        Console.WriteLine("   neu signiert: " + (PsSign.VerifyVmp(re) ? "GÜLTIG" : "UNGÜLTIG") + ", Daten gleich: " + re.AsSpan(0x80).SequenceEqual(d.AsSpan(0x80)));
    }
    return ok ? 0 : 1;
}
if (args.Length == 4 && args[0] == "--sheet")
{
    // contact sheet: first frame of every save icon of a card, at the given size
    var c = CardIO.Open(args[1]);
    int sz = int.Parse(args[3]);
    var list = c.Entries.Select(e => (e.Name, a: IconRenderer.Build(e.Save, sz))).Where(x => x.a != null).ToList();
    int cols = 6, rows = (list.Count + cols - 1) / cols;
    using var sheet = new Bitmap(cols * sz, rows * sz, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(sheet)) g.Clear(Color.FromArgb(40, 44, 52));
    for (int k = 0; k < list.Count; k++)
    {
        var fr = list[k].a!.Frames[0];
        for (int y = 0; y < sz; y++) for (int x = 0; x < sz; x++)
        {
            int px = fr[y * sz + x];
            if (((px >> 24) & 0xFF) != 0) sheet.SetPixel(k % cols * sz + x, k / cols * sz + y, Color.FromArgb(px));
        }
    }
    sheet.Save(args[2]);
    Console.WriteLine(string.Join(" | ", list.Select(x => x.Name)));
    return 0;
}
if (args.Length == 2 && args[0] == "--make-icon") { MemcardManager.UI.CardArt.WriteIco(args[1], 16, 20, 24, 32, 40, 48, 64, 128, 256); using var big = MemcardManager.UI.CardArt.Render(256, true); big.Save(Path.ChangeExtension(args[1], ".png")); return 0; }

// Usage: CoreTest <testdir> <outdir>
string testDir = args[0];
string outDir = args[1];
Directory.CreateDirectory(outDir);
int fails = 0;
void Check(bool ok, string what)
{
    Console.WriteLine((ok ? "  OK   " : "  FAIL ") + what);
    if (!ok) fails++;
}

void SavePng(IconAnimation a, string path)
{
    int n = a.Frames.Count;
    int cols = Math.Min(n, 8), rows = (n + 7) / 8;
    using var bmp = new Bitmap(a.Size * cols, a.Size * rows, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(bmp)) g.Clear(Color.FromArgb(255, 40, 44, 52));
    for (int f = 0; f < n; f++)
        for (int y = 0; y < a.Size; y++)
            for (int x = 0; x < a.Size; x++)
            {
                int c = a.Frames[f][y * a.Size + x];
                if (((c >> 24) & 0xFF) == 0) continue;
                bmp.SetPixel((f % 8) * a.Size + x, (f / 8) * a.Size + y, Color.FromArgb(c));
            }
    bmp.Save(path, ImageFormat.Png);
}

// ---------------------------------------------------------------- cards
foreach (var cardPath in Directory.GetFiles(testDir, "*.ps2"))
{
    Console.WriteLine($"== {Path.GetFileName(cardPath)}");
    var card = CardIO.Open(cardPath);
    Console.WriteLine($"  {card.FormatName}  frei {card.FreeBytes / 1024} KB / {card.TotalBytes / 1024} KB");
    foreach (var w in card.Warnings) Console.WriteLine("  warn: " + w);
    foreach (var e in card.Entries)
    {
        Console.WriteLine($"  {e.Name,-34} {e.Size / 1024,5} KB  {RegionUtil.Display(e.Region),-15} {e.Modified:yyyy-MM-dd HH:mm}  {e.Title}");
        var anim = IconRenderer.Build(e.Save, 64);
        if (anim != null) SavePng(anim, Path.Combine(outDir, TextUtil.SafeFileName(Path.GetFileNameWithoutExtension(cardPath) + "_" + e.Name) + ".png"));
        else Console.WriteLine("        (kein Icon)");
    }
    if (card is Ps2Card p2)
    {
        var msgs = p2.Check();
        Console.WriteLine((msgs.Count == 0 ? "  OK   " : "  INFO ") + "Check: " + string.Join("; ", msgs));
    }
}

// ---------------------------------------------------------------- save files
var max = SaveFormats.LoadAny(Path.Combine(testDir, "NFL2K16.max"))[0];
var psu = SaveFormats.LoadAny(Path.Combine(testDir, "NFL2K16.psu"))[0];
Console.WriteLine($"MAX: {max.Name} {max.Ps2!.Files.Count} Dateien, PSU: {psu.Name} {psu.Ps2!.Files.Count} Dateien");
foreach (var f in max.Ps2.Files) Console.WriteLine($"    max {f.Name} {f.Data.Length}");
foreach (var f in psu.Ps2.Files) Console.WriteLine($"    psu {f.Name} {f.Data.Length}");
foreach (var mf in Directory.GetFiles(testDir, "*.max"))
{
    var s = SaveFormats.LoadAny(mf)[0];
    Console.WriteLine($"  load {Path.GetFileName(mf)} -> {s.Name} / {s.Title} ({s.Ps2!.Files.Count} Dateien)");
}

foreach (var fmt in new[] { ExportFormat.Psu, ExportFormat.Max, ExportFormat.Cbs, ExportFormat.Ps2Zip })
{
    var bytes = SaveFormats.ExportBytes(psu, fmt);
    var back = SaveFormats.LoadBytes(bytes, "x" + SaveFormats.Extension(fmt))[0];
    Check(SaveFormats.SameContent(psu, back), $"Roundtrip {fmt} ({bytes.Length} Bytes)");
}
var psuBytes = File.ReadAllBytes(Path.Combine(testDir, "NFL2K16.psu"));
Check(SaveFormats.SavePsu(psu.Ps2).Length == psuBytes.Length, "PSU-Export hat gleiche Länge wie Original");

// ---------------------------------------------------------------- import into a fresh card
foreach (var (mb, ecc) in new[] { (8, true), (8, false), (16, true) })
{
    var img = Ps2Card.Format(mb, ecc);
    string p = Path.Combine(outDir, $"new_{mb}_{(ecc ? "ecc" : "noecc")}.ps2");
    var nc = new Ps2Card(img, p, "test");
    Check(nc.Entries.Count == 0, $"Neue Karte {mb} MB ecc={ecc}: leer, frei {nc.FreeBytes / 1024} KB");
    var src = CardIO.Open(Path.Combine(testDir, "Mcd001.ps2"));
    foreach (var e in src.Entries) nc.Import(e.Save.Clone());
    Check(nc.Entries.Count == src.Entries.Count, $"  {nc.Entries.Count} Saves importiert");
    Check(nc.Check().Count == 0, "  Integritätsprüfung: " + string.Join("; ", nc.Check()));
    nc.Import(new SaveGame { Ps2 = RenameClone(max.Ps2!, "BATEST-00000MAX") });
    nc.Commit(p);
    var re = CardIO.Open(p);
    Check(re.Entries.Count == src.Entries.Count + 1, $"  wieder geöffnet: {re.Entries.Count} Saves");
    foreach (var e in src.Entries)
    {
        var o = re.Find(e.Name);
        Check(o != null && SaveFormats.SameContent(e.Save, o.Save), $"  Inhalt identisch: {e.Name}");
    }
    // delete + reimport
    var first = re.Entries[0];
    var keep = first.Save.Clone();
    long freeBefore = re.FreeBytes;
    re.Delete(first);
    Check(re.Find(keep.Name) == null && re.FreeBytes > freeBefore, $"  gelöscht: {keep.Name}");
    re.Import(keep);
    Check(re.FreeBytes == freeBefore && ((Ps2Card)re).Check().Count == 0, "  wieder importiert, Platz identisch");
    // fill card until full
    int n = 0;
    try { while (n < 1000) { re.Import(new SaveGame { Ps2 = RenameClone(psu.Ps2!, $"BAFILL-{n:00000}") }); n++; } }
    catch (CardFullException) { }
    Check(((Ps2Card)re).Check().Count == 0, $"  Karte vollgeschrieben mit {n} Kopien, konsistent, frei {re.FreeBytes / 1024} KB");
    re.Commit(p);
}

// ---------------------------------------------------------------- PS1
{
    var raw = Ps1Card.FormatRaw();
    string p = Path.Combine(outDir, "new_ps1.mcr");
    var c1 = new Ps1Card(raw, p, "test");
    Check(c1.Entries.Count == 0 && c1.FreeBytes == 15 * 8192, "PS1 neu formatiert");
    var s = MakePs1Save("BASLUS-00000TEST", 3);
    c1.Import(new SaveGame { Ps1 = s });
    c1.Import(new SaveGame { Ps1 = MakePs1Save("BESLES-11111ABC", 1) });
    c1.Commit(p);
    foreach (var cont in new[] { Ps1Container.Raw, Ps1Container.DexDrive, Ps1Container.Vgs })
    {
        var bytes = ((Ps1Card)CardIO.Open(p)).Build(cont);
        var r = new Ps1Card(bytes, "x", "x");
        Check(r.Container == cont && r.Entries.Count == 2 && r.Entries[0].Save.Ps1!.Data.AsSpan().SequenceEqual(s.Data), $"PS1 Container {cont}");
    }
    var rc = CardIO.Open(p);
    var e0 = rc.Entries[0];
    foreach (var fmt in SaveFormats.FormatsFor(Platform.PS1))
    {
        var bytes = SaveFormats.ExportBytes(e0.Save, fmt);
        var back = SaveFormats.LoadBytes(bytes, e0.Name + SaveFormats.Extension(fmt))[0];
        Check(back.Ps1 != null && back.Ps1.Data.AsSpan().SequenceEqual(e0.Save.Ps1!.Data) && (fmt == ExportFormat.Ps1Raw || back.Name == e0.Name), $"PS1 Roundtrip {fmt}");
    }
    rc.Delete(e0);
    Check(rc.Entries.Count == 1 && rc.FreeBytes == 14 * 8192, "PS1 löschen");
    var anim = IconRenderer.Build(rc.Entries[0].Save, 64);
    Check(anim != null, "PS1 Icon");
    if (anim != null) SavePng(anim, Path.Combine(outDir, "ps1_icon.png"));
}

// PCSX2 folder card
{
    string fdir = Path.Combine(outDir, "foldercard_" + DateTime.Now.Ticks);
    Directory.CreateDirectory(fdir);
    File.WriteAllBytes(Path.Combine(fdir, Ps2FolderCard.SuperblockFile), new byte[8192]);
    var fc = (Ps2FolderCard)CardIO.Open(fdir);
    Check(fc.Entries.Count == 0, "Ordnerkarte leer geöffnet");
    fc.Import(psu.Clone());
    fc.Import(new SaveGame { Ps2 = RenameClone(max.Ps2!, "BATEST-FOLDER") });
    fc.Commit(fdir);
    var fc2 = CardIO.Open(fdir);
    Check(fc2.Entries.Count == 2 && SaveFormats.SameContent(fc2.Find(psu.Name)!.Save, psu), "Ordnerkarte: 2 Saves geschrieben & identisch");
    fc2.Delete(fc2.Find("BATEST-FOLDER")!);
    fc2.Commit(fdir);
    Check(!Directory.Exists(Path.Combine(fdir, "BATEST-FOLDER")) && CardIO.Open(fdir).Entries.Count == 1, "Ordnerkarte: gelöscht (in Backup verschoben)");
}

// signed formats: VMP (PSP/Vita) and PSV (PS3)
{
    var vmpBytes = CardIO.CreateNew(NewCardType.Ps1Vmp);
    Check(PsSign.VerifyVmp(vmpBytes), "Neue VMP-Karte ist gültig signiert");
    var vcard = new Ps1Card(vmpBytes, "test.VMP", "vmp");
    vcard.Import(new SaveGame { Ps1 = MakePs1Save("BESLES-12345VMP", 2) });
    var signed = vcard.Build(Ps1Container.Vmp);
    Check(PsSign.VerifyVmp(signed) && new Ps1Card(signed, "x", "x").Entries.Count == 1, "VMP nach Import neu signiert & lesbar");
    signed[0x200] ^= 0xFF;
    Check(!PsSign.VerifyVmp(signed), "Manipulierte VMP wird als ungültig erkannt");

    var p1 = new SaveGame { Ps1 = MakePs1Save("BASLUS-00001PSV", 3) };
    var psv1 = SaveFormats.ExportBytes(p1, ExportFormat.Ps1Psv);
    var back1 = SaveFormats.LoadSingle(psv1, "x.psv");
    Check(PsSign.VerifyPsv(psv1) && SaveFormats.SameContent(p1, back1), "PS1-PSV signiert & Roundtrip");
    var psv2 = SaveFormats.ExportBytes(psu, ExportFormat.Psv);
    var back2 = SaveFormats.LoadSingle(psv2, "x.psv");
    Check(PsSign.VerifyPsv(psv2) && SaveFormats.SameContent(psu, back2), $"PS2-PSV signiert & Roundtrip ({psv2.Length} Bytes)");
    Check(SaveFormats.PsvFileName("BESLES-53703kk") == "BESLES-537036B6B.PSV", "PSV-Dateiname " + SaveFormats.PsvFileName("BESLES-53703kk"));

    var ren = psu.Renamed(SaveGame.WithRegionPrefix(psu.Name, 'E'), true);
    Check(ren.Name == "BESLUS-20919NFL2K16" && ren.Ps2!.Find("BESLUS-20919NFL2K16") != null && psu.Renamed("BESLES-53703kk", true).Region == MemcardManager.Core.Region.Europa,
        "Umbenennen inkl. gleichnamiger Datei + Region");
}

// damage detection / repair
{
    // damaged test card from dart_mymc: one save with broken chains + ECC bit errors
    var bad = (Ps2Card)CardIO.Open(Path.Combine(testDir, "Mcd_test.ps2"));
    var issues = bad.Diagnose();
    foreach (var i in issues) Console.WriteLine($"    [{i.Level}{(i.Repairable ? ",rep" : "")}] {i.Text}");
    var dmg = bad.Entries.FirstOrDefault(e => e.Damaged);
    Check(dmg != null && dmg.Name == "BASLUS-20919NFL2K16", $"Beschädigter Save erkannt und gerettet: {dmg?.Name} ({dmg?.Problem})");
    Check(issues.Any(i => i.Repairable && i.Text.StartsWith("ECC")), "ECC-Fehler als reparierbar gemeldet");
    var healthyBefore = bad.Entries.Where(e => !e.Damaged).Select(e => e.Save.Clone()).ToList();
    var rep = bad.Repair();
    foreach (var r in rep) Console.WriteLine("    rep: " + r);
    Check(!bad.Diagnose().Any(i => i.Repairable && i.Text.StartsWith("ECC")), "ECC nach Reparatur: nichts Reparierbares mehr offen");
    Check(!rep.Any(r => r.Contains("freigegeben (+")), "Verlorene Cluster bei beschädigtem Save NICHT freigegeben");
    bad.Delete(dmg!);
    Check(healthyBefore.All(h => bad.Find(h.Name) is { } x && SaveFormats.SameContent(h, x.Save)), "Löschen des beschädigten Saves lässt andere Saves unversehrt");

    // copy of the user card with 80 lost clusters
    var lostCard = (Ps2Card)CardIO.Open(Path.Combine(testDir, "user_Mcd002.ps2"));
    long freeBefore = lostCard.FreeBytes;
    var before = lostCard.Entries.Select(e => e.Save.Clone()).ToList();
    var rep2 = lostCard.Repair();
    Check(lostCard.FreeBytes == freeBefore + 80 * 1024 && lostCard.Check().Count == 0, $"80 verlorene Cluster freigegeben: {string.Join(" ", rep2)}");
    Check(before.All(h => lostCard.Find(h.Name) is { } x && SaveFormats.SameContent(h, x.Save)), "Alle Saves nach Reparatur identisch");
    string repaired = Path.Combine(outDir, "repaired_Mcd002.ps2");
    lostCard.Commit(repaired);

    // PS1: wrong checksum, orphan block, deleted save + undelete
    var raw = Ps1Card.FormatRaw();
    var c1 = new Ps1Card(raw, "x.mcr", "x");
    c1.Import(new SaveGame { Ps1 = MakePs1Save("BESLES-11111AAA", 2) });
    c1.Import(new SaveGame { Ps1 = MakePs1Save("BESLES-22222BBB", 3) });
    var img = c1.Build(Ps1Container.Raw);
    img[1 * 128 + 0x30] ^= 0x01;              // corrupt checksum of frame 1
    img[10 * 128] = 0x52; img[10 * 128 + 8] = 0xFF; img[10 * 128 + 9] = 0xFF;  // orphan block in slot 9
    var c2 = new Ps1Card(img, "x.mcr", "x");
    var iss = c2.Diagnose();
    foreach (var i in iss) Console.WriteLine($"    [{i.Level}{(i.Repairable ? ",rep" : "")}] {i.Text}");
    Check(iss.Count(i => i.Repairable) >= 2, "PS1: Prüfsumme + verwaister Block erkannt");
    c2.Repair();
    Check(c2.Diagnose().All(i => !i.Repairable), "PS1: nach Reparatur sauber");
    var keep = c2.Find("BESLES-22222BBB")!.Save.Clone();
    c2.Delete(c2.Find("BESLES-22222BBB")!);
    Check(c2.DeletedSaves.Count == 1, "PS1: gelöschter Save wiederherstellbar");
    c2.Undelete(c2.DeletedSaves[0]);
    Check(c2.Find("BESLES-22222BBB") is { } u && SaveFormats.SameContent(u.Save, keep), "PS1: Save vollständig wiederhergestellt");

    // unformatted cards
    var blank = new byte[8650752]; Array.Fill(blank, (byte)0xFF);
    var unf = CardIO.DetectUnformatted(blank);
    Check(unf != null && unf.SizeMb == 8 && unf.Ecc && CardIO.TryOpenBytes(CardIO.FormatLike(unf), "x.ps2") != null, "Unformatierte 8-MB-Karte erkannt & formatierbar");
}

// rescue import of truncated save files
{
    var ps1 = new SaveGame { Ps1 = MakePs1Save("BESLES-99999RESC", 3) };
    var cases = new (string name, byte[] bytes, SaveGame orig)[]
    {
        ("psu", SaveFormats.ExportBytes(psu, ExportFormat.Psu), psu),
        ("max", SaveFormats.ExportBytes(psu, ExportFormat.Max), psu),
        ("cbs", SaveFormats.ExportBytes(psu, ExportFormat.Cbs), psu),
        ("psv", SaveFormats.ExportBytes(psu, ExportFormat.Psv), psu),
        ("mcs", SaveFormats.ExportBytes(ps1, ExportFormat.Mcs), ps1),
        ("psx", SaveFormats.ExportBytes(ps1, ExportFormat.ActionReplay), ps1),
    };
    foreach (var (name, bytes, orig) in cases)
    {
        var cut = bytes.AsSpan(0, (int)(bytes.Length * 0.6)).ToArray();
        bool normalFails;
        try { SaveFormats.LoadSingle(cut, "x." + name); normalFails = false; } catch { normalFails = true; }
        SaveGame? r = null;
        string err = "";
        try { r = SaveFormats.LoadSingle(cut, "x." + name, new SaveFormats.Rescue()); } catch (Exception ex) { err = ex.Message; }
        bool prefixOk = false;
        if (r?.Ps2 != null && r.Ps2.Files.Count > 0)
        {
            var f0 = r.Ps2.Files[0]; var o0 = orig.Ps2!.Find(f0.Name)!;
            int n = Math.Min(4096, Math.Min(f0.Data.Length, o0.Data.Length));
            prefixOk = f0.Data.AsSpan(0, n).SequenceEqual(o0.Data.AsSpan(0, n));
        }
        else if (r?.Ps1 != null) prefixOk = r.Ps1.Data.AsSpan(0, 8192).SequenceEqual(orig.Ps1!.Data.AsSpan(0, 8192));
        Check(normalFails && r != null && r.Damaged && prefixOk && r.Problem!.StartsWith(SaveFormats.RescueTag),
            $"Rettungsimport {name}: normal abgelehnt={normalFails}, gerettet={(r != null)} {(r?.Ps2?.Files.Count ?? r?.Ps1?.Blocks)} Teile – {r?.Problem ?? err}");
    }
    // rescued saves can be imported onto a card
    var rc = new Ps2Card(Ps2Card.Format(8, true), "r.ps2", "t");
    var rescued = SaveFormats.LoadSingle(SaveFormats.ExportBytes(psu, ExportFormat.Psu).AsSpan(0, 400000).ToArray(), "x.psu", new SaveFormats.Rescue());
    rc.Import(rescued);
    Check(rc.Entries.Count == 1 && rc.Check().Count == 0, "Geretteter Save auf Karte importiert, Karte konsistent");
}

// LZARI raw roundtrips
var rnd = new Random(1);
foreach (int len in new[] { 1, 2, 59, 60, 61, 4096, 10000, 300000 })
{
    var data = new byte[len];
    for (int i = 0; i < len; i++) data[i] = (byte)(rnd.Next(3) == 0 ? rnd.Next(256) : i % 7);
    var enc = Lzari.Encode(data);
    var dec = Lzari.Decode(enc, len);
    Check(dec.AsSpan().SequenceEqual(data), $"LZARI {len} -> {enc.Length}");
}

Console.WriteLine(fails == 0 ? "ALLE TESTS OK" : $"{fails} FEHLER");
return fails;

static Ps2Save RenameClone(Ps2Save s, string name) { var c = s.Clone(); c.DirName = name; return c; }

static Ps1Save MakePs1Save(string name, int blocks)
{
    var d = new byte[blocks * 8192];
    d[0] = (byte)'S'; d[1] = (byte)'C'; d[2] = 0x13; d[3] = (byte)blocks;
    var t = TextUtil.EncodeSjis("ＴＥＳＴ　ＳＡＶＥ " + name);
    t.AsSpan(0, Math.Min(64, t.Length)).CopyTo(d.AsSpan(4));
    for (int i = 0; i < 16; i++)
    {
        int r = i * 2, g = 31 - i * 2, b = 15;
        Bin16(d, 0x60 + i * 2, (ushort)(i == 0 ? 0 : r | (g << 5) | (b << 10)));
    }
    for (int f = 0; f < 3; f++)
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x += 2)
            {
                int c0 = ((x + y + f * 4) / 2) % 16, c1 = ((x + 1 + y + f * 4) / 2) % 16;
                if ((x - 8) * (x - 8) + (y - 8) * (y - 8) > 49) { c0 = 0; c1 = 0; }
                d[0x80 + f * 0x80 + y * 8 + x / 2] = (byte)(c0 | (c1 << 4));
            }
    for (int i = 0x200; i < d.Length; i++) d[i] = (byte)(i * 31);
    return new Ps1Save { FileName = name, Data = d };
}

static void Bin16(byte[] b, int o, ushort v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
