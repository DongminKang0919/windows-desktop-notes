using System.IO;
using System.Windows.Documents;
using WindowsMemo.Infrastructure;
using WindowsMemo.Models;
using WindowsMemo.Services;

namespace WindowsMemo.Tests;

internal static class PendingRecoveryTests
{
    private static void Require(bool value) { if (!value) throw new InvalidOperationException("Pending recovery assertion failed."); }
    internal static void Run(Action<string, Action> check)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "pending-recovery-tests", Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.UtcNow;
        var record = new NoteRecord(4, Guid.NewGuid(), RtfDocumentCodec.Save(new FlowDocument(new Paragraph(new Run("복구할 미완료 내용")))),
            new(48, 64, 320, 300), now.AddDays(-2), now.AddDays(-1), Paper: "Peach");
        (PendingNoteRecovery Store, Guid Token, string Path) Pending(string folder, NoteRecord? value = null)
        {
            var seed = new FileNoteRepository(Path.Combine(root, "seeds", Guid.NewGuid().ToString("N")), (value ?? record).Id);
            seed.Save(value ?? record);
            string target = Path.Combine(root, folder); Directory.CreateDirectory(target);
            Guid token = Guid.NewGuid(); string path = Path.Combine(target, token.ToString("N") + ".tmp");
            File.Copy(seed.PrimaryPath, path); return (new(target), token, path);
        }
        void Rejected(Action action)
        {
            bool rejected = false;
            try { action(); } catch (Exception ex) when (NoteWorkspace.IsNoteError(ex)) { rejected = true; }
            Require(rejected);
        }
        check("미완료 복구: 빈 목록 조회는 폴더를 만들지 않음", () =>
        {
            string missing = Path.Combine(root, "missing");
            var scan = new PendingNoteRecovery(missing).Scan();
            Require(scan.Files.Count == 0 && !Directory.Exists(missing));
        });
        check("미완료 복구: 최근 파일 검색·선택 읽기와 원본 불변·핸들 해제", () =>
        {
            var pending = Pending("read"); byte[] before = File.ReadAllBytes(pending.Path);
            var scan = pending.Store.Scan(); Require(scan.Files.Single().Token == pending.Token);
            var preview = pending.Store.Read(pending.Token);
            Require(preview.Record.Id == record.Id && preview.Record.Rtf.SequenceEqual(record.Rtf) && preview.Record.Paper == "Peach");
            Require(File.ReadAllBytes(pending.Path).SequenceEqual(before));
            using var exclusive = new FileStream(pending.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        });
        check("미완료 복구: 미리보기 이후 변경·삭제·잠긴 파일 거부", () =>
        {
            var pending = Pending("changed"); var preview = pending.Store.Read(pending.Token);
            var changed = Pending("replacement", record with { UpdatedAtUtc = now });
            File.Copy(changed.Path, pending.Path, overwrite: true);
            Rejected(() => pending.Store.Read(pending.Token, preview.Fingerprint));
            using (var held = new FileStream(pending.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Rejected(() => pending.Store.Read(pending.Token));
            File.Delete(pending.Path); Rejected(() => pending.Store.Read(pending.Token));
        });
        check("미완료 복구: 손상·과대·빈 토큰 거부 후 파일 보존", () =>
        {
            var pending = Pending("bad"); File.WriteAllText(pending.Path, "broken");
            Rejected(() => pending.Store.Read(pending.Token)); Require(File.Exists(pending.Path));
            using (var large = new FileStream(pending.Path, FileMode.Create, FileAccess.Write)) large.SetLength(FileNoteRepository.MaxFileBytes + 1);
            Rejected(() => pending.Store.Read(pending.Token)); Require(File.Exists(pending.Path));
            Rejected(() => pending.Store.Read(Guid.Empty));
        });
        check("미완료 복구: 후보 64개 제한과 모르는 이름 보존", () =>
        {
            var pending = Pending("bounded"); string folder = Path.GetDirectoryName(pending.Path)!;
            for (int i = 0; i < 66; i++) File.Copy(pending.Path, Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp"));
            var scan = pending.Store.Scan(); Require(scan.Limited && scan.Files.Count == 64 && Directory.GetFiles(folder).Length == 67);
            var unknown = Pending("unknown"); string renamed = Path.Combine(Path.GetDirectoryName(unknown.Path)!, "unknown.tmp");
            File.Move(unknown.Path, renamed); scan = unknown.Store.Scan(); Require(scan.Files.Count == 0 && scan.Skipped == 1 && File.Exists(renamed));
        });
    }
}
