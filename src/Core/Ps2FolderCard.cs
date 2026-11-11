namespace MemcardManager.Core;

/// <summary>
/// PCSX2 "folder memory card": a directory containing _pcsx2_superblock and one sub folder per save.
/// Changes are staged in memory; on commit new saves are written to temp folders and renamed,
/// deleted saves are MOVED into the backup folder (never really deleted).
/// </summary>
public sealed class Ps2FolderCard : MemoryCard
{
    public const string SuperblockFile = "_pcsx2_superblock";
    const string IndexFile = "_pcsx2_index";
    const int TotalClusters = 8135; // like a standard 8 MB card

    readonly List<CardEntry> _entries = new();
    readonly List<string> _pendingDeletes = new();
    readonly List<Ps2Save> _pendingImports = new();

    public override Platform Platform => Platform.PS2;
    public override string FormatName => L.T("PCSX2 Ordner-Karte – PS2, 8 MB", "PCSX2 folder card – PS2, 8 MB");
    public override IReadOnlyList<CardEntry> Entries => _entries;
    public override long TotalBytes => TotalClusters * 1024L;
    public override long FreeBytes => Math.Max(0, TotalBytes - UsedBytes());

    public static bool IsFolderCard(string dir) => Directory.Exists(dir) && File.Exists(Path.Combine(dir, SuperblockFile));

    public Ps2FolderCard(string dir)
    {
        FilePath = Path.GetFullPath(dir);
        Load();
    }

    void Load()
    {
        _entries.Clear();
        foreach (var sub in Directory.GetDirectories(FilePath).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            string name = Path.GetFileName(sub);
            if (name.StartsWith('.') || name.EndsWith(".mcmtmp")) continue;
            try
            {
                var save = SaveFormats.LoadPs2Folder(sub);
                _entries.Add(MakeEntry(save, Directory.GetLastWriteTime(sub)));
            }
            catch (Exception ex) { Warnings.Add(L.T($"Ordner '{name}' übersprungen: {ex.Message}", $"Folder '{name}' skipped: {ex.Message}")); }
        }
    }

    static CardEntry MakeEntry(Ps2Save save, DateTime? mod) => new()
    {
        Save = new SaveGame { Ps2 = save },
        Size = save.ClustersNeeded() * 1024L,
        Modified = save.Modified.ToLocal() ?? mod
    };

    long UsedBytes() => _entries.Sum(e => e.Size) + Bin.DivUp(_entries.Count + 2, 2) * 1024L;

    public override bool Exists(string name) => _entries.Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    public override long SpaceNeeded(SaveGame save) => save.Ps2 == null ? long.MaxValue : save.Ps2.ClustersNeeded() * 1024L;

    public override void Delete(CardEntry entry)
    {
        _entries.Remove(entry);
        var pend = _pendingImports.FirstOrDefault(p => p.DirName == entry.Name);
        if (pend != null) _pendingImports.Remove(pend);
        else _pendingDeletes.Add(entry.Name);
        MarkModified();
    }

    public override void Import(SaveGame save)
    {
        var s = save.Ps2 ?? throw new InvalidOperationException(L.T("PS1-Savegames können nicht auf eine PS2-Karte kopiert werden.", "PS1 saves cannot be copied to a PS2 card."));
        s.Validate();
        if (Exists(s.DirName)) throw new InvalidOperationException(L.T($"Savegame '{s.DirName}' existiert bereits auf der Karte.", $"Save '{s.DirName}' already exists on the card."));
        if (SpaceNeeded(save) > FreeBytes) throw new CardFullException(L.T($"Nicht genug Platz für '{s.DirName}'.", $"Not enough space for '{s.DirName}'."));
        if (s.DirName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException(L.T($"'{s.DirName}' ist als Windows-Ordnername ungültig.", $"'{s.DirName}' is not a valid Windows folder name."));
        var copy = s.Clone();
        _pendingImports.Add(copy);
        _entries.Add(MakeEntry(copy, DateTime.Now));
        MarkModified();
    }

    public override void Commit(string path)
    {
        if (!string.Equals(Path.GetFullPath(path), FilePath, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException(L.T("Ordner-Karten können nur an Ort und Stelle gespeichert werden.", "Folder cards can only be saved in place."));
        string backupRoot = Path.Combine(CardIO.BackupDir, TextUtil.SafeFileName(Path.GetFileName(FilePath)) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        foreach (var name in _pendingDeletes.ToList())
        {
            string src = Path.Combine(FilePath, name);
            if (Directory.Exists(src))
            {
                Directory.CreateDirectory(backupRoot);
                string dst = Path.Combine(backupRoot, name);
                CardIO.MoveDirectory(src, dst);
            }
            _pendingDeletes.Remove(name);
        }
        foreach (var s in _pendingImports.ToList())
        {
            string final = Path.Combine(FilePath, s.DirName);
            string tmp = final + ".mcmtmp";
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            SaveFormats.WritePs2Folder(s, tmp, writeIndexHint: false);
            // remove a stale PCSX2 index inside the new folder: PCSX2 then uses file system data
            string idx = Path.Combine(tmp, IndexFile);
            if (File.Exists(idx)) File.Delete(idx);
            Directory.Move(tmp, final);
            _pendingImports.Remove(s);
        }
        MarkSaved();
        Warnings.Clear();
        Load();
    }
}
