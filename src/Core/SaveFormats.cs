using System.IO.Compression;

namespace MemcardManager.Core;

public enum ExportFormat
{
    // PS2
    Psu, Max, Cbs, Psv, Ps2Folder, Ps2Zip,
    // PS1
    Mcs, ActionReplay, Ps1Raw, Ps1Psv
}

public static class SaveFormats
{
    public static string Extension(ExportFormat f) => f switch
    {
        ExportFormat.Psu => ".psu",
        ExportFormat.Max => ".max",
        ExportFormat.Cbs => ".cbs",
        ExportFormat.Psv or ExportFormat.Ps1Psv => ".PSV",
        ExportFormat.Ps2Zip => ".zip",
        ExportFormat.Mcs => ".mcs",
        ExportFormat.ActionReplay => ".psx",
        _ => ""
    };

    public static string Describe(ExportFormat f) => f switch
    {
        ExportFormat.Psu => L.T("PSU (EMS / uLaunchELF / PCSX2) – empfohlen", "PSU (EMS / uLaunchELF / PCSX2) – recommended"),
        ExportFormat.Max => "MAX (Action Replay MAX)",
        ExportFormat.Cbs => "CBS (CodeBreaker)",
        ExportFormat.Psv => L.T("PSV (PS3, signiert)", "PSV (PS3, signed)"),
        ExportFormat.Ps2Folder => L.T("Ordner (Rohdateien)", "Folder (raw files)"),
        ExportFormat.Ps2Zip => L.T("ZIP (Ordner gepackt)", "ZIP (zipped folder)"),
        ExportFormat.Mcs => L.T("MCS (PSXGameEdit / MemcardRex / ePSXe) – empfohlen", "MCS (PSXGameEdit / MemcardRex / ePSXe) – recommended"),
        ExportFormat.ActionReplay => "PSX/MCB (Action Replay / GameShark / Xploder)",
        ExportFormat.Ps1Raw => L.T("RAW (Blockdaten, Dateiname = Savename)", "RAW (block data, file name = save name)"),
        ExportFormat.Ps1Psv => L.T("PSV (PS3, signiert)", "PSV (PS3, signed)"),
        _ => f.ToString()
    };

    public static ExportFormat[] FormatsFor(Platform p) => p == Platform.PS2
        ? new[] { ExportFormat.Psu, ExportFormat.Max, ExportFormat.Cbs, ExportFormat.Ps2Folder, ExportFormat.Ps2Zip, ExportFormat.Psv }
        : new[] { ExportFormat.Mcs, ExportFormat.ActionReplay, ExportFormat.Ps1Raw, ExportFormat.Ps1Psv };

    // ======================================================================== detection / import

    /// <summary>
    /// Loads every save contained in a file or folder (single save files, ZIPs, folders, even complete
    /// memory cards). Nothing on disk is modified.
    /// </summary>
    public static List<SaveGame> LoadAny(string path, List<string>? warnings = null, Rescue? rx = null)
    {
        var res = new List<SaveGame>();
        if (Directory.Exists(path))
        {
            if (Ps2FolderCard.IsFolderCard(path))
            {
                res.AddRange(new Ps2FolderCard(path).Entries.Select(e => e.Save));
                return res;
            }
            if (File.Exists(Path.Combine(path, "icon.sys")) || Directory.GetFiles(path).Any(f => Path.GetFileName(f).Equals("icon.sys", StringComparison.OrdinalIgnoreCase)))
            {
                res.Add(new SaveGame { Ps2 = LoadPs2Folder(path) });
                return res;
            }
            foreach (var f in Directory.GetFiles(path))
            {
                try { res.AddRange(LoadAny(f, warnings, rx == null ? null : new Rescue())); }
                catch (Exception ex) { warnings?.Add($"{Path.GetFileName(f)}: {ex.Message}"); }
            }
            foreach (var d in Directory.GetDirectories(path))
            {
                try { res.AddRange(LoadAny(d, warnings, rx == null ? null : new Rescue())); }
                catch (Exception ex) { warnings?.Add($"{Path.GetFileName(d)}: {ex.Message}"); }
            }
            return res;
        }
        var data = File.ReadAllBytes(path);
        return LoadBytes(data, Path.GetFileName(path), warnings, rx);
    }

    public static List<SaveGame> LoadBytes(byte[] d, string fileName, List<string>? warnings = null, Rescue? rx = null)
    {
        var res = new List<SaveGame>();
        // complete memory cards → all their saves
        var card = CardIO.TryOpenBytes(d, fileName);
        if (card != null)
        {
            res.AddRange(card.Entries.Select(e => e.Save));
            warnings?.AddRange(card.Warnings);
            return res;
        }
        if (d.Length >= 4 && d[0] == 'P' && d[1] == 'K' && d[2] == 3 && d[3] == 4)
            return LoadZip(d, warnings, rx);
        res.Add(LoadSingle(d, fileName, rx));
        return res;
    }

    /// <summary>
    /// Lenient reading for the rescue import ("ohne Gewähr"): truncated data is padded with zeros
    /// and every problem is collected instead of aborting the import.
    /// </summary>
    public sealed class Rescue
    {
        public readonly List<string> Problems = new();
    }

    public static string RescueTag => L.T("Rettungsimport (ohne Gewähr)", "Rescue import (no guarantee)");

    static byte[] Take(byte[] d, long off, long len, Rescue? rx, string what)
    {
        if (len < 0 || len > 64L * 1024 * 1024) throw new InvalidDataException(L.T($"{what}: Größenangabe ungültig.", $"{what}: invalid size."));
        if (off >= 0 && off + len <= d.Length) return d.AsSpan((int)off, (int)len).ToArray();
        if (rx == null) throw new InvalidDataException(L.T($"{what}: Datei ist abgeschnitten.", $"{what}: file is truncated."));
        var r = new byte[len];
        long avail = Math.Clamp(d.Length - off, 0, len);
        if (avail > 0) d.AsSpan((int)off, (int)avail).CopyTo(r);
        rx.Problems.Add(L.T($"{what}: {avail:#,0} von {len:#,0} Bytes", $"{what}: {avail:#,0} of {len:#,0} bytes"));
        return r;
    }

