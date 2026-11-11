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
}

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
}

public sealed class CardFullException : Exception
{
    public CardFullException(string msg) : base(msg) { }
}
