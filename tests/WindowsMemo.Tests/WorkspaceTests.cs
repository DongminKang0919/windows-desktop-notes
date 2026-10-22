using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using WindowsMemo.Infrastructure;
using WindowsMemo.Models;
using WindowsMemo.Services;
using WindowsMemo.Views;

namespace WindowsMemo.Tests;

internal static class WorkspaceTests
{
    private sealed class Surface : INoteSurface
    {
        public NoteView? View { get; private set; }
        public bool Disposed { get; private set; }
        public Surface(NoteView view) => View = view;
        public NoteBounds CaptureBounds() => new(-120, 80, 420, 480, 1.5);
        public event EventHandler? BoundsChanged { add { } remove { } }
        public void Dispose() { Disposed = true; View = null; }
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
        string root = Path.Combine(AppContext.BaseDirectory, "workspace-tests", Guid.NewGuid().ToString("N"));
        NoteWorkspace Workspace(string name, List<Surface> surfaces) => new(Path.Combine(root, name), (view, _) =>
        { var surface = new Surface(view); surfaces.Add(surface); return surface; });
        string Text(NoteView view) => new TextRange(view.TextEditor.Document.ContentStart, view.TextEditor.Document.ContentEnd).Text.Trim();
        NoteRecord Load(string folder, Guid id) => new FileNoteRepository(Path.Combine(root, folder), id).Load(record => RtfDocumentCodec.Load(record.Rtf), out _)!;
        check("메모 수명: 생성·서식·숨김·재표시·재실행", () =>
        {
            var surfaces = new List<Surface>();
            Guid hiddenId;
            using (var workspace = Workspace("lifecycle", surfaces))
            {
                Wait(workspace.InitializeAsync());
                Assert(workspace.Items.Single().Id != Guid.Empty);
                var view = surfaces[0].View!;
                view.InsertPlainText("한글 메모"); view.TextEditor.SelectAll(); view.ApplyColor(Brushes.RoyalBlue);
                hiddenId = workspace.Items.Single().Id;
                workspace.Create();
                var otherId = workspace.Items.Single(item => item.Id != hiddenId).Id;
                surfaces[1].View!.InsertPlainText("다른 메모");
                Assert(Wait(workspace.HideAsync(hiddenId)) && surfaces[0].Disposed);
                Assert(workspace.Items.Single(item => item.Id == hiddenId).Hidden);
                var record = Load("lifecycle", hiddenId);
                Assert(record.Hidden && record.SchemaVersion == 3 && record.Bounds.X == -120);
                Assert(Wait(workspace.ShowAsync(hiddenId)));
                var restored = surfaces[2].View!;
                Assert(Text(restored) == "한글 메모");
                var range = new TextRange(restored.TextEditor.Document.ContentStart, restored.TextEditor.Document.ContentEnd.GetPositionAtOffset(-1)!);
                Assert(range.GetPropertyValue(TextElement.ForegroundProperty) is SolidColorBrush brush && brush.Color == Colors.RoyalBlue);
                Assert(!Load("lifecycle", hiddenId).Hidden);
                Assert(Wait(workspace.HideAsync(hiddenId)));
                Assert(Wait(workspace.FlushAllAsync()));
                Assert(!Load("lifecycle", otherId).Hidden);
            }
            var restarted = new List<Surface>();
            using var next = Workspace("lifecycle", restarted);
            Wait(next.InitializeAsync());
            Assert(next.Items.Count == 2 && next.Items.Single(item => item.Id == hiddenId).Hidden);
            Assert(restarted.Count == 1 && Text(restarted[0].View!) == "다른 메모");
        });
        check("숨김 실패: 최신 편집기 보존 후 재시도", () =>
        {
            var surfaces = new List<Surface>(); using var workspace = Workspace("hide-failure", surfaces);
            Wait(workspace.InitializeAsync()); Guid id = workspace.Items.Single().Id;
            var view = surfaces[0].View!; view.InsertPlainText("저장된 내용");
            Assert(Wait(workspace.FlushAllAsync()));
            view.TextEditor.SelectAll(); view.InsertPlainText("미저장 최신 내용");
            var repo = new FileNoteRepository(Path.Combine(root, "hide-failure"), id);
            using (var held = new FileStream(repo.PrimaryPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert(!Wait(workspace.HideAsync(id)));
            Assert(!surfaces[0].Disposed && view.IsEnabled && Text(view) == "미저장 최신 내용");
            Assert(!workspace.Items.Single().Hidden && !Load("hide-failure", id).Hidden);
            Assert(Wait(workspace.HideAsync(id)) && surfaces[0].Disposed);
            Assert(Load("hide-failure", id).Hidden);
        });
        check("전체 저장: 한 메모 실패 시 모든 창 보존·나머지 저장·재시도", () =>
        {
            var surfaces = new List<Surface>(); using var workspace = Workspace("exit-failure", surfaces);
            Wait(workspace.InitializeAsync()); workspace.Create();
            var ids = workspace.Items.Select(item => item.Id).ToArray();
            surfaces[0].View!.InsertPlainText("첫 메모"); surfaces[1].View!.InsertPlainText("둘째 메모");
            Assert(Wait(workspace.FlushAllAsync()));
            surfaces[0].View!.TextEditor.SelectAll(); surfaces[0].View!.InsertPlainText("첫 수정");
            surfaces[1].View!.TextEditor.SelectAll(); surfaces[1].View!.InsertPlainText("둘째 수정");
            var repo = new FileNoteRepository(Path.Combine(root, "exit-failure"), ids[0]);
            using (var held = new FileStream(repo.PrimaryPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert(!Wait(workspace.SaveThenAsync(() => throw new Exception("must not apply after save failure"))));
            Assert(surfaces.All(surface => !surface.Disposed && surface.View!.IsEnabled));
            var second = RtfDocumentCodec.Load(Load("exit-failure", ids[1]).Rtf);
            Assert(new TextRange(second.ContentStart, second.ContentEnd).Text.Trim() == "둘째 수정");
            Assert(Wait(workspace.FlushAllAsync()));
            workspace.Dispose(); Assert(surfaces.All(surface => surface.Disposed));
        });
        check("업데이트 저장: 적용 중 편집·생성 차단, 적용 오류 후 편집 복구", () =>
        {
            var surfaces = new List<Surface>(); using var workspace = Workspace("update-handoff", surfaces);
            Wait(workspace.InitializeAsync()); surfaces[0].View!.InsertPlainText("업데이트 전 내용");
            bool called = false;
            Assert(Wait(workspace.SaveThenAsync(() =>
            {
                called = true;
                Assert(workspace.IsBusy && !surfaces[0].View!.IsEnabled);
                workspace.Create(); Assert(workspace.Items.Count == 1);
                var document = RtfDocumentCodec.Load(Load("update-handoff", workspace.Items.Single().Id).Rtf);
                Assert(new TextRange(document.ContentStart, document.ContentEnd).Text.Trim() == "업데이트 전 내용");
            })));
            Assert(called && !workspace.IsBusy && surfaces[0].View!.IsEnabled);
            bool failed = false;
            try { Wait(workspace.SaveThenAsync(() => throw new IOException("update failed"))); }
            catch (IOException) { failed = true; }
            Assert(failed && !workspace.IsBusy && surfaces[0].View!.IsEnabled);
            Assert(Text(surfaces[0].View!) == "업데이트 전 내용");
        });
        check("복원 실패: 손상 메모 보존과 정상 메모 독립 복원", () =>
        {
            var initial = new List<Surface>(); Guid badId;
            using (var workspace = Workspace("corrupt", initial))
            {
                Wait(workspace.InitializeAsync()); badId = workspace.Items.Single().Id;
                workspace.Create(); initial[1].View!.InsertPlainText("정상 메모");
                Assert(Wait(workspace.FlushAllAsync()));
            }
            var badRepo = new FileNoteRepository(Path.Combine(root, "corrupt"), badId);
            File.WriteAllText(badRepo.PrimaryPath, "damaged");
            if (File.Exists(badRepo.BackupPath)) File.WriteAllText(badRepo.BackupPath, "damaged-backup");
            var restored = new List<Surface>(); using var next = Workspace("corrupt", restored);
            Wait(next.InitializeAsync());
            Assert(next.Items.Count == 2 && next.Items.Single(item => item.Id == badId).Error is not null);
            Assert(restored.Count == 1 && Text(restored[0].View!) == "정상 메모");
            Assert(!Wait(next.ShowAsync(badId)) && File.ReadAllText(badRepo.PrimaryPath) == "damaged");
        });
        check("휴지통: 최신 본문·서식 보존과 숨김 복원·재실행", () =>
        {
            var surfaces = new List<Surface>(); Guid id;
            using (var workspace = Workspace("trash", surfaces))
            {
                Wait(workspace.InitializeAsync()); id = workspace.Items.Single().Id;
                var view = surfaces[0].View!; view.InsertPlainText("휴지통 한글 메모");
                view.TextEditor.SelectAll(); view.ApplyColor(Brushes.RoyalBlue);
                Assert(Wait(workspace.TrashAsync(id)) && surfaces[0].Disposed);
                Assert(workspace.Items.Count == 0 && workspace.TrashItems.Single().Id == id);
                var record = Load("trash", id);
                Assert(record.SchemaVersion == 3 && record.Hidden && record.DeletedAtUtc is not null);
                Assert(!Wait(workspace.ShowAsync(id)));
            }
            var restarted = new List<Surface>(); using var next = Workspace("trash", restarted);
            Wait(next.InitializeAsync());
            Assert(next.Items.Count == 0 && next.TrashItems.Single().Id == id && restarted.Count == 0);
            Assert(Wait(next.RestoreAsync(id)) && next.Items.Single().Hidden && next.TrashItems.Count == 0);
            Assert(Load("trash", id).DeletedAtUtc is null && restarted.Count == 0);
            Assert(Wait(next.ShowAsync(id)) && Text(restarted[0].View!) == "휴지통 한글 메모");
            var doc = restarted[0].View!.TextEditor.Document;
            Assert(new TextRange(doc.ContentStart, doc.ContentEnd.GetPositionAtOffset(-1)!).GetPropertyValue(TextElement.ForegroundProperty)
                is SolidColorBrush brush && brush.Color == Colors.RoyalBlue);
        });
        check("휴지통 이동 실패: 최신 편집기 유지 후 재시도", () =>
        {
            var surfaces = new List<Surface>(); using var workspace = Workspace("trash-failure", surfaces);
            Wait(workspace.InitializeAsync()); Guid id = workspace.Items.Single().Id;
            var view = surfaces[0].View!; view.InsertPlainText("이전 내용"); Assert(Wait(workspace.FlushAllAsync()));
            view.TextEditor.SelectAll(); view.InsertPlainText("최신 미저장 내용");
            var repo = new FileNoteRepository(Path.Combine(root, "trash-failure"), id);
            using (var held = new FileStream(repo.PrimaryPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert(!Wait(workspace.TrashAsync(id)));
            Assert(!surfaces[0].Disposed && view.IsEnabled && Text(view) == "최신 미저장 내용");
            Assert(workspace.TrashItems.Count == 0 && Load("trash-failure", id).DeletedAtUtc is null);
            Assert(Wait(workspace.TrashAsync(id)));
        });
        check("숨긴 메모 휴지통 이동·복원 실패 보존과 재시도", () =>
        {
            var surfaces = new List<Surface>(); using var workspace = Workspace("hidden-trash", surfaces);
            Wait(workspace.InitializeAsync()); Guid id = workspace.Items.Single().Id;
            surfaces[0].View!.InsertPlainText("숨김 내용"); Assert(Wait(workspace.HideAsync(id)));
            Assert(Wait(workspace.TrashAsync(id)) && surfaces.Count == 1);
            var repo = new FileNoteRepository(Path.Combine(root, "hidden-trash"), id);
            using (var held = new FileStream(repo.PrimaryPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert(!Wait(workspace.RestoreAsync(id)));
            Assert(workspace.Items.Count == 0 && workspace.TrashItems.Count == 1 && Load("hidden-trash", id).DeletedAtUtc is not null);
            Assert(Wait(workspace.RestoreAsync(id)) && Load("hidden-trash", id).DeletedAtUtc is null);
        });
        check("영구 삭제: 확인·휴지통 제한과 백업·손상 보존본 제거", () =>
        {
            var surfaces = new List<Surface>(); using var workspace = Workspace("purge", surfaces);
            Wait(workspace.InitializeAsync()); Guid id = workspace.Items.Single().Id;
            surfaces[0].View!.InsertPlainText("삭제 대상"); Assert(Wait(workspace.FlushAllAsync()));
            Assert(!Wait(workspace.PermanentlyDeleteAsync(id, confirmed: true)));
            Assert(Wait(workspace.TrashAsync(id)));
            var repo = new FileNoteRepository(Path.Combine(root, "purge"), id);
            string damaged = repo.PrimaryPath + ".damaged-" + Guid.NewGuid().ToString("N");
            File.Copy(repo.BackupPath, damaged);
            workspace.Create(); Guid otherId = workspace.Items.Single().Id;
            surfaces[^1].View!.InsertPlainText("보존 대상"); Assert(Wait(workspace.FlushAllAsync()));
            var other = new FileNoteRepository(Path.Combine(root, "purge"), otherId);
            byte[] otherBytes = File.ReadAllBytes(other.PrimaryPath);
            Assert(!Wait(workspace.PermanentlyDeleteAsync(id, confirmed: false)) && File.Exists(repo.PrimaryPath));
            Assert(Wait(workspace.PermanentlyDeleteAsync(id, confirmed: true)));
            Assert(!File.Exists(repo.PrimaryPath) && !File.Exists(repo.BackupPath) && !File.Exists(damaged));
            Assert(!FileNoteRepository.DiscoverIds(Path.Combine(root, "purge")).Contains(id));
            Assert(File.ReadAllBytes(other.PrimaryPath).SequenceEqual(otherBytes) && workspace.TrashItems.Count == 0);
        });
        check("영구 삭제 실패: 휴지통 본체 유지·재실행·재시도", () =>
        {
            Guid id; var surfaces = new List<Surface>();
            using (var workspace = Workspace("purge-failure", surfaces))
            {
                Wait(workspace.InitializeAsync()); id = workspace.Items.Single().Id;
                surfaces[0].View!.InsertPlainText("영구 삭제 시험"); Assert(Wait(workspace.FlushAllAsync()));
                Assert(Wait(workspace.TrashAsync(id)));
                var repo = new FileNoteRepository(Path.Combine(root, "purge-failure"), id);
                using (var held = new FileStream(repo.PrimaryPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Assert(!Wait(workspace.PermanentlyDeleteAsync(id, confirmed: true)));
                Assert(File.Exists(repo.PrimaryPath) && Load("purge-failure", id).DeletedAtUtc is not null);
            }
            var restarted = new List<Surface>(); using var next = Workspace("purge-failure", restarted);
            Wait(next.InitializeAsync()); Assert(next.TrashItems.Single().Id == id && restarted.Count == 0);
            Assert(Wait(next.PermanentlyDeleteAsync(id, confirmed: true)) && next.TrashItems.Count == 0);
        });
        check("영구 삭제: 백업만 남은 휴지통 정리", () =>
        {
            var surfaces = new List<Surface>(); Guid id;
            using (var workspace = Workspace("backup-trash", surfaces))
            {
                Wait(workspace.InitializeAsync()); id = workspace.Items.Single().Id;
                surfaces[0].View!.InsertPlainText("백업 휴지통"); Assert(Wait(workspace.TrashAsync(id)));
            }
            var repo = new FileNoteRepository(Path.Combine(root, "backup-trash"), id);
            File.Move(repo.PrimaryPath, repo.BackupPath, overwrite: true);
            var restarted = new List<Surface>(); using var next = Workspace("backup-trash", restarted);
            Wait(next.InitializeAsync()); Assert(next.TrashItems.Single().Id == id);
            Assert(Wait(next.PermanentlyDeleteAsync(id, confirmed: true)));
            Assert(!File.Exists(repo.PrimaryPath) && !File.Exists(repo.BackupPath));
        });
        check("영구 삭제 후 빈 시작: 삭제한 ID 재사용 방지", () =>
        {
            var surfaces = new List<Surface>(); Guid oldId;
            using (var workspace = Workspace("empty-after-purge", surfaces))
            {
                Wait(workspace.InitializeAsync()); oldId = workspace.Items.Single().Id;
                Assert(Wait(workspace.TrashAsync(oldId)));
                Assert(Wait(workspace.PermanentlyDeleteAsync(oldId, confirmed: true)));
            }
            var restarted = new List<Surface>(); using var next = Workspace("empty-after-purge", restarted);
            Wait(next.InitializeAsync());
            Assert(next.Items.Single().Id != oldId && next.TrashItems.Count == 0);
        });
        check("로그인 초기화 대기: 준비 중 생성·종료 중복 차단", () =>
        {
            var surfaces = new List<Surface>(); using var workspace = Workspace("startup-wait", surfaces);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var initialization = workspace.InitializeAsync(() => ready.Task);
            Assert(workspace.IsBusy);
            workspace.Create(); Assert(workspace.Items.Count == 0 && surfaces.Count == 0);
            Assert(!Wait(workspace.FlushAllAsync()));
            ready.SetResult(); Wait(initialization);
            Assert(!workspace.IsBusy && workspace.Items.Count == 1 && surfaces.Count == 1);
        });
        check("숨김 20회: 폐기한 편집기 수집과 창 수 유지", () =>
        {
            var surfaces = new List<Surface>(); using var workspace = Workspace("repeated", surfaces);
            Wait(workspace.InitializeAsync()); Guid id = workspace.Items.Single().Id;
            var references = new List<WeakReference>();
            for (int i = 0; i < 20; i++)
            {
                references.Add(HideAndCapture(workspace, id, surfaces[^1]));
                Assert(Wait(workspace.ShowAsync(id)));
            }
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Assert(references.All(reference => !reference.IsAlive));
            Assert(surfaces.Count(surface => !surface.Disposed) == 1 && workspace.Items.Count == 1);
        });
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference HideAndCapture(NoteWorkspace workspace, Guid id, Surface surface)
    {
        var reference = new WeakReference(surface.View!);
        Assert(Wait(workspace.HideAsync(id)));
        return reference;
    }
    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame(); var dispatcher = Dispatcher.CurrentDispatcher;
        task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
    }
    private static void Wait(Task task) { Pump(task); task.GetAwaiter().GetResult(); }
    private static bool Wait(Task<bool> task) { Pump(task); return task.GetAwaiter().GetResult(); }
    private static void Assert(bool value) { if (!value) throw new InvalidOperationException("Workspace assertion failed."); }
}
