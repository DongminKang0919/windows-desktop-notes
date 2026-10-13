using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using WindowsMemo.Models;

namespace WindowsMemo.Infrastructure;

public sealed class FileNoteRepository
{
    // One app-owned ID while the prototype manages a single note.
    public static readonly Guid NoteId = new("9eeb0907-8f5e-47c6-92dc-ea3c63e4f846");
    public const int MaxFileBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 16 };
    private readonly string _root;
    private bool _preservePrimary;
    private readonly object _gate = new();
    private sealed record Envelope(int Version, string Protection, byte[] Ciphertext);
    public string PrimaryPath => Path.Combine(_root, NoteId.ToString("N") + ".json");
    public string BackupPath => PrimaryPath + ".bak";
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsMemo", "Notes");

    public FileNoteRepository(string root) => _root = Path.GetFullPath(root);

    public NoteRecord? Load(Action<NoteRecord> validateContent, out bool recovered)
    {
        lock (_gate)
        {
            recovered = false;
            CheckPaths();
            bool primary = File.Exists(PrimaryPath), backup = File.Exists(BackupPath);
            if (!primary && !backup) return null;
            try
            {
                if (!primary) throw new InvalidDataException("메모 원본이 없습니다.");
                var record = Read(PrimaryPath);
                validateContent(record);
                return record;
            }
            catch (Exception ex) when (IsStorageError(ex))
            {
                if (!backup) throw new InvalidDataException("메모를 복원하지 못했습니다. 원본 파일은 보존됩니다.", ex);
                try
                {
                    var record = Read(BackupPath);
                    validateContent(record);
                    recovered = true;
                    _preservePrimary = primary;
                    return record;
                }
                catch (Exception backupError) when (IsStorageError(backupError))
                { throw new InvalidDataException("원본과 백업을 복원하지 못했습니다. 파일은 보존됩니다.", backupError); }
            }
        }
    }

    public void Save(NoteRecord record)
    {
        lock (_gate)
        {
            Validate(record);
            CheckPaths();
            Directory.CreateDirectory(_root);
            CheckPaths();
            byte[] plain = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
            byte[] cipher;
            try { cipher = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, "DPAPI-CurrentUser", cipher), JsonOptions);
            if (bytes.Length > MaxFileBytes) throw new InvalidDataException("저장 파일이 크기 한도를 초과했습니다.");
            string temp = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(bytes); stream.Flush(flushToDisk: true); }
                CheckPaths();
                if (_preservePrimary && File.Exists(PrimaryPath))
                {
                    // Keep the damaged primary and known-good backup unchanged.
                    File.Move(PrimaryPath, PrimaryPath + ".damaged-" + Guid.NewGuid().ToString("N"));
                }
                if (_preservePrimary || !File.Exists(PrimaryPath)) File.Move(temp, PrimaryPath);
                else File.Replace(temp, PrimaryPath, BackupPath);
                _preservePrimary = false;
            }
            finally
            {
                CheckPaths();
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
    }

    private NoteRecord Read(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is 0 or > MaxFileBytes) throw new InvalidDataException("잘못된 저장 파일 크기입니다.");
        byte[] bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        var envelope = JsonSerializer.Deserialize<Envelope>(bytes, JsonOptions);
        if (envelope is not { Version: 1, Protection: "DPAPI-CurrentUser", Ciphertext.Length: > 0 })
            throw new InvalidDataException("지원하지 않는 암호화 형식입니다.");
        byte[] plain = ProtectedData.Unprotect(envelope.Ciphertext, null, DataProtectionScope.CurrentUser);
        try
        {
            if (plain.Length > 3 * 1024 * 1024) throw new InvalidDataException("복원 데이터가 크기 한도를 초과했습니다.");
            var record = JsonSerializer.Deserialize<NoteRecord>(plain, JsonOptions) ?? throw new InvalidDataException("메모 데이터가 없습니다.");
            Validate(record);
            return record;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public static void Validate(NoteRecord record)
    {
        if (record.SchemaVersion != 1 || record.Id != NoteId || record.Rtf is not { Length: > 0 and <= 2 * 1024 * 1024 }
            || record.Bounds is not { } b || !double.IsFinite(b.X) || !double.IsFinite(b.Y)
            || Math.Abs(b.X) > 1_000_000 || Math.Abs(b.Y) > 1_000_000
            || !double.IsFinite(b.Width) || !double.IsFinite(b.Height) || b.Width is < 1 or > 100_000 || b.Height is < 1 or > 100_000
            || !double.IsFinite(b.Scale) || b.Scale is < 0.5 or > 8
            || record.CreatedAtUtc == default || record.UpdatedAtUtc < record.CreatedAtUtc)
            throw new InvalidDataException("잘못된 메모 데이터입니다.");
    }

    private void CheckPaths()
    {
        for (string? current = _root; current is not null; current = Path.GetDirectoryName(current)) CheckLink(current);
        CheckLink(PrimaryPath);
        CheckLink(BackupPath);
    }
    private static void CheckLink(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("링크된 저장 경로를 사용할 수 없습니다.");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }
    public static bool IsStorageError(Exception ex) => ex is InvalidDataException or IOException or UnauthorizedAccessException
        or CryptographicException or JsonException or ArgumentException or NotSupportedException;
}
