using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Documents;
using WindowsMemo.Infrastructure;
using WindowsMemo.Models;
using WindowsMemo.Services;

namespace WindowsMemo.Tests;

internal static class TemporaryFileTests
{
    private static void Require(bool value) { if (!value) throw new InvalidOperationException("Temporary file maintenance assertion failed."); }
    internal static void Run(Action<string, Action> check)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "temporary-tests", Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.UtcNow;
        var doc = new FlowDocument(new Paragraph(new Run("정상 저장본과 중복인 임시 내용")));
        var baseline = new NoteRecord(4, FileNoteRepository.NoteId, RtfDocumentCodec.Save(doc), new(48, 64, 320, 300), now.AddDays(-10), now.AddDays(-2));
        FileNoteRepository Repository(string name) => new(Path.Combine(root, name));
        TemporaryFileResult Clean(string name) => TemporaryNoteMaintenance.Run(Path.Combine(root, name), now, value => RtfDocumentCodec.Load(value.Rtf));
        string Pending(string name, NoteRecord record, bool recent = false, string? fileName = null)
        {
            var seed = new FileNoteRepository(Path.Combine(root, "seeds", Guid.NewGuid().ToString("N")), record.Id);
            seed.Save(record);
            string folder = Path.Combine(root, name); Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, fileName ?? Guid.NewGuid().ToString("N") + ".tmp");
            File.Copy(seed.PrimaryPath, path);
            File.SetLastWriteTimeUtc(path, now.AddDays(recent ? 0 : -2).UtcDateTime);
            return path;
        }
        check("임시 파일: 오래된 동일 스냅샷만 삭제·원본/백업 보존", () =>
        {
            var repo = Repository("duplicate"); repo.Save(baseline); repo.Save(baseline with { UpdatedAtUtc = now.AddDays(-1) });
            byte[] primary = File.ReadAllBytes(repo.PrimaryPath), backup = File.ReadAllBytes(repo.BackupPath);
            string path = Pending("duplicate", baseline);
            var result = TemporaryNoteMaintenance.Run(Path.Combine(root, "duplicate"), now, value =>
            {
                RtfDocumentCodec.Load(value.Rtf);
                foreach (string lockedPath in new[] { path, repo.PrimaryPath })
                {
                    bool blocked = false;
                    try { using var attempt = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete); }
                    catch (IOException) { blocked = true; }
                    Require(blocked);
                }
            });
            Require(result.Removed == 1 && result.Preserved == 0 && result.Errors == 0 && !result.Limited && !File.Exists(path));
            Require(File.ReadAllBytes(repo.PrimaryPath).SequenceEqual(primary) && File.ReadAllBytes(repo.BackupPath).SequenceEqual(backup));
        });
        check("임시 파일: 본문·서식·위치·상태·ID·최신 시각 차이는 보존", () =>
        {
            var repo = Repository("unique"); repo.Save(baseline with { UpdatedAtUtc = now.AddDays(-1) });
            var otherDoc = new FlowDocument(new Paragraph(new Run("새로운 미완료 본문")));
            var formatted = new FlowDocument(new Paragraph(new Run("정상 저장본과 중복인 임시 내용") { FontWeight = System.Windows.FontWeights.Bold }));
            NoteRecord[] variants = [
                baseline with { Rtf = RtfDocumentCodec.Save(otherDoc) },
                baseline with { Rtf = RtfDocumentCodec.Save(formatted) },
                baseline with { Bounds = baseline.Bounds with { X = 100 } },
                baseline with { Bounds = baseline.Bounds with { Width = 420 } },
                baseline with { Bounds = baseline.Bounds with { Scale = 1.5 } },
                baseline with { Paper = "Peach" }, baseline with { Collapsed = true },
                baseline with { Hidden = true }, baseline with { Hidden = true, Archived = true },
                baseline with { Hidden = true, DeletedAtUtc = now.AddDays(-3) },
                baseline with { Id = Guid.NewGuid() }, baseline with { CreatedAtUtc = now.AddDays(-11) },
                baseline with { UpdatedAtUtc = now }, baseline with { SchemaVersion = 3 }
            ];
            string[] paths = variants.Select(record => Pending("unique", record)).ToArray();
            var before = paths.Select(File.ReadAllBytes).ToArray(); var result = Clean("unique");
            Require(result.Removed == 0 && result.Preserved == variants.Length);
            for (int i = 0; i < paths.Length; i++) Require(File.ReadAllBytes(paths[i]).SequenceEqual(before[i]));
        });
        check("임시 파일: 최근·사용 중·읽기 전용 파일 보존", () =>
        {
            Repository("busy").Save(baseline with { UpdatedAtUtc = now.AddDays(-1) });
            string recent = Pending("busy", baseline, recent: true), locked = Pending("busy", baseline), readOnly = Pending("busy", baseline);
            File.SetAttributes(readOnly, FileAttributes.ReadOnly);
            try
            {
                using var held = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                var result = Clean("busy");
                Require(result.Removed == 0 && result.Preserved == 3 && File.Exists(recent) && File.Exists(locked) && File.Exists(readOnly));
            }
            finally { File.SetAttributes(readOnly, FileAttributes.Normal); }
        });
        check("임시 파일: 손상·미래 형식·초과 크기·알 수 없는 이름 보존", () =>
        {
            Repository("invalid").Save(baseline with { UpdatedAtUtc = now.AddDays(-1) });
            string broken = Pending("invalid", baseline); File.WriteAllText(broken, "broken");
            string future = Pending("invalid", baseline);
            byte[] plain = JsonSerializer.SerializeToUtf8Bytes(baseline with { SchemaVersion = 5 });
            try { File.WriteAllBytes(future, JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, Protection = "DPAPI-CurrentUser", Ciphertext = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser) })); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            string oversized = Pending("invalid", baseline);
            using (var stream = new FileStream(oversized, FileMode.Open, FileAccess.Write)) stream.SetLength(FileNoteRepository.MaxFileBytes + 1L);
            string unknown = Pending("invalid", baseline, fileName: "keep.tmp");
            foreach (string path in new[] { broken, future, oversized }) File.SetLastWriteTimeUtc(path, now.AddDays(-2).UtcDateTime);
            var result = Clean("invalid");
            Require(result.Removed == 0 && result.Preserved == 4 && new[] { broken, future, oversized, unknown }.All(File.Exists));
        });
        check("임시 파일: 손상 원본·백업만 존재·잘못된 RTF에서는 삭제 금지", () =>
        {
            foreach (string mode in new[] { "damaged-primary", "backup-only", "invalid-rtf", "locked-primary", "decoder-failure" })
            {
                var record = mode == "invalid-rtf" ? baseline with { Rtf = [1, 2, 3] } : baseline;
                var repo = Repository(mode); repo.Save(record); repo.Save(record with { UpdatedAtUtc = now.AddDays(-1) });
                string path = Pending(mode, record);
                if (mode == "damaged-primary") File.WriteAllText(repo.PrimaryPath, "damaged");
                if (mode == "backup-only") File.Delete(repo.PrimaryPath);
                using var held = mode == "locked-primary" ? new FileStream(repo.PrimaryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
                var result = mode == "decoder-failure"
                    ? TemporaryNoteMaintenance.Run(Path.Combine(root, mode), now, _ => throw new InvalidOperationException("Synthetic decoder failure"))
                    : Clean(mode);
                Require(result.Removed == 0 && result.Preserved == 1 && File.Exists(path));
            }
        });
        check("임시 파일: 검사 개수 한도·남은 중복의 다음 실행 정리", () =>
        {
            Repository("count-limit").Save(baseline with { UpdatedAtUtc = now.AddDays(-1) });
            for (int i = 0; i < TemporaryNoteMaintenance.MaxFiles + 3; i++) Pending("count-limit", baseline);
            var first = Clean("count-limit");
            Require(first.Examined == TemporaryNoteMaintenance.MaxFiles && first.Removed == TemporaryNoteMaintenance.MaxFiles && first.Limited);
            var second = Clean("count-limit"); Require(second.Removed == 3 && !second.Limited);
        });
        check("임시 파일: 읽기 예산을 넘기면 나머지 파일 보존", () =>
        {
            var large = baseline with { Rtf = new byte[1024 * 1024] };
            Repository("byte-limit").Save(large with { UpdatedAtUtc = now.AddDays(-1) });
            for (int i = 0; i < 8; i++) Pending("byte-limit", large);
            var result = Clean("byte-limit");
            Require(result.Removed == 0 && result.Limited && result.Examined <= 8
                && Directory.GetFiles(Path.Combine(root, "byte-limit"), "*.tmp").Length == 8);
        });
    }
}
