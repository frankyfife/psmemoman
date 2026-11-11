namespace MemcardManager.Core;

public enum NewCardType
{
    Ps2Pcsx2_8, Ps2Pcsx2_16, Ps2Pcsx2_32, Ps2Pcsx2_64,
    Ps2Ps3Vm2,
    Ps2NoEcc_8,   // OPL .vmc, MemCard PRO2 / SD2PSX .mc2, .bin
    Ps1Raw,       // .mcr .mcd .mc .srm .ps
    Ps1Ps3Vm1,
    Ps1Gme,
    Ps1Vgs,
    Ps1Vmp        // PSP / PS Vita (signed)
}

public static class CardIO
{
    public static string BackupDir
    {
        get
        {
            string d = Path.Combine(AppContext.BaseDirectory, "Backups");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    public static string OpenFilter =>
        L.T("Alle Memory Cards", "All memory cards") + "|*.ps2;*.vm2;*.vmc;*.mc2;*.bin;*.mcr;*.mcd;*.mc;*.gme;*.vgs;*.mem;*.vmp;*.vm1;*.srm;*.ps;*.ddf;*.mci;*.psm;*.raw;*.img|" +
        L.T("PS2-Karten", "PS2 cards") + " (*.ps2;*.vm2;*.vmc;*.mc2;*.bin)|*.ps2;*.vm2;*.vmc;*.mc2;*.bin|" +
        L.T("PS1-Karten", "PS1 cards") + " (*.mcr;*.mcd;*.mc;*.gme;*.vgs;*.mem;*.vmp;*.vm1;*.srm)|*.mcr;*.mcd;*.mc;*.gme;*.vgs;*.mem;*.vmp;*.vm1;*.srm;*.ps;*.ddf;*.mci;*.psm|" +
        L.T("Alle Dateien", "All files") + "|*.*";

    public static string ImportFilter =>
        L.T("Alle Savegames", "All saves") + "|*.psu;*.max;*.cbs;*.sps;*.xps;*.psv;*.zip;*.mcs;*.psx;*.mcb;*.mcx;*.pda;*.ps1;*.ps2;*.vm2;*.vmc;*.mc2;*.mcr;*.mcd;*.gme;*.vm1;*.vmp|" +
        "PS2-Saves (*.psu;*.max;*.cbs;*.sps;*.xps;*.psv;*.zip)|*.psu;*.max;*.cbs;*.sps;*.xps;*.psv;*.zip|" +
        "PS1-Saves (*.mcs;*.psx;*.mcb;*.mcx;*.pda;*.psv;*.ps1)|*.mcs;*.psx;*.mcb;*.mcx;*.pda;*.psv;*.ps1|" +
        L.T("Alle Dateien", "All files") + "|*.*";

    static string KindFromExtension(string path, Platform p)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (p == Platform.PS2)
            return ext switch
            {
                ".ps2" => "PCSX2 (.ps2)",
                ".vm2" => L.T("PS3 virtuelle Karte (.VM2)", "PS3 virtual card (.VM2)"),
                ".vmc" => "OPL VMC (.vmc)",
                ".mc2" => "MemCard PRO2 / SD2PSX (.mc2)",
                ".bin" => L.T("Rohabbild (.bin)", "Raw image (.bin)"),
                _ => L.T($"PS2-Abbild ({ext})", $"PS2 image ({ext})")
            };
        return ext switch
        {
            ".mcr" => "ePSXe/PCSX (.mcr)",
            ".mcd" => "DuckStation/ePSXe (.mcd)",
            ".mc" => L.T("Rohabbild (.mc)", "Raw image (.mc)"),
            ".gme" => "DexDrive (.gme)",
            ".vgs" or ".mem" => "VGS/Bleem (.vgs)",
            ".vmp" => "PSP/Vita (.vmp)",
            ".vm1" => L.T("PS3 virtuelle Karte (.VM1)", "PS3 virtual card (.VM1)"),
            ".srm" => "RetroArch (.srm)",
            ".vmc" => "POPStarter VMC (.vmc)",
            _ => L.T($"PS1-Abbild ({ext})", $"PS1 image ({ext})")
        };
    }

    /// <summary>Opens any supported card from a file or folder.</summary>
    public static MemoryCard Open(string path)
    {
        if (Directory.Exists(path))
        {
            if (Ps2FolderCard.IsFolderCard(path)) return new Ps2FolderCard(path) { LoadedWriteTimeUtc = Directory.GetLastWriteTimeUtc(path) };
            throw new InvalidDataException(L.T("Der Ordner ist keine PCSX2-Ordner-Memory-Card (_pcsx2_superblock fehlt).", "The folder is not a PCSX2 folder memory card (_pcsx2_superblock missing)."));
        }
        var info = new FileInfo(path);
        if (info.Length > 256L * 1024 * 1024) throw new InvalidDataException(L.T("Datei ist zu groß für eine Memory Card.", "File is too large for a memory card."));
        var data = File.ReadAllBytes(path);
        var card = TryOpenBytes(data, path);
        if (card == null)
        {
            var unf = DetectUnformatted(data);
            if (unf != null) throw unf;
            throw new InvalidDataException(L.T("Unbekanntes oder stark beschädigtes Memory-Card-Format. ", "Unknown or badly damaged memory card format. ") +
                                           (ListBackups(path).Count > 0 ? L.T("Für diese Datei gibt es Backups (Datei > Backup wiederherstellen).", "Backups exist for this file (File > Restore backup).") : ""));
        }
        card.LoadedWriteTimeUtc = info.LastWriteTimeUtc;
        return card;
    }

    /// <summary>Recognises never formatted (blank) card images by size.</summary>
    public static UnformattedCardException? DetectUnformatted(byte[] data)
    {
        if (data.Length == 0) return null;
        byte first = data[0];
        if ((first != 0xFF && first != 0x00) || data.AsSpan().IndexOfAnyExcept(first) >= 0) return null;
        const int mbEcc = 1081344, mb = 1048576;
        if (data.Length == Ps1Card.CardSize) return new UnformattedCardException(Platform.PS1, 0, false);
        if (data.Length % mbEcc == 0 && data.Length / mbEcc is 8 or 16 or 32 or 64) return new UnformattedCardException(Platform.PS2, data.Length / mbEcc, true);
        if (data.Length % mb == 0 && data.Length / mb is 8 or 16 or 32 or 64) return new UnformattedCardException(Platform.PS2, data.Length / mb, false);
        return null;
    }

    /// <summary>Formatted image matching an unformatted one.</summary>
    public static byte[] FormatLike(UnformattedCardException u) =>
        u.Platform == Platform.PS1 ? Ps1Card.FormatRaw() : Ps2Card.Format(u.SizeMb, u.Ecc);

    /// <summary>Backups of a card file made by this program, newest first.</summary>
    public static List<FileInfo> ListBackups(string cardPath)
    {
        try
        {
            string name = Path.GetFileName(cardPath.TrimEnd('\\', '/'));
            // file names carry the backup time (name.yyyyMMdd_HHmmss_fff.bak) → sort by name
            return new DirectoryInfo(BackupDir).GetFiles(name + ".*.bak").OrderByDescending(f => f.Name, StringComparer.Ordinal).ToList();
        }
        catch { return new List<FileInfo>(); }
    }

    /// <summary>Restores a backup over the card file (the current file is backed up first).</summary>
    public static void RestoreBackup(string cardPath, string backupFile)
    {
        var bytes = File.ReadAllBytes(backupFile);
        SafeWrite(cardPath, bytes, b =>
        {
            if (TryOpenBytes(b, cardPath) == null && DetectUnformatted(b) == null)
                throw new InvalidDataException(L.T("Das Backup ist selbst keine lesbare Memory Card.", "The backup itself is not a readable memory card."));
        });
    }

    public static MemoryCard? TryOpenBytes(byte[] data, string path)
    {
        if (Ps2Card.IsPs2Image(data)) return new Ps2Card(data, path, KindFromExtension(path, Platform.PS2));
        if (Ps1Card.TryDetect(data, out _, out _)) return new Ps1Card(data, path, KindFromExtension(path, Platform.PS1));
        return null;
    }

    public static (string ext, string filter) NewCardFile(NewCardType t) => t switch
    {
        NewCardType.Ps2Ps3Vm2 => (".VM2", "PS3 VM2 (*.VM2)|*.VM2"),
        NewCardType.Ps2NoEcc_8 => (".vmc", "OPL VMC (*.vmc)|*.vmc|MemCard PRO2 / SD2PSX (*.mc2)|*.mc2|" + L.T("Rohabbild", "Raw image") + " (*.bin)|*.bin"),
        NewCardType.Ps1Raw => (".mcr", "PS1 Raw (*.mcr)|*.mcr|DuckStation (*.mcd)|*.mcd|RetroArch (*.srm)|*.srm|" + L.T("Rohabbild", "Raw image") + " (*.mc)|*.mc"),
        NewCardType.Ps1Ps3Vm1 => (".VM1", "PS3 VM1 (*.VM1)|*.VM1"),
        NewCardType.Ps1Gme => (".gme", "DexDrive (*.gme)|*.gme"),
        NewCardType.Ps1Vgs => (".vgs", "VGS (*.vgs)|*.vgs|Bleem (*.mem)|*.mem"),
        NewCardType.Ps1Vmp => (".VMP", "PSP / PS Vita (*.VMP)|*.VMP"),
        _ => (".ps2", "PCSX2 (*.ps2)|*.ps2")
    };

    public static string Describe(NewCardType t) => t switch
    {
        NewCardType.Ps2Pcsx2_8 => L.T("PS2 – PCSX2 / Standard 8 MB (.ps2, mit ECC)", "PS2 – PCSX2 / standard 8 MB (.ps2, with ECC)"),
        NewCardType.Ps2Pcsx2_16 => "PS2 – PCSX2 16 MB (.ps2)",
        NewCardType.Ps2Pcsx2_32 => "PS2 – PCSX2 32 MB (.ps2)",
        NewCardType.Ps2Pcsx2_64 => "PS2 – PCSX2 64 MB (.ps2)",
        NewCardType.Ps2Ps3Vm2 => L.T("PS2 – PS3 virtuelle Karte 8 MB (.VM2)", "PS2 – PS3 virtual card 8 MB (.VM2)"),
        NewCardType.Ps2NoEcc_8 => L.T("PS2 – 8 MB ohne ECC (OPL .vmc / MemCard PRO2 .mc2 / .bin)", "PS2 – 8 MB without ECC (OPL .vmc / MemCard PRO2 .mc2 / .bin)"),
        NewCardType.Ps1Raw => L.T("PS1 – Rohformat 128 KB (.mcr / .mcd / .srm – ePSXe, DuckStation, RetroArch, PCSX2)", "PS1 – raw format 128 KB (.mcr / .mcd / .srm – ePSXe, DuckStation, RetroArch, PCSX2)"),
        NewCardType.Ps1Ps3Vm1 => L.T("PS1 – PS3 virtuelle Karte (.VM1)", "PS1 – PS3 virtual card (.VM1)"),
        NewCardType.Ps1Gme => "PS1 – DexDrive (.gme)",
        NewCardType.Ps1Vgs => "PS1 – VGS / Bleem (.vgs / .mem)",
        NewCardType.Ps1Vmp => L.T("PS1 – PSP / PS Vita (SCEVMC0.VMP, signiert)", "PS1 – PSP / PS Vita (SCEVMC0.VMP, signed)"),
        _ => t.ToString()
    };

    public static byte[] CreateNew(NewCardType t) => t switch
    {
        NewCardType.Ps2Pcsx2_8 or NewCardType.Ps2Ps3Vm2 => Ps2Card.Format(8, true),
        NewCardType.Ps2Pcsx2_16 => Ps2Card.Format(16, true),
        NewCardType.Ps2Pcsx2_32 => Ps2Card.Format(32, true),
        NewCardType.Ps2Pcsx2_64 => Ps2Card.Format(64, true),
        NewCardType.Ps2NoEcc_8 => Ps2Card.Format(8, false),
        NewCardType.Ps1Raw or NewCardType.Ps1Ps3Vm1 => Ps1Card.FormatRaw(),
        NewCardType.Ps1Gme => WrapPs1(Ps1Container.DexDrive),
        NewCardType.Ps1Vgs => WrapPs1(Ps1Container.Vgs),
        NewCardType.Ps1Vmp => WrapPs1(Ps1Container.Vmp),
        _ => throw new ArgumentOutOfRangeException(nameof(t))
    };

    static byte[] WrapPs1(Ps1Container c) => new Ps1Card(Ps1Card.FormatRaw(), "neu", "neu").Build(c);

    /// <summary>
    /// Safe write: build in a temp file, verify by re-parsing, back up the old file, then atomically replace.
    /// The original file is never touched before the new one has been verified.
    /// </summary>
    public static void SafeWrite(string path, byte[] bytes, Action<byte[]> verify)
    {
        verify(bytes); // throws if the produced image is broken
        string full = Path.GetFullPath(path);
        string dir = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(dir);
        string tmp = full + ".mcmtmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(bytes);
            fs.Flush(true);
        }
        var reread = File.ReadAllBytes(tmp);
        if (!reread.AsSpan().SequenceEqual(bytes))
        {
            File.Delete(tmp);
            throw new IOException(L.T("Geschriebene Daten stimmen nicht überein – Original bleibt unverändert.", "Written data does not match – the original remains unchanged."));
        }
        if (File.Exists(full))
        {
            string bak = BackupPathFor(full);
            File.Copy(full, bak, false);
            PruneBackups(full);
            File.Replace(tmp, full, null, true);
        }
        else
        {
            File.Move(tmp, full);
        }
    }