    /// <summary>PS1 data padded to whole 8 KB blocks (rescue).</summary>
    static byte[] PadBlocks(byte[] d, int off, Rescue rx)
    {
        int len = d.Length - off;
        int blocks = Math.Clamp(Bin.DivUp(len, Ps1Save.BlockSize), 1, 15);
        return Take(d, off, (long)blocks * Ps1Save.BlockSize, rx, L.T("Blockdaten", "block data"));
    }

    public static SaveGame LoadSingle(byte[] d, string fileName, Rescue? rx = null)
    {
        var g = LoadSingleCore(d, fileName, rx);
        if (rx != null)
        {
            g.Damaged = true;
            g.Problem = RescueTag + (rx.Problems.Count > 0 ? ": " + string.Join(", ", rx.Problems.Distinct().Take(6)) : "");
        }
        return g;
    }

    static SaveGame LoadSingleCore(byte[] d, string fileName, Rescue? rx)
    {
        if (Bin.StartsWith(d, "Ps2PowerSave")) return new SaveGame { Ps2 = LoadMax(d, rx) };
        if (d.Length >= 4 && d[0] == 'C' && d[1] == 'F' && d[2] == 'U' && d[3] == 0) return new SaveGame { Ps2 = LoadCbs(d, rx) };
        if (d.Length >= 17 && d[0] == 0x0D && d[1] == 0 && d[2] == 0 && d[3] == 0 && Bin.StartsWith(d.AsSpan(4), "SharkPortSave"))
            return new SaveGame { Ps2 = LoadSps(d, rx) };
        if (d.Length >= 0x84 && d[0] == 0 && d[1] == 'V' && d[2] == 'S' && d[3] == 'P') return LoadPsv(d, rx);
        if (Bin.StartsWith(d, "nPort")) throw new NotSupportedException(L.T("nPort-Saves (.npo) werden nicht unterstützt.", "nPort saves (.npo) are not supported."));
        if (LooksLikePsu(d)) return new SaveGame { Ps2 = LoadPsu(d, rx) };

        if (rx != null)
        {
            // lenient PS1 detection: block data does not need to be complete
            if (d.Length > 130 && d[0] == 0x51 && d[128] == 'S' && d[129] == 'C')
                return new SaveGame { Ps1 = new Ps1Save { FileName = Bin.ZStr(d.AsSpan(0x0A, 20)), Data = PadBlocks(d, 128, rx) } };
            if (d.Length > 56 && d[54] == 'S' && d[55] == 'C')
            {
                string n = Bin.ZStr(d.AsSpan(0, 20));
                return new SaveGame { Ps1 = new Ps1Save { FileName = n.Length > 0 ? n : NameFromFile(fileName), Data = PadBlocks(d, 54, rx) } };
            }
            if (d.Length > 2 && d[0] == 'S' && d[1] == 'C')
                return new SaveGame { Ps1 = new Ps1Save { FileName = NameFromFile(fileName), Data = PadBlocks(d, 0, rx) } };
        }

        // PS1 single saves
        if (d.Length >= 128 + Ps1Save.BlockSize && (d.Length - 128) % Ps1Save.BlockSize == 0 && d[0] == 0x51 && d[128] == 'S' && d[129] == 'C')
            return new SaveGame { Ps1 = new Ps1Save { FileName = Bin.ZStr(d.AsSpan(0x0A, 20)), Data = d.AsSpan(128).ToArray() } };
        if (d.Length >= 54 + Ps1Save.BlockSize && (d.Length - 54) % Ps1Save.BlockSize == 0 && d[54] == 'S' && d[55] == 'C')
        {
            string n = Bin.ZStr(d.AsSpan(0, 20));
            return new SaveGame { Ps1 = new Ps1Save { FileName = n.Length > 0 ? n : NameFromFile(fileName), Data = d.AsSpan(54).ToArray() } };
        }
        if (d.Length >= Ps1Save.BlockSize && d.Length % Ps1Save.BlockSize == 0 && d.Length <= 15 * Ps1Save.BlockSize && d[0] == 'S' && d[1] == 'C')
            return new SaveGame { Ps1 = new Ps1Save { FileName = NameFromFile(fileName), Data = (byte[])d.Clone() } };

        throw new InvalidDataException(L.T("Unbekanntes oder beschädigtes Savegame-Format.", "Unknown or damaged save format."));
    }

    static string NameFromFile(string fileName)
    {
        string n = Path.GetFileNameWithoutExtension(fileName);
        if (n.Length > 20) n = n[..20];
        return n;
    }

    static bool LooksLikePsu(byte[] d)
    {
        if (d.Length < 3 * 512) return false;
        var a = Ps2DirEntry.Parse(d);
        var b = Ps2DirEntry.Parse(d.AsSpan(512));
        var c = Ps2DirEntry.Parse(d.AsSpan(1024));
        return a.IsDir && a.Length >= 2 && b.IsDir && c.IsDir && b.Name == "." && c.Name == "..";
    }

    // ------------------------------------------------------------------------ PSU

