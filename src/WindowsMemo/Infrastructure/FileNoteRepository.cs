using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using WindowsMemo.Models;

namespace WindowsMemo.Infrastructure;

internal sealed class UnsupportedNoteSchemaException : IOException
{
    public UnsupportedNoteSchemaException() : base("더 새로운 메모 저장 형식입니다. 파일은 보존됩니다.") { }
}

public sealed class FileNoteRepository
{
    // Legacy 0.3.0 note ID; keep its path unchanged for compatibility.
    public static readonly Guid NoteId = new("9eeb0907-8f5e-47c6-92dc-ea3c63e4f846");
    public const int MaxFileBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 16 };
    private readonly string _root;
    private bool _preservePrimary;
    private readonly object _gate = new();
    private sealed record Envelope(int Version, string Protection, byte[] Ciphertext);
    public Guid Id { get; }
    public string PrimaryPath => Path.Combine(_root, Id.ToString("N") + ".json");
    public string BackupPath => PrimaryPath + ".bak";
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsMemo", "Notes");

    public FileNoteRepository(string root) : this(root, NoteId) { }

    public FileNoteRepository(string root, Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("메모 ID가 비어 있습니다.", nameof(id));
        _root = Path.GetFullPath(root);
        Id = id;
    }

    // Discover only app-owned names, including notes with just a surviving backup.
    // Do not decrypt or construct editors while listing notes.
    public static IReadOnlyList<Guid> DiscoverIds(string root)
    {
        string fullRoot = Path.GetFullPath(root);
        CheckRoot(fullRoot);
        if (!Directory.Exists(fullRoot)) return Array.Empty<Guid>();
        var ids = new HashSet<Guid>();
        foreach (string file in Directory.EnumerateFiles(fullRoot))
        {
            string name = Path.GetFileName(file);
            string? stem = name.EndsWith(".json.bak", StringComparison.OrdinalIgnoreCase)
                ? name[..^9] : name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? name[..^5] : null;
            if (stem is null || !Guid.TryParseExact(stem, "N", out Guid id) || id == Guid.Empty) continue;
            CheckLink(file);
            ids.Add(id);
            if (ids.Count > 1000) throw new InvalidDataException("메모 목록이 초기 한도 1,000개를 초과했습니다. 파일은 보존됩니다.");
        }
        return ids.OrderBy(id => id).ToArray();
    }

    private void ValidateIdentity(NoteRecord record)
    {
        Validate(record);
        if (record.Id != Id) throw new InvalidDataException("메모 파일과 데이터 ID가 일치하지 않습니다.");
    }

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
            catch (Exception ex) when (IsStorageError(ex) && ex is not UnsupportedNoteSchemaException)
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
                catch (Exception backupError) when (IsStorageError(backupError) && backupError is not UnsupportedNoteSchemaException)
                { throw new InvalidDataException("원본과 백업을 복원하지 못했습니다. 파일은 보존됩니다.", backupError); }
            }
        }
    }

    public void Save(NoteRecord record)
    {
        lock (_gate)
        {
            ValidateIdentity(record);
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

    // Only a successfully saved trash record may be permanently removed.
    public void Purge()
    {
        lock (_gate)
        {
            var record = Load(_ => { }, out bool recovered) ?? throw new InvalidDataException("메모 파일이 없습니다.");
            if (record.DeletedAtUtc is null) throw new InvalidOperationException("휴지통 메모만 영구 삭제할 수 있습니다.");
            // Repair a backup-only/damaged trash primary before removing recovery copies.
            if (recovered) Save(record);
            string prefix = Path.GetFileName(PrimaryPath) + ".damaged-";
            var files = Directory.EnumerateFiles(_root)
                .Where(file => Path.GetFileName(file).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && Guid.TryParseExact(Path.GetFileName(file)[prefix.Length..], "N", out _))
                .Concat([BackupPath, PrimaryPath]).ToArray();
            CheckPaths();
            foreach (string file in files) CheckLink(file);
            // Primary is last: failure or interruption cannot resurrect the older normal backup.
            foreach (string file in files)
            {
                CheckPaths(); CheckLink(file);
                File.Delete(file);
            }
            _preservePrimary = false;
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
            ValidateIdentity(record);
            return record;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public static void Validate(NoteRecord record)
    {
        if (record.SchemaVersion > 3) throw new UnsupportedNoteSchemaException();
        if (record.SchemaVersion is not (1 or 2 or 3) || (record.SchemaVersion == 1 && record.Hidden)
            || (record.SchemaVersion < 3 && record.DeletedAtUtc is not null)
            || (record.DeletedAtUtc is { } deleted && (!record.Hidden || deleted < record.CreatedAtUtc || deleted > record.UpdatedAtUtc)) || record.Id == Guid.Empty || record.Rtf is not { Length: > 0 and <= 2 * 1024 * 1024 }
            || record.Bounds is not { } b || !double.IsFinite(b.X) || !double.IsFinite(b.Y)
            || Math.Abs(b.X) > 1_000_000 || Math.Abs(b.Y) > 1_000_000
            || !double.IsFinite(b.Width) || !double.IsFinite(b.Height) || b.Width is < 1 or > 100_000 || b.Height is < 1 or > 100_000
            || !double.IsFinite(b.Scale) || b.Scale is < 0.5 or > 8
            || record.CreatedAtUtc == default || record.UpdatedAtUtc < record.CreatedAtUtc)
            throw new InvalidDataException("잘못된 메모 데이터입니다.");
    }

    private void CheckPaths()
    {
        CheckRoot(_root);
        CheckLink(PrimaryPath);
        CheckLink(BackupPath);
    }
    private static void CheckRoot(string root)
    {
        for (string? current = root; current is not null; current = Path.GetDirectoryName(current)) CheckLink(current);
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
