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
                Assert(workspace.Items.Single().Id == FileNoteRepository.NoteId);
                var view = surfaces[0].View!;
                view.InsertPlainText("한글 메모"); view.TextEditor.SelectAll(); view.ApplyColor(Brushes.RoyalBlue);
                hiddenId = workspace.Items.Single().Id;
                workspace.Create();
                var otherId = workspace.Items.Single(item => item.Id != hiddenId).Id;
                surfaces[1].View!.InsertPlainText("다른 메모");
                Assert(Wait(workspace.HideAsync(hiddenId)) && surfaces[0].Disposed);
                Assert(workspace.Items.Single(item => item.Id == hiddenId).Hidden);
                var record = Load("lifecycle", hiddenId);
                Assert(record.Hidden && record.SchemaVersion == 2 && record.Bounds.X == -120);
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
                Assert(!Wait(workspace.FlushAllAsync()));
            Assert(surfaces.All(surface => !surface.Disposed && surface.View!.IsEnabled));
            var second = RtfDocumentCodec.Load(Load("exit-failure", ids[1]).Rtf);
            Assert(new TextRange(second.ContentStart, second.ContentEnd).Text.Trim() == "둘째 수정");
            Assert(Wait(workspace.FlushAllAsync()));
            workspace.Dispose(); Assert(surfaces.All(surface => surface.Disposed));
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