    public static Ps2Save LoadPsu(byte[] d, Rescue? rx = null)
    {
        var dir = Ps2DirEntry.Parse(d);
        if (!dir.IsDir || dir.Length < 2) throw new InvalidDataException(L.T("Keine gültige PSU-Datei.", "Not a valid PSU file."));
        var s = new Ps2Save { DirName = dir.Name, Mode = dir.Mode, Created = dir.Created, Modified = dir.Modified, Attr = dir.Attr };
        long off = 3 * 512;
        for (int i = 0; i < Math.Min(dir.Length - 2, 1000u); i++)
        {
            if (off + 512 > d.Length)
            {
                if (rx == null) throw new InvalidDataException(L.T("PSU-Datei ist abgeschnitten.", "PSU file is truncated."));
                rx.Problems.Add(L.T($"Verzeichnis abgeschnitten nach {s.Files.Count} von {dir.Length - 2} Dateien", $"directory truncated after {s.Files.Count} of {dir.Length - 2} files"));
                break;
            }
            var e = Ps2DirEntry.Parse(d.AsSpan((int)off));
            off += 512;
            if (!e.Exists) continue;
            if (!e.IsFile)
            {
                if (rx == null) throw new InvalidDataException(L.T("PSU enthält Unterverzeichnisse (nicht unterstützt).", "PSU contains subdirectories (not supported)."));
                rx.Problems.Add(L.T($"Eintrag „{e.Name}“ übersprungen (kein Dateieintrag)", $"entry \"{e.Name}\" skipped (not a file entry)"));
                break; // the following layout is unknown
            }
            s.Files.Add(new Ps2SaveFile
            {
                Name = e.Name, Mode = e.Mode, Created = e.Created, Modified = e.Modified, Attr = e.Attr,
                Data = Take(d, off, e.Length, rx, e.Name)
            });
            off += Bin.DivUp(e.Length, 1024L) * 1024;
        }
        return s;
    }

    public static byte[] SavePsu(Ps2Save s)
    {
        using var ms = new MemoryStream();
        var created = s.Created.OrNow();
        var modified = s.Modified.OrNow();
        ms.Write(new Ps2DirEntry
        {
            Mode = (ushort)((s.Mode & ~Ps2DirEntry.DfFile) | Ps2DirEntry.DfDir | Ps2DirEntry.DfExists),
            Length = (uint)(s.Files.Count + 2), Created = created, Modified = modified, Attr = s.Attr, Name = s.DirName
        }.ToBytes());
        ms.Write(new Ps2DirEntry { Mode = Ps2DirEntry.DefaultDirMode, Created = created, Modified = modified, Name = "." }.ToBytes());
        ms.Write(new Ps2DirEntry { Mode = Ps2DirEntry.DefaultDirMode, Created = created, Modified = modified, Name = ".." }.ToBytes());
        foreach (var f in s.Files)
        {
            ms.Write(new Ps2DirEntry
            {
                Mode = (ushort)((f.Mode & ~Ps2DirEntry.DfDir) | Ps2DirEntry.DfFile | Ps2DirEntry.DfExists),
                Length = (uint)f.Data.Length, Created = f.Created.OrNow(), Modified = f.Modified.OrNow(), Attr = f.Attr, Name = f.Name
            }.ToBytes());
            ms.Write(f.Data);
            int pad = (int)(Bin.DivUp(f.Data.Length, 1024L) * 1024 - f.Data.Length);
            if (pad > 0) ms.Write(new byte[pad]);
        }
        return ms.ToArray();
    }

    // ------------------------------------------------------------------------ MAX

    public static Ps2Save LoadMax(byte[] d, Rescue? rx = null)
    {
        if (d.Length < 0x5C || !Bin.StartsWith(d, "Ps2PowerSave")) throw new InvalidDataException(L.T("Keine MAX-Datei.", "Not a MAX file."));
        string dirName = Bin.ZStr(d.AsSpan(16, 32));
        uint clen = Bin.U32(d, 80);
        uint count = Bin.U32(d, 84);
        uint ulen = Bin.U32(d, 88);
        if (count > 10000 || ulen > 64 * 1024 * 1024) throw new InvalidDataException(L.T("MAX-Kopf ist ungültig.", "MAX header is invalid."));
        int bodyLen = clen == ulen || clen < 4 ? d.Length - 0x5C : (int)Math.Min(clen - 4, (uint)(d.Length - 0x5C));
        var body = d.AsSpan(0x5C, bodyLen).ToArray();
        byte[] u;
        int produced = (int)ulen;
        if (rx == null) u = Lzari.Decode(body, (int)ulen);
        else
        {
            u = Lzari.DecodePartial(body, (int)ulen, out produced);
            if (produced < ulen) rx.Problems.Add(L.T($"Entpacken nach {produced:#,0} von {ulen:#,0} Bytes abgebrochen", $"unpacking aborted after {produced:#,0} of {ulen:#,0} bytes"));
        }
        var now = Ps2Tod.Now;
        var s = new Ps2Save { DirName = dirName, Created = now, Modified = now };
        int off = 0;
        for (int i = 0; i < count; i++)
        {
            if (off + 36 > produced)
            {
                if (rx == null) throw new InvalidDataException(L.T("MAX-Datei ist abgeschnitten.", "MAX file is truncated."));
                rx.Problems.Add(L.T($"nur {s.Files.Count} von {count} Dateien lesbar", $"only {s.Files.Count} of {count} files readable"));
                break;
            }
            int len = (int)Bin.U32(u, off);
            string name = Bin.ZStr(u.AsSpan(off + 4, 32));
            off += 36;
            if (len < 0 || off + (long)len > u.Length) throw new InvalidDataException(L.T("MAX-Datei ist abgeschnitten.", "MAX file is truncated."));
            var data = u.AsSpan(off, len).ToArray();
            if (rx != null && off + len > produced) rx.Problems.Add(L.T($"{name}: {Math.Max(0, produced - off):#,0} von {len:#,0} Bytes", $"{name}: {Math.Max(0, produced - off):#,0} of {len:#,0} bytes"));
            s.Files.Add(new Ps2SaveFile { Name = name, Mode = 0x8497, Created = now, Modified = now, Data = data });
            off += len;
            off = (off + 8 + 15) / 16 * 16 - 8;
        }
        return s;
    }

