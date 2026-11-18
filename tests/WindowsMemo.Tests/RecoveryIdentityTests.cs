using System.IO;
using System.Windows.Documents;
using System.Windows.Threading;
using WindowsMemo.Infrastructure;
using WindowsMemo.Models;
using WindowsMemo.Services;
using WindowsMemo.Views;

namespace WindowsMemo.Tests;

internal static class RecoveryIdentityTests
{
    private sealed class Surface(NoteView view) : INoteSurface
    {
        internal NoteView View => view;
        public NoteBounds CaptureBounds() => new(48, 64, 320, 300);
        public event EventHandler? BoundsChanged { add { } remove { } }
        public void Dispose() { }
    }
    internal static void Run(Action<string, Action> check)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try { RunCore(check); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private static void RunCore(Action<string, Action> check)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "recovery-identity-tests", Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.UtcNow;
        NoteRecord Record(string text) => new(4, Guid.NewGuid(), RtfDocumentCodec.Save(new FlowDocument(new Paragraph(new Run(text)))),
            new(48, 64, 320, 300), now.AddDays(-2), now.AddDays(-1), Hidden: true, Paper: "Peach");
        (string Folder, Guid Token, string Source) Fixture(string name)
        {
            string folder = Path.Combine(root, name);
            var original = Record("기존 메모"); new FileNoteRepository(folder, original.Id).Save(original);
            var pending = Record("복구할 미완료 본문"); var seed = new FileNoteRepository(Path.Combine(folder, "seed"), pending.Id); seed.Save(pending);
            Guid token = Guid.NewGuid(); string source = Path.Combine(folder, token.ToString("N") + ".tmp");
            File.Copy(seed.PrimaryPath, source); return (folder, token, source);
        }
        NoteWorkspace Workspace(string folder, List<Surface>? surfaces = null) => new(folder, (view, _) =>
        { var surface = new Surface(view); surfaces?.Add(surface); return surface; });
        PendingRecoveryResult Recover(NoteWorkspace workspace, Guid token)
        {
            var result = Result(workspace.RecoverPendingAsync(Result(workspace.PreviewPendingAsync(token))));
            Require(result.Succeeded); return result;
        }
        void Refused(Action action)
        {
            bool refused = false; try { action(); } catch (IOException) { refused = true; } Require(refused);
        }
        check("복구 ID: 고정 벡터·대소문자 일치·다른 파일/지문 구분", () =>
        {
            Guid token = new("11111111-2222-4333-8444-555555555555");
            string hash = new('A', 64);
            Require(PendingNoteRecovery.CopyId(token, hash) == new Guid("1966fac2-d98d-8de1-8799-78f9d628e442"));
            Require(PendingNoteRecovery.CopyId(token, hash) == PendingNoteRecovery.CopyId(token, hash.ToLowerInvariant()));
            Require(PendingNoteRecovery.CopyId(token, hash) != PendingNoteRecovery.CopyId(Guid.NewGuid(), hash));
            Require(PendingNoteRecovery.CopyId(token, hash) != PendingNoteRecovery.CopyId(token, new string('B', 64)));
            foreach (var input in new[] { "", "../file", new string('G', 64) })
            {
                bool refused = false; try { PendingNoteRecovery.CopyId(token, input); } catch (ArgumentException) { refused = true; } Require(refused);
            }
        });
        check("새 파일 저장: 본체·백업만 존재할 때 덮어쓰기 거부·원본 불변", () =>
        {
            var record = Record("보존할 내용"); var repo = new FileNoteRepository(Path.Combine(root, "create-only"), record.Id);
            repo.SaveNew(record); byte[] primary = File.ReadAllBytes(repo.PrimaryPath);
            Refused(() => repo.SaveNew(record)); Require(File.ReadAllBytes(repo.PrimaryPath).SequenceEqual(primary) && !File.Exists(repo.BackupPath));
            repo.Save(record with { UpdatedAtUtc = now }); byte[] backup = File.ReadAllBytes(repo.BackupPath);
            primary = File.ReadAllBytes(repo.PrimaryPath); Refused(() => repo.SaveNew(record));
            Require(File.ReadAllBytes(repo.PrimaryPath).SequenceEqual(primary) && File.ReadAllBytes(repo.BackupPath).SequenceEqual(backup));
            File.Delete(repo.PrimaryPath); Refused(() => repo.SaveNew(record));
            Require(!File.Exists(repo.PrimaryPath) && File.ReadAllBytes(repo.BackupPath).SequenceEqual(backup));
        });
        check("새 파일 저장: 검사 뒤 경합한 파일은 원자적 생성에서 보존", () =>
        {
            var record = Record("경합한 저장본"); string folder = Path.Combine(root, "competing-writer");
            var winner = new FileNoteRepository(folder, record.Id); byte[]? committed = null;
            var contender = new FileNoteRepository(folder, record.Id, stage =>
            {
                if (stage != FileNoteRepository.SaveCheckpoint.TemporaryFlushed) return;
                winner.SaveNew(record); committed = File.ReadAllBytes(winner.PrimaryPath);
            });
            Refused(() => contender.SaveNew(record with { Paper = "Sage" }));
            Require(committed is not null && File.ReadAllBytes(winner.PrimaryPath).SequenceEqual(committed));
            Require(!File.Exists(winner.BackupPath) && Directory.GetFiles(folder, "*.tmp").Length == 0);
        });
        check("재실행 복구: 편집한 사본·백업·원본을 다시 쓰지 않고 기존 ID 반환", () =>
        {
            var fixture = Fixture("edited"); var surfaces = new List<Surface>(); Guid id;
            byte[] pending = File.ReadAllBytes(fixture.Source);
            using (var workspace = Workspace(fixture.Folder, surfaces))
            {
                Wait(workspace.InitializeAsync()); var created = Recover(workspace, fixture.Token); id = created.Id; Require(!created.AlreadyRecovered);
                Require(Result(workspace.ShowAsync(id))); surfaces[^1].View.TextEditor.SelectAll();
                surfaces[^1].View.InsertPlainText("복구 후 편집한 최신 내용"); surfaces[^1].View.SetPaper("Sage");
                Require(Result(workspace.HideAsync(id)));
            }
            var repository = new FileNoteRepository(fixture.Folder, id); byte[] primary = File.ReadAllBytes(repository.PrimaryPath), backup = File.ReadAllBytes(repository.BackupPath);
            using var restarted = Workspace(fixture.Folder); Wait(restarted.InitializeAsync());
            var existing = Recover(restarted, fixture.Token);
            Require(existing.AlreadyRecovered && existing.Id == id && restarted.Items.Count == 2);
            Require(File.ReadAllBytes(repository.PrimaryPath).SequenceEqual(primary) && File.ReadAllBytes(repository.BackupPath).SequenceEqual(backup));
            Require(File.ReadAllBytes(fixture.Source).SequenceEqual(pending));
        });
        check("재실행 복구: 보관함·휴지통 사본의 상태를 유지", () =>
        {
            foreach (bool trash in new[] { false, true })
            {
                var fixture = Fixture(trash ? "trashed" : "archived"); Guid id;
                using (var workspace = Workspace(fixture.Folder))
                {
                    Wait(workspace.InitializeAsync()); id = Recover(workspace, fixture.Token).Id;
                    Require(Result(trash ? workspace.TrashAsync(id) : workspace.ArchiveAsync(id)));
                }
                var repository = new FileNoteRepository(fixture.Folder, id); byte[] before = File.ReadAllBytes(repository.PrimaryPath);
                using var restarted = Workspace(fixture.Folder); Wait(restarted.InitializeAsync());
                var result = Recover(restarted, fixture.Token); Require(result.Id == id && result.AlreadyRecovered && restarted.Items.Count == 1);
                Require((trash ? restarted.TrashItems : restarted.ArchiveItems).Single().Id == id);
                Require(File.ReadAllBytes(repository.PrimaryPath).SequenceEqual(before));
            }
        });
        check("재실행 복구: 손상되거나 사라진 사본을 조용히 덮어쓰지 않음", () =>
        {
            var fixture = Fixture("damaged"); Guid id;
            using (var workspace = Workspace(fixture.Folder)) { Wait(workspace.InitializeAsync()); id = Recover(workspace, fixture.Token).Id; }
            var repo = new FileNoteRepository(fixture.Folder, id); File.WriteAllText(repo.PrimaryPath, "damaged copy");
            using (var restarted = Workspace(fixture.Folder))
            {
                Wait(restarted.InitializeAsync()); Require(restarted.Items.Single(item => item.Id == id).Error is not null);
                Require(Result(restarted.RecoverPendingAsync(Result(restarted.PreviewPendingAsync(fixture.Token)))).Status == PendingRecoveryStatus.ExistingCopyUnavailable);
                Require(File.ReadAllText(repo.PrimaryPath) == "damaged copy" && restarted.Items.Count == 2);
            }
            var missing = Fixture("missing"); using var next = Workspace(missing.Folder); Wait(next.InitializeAsync());
            id = Recover(next, missing.Token).Id; var missingRepo = new FileNoteRepository(missing.Folder, id); File.Delete(missingRepo.PrimaryPath);
            Require(Result(next.RecoverPendingAsync(Result(next.PreviewPendingAsync(missing.Token)))).Status == PendingRecoveryStatus.ExistingCopyUnavailable && !File.Exists(missingRepo.PrimaryPath));
        });
        check("재실행 복구: 백업만 남은 사본도 중복 생성·본체 쓰기 없이 인식", () =>
        {
            var fixture = Fixture("backup-only"); Guid id;
            using (var workspace = Workspace(fixture.Folder)) { Wait(workspace.InitializeAsync()); id = Recover(workspace, fixture.Token).Id; }
            var repo = new FileNoteRepository(fixture.Folder, id);
            var record = repo.Load(value => RtfDocumentCodec.Load(value.Rtf), out _)!;
            repo.Save(record with { UpdatedAtUtc = DateTimeOffset.UtcNow });
            byte[] backup = File.ReadAllBytes(repo.BackupPath); File.Delete(repo.PrimaryPath);
            using var restarted = Workspace(fixture.Folder); Wait(restarted.InitializeAsync());
            var existing = Recover(restarted, fixture.Token);
            Require(existing.AlreadyRecovered && existing.Id == id && restarted.Items.Count == 2);
            Require(!File.Exists(repo.PrimaryPath) && File.ReadAllBytes(repo.BackupPath).SequenceEqual(backup));
        });
        check("복구 ID: 원본 변경은 새 사본·영구 삭제 후 명시적 재복구 허용", () =>
        {
            var fixture = Fixture("new-snapshot"); using var workspace = Workspace(fixture.Folder); Wait(workspace.InitializeAsync());
            Guid first = Recover(workspace, fixture.Token).Id;
            var changed = Record("다른 스냅샷"); var seed = new FileNoteRepository(Path.Combine(fixture.Folder, "changed"), changed.Id); seed.Save(changed);
            File.Copy(seed.PrimaryPath, fixture.Source, overwrite: true); var second = Recover(workspace, fixture.Token);
            Require(!second.AlreadyRecovered && second.Id != first && workspace.Items.Count == 3);
            Require(Result(workspace.TrashAsync(second.Id)) && Result(workspace.PermanentlyDeleteAsync(second.Id, confirmed: true)));
            var again = Recover(workspace, fixture.Token); Require(!again.AlreadyRecovered && again.Id == second.Id && workspace.Items.Count == 3);
        });
    }
    private static void Wait(Task task)
    {
        var frame = new DispatcherFrame(); var dispatcher = Dispatcher.CurrentDispatcher;
        task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
        if (!task.IsCompleted) Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
    }
    private static T Result<T>(Task<T> task) { Wait(task); return task.GetAwaiter().GetResult(); }
    private static void Require(bool value) { if (!value) throw new InvalidOperationException("Recovery identity assertion failed."); }
}
