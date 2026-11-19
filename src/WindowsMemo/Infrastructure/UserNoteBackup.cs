using System.IO;
using System.Security.Cryptography;
using WindowsMemo.Models;

namespace WindowsMemo.Infrastructure;

internal static class UserNoteBackup
{
    private static ReadOnlySpan<byte> Format => "WindowsMemoBackup/1"u8;
    private const long MaxBytes = 128 * 1024 * 1024;
    private const int MaxNotes = 1000;

    internal static void Write(string path, IReadOnlyList<FileNoteRepository> notes, Action<NoteRecord> validate)
    {
        path = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(path), ".wmbackup", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("백업에는 .wmbackup 확장자가 필요합니다.");
        string directory = Path.GetDirectoryName(path) ?? throw new InvalidDataException("백업 폴더가 필요합니다.");
        FileNoteRepository.CheckRoot(directory);
        string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        bool created = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream))
            {
                created = true;
                writer.Write(Format);
                writer.Write(notes.Count);
                foreach (var repository in notes)
                {
                    var record = repository.Load(validate, out _)
                        ?? throw new InvalidDataException("메모를 읽을 수 없습니다.");
                    byte[] snapshot = FileNoteRepository.EncodeSnapshot(record);
                    if (stream.Position + sizeof(int) + snapshot.Length > MaxBytes)
                        throw new InvalidDataException("백업 크기 한도(128MB)를 초과했습니다.");
                    writer.Write(snapshot.Length);
                    writer.Write(snapshot);
                }
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path);
            created = false;
        }
        finally
        {
            if (created)
            {
                FileNoteRepository.CheckRoot(directory);
                File.Delete(temporary);
            }
        }
    }

    internal static IReadOnlyList<NoteRecord> Read(string path)
    {
        FileNoteRepository.CheckRoot(Path.GetFullPath(path));
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is 0 or > MaxBytes) throw new InvalidDataException("잘못된 백업 크기입니다.");
        byte[] fingerprint = SHA256.HashData(stream);
        stream.Position = 0;
        using var reader = new BinaryReader(stream);
        if (!reader.ReadBytes(Format.Length).AsSpan().SequenceEqual(Format))
            throw new InvalidDataException("지원하지 않는 백업 형식입니다.");
        int count = reader.ReadInt32();
        if (count is < 0 or > MaxNotes) throw new InvalidDataException("잘못된 메모 개수입니다.");
        var records = new List<NoteRecord>(count);
        var ids = new HashSet<Guid>();
        Span<byte> identity = stackalloc byte[48];
        fingerprint.CopyTo(identity);
        for (int i = 0; i < count; i++)
        {
            int length = reader.ReadInt32();
            if (length is < 1 or > FileNoteRepository.MaxFileBytes || length > stream.Length - stream.Position)
                throw new InvalidDataException("잘못된 백업 항목입니다.");
            using var snapshot = new MemoryStream(reader.ReadBytes(length), writable: false);
            var record = FileNoteRepository.ReadSnapshot(snapshot);
            if (!ids.Add(record.Id)) throw new InvalidDataException("중복된 메모가 있습니다.");
            record.Id.TryWriteBytes(identity[32..]);
            records.Add(record with { Id = new Guid(SHA256.HashData(identity).AsSpan(0, 16)),
                Hidden = true, SchemaVersion = NoteRecord.CurrentSchema });
        }
        if (stream.Position != stream.Length) throw new InvalidDataException("잘못된 백업 끝입니다.");
        return records;
    }
}
