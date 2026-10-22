using System.IO;
using System.Security.Cryptography;
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
    internal PendingNoteScan Scan()
    {
        FileNoteRepository.CheckRoot(_root);
        if (!Directory.Exists(_root)) return new([], false, 0);
        var candidates = Directory.EnumerateFiles(_root, "*.tmp").Take(TemporaryNoteMaintenance.MaxFiles + 1).ToArray();
        var files = new List<PendingNoteFile>(); int skipped = 0;
        foreach (string path in candidates.Take(TemporaryNoteMaintenance.MaxFiles))
        {
            string name = Path.GetFileName(path);
            if (name.Length != 36 || !name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParseExact(name[..32], "N", out Guid token) || token == Guid.Empty) { skipped++; continue; }
            try
            {
                FileNoteRepository.CheckLink(path);
                files.Add(new(token, File.GetLastWriteTimeUtc(path)));
            }
            catch (Exception ex) when (FileNoteRepository.IsStorageError(ex)) { skipped++; }
        }
        return new(files.OrderByDescending(file => file.ModifiedUtc).ToArray(), candidates.Length > TemporaryNoteMaintenance.MaxFiles, skipped);
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