    public static byte[] SaveMax(Ps2Save s)
    {
        using var body = new MemoryStream();
        foreach (var f in s.Files)
        {
            var h = new byte[36];
            Bin.W32(h, 0, (uint)f.Data.Length);
            Bin.WStr(h.AsSpan(4, 32), f.Name);
            body.Write(h);
            body.Write(f.Data);
            long len = body.Length;
            long pad = (len + 8 + 15) / 16 * 16 - 8 - len;
            if (pad > 0) body.Write(new byte[pad]);
        }
        var raw = body.ToArray();
        var comp = Lzari.Encode(raw);
        string iconName = "";
        var ic = s.IconSys;
        if (ic != null)
        {
            var (a, b) = ic.Title();
            iconName = a.Length > 0 && a[^1] != ' ' ? a + " " + b.TrimEnd() : a + b.TrimEnd();
        }
        var hdr = new byte[0x5C];
        Bin.WStr(hdr.AsSpan(0, 12), "Ps2PowerSave");
        Bin.WStr(hdr.AsSpan(16, 32), s.DirName);
        var iconBytes = TextUtil.EncodeSjis(iconName);
        iconBytes.AsSpan(0, Math.Min(iconBytes.Length, 31)).CopyTo(hdr.AsSpan(48, 32));
        Bin.W32(hdr, 80, (uint)comp.Length + 4);
        Bin.W32(hdr, 84, (uint)s.Files.Count);
        Bin.W32(hdr, 88, (uint)raw.Length);
        uint crc = Crc32.Compute(hdr);
        crc = Crc32.Compute(comp, crc);
        Bin.W32(hdr, 12, crc);
        var res = new byte[hdr.Length + comp.Length];
        hdr.CopyTo(res, 0);
        comp.CopyTo(res, hdr.Length);
        return res;
    }

    // ------------------------------------------------------------------------ CodeBreaker

    static readonly byte[] CbsRc4 =
    {
        0x5f,0x1f,0x85,0x6f,0x31,0xaa,0x3b,0x18,0x21,0xb9,0xce,0x1c,0x07,0x4c,0x9c,0xb4,
        0x81,0xb8,0xef,0x98,0x59,0xae,0xf9,0x26,0xe3,0x80,0xa3,0x29,0x2d,0x73,0x51,0x62,
        0x7c,0x64,0x46,0xf4,0x34,0x1a,0xf6,0xe1,0xba,0x3a,0x0d,0x82,0x79,0x0a,0x5c,0x16,
        0x71,0x49,0x8e,0xac,0x8c,0x9f,0x35,0x19,0x45,0x94,0x3f,0x56,0x0c,0x91,0x00,0x0b,
        0xd7,0xb0,0xdd,0x39,0x66,0xa1,0x76,0x52,0x13,0x57,0xf3,0xbb,0x4e,0xe5,0xdc,0xf0,
        0x65,0x84,0xb2,0xd6,0xdf,0x15,0x3c,0x63,0x1d,0x89,0x14,0xbd,0xd2,0x36,0xfe,0xb1,
        0xca,0x8b,0xa4,0xc6,0x9e,0x67,0x47,0x37,0x42,0x6d,0x6a,0x03,0x92,0x70,0x05,0x7d,
        0x96,0x2f,0x40,0x90,0xc4,0xf1,0x3e,0x3d,0x01,0xf7,0x68,0x1e,0xc3,0xfc,0x72,0xb5,
        0x54,0xcf,0xe7,0x41,0xe4,0x4d,0x83,0x55,0x12,0x22,0x09,0x78,0xfa,0xde,0xa7,0x06,
        0x08,0x23,0xbf,0x0f,0xcc,0xc1,0x97,0x61,0xc5,0x4a,0xe6,0xa0,0x11,0xc2,0xea,0x74,
        0x02,0x87,0xd5,0xd1,0x9d,0xb7,0x7e,0x38,0x60,0x53,0x95,0x8d,0x25,0x77,0x10,0x5e,
        0x9b,0x7f,0xd8,0x6e,0xda,0xa2,0x2e,0x20,0x4f,0xcd,0x8f,0xcb,0xbe,0x5a,0xe0,0xed,
        0x2c,0x9a,0xd4,0xe2,0xaf,0xd0,0xa9,0xe8,0xad,0x7a,0xbc,0xa8,0xf2,0xee,0xeb,0xf5,
        0xa6,0x99,0x28,0x24,0x6c,0x2b,0x75,0x5d,0xf8,0xd3,0x86,0x17,0xfb,0xc0,0x7b,0xb3,
        0x58,0xdb,0xc7,0x4b,0xff,0x04,0x50,0xe9,0x88,0x69,0xc9,0x2a,0xab,0xfd,0x5b,0x1b,
        0x8a,0xd9,0xec,0x27,0x44,0x0e,0x33,0xc8,0x6b,0x93,0x32,0x48,0xb6,0x30,0x43,0xa5
    };