    public static string BackupPathFor(string cardPath)
    {
        string name = Path.GetFileName(cardPath);
        string p = Path.Combine(BackupDir, $"{name}.{DateTime.Now:yyyyMMdd_HHmmss_fff}.bak");
        return SaveFormats.UniquePath(p);
    }

    /// <summary>Keeps the newest 30 backups per card file name.</summary>
    static void PruneBackups(string cardPath)
    {
        try
        {
            string name = Path.GetFileName(cardPath);
            var files = new DirectoryInfo(BackupDir).GetFiles(name + ".*.bak").OrderByDescending(f => f.Name, StringComparer.Ordinal).Skip(30);
            foreach (var f in files) f.Delete();
        }
        catch { /* backups pruning is best effort */ }
    }

    public static void MoveDirectory(string src, string dst)
    {
        dst = SaveFormats.UniquePath(dst);
        if (string.Equals(Path.GetPathRoot(src), Path.GetPathRoot(dst), StringComparison.OrdinalIgnoreCase))
        {
            Directory.Move(src, dst);
            return;
        }
        CopyDirectory(src, dst);
        Directory.Delete(src, true);
    }

    static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(src)) CopyDirectory(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    /// <summary>Saves a copy of the card to another path/format ("Speichern unter").</summary>
    public static void SaveAs(MemoryCard card, string path, Ps1Container? ps1Target = null, bool? ps2Ecc = null)
    {
        switch (card)
        {
            case Ps1Card p1:
            {
                var target = ps1Target ?? GuessPs1Container(path);
                var bytes = p1.Build(target);
                SafeWrite(path, bytes, b => { _ = new Ps1Card(b, path, "check"); });
                break;
            }
            case Ps2Card p2:
            {
                var img = p2.BuildImage();
                bool wantEcc = ps2Ecc ?? GuessEcc(path, p2.HasEcc);
                if (wantEcc != p2.HasEcc) img = ConvertEcc(img, p2.HasEcc, wantEcc);
                SafeWrite(path, img, b => { _ = new Ps2Card(b, path, "check"); });
                break;
            }
            case Ps2FolderCard fc:
            {
                // folder card → image card: build a fresh 8 MB image and import everything
                var img = Ps2Card.Format(8, ps2Ecc ?? GuessEcc(path, true));
                var nc = new Ps2Card(img, path, "neu");
                foreach (var e in fc.Entries) nc.Import(e.Save);
                var bytes = nc.BuildImage();
                SafeWrite(path, bytes, b => { _ = new Ps2Card(b, path, "check"); });
                break;
            }
        }
    }

    public static Ps1Container GuessPs1Container(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".gme" => Ps1Container.DexDrive,
        ".vgs" or ".mem" => Ps1Container.Vgs,
        ".vmp" => Ps1Container.Vmp,
        _ => Ps1Container.Raw
    };

    static bool GuessEcc(string path, bool current) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".ps2" or ".vm2" => true,
        ".vmc" or ".mc2" => false,
        _ => current
    };

    /// <summary>Converts a PS2 image between the ECC (528 byte pages) and plain (512 byte pages) layout.</summary>
    public static byte[] ConvertEcc(byte[] img, bool fromEcc, bool toEcc)
    {
        const int ps = 512, sp = 16;
        int fromRaw = fromEcc ? ps + sp : ps, toRaw = toEcc ? ps + sp : ps;
        int pages = img.Length / fromRaw;
        var res = new byte[(long)pages * toRaw];
        for (int p = 0; p < pages; p++)
        {
            var page = img.AsSpan(p * fromRaw, ps);
            page.CopyTo(res.AsSpan(p * toRaw, ps));
            if (toEcc)
            {
                var spare = res.AsSpan(p * toRaw + ps, sp);
                if (page.IndexOfAnyExcept((byte)0xFF) < 0) spare.Fill(0xFF);
                else Ecc.CalculatePage(page, spare);
            }
        }
        return res;
    }
}
