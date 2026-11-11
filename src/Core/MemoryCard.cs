namespace MemcardManager.Core;

/// <summary>One save as shown in the card list.</summary>
public sealed class CardEntry
{
    public required SaveGame Save { get; init; }
    public string Name => Save.Name;
    public string Title => Save.Title;
    public long Size { get; init; }
    public DateTime? Modified { get; init; }
    public int Blocks { get; init; }
    public object? Tag { get; init; }
    public Platform Platform => Save.Platform;
    public Region Region => Save.Region;

    /// <summary>Save data is damaged (only partially readable).</summary>
    public bool Damaged => Save.Damaged;

    /// <summary>Description of a problem (damage or a minor issue like a missing icon).</summary>
    public string? Problem => Save.Problem;
}

public enum IssueLevel { Info, Warning, Error }

/// <summary>A problem found by <see cref="MemoryCard.Diagnose"/>.</summary>
public sealed record CardIssue(IssueLevel Level, string Text, bool Repairable = false);

/// <summary>Base class for all memory card kinds. All changes happen in memory until <see cref="Commit"/>.</summary>
public abstract class MemoryCard
{
    public string FilePath { get; protected set; } = "";
    public abstract Platform Platform { get; }
    public abstract string FormatName { get; }
    public virtual bool CanWrite => true;
    public virtual string ReadOnlyReason => "";
    public bool IsModified { get; protected set; }
    public List<string> Warnings { get; } = new();
    public abstract IReadOnlyList<CardEntry> Entries { get; }
    public abstract long TotalBytes { get; }
    public abstract long FreeBytes { get; }
    public abstract bool Exists(string name);
    public abstract void Delete(CardEntry entry);

    /// <summary>Space needed on this card for the save, in bytes (cluster/block rounded).</summary>
    public abstract long SpaceNeeded(SaveGame save);

    /// <summary>Imports a save. Throws if the name exists or space is insufficient.</summary>
    public abstract void Import(SaveGame save);

    /// <summary>Writes the card to disk (with backup + verification, see CardIO).</summary>
    public abstract void Commit(string path);

    /// <summary>File system timestamp of the card at load time (to detect external changes).</summary>
    public DateTime LoadedWriteTimeUtc { get; set; }

    public string DisplayName => string.IsNullOrEmpty(FilePath) ? "(neu)" : Path.GetFileName(FilePath.TrimEnd('\\', '/'));

    public CardEntry? Find(string name) => Entries.FirstOrDefault(e => e.Name == name);

    public void MarkSaved() => IsModified = false;
    protected void MarkModified() => IsModified = true;

    // ------------------------------------------------------------------ health

    /// <summary>Checks the card structure and the saves. Never modifies anything.</summary>
    public virtual List<CardIssue> Diagnose()
    {
        var list = Warnings.Select(w => new CardIssue(IssueLevel.Warning, w)).ToList();
        AddSaveIssues(list);
        return list;
    }

    protected void AddSaveIssues(List<CardIssue> list)
    {
        foreach (var e in Entries)
        {
            if (e.Damaged) list.Add(new CardIssue(IssueLevel.Error, $"Savegame „{e.Name}“ ist beschädigt: {e.Problem} – Rettungs-Export möglich."));
            else if (e.Problem != null) list.Add(new CardIssue(IssueLevel.Info, $"„{e.Name}“: {e.Problem}"));
        }
    }

    /// <summary>Fixes all repairable issues in memory (the file changes only when saved). Returns a report.</summary>
    public virtual List<string> Repair() => new();

    /// <summary>Deleted saves that can still be restored completely.</summary>
    public virtual IReadOnlyList<CardEntry> DeletedSaves => Array.Empty<CardEntry>();

    public virtual void Undelete(CardEntry entry) => throw new NotSupportedException();
}

public sealed class CardFullException : Exception
{
    public CardFullException(string msg) : base(msg) { }
}

/// <summary>The file is a memory card image that has never been formatted (all 0xFF / 0x00).</summary>
public sealed class UnformattedCardException : Exception
{
    public Platform Platform { get; }
    public int SizeMb { get; }
    public bool Ecc { get; }

    public UnformattedCardException(Platform p, int sizeMb, bool ecc)
        : base("Die Karte ist unformatiert (leer).")
    {
        Platform = p;
        SizeMb = sizeMb;
        Ecc = ecc;
    }
}
