namespace TrackBoard.Entities;

/// <summary>What kind of session an archive holds. Informational: the server never opens it.</summary>
public enum BackupKind
{
    Track,
    Drag,
    Other,
}

/// <summary>
/// One recorded session's archive in the private backup bucket. The bytes are the app's own
/// format, gzip'd JSON; the server stores and hands them back, and never parses them.
/// </summary>
/// <remarks>
/// <see cref="BaseEntity.Id"/> is the app's id for the local session. The key is the owner
/// plus that id, so one driver's ids can never collide with another's.
/// </remarks>
public class BackupSession : BaseEntity
{
    public Guid OwnerId { get; set; }

    public User Owner { get; set; } = null!;

    /// <summary>Object path in the backup bucket, under <c>users/{owner}/sessions/{id}/</c>.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>SHA-256 of the archive bytes, lowercase hex, as the app computed it.</summary>
    public string ContentHash { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    /// <summary>The archive format's version, so the app knows how to read an old one.</summary>
    public int SchemaVersion { get; set; }

    public BackupKind Kind { get; set; }

    public DateTimeOffset StartedAt { get; set; }
}

/// <summary>The app's preferences, one opaque JSON document per driver.</summary>
public class BackupSettings
{
    public Guid OwnerId { get; set; }

    public User Owner { get; set; } = null!;

    public int SchemaVersion { get; set; }

    /// <summary>A JSON object, stored as the app sent it. The server does not interpret it.</summary>
    public string Data { get; set; } = "{}";

    public DateTimeOffset UpdatedAt { get; set; }
}
