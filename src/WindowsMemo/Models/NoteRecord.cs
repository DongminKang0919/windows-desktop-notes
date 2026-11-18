namespace WindowsMemo.Models;

public sealed record NoteBounds(double X, double Y, double Width, double Height, double Scale = 1);

public sealed record NoteRecord(int SchemaVersion, Guid Id, byte[] Rtf, NoteBounds Bounds,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, bool Hidden = false, DateTimeOffset? DeletedAtUtc = null,
    string Paper = "Butter", bool Collapsed = false, bool Archived = false);
