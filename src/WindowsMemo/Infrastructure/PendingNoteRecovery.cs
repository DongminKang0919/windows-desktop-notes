using System.IO;
using System.Security.Cryptography;
using System.Text;
using WindowsMemo.Models;

namespace WindowsMemo.Infrastructure;

internal sealed record PendingNoteFile(Guid Token, DateTime ModifiedUtc)
{
    public string Label => $"{ModifiedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {Token.ToString("N")[..8]}";
}
internal sealed record PendingNoteScan(IReadOnlyList<PendingNoteFile> Files, bool Limited, int Skipped);
internal sealed record PendingNotePreview(Guid Token, string Fingerprint, NoteRecord Record);

// Never writes, promotes, or removes the source. Only caller-selected GUID.tmp paths are opened.
internal sealed class PendingNoteRecovery(string root)
{
    private readonly string _root = Path.GetFullPath(root);
    // Persisted identity contract: keep v1 stable across app versions and process restarts.
    internal static Guid CopyId(Guid token, string fingerprint)
    {
        if (token == Guid.Empty || fingerprint is not { Length: 64 } || fingerprint.Any(c => !char.IsAsciiHexDigit(c)))
            throw new ArgumentException("잘못된 복구 식별자입니다.");
        byte[] hash = SHA256.HashData(Encoding.ASCII.GetBytes($"WindowsMemo.PendingCopy.v1:{token:N}:{fingerprint.ToUpperInvariant()}"));
        hash[6] = (byte)((hash[6] & 0x0f) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }
    internal PendingNoteScan Scan()
    {
        using var cursor = new PendingNoteCursor(_root);
        return cursor.NextPage();
    }
    internal PendingNotePreview Read(Guid token, string? expectedFingerprint = null)
    {
        if (token == Guid.Empty) throw new ArgumentException("잘못된 임시 파일입니다.");
        FileNoteRepository.CheckRoot(_root);
        using var stream = TemporaryNoteMaintenance.OpenSnapshot(Path.Combine(_root, token.ToString("N") + ".tmp"), canDelete: false);
        if (stream.Length is 0 or > FileNoteRepository.MaxFileBytes) throw new InvalidDataException("임시 파일 크기 한도를 초과했습니다.");
        string fingerprint = Convert.ToHexString(SHA256.HashData(stream));
        if (expectedFingerprint is not null && fingerprint != expectedFingerprint)
            throw new InvalidDataException("미리보기 이후 파일이 변경되었습니다.");
        stream.Position = 0;
        var record = FileNoteRepository.ReadSnapshot(stream);
        FileNoteRepository.CheckRoot(_root);
        return new(token, fingerprint, record);
    }
}