    static byte[] Rc4(ReadOnlySpan<byte> input)
    {
        var s = (byte[])CbsRc4.Clone();
        var o = input.ToArray();
        int j = 0;
        for (int ii = 0; ii < o.Length; ii++)
        {
            int i = (ii + 1) & 0xFF;
            j = (j + s[i]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
            o[ii] ^= s[(s[i] + s[j]) & 0xFF];
        }
        return o;
    }

    public static Ps2Save LoadCbs(byte[] d, Rescue? rx = null)
    {
        if (d.Length < 0x4C) throw new InvalidDataException(L.T("CBS-Datei ist zu kurz.", "CBS file is too short."));
        int hlen = (int)Bin.U32(d, 8);
        int dlen = (int)Bin.U32(d, 12);
        int flen = (int)Bin.U32(d, 16);
        if (hlen < 0x4C || hlen > d.Length || dlen < 0 || dlen > 64 * 1024 * 1024) throw new InvalidDataException(L.T("CBS-Kopf ist ungültig.", "CBS header is invalid."));
        if (rx != null && flen > d.Length - hlen) rx.Problems.Add(L.T($"komprimierte Daten abgeschnitten ({d.Length - hlen:#,0} von {flen:#,0} Bytes)", $"compressed data truncated ({d.Length - hlen:#,0} of {flen:#,0} bytes)"));
        flen = Math.Min(flen, d.Length - hlen);
        var s = new Ps2Save
        {
            DirName = Bin.ZStr(d.AsSpan(0x14, 32)), Created = Ps2Tod.Read(d, 0x34), Modified = Ps2Tod.Read(d, 0x3C),
            Mode = (ushort)Bin.U32(d, 0x48)
        };
        if ((s.Mode & Ps2DirEntry.DfDir) == 0) s.Mode = Ps2DirEntry.DefaultDirMode;
        var dec = Rc4(d.AsSpan(hlen, flen));
        byte[] body;
        using (var zs = new ZLibStream(new MemoryStream(dec), CompressionMode.Decompress))
        using (var outMs = new MemoryStream())
        {
            if (rx == null) zs.CopyTo(outMs);
            else
            {
                // read in small pieces so everything before the damage is kept
                var buf = new byte[4096];
                try { int n; while ((n = zs.Read(buf, 0, buf.Length)) > 0) outMs.Write(buf, 0, n); }
                catch (Exception ex) { rx.Problems.Add(L.T($"Entpacken nach {outMs.Length:#,0} Bytes abgebrochen ({ex.GetType().Name})", $"unpacking aborted after {outMs.Length:#,0} bytes ({ex.GetType().Name})")); }
            }
            body = outMs.ToArray();
        }
        int off = 0;
        while (off + 64 <= body.Length)
        {
            int size = (int)Bin.U32(body, off + 16);
            if (size < 0 || (rx == null && off + 64 + size > body.Length)) throw new InvalidDataException(L.T("CBS-Inhalt ist beschädigt.", "CBS content is damaged."));
            string fname = Bin.ZStr(body.AsSpan(off + 32, 32));
            s.Files.Add(new Ps2SaveFile
            {
                Created = Ps2Tod.Read(body, off), Modified = Ps2Tod.Read(body, off + 8),
                Mode = Bin.U16(body, off + 20), Name = fname,
                Data = Take(body, off + 64, size, rx, fname)
            });
            off += 64 + size;
        }
        foreach (var f in s.Files) if ((f.Mode & Ps2DirEntry.DfFile) == 0) f.Mode = Ps2DirEntry.DefaultFileMode;
        return s;
    }

    public static byte[] SaveCbs(Ps2Save s)
    {
        using var body = new MemoryStream();
        foreach (var f in s.Files)
        {
            var h = new byte[64];
            f.Created.OrNow().Write(h, 0);
            f.Modified.OrNow().Write(h, 8);
            Bin.W32(h, 16, (uint)f.Data.Length);
            Bin.W16(h, 20, (ushort)((f.Mode & ~Ps2DirEntry.DfDir) | Ps2DirEntry.DfFile | Ps2DirEntry.DfExists));
            Bin.WStr(h.AsSpan(32, 32), f.Name);
            body.Write(h);
            body.Write(f.Data);
        }
        var raw = body.ToArray();
        byte[] comp;
        using (var ms = new MemoryStream())
        {
            using (var zs = new ZLibStream(ms, CompressionLevel.Optimal, true)) zs.Write(raw);
            comp = ms.ToArray();
        }
        var enc = Rc4(comp);
        var hdr = new byte[0x128];
        Bin.WStr(hdr.AsSpan(0, 4), "CFU");
        Bin.W32(hdr, 4, 0x1F40);
        Bin.W32(hdr, 8, 0x128);
        Bin.W32(hdr, 12, (uint)raw.Length);
        Bin.W32(hdr, 16, (uint)enc.Length);
        Bin.WStr(hdr.AsSpan(0x14, 32), s.DirName);
        s.Created.OrNow().Write(hdr, 0x34);
        s.Modified.OrNow().Write(hdr, 0x3C);
        Bin.W32(hdr, 0x48, (uint)((s.Mode & ~Ps2DirEntry.DfFile) | Ps2DirEntry.DfDir | Ps2DirEntry.DfExists));
        var title = TextUtil.EncodeSjis(s.Title);
        title.AsSpan(0, Math.Min(title.Length, 71)).CopyTo(hdr.AsSpan(0x5C, 72));
        var res = new byte[hdr.Length + enc.Length];
        hdr.CopyTo(res, 0);
        enc.CopyTo(res, hdr.Length);
        return res;
    }

    // ------------------------------------------------------------------------ SharkPort / X-Port

    public static Ps2Save LoadSps(byte[] d, Rescue? rx = null)
    {
        int off = 17;
        uint ReadU32() { var v = Bin.U32(d, off); off += 4; return v; }
        void SkipStr() { int l = (int)ReadU32(); if (l < 0 || off + l > d.Length) throw new InvalidDataException(L.T("SPS-Datei ist beschädigt.", "SPS file is damaged.")); off += l; }
        ReadU32();      // save type
        SkipStr();      // name
        SkipStr();      // date
        SkipStr();      // comment
        ReadU32();      // total length
        if (off + 98 > d.Length) throw new InvalidDataException(L.T("SPS-Datei ist abgeschnitten.", "SPS file is truncated."));
        int hl = Bin.U16(d, off);
        string dirName = Bin.ZStr(d.AsSpan(off + 2, 64));
        int dirLen = (int)Bin.U32(d, off + 66);
        int dirMode = Bin.U16(d, off + 78);
        var created = Ps2Tod.Read(d, off + 82);
        var modified = Ps2Tod.Read(d, off + 90);
        off += Math.Max(98, hl);
        dirMode = ((dirMode & 0xFF) << 8) | (dirMode >> 8);
        dirMode |= Ps2DirEntry.DfExists;
        if ((dirMode & Ps2DirEntry.DfDir) == 0 || dirLen < 2) throw new InvalidDataException(L.T("SPS-Verzeichniseintrag ungültig.", "SPS directory entry invalid."));
        var s = new Ps2Save { DirName = dirName, Mode = (ushort)dirMode, Created = created, Modified = modified };
        for (int i = 0; i < Math.Min(dirLen - 2, 1000); i++)
        {
            if (off + 98 > d.Length)
            {
                if (rx == null) throw new InvalidDataException(L.T("SPS-Datei ist abgeschnitten.", "SPS file is truncated."));
                rx.Problems.Add(L.T($"nur {s.Files.Count} von {dirLen - 2} Dateien vorhanden", $"only {s.Files.Count} of {dirLen - 2} files present"));
                break;
            }
            int fhl = Bin.U16(d, off);
            string name = Bin.ZStr(d.AsSpan(off + 2, 64));
            int flen = (int)Bin.U32(d, off + 66);
            int mode = Bin.U16(d, off + 78);
            var fc = Ps2Tod.Read(d, off + 82);
            var fm = Ps2Tod.Read(d, off + 90);
            off += Math.Max(98, fhl);
            mode = ((mode & 0xFF) << 8) | (mode >> 8);
            mode |= Ps2DirEntry.DfExists;
            if (flen < 0) throw new InvalidDataException(L.T("SPS-Datei ist beschädigt.", "SPS file is damaged."));
            s.Files.Add(new Ps2SaveFile { Name = name, Mode = (ushort)mode, Created = fc, Modified = fm, Data = Take(d, off, flen, rx, name) });
            off += flen;
        }
        return s;
    }

    // ------------------------------------------------------------------------ PSV (PS3)

    public static SaveGame LoadPsv(byte[] d, Rescue? rx = null)
    {
        // 0x38 = sub-header size (0x14 PS1 / 0x2C PS2), 0x3C = type (1 = PS1, 2 = PS2)
        uint type = Bin.U32(d, 0x3C);
        if (type == 1)
        {
            int size = (int)Bin.U32(d, 0x40);
            int pos = (int)Bin.U32(d, 0x44);
            if (pos <= 0 || pos >= d.Length) pos = 0x84;
            string name = Bin.ZStr(d.AsSpan(0x64, 20));
            if (rx != null)
            {
                if (size <= 0 || size > 15 * Ps1Save.BlockSize) size = d.Length - pos;
                size = Math.Clamp(Bin.DivUp(size, Ps1Save.BlockSize), 1, 15) * Ps1Save.BlockSize;
                return new SaveGame { Ps1 = new Ps1Save { FileName = name, Data = Take(d, pos, size, rx, L.T("Blockdaten", "block data")) } };
            }
            if (size <= 0 || pos + size > d.Length) size = d.Length - pos;
            size = size / Ps1Save.BlockSize * Ps1Save.BlockSize;
            return new SaveGame { Ps1 = new Ps1Save { FileName = name, Data = d.AsSpan(pos, size).ToArray() } };
        }
        if (type == 2)
        {
            if (d.Length < 0xA0) throw new InvalidDataException(L.T("PSV-Datei ist zu kurz.", "PSV file is too short."));
            int numFiles = (int)Bin.U32(d, 0x64);
            var s = new Ps2Save
            {
                Created = Ps2Tod.Read(d, 0x68), Modified = Ps2Tod.Read(d, 0x70),
                Attr = 0, Mode = (ushort)(Bin.U32(d, 0x7C) & 0xFFFF), DirName = Bin.ZStr(d.AsSpan(0x80, 32))
            };
            if ((s.Mode & Ps2DirEntry.DfDir) == 0) s.Mode = Ps2DirEntry.DefaultDirMode;
            if (numFiles < 0 || numFiles > 1000) throw new InvalidDataException(L.T("PSV-Kopf ist ungültig.", "PSV header is invalid."));
            int off = 0xA0;
            for (int i = 0; i < numFiles; i++)
            {
                if (off + 60 > d.Length)
                {
                    if (rx == null) throw new InvalidDataException(L.T("PSV-Datei ist abgeschnitten.", "PSV file is truncated."));
                    rx.Problems.Add(L.T($"nur {s.Files.Count} von {numFiles} Dateien vorhanden", $"only {s.Files.Count} of {numFiles} files present"));
                    break;
                }
                var f = new Ps2SaveFile
                {
                    Created = Ps2Tod.Read(d, off), Modified = Ps2Tod.Read(d, off + 8),
                    Mode = (ushort)(Bin.U32(d, off + 20) & 0xFFFF), Name = Bin.ZStr(d.AsSpan(off + 24, 32))
                };
                int size = (int)Bin.U32(d, off + 16);
                int pos = (int)Bin.U32(d, off + 56);
                if (size < 0 || pos < 0) throw new InvalidDataException(L.T($"PSV-Datei ist beschädigt ({f.Name}).", $"PSV file is damaged ({f.Name})."));
                f.Data = Take(d, pos, size, rx, f.Name);
                if ((f.Mode & Ps2DirEntry.DfFile) == 0) f.Mode = Ps2DirEntry.DefaultFileMode;
                s.Files.Add(f);
                off += 60;
            }
            return new SaveGame { Ps2 = s };
        }
        throw new InvalidDataException(L.T("Unbekannter PSV-Typ.", "Unknown PSV type."));
    }

    // ------------------------------------------------------------------------ folders / zip

    public static Ps2Save LoadPs2Folder(string dir)
    {
        var info = new DirectoryInfo(dir);
        var s = new Ps2Save
        {
            DirName = info.Name,
            Created = Ps2Tod.FromDateTime(info.CreationTime),
            Modified = Ps2Tod.FromDateTime(info.LastWriteTime)
        };
        foreach (var fi in info.GetFiles().OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            if (fi.Name.StartsWith("_pcsx2_", StringComparison.OrdinalIgnoreCase)) continue;
            s.Files.Add(new Ps2SaveFile
            {
                Name = fi.Name, Data = File.ReadAllBytes(fi.FullName),
                Created = Ps2Tod.FromDateTime(fi.CreationTime), Modified = Ps2Tod.FromDateTime(fi.LastWriteTime)
            });
        }
        if (info.GetDirectories().Length > 0) s.Warnings.Add(L.T("Unterordner werden ignoriert.", "Subfolders are ignored."));
        // put icon.sys first like the PS2 does
        var ic = s.Files.FindIndex(f => f.Name.Equals("icon.sys", StringComparison.OrdinalIgnoreCase));
        if (ic > 0) { var f = s.Files[ic]; s.Files.RemoveAt(ic); s.Files.Insert(0, f); }
        return s;
    }

    public static void WritePs2Folder(Ps2Save s, string dir, bool writeIndexHint = false)
    {
        Directory.CreateDirectory(dir);
        foreach (var f in s.Files)
        {
            if (f.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new IOException(L.T($"Dateiname '{f.Name}' ist unter Windows ungültig.", $"File name '{f.Name}' is invalid on Windows."));
            string p = Path.Combine(dir, f.Name);
            File.WriteAllBytes(p, f.Data);
            var c = f.Created.ToLocal();
            var m = f.Modified.ToLocal();
            if (c != null) File.SetCreationTime(p, c.Value);
            if (m != null) File.SetLastWriteTime(p, m.Value);
        }
        var dc = s.Created.ToLocal();
        var dm = s.Modified.ToLocal();
        if (dc != null) Directory.SetCreationTime(dir, dc.Value);
        if (dm != null) Directory.SetLastWriteTime(dir, dm.Value);
    }

    static List<SaveGame> LoadZip(byte[] d, List<string>? warnings, Rescue? rx = null)
    {
        var res = new List<SaveGame>();
        using var za = new ZipArchive(new MemoryStream(d), ZipArchiveMode.Read);
        var groups = new Dictionary<string, List<ZipArchiveEntry>>(StringComparer.Ordinal);
        foreach (var e in za.Entries)
        {
            if (e.FullName.EndsWith('/')) continue;
            string full = e.FullName.Replace('\\', '/');
            int slash = full.LastIndexOf('/');
            string folder = slash < 0 ? "" : full[..slash];
            byte[] data;
            using (var st = e.Open()) using (var ms = new MemoryStream()) { st.CopyTo(ms); data = ms.ToArray(); }
            // save files inside the zip
            string ext = Path.GetExtension(e.Name).ToLowerInvariant();
            if (ext is ".psu" or ".max" or ".cbs" or ".sps" or ".xps" or ".psv" or ".mcs" or ".psx" or ".mcb" or ".mcx" or ".pda")
            {
                try { res.AddRange(LoadBytes(data, e.Name, warnings, rx == null ? null : new Rescue())); }
                catch (Exception ex) { warnings?.Add($"{e.FullName}: {ex.Message}"); }
                continue;
            }
            if (!groups.TryGetValue(folder, out var list)) groups[folder] = list = new();
            list.Add(e);
        }
        foreach (var (folder, entries) in groups)
        {
            if (!entries.Any(x => x.Name.Equals("icon.sys", StringComparison.OrdinalIgnoreCase)))
            {
                // maybe single raw saves / cards
                foreach (var e in entries)
                {
                    try
                    {
                        using var st = e.Open(); using var ms = new MemoryStream(); st.CopyTo(ms);
                        res.AddRange(LoadBytes(ms.ToArray(), e.Name, warnings, rx == null ? null : new Rescue()));
                    }
                    catch (Exception ex) { warnings?.Add($"{e.FullName}: {ex.Message}"); }
                }
                continue;
            }
            string dirName = folder.Length == 0 ? "UNBENANNT" : folder[(folder.LastIndexOf('/') + 1)..];
            var s = new Ps2Save { DirName = dirName };
            foreach (var e in entries.OrderBy(x => x.Name.Equals("icon.sys", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
            {
                using var st = e.Open(); using var ms = new MemoryStream(); st.CopyTo(ms);
                var t = Ps2Tod.FromDateTime(e.LastWriteTime.LocalDateTime);
                s.Files.Add(new Ps2SaveFile { Name = e.Name, Data = ms.ToArray(), Created = t, Modified = t });
            }
            res.Add(new SaveGame { Ps2 = s });
        }
        return res;
    }

    // ======================================================================== export

    /// <summary>Builds the bytes for single file formats.</summary>
    public static byte[] ExportBytes(SaveGame g, ExportFormat f)
    {
        if (g.Ps2 != null)
        {
            return f switch
            {
                ExportFormat.Psu => SavePsu(g.Ps2),
                ExportFormat.Max => SaveMax(g.Ps2),
                ExportFormat.Cbs => SaveCbs(g.Ps2),
                ExportFormat.Psv => PsSign.MakePs2Psv(g.Ps2),
                ExportFormat.Ps2Zip => SaveZip(g.Ps2),
                _ => throw new NotSupportedException(L.T($"Format {f} ist für PS2-Saves nicht möglich.", $"Format {f} is not possible for PS2 saves."))
            };
        }
        var p = g.Ps1!;
        switch (f)
        {
            case ExportFormat.Mcs:
            {
                var r = new byte[128 + p.Data.Length];
                r[0] = 0x51;
                Bin.W32(r, 4, (uint)p.Data.Length);
                Bin.W16(r, 8, 0xFFFF);
                Bin.WStr(r.AsSpan(0x0A, 20), p.FileName);
                byte x = 0;
                for (int i = 0; i < 127; i++) x ^= r[i];
                r[127] = x;
                p.Data.CopyTo(r, 128);
                return r;
            }
            case ExportFormat.ActionReplay:
            {
                var r = new byte[54 + p.Data.Length];
                Bin.WStr(r.AsSpan(0, 20), p.FileName);
                var t = TextUtil.EncodeSjis(p.Title);
                // the AR header carries a plain title; keep it ASCII safe
                for (int i = 0; i < Math.Min(t.Length, 32); i++) r[21 + i] = t[i] < 0x80 ? t[i] : (byte)' ';
                p.Data.CopyTo(r, 54);
                return r;
            }
            case ExportFormat.Ps1Raw:
                return (byte[])p.Data.Clone();
            case ExportFormat.Ps1Psv:
                return PsSign.MakePs1Psv(p);
            default:
                throw new NotSupportedException(L.T($"Format {f} ist für PS1-Saves nicht möglich.", $"Format {f} is not possible for PS1 saves."));
        }
    }

    static byte[] SaveZip(Ps2Save s)
    {
        using var ms = new MemoryStream();
        using (var za = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            foreach (var f in s.Files)
            {
                var e = za.CreateEntry(s.DirName + "/" + f.Name, CompressionLevel.Optimal);
                var m = f.Modified.ToLocal();
                if (m != null && m.Value.Year >= 1980) e.LastWriteTime = m.Value;
                using var st = e.Open();
                st.Write(f.Data);
            }
        }
        return ms.ToArray();
    }

    /// <summary>Suggested file name (without directory) for an export.</summary>
    public static string SuggestName(SaveGame g, ExportFormat f)
    {
        if (f is ExportFormat.Psv or ExportFormat.Ps1Psv) return PsvFileName(g.Name);
        return TextUtil.SafeFileName(g.Name) + Extension(f);
    }

    /// <summary>
    /// File name as the PS3 writes it into PS3/EXPORT/PSV: the first 12 characters (e.g. "BESLES-53703")
    /// followed by the rest of the save name in upper-case hex ("kk" → "6B6B").
    /// </summary>
    public static string PsvFileName(string name)
    {
        var bytes = System.Text.Encoding.Latin1.GetBytes(name);
        int head = Math.Min(12, bytes.Length);
        var sb = new System.Text.StringBuilder(System.Text.Encoding.Latin1.GetString(bytes, 0, head));
        for (int i = head; i < bytes.Length; i++) sb.Append(bytes[i].ToString("X2"));
        return TextUtil.SafeFileName(sb.ToString()) + ".PSV";
    }

    /// <summary>Exports to disk. Never overwrites an existing file/folder: appends (2), (3)…</summary>
    public static string ExportTo(SaveGame g, ExportFormat f, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        string name = SuggestName(g, f);
        string path = UniquePath(Path.Combine(targetDir, name));
        if (f == ExportFormat.Ps2Folder)
        {
            var s = g.Ps2 ?? throw new NotSupportedException(L.T("Ordner-Export nur für PS2-Saves.", "Folder export only for PS2 saves."));
            string tmp = path + ".mcmtmp";
            WritePs2Folder(s, tmp);
            Directory.Move(tmp, path);
            return path;
        }
        var bytes = ExportBytes(g, f);
        // verify by reading back what we produced
        var check = f == ExportFormat.Ps2Zip ? LoadZip(bytes, null).FirstOrDefault() : LoadSingle(bytes, Path.GetFileName(path));
        bool ok = check != null && (f == ExportFormat.Ps1Raw
            ? check.Ps1 != null && check.Ps1.Data.AsSpan().SequenceEqual(g.Ps1!.Data)
            : SameContent(g, check));
        if (!ok) throw new InvalidDataException(L.T("Überprüfung des Exports fehlgeschlagen – nichts geschrieben.", "Verification of the export failed – nothing written."));
        string tmpFile = path + ".mcmtmp";
        File.WriteAllBytes(tmpFile, bytes);
        File.Move(tmpFile, path);
        return path;
    }

    public static string UniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!, n = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        if (Directory.Exists(path)) { n = Path.GetFileName(path); ext = ""; }
        for (int i = 2; ; i++)
        {
            string p = Path.Combine(dir, $"{n} ({i}){ext}");
            if (!File.Exists(p) && !Directory.Exists(p)) return p;
        }
    }

    /// <summary>Compares names and file data of two saves (timestamps excluded).</summary>
    public static bool SameContent(SaveGame a, SaveGame b)
    {
        if (a.Ps2 != null && b.Ps2 != null)
        {
            if (a.Ps2.DirName != b.Ps2.DirName || a.Ps2.Files.Count != b.Ps2.Files.Count) return false;
            foreach (var f in a.Ps2.Files)
            {
                var o = b.Ps2.Files.FirstOrDefault(x => x.Name == f.Name);
                if (o == null || !o.Data.AsSpan().SequenceEqual(f.Data)) return false;
            }
            return true;
        }
        if (a.Ps1 != null && b.Ps1 != null)
            return a.Ps1.FileName == b.Ps1.FileName && a.Ps1.Data.AsSpan().SequenceEqual(b.Ps1.Data);
        return false;
    }
}
