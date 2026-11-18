using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Documents;
using System.Windows.Threading;
using WindowsMemo.Infrastructure;
using WindowsMemo.Interop;
using WindowsMemo.Models;
using WindowsMemo.Services;
using WindowsMemo.Views;

namespace WindowsMemo.Tests;

internal static class SessionEndingTests
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShutdownBlockReasonQuery(nint window, StringBuilder reason, ref uint length);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);
    private sealed class Surface(NoteView view) : INoteSurface
    {
        internal NoteView View { get; } = view;
        internal bool Disposed { get; private set; }
        public NoteBounds CaptureBounds() => new(48, 64, 320, 300);
        public event EventHandler? BoundsChanged { add { } remove { } }
        public void Dispose() => Disposed = true;
    }
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(AppContext.BaseDirectory, "session-ending-tests", Guid.NewGuid().ToString("N"));
        internal List<Surface> Surfaces { get; } = [];
        internal NoteWorkspace Workspace { get; }
        internal Fixture() => Workspace = new(Root, (view, _) => { var surface = new Surface(view); Surfaces.Add(surface); return surface; });
        public void Dispose() => Workspace.Dispose();
    }
    private static TaskCompletionSource<bool> Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static void Run(Action<string, Action> check)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            check("세션 종료: 저장 완료는 즉시 허용·준비/작업/실패/진행/미저장은 구분해 취소", () =>
            {
                foreach (var state in Enum.GetValues<SessionEndState>())
                {
                    int work = 0;
                    using var coordinator = new SessionEndingCoordinator(() => state, () => { work++; return Task.FromResult(true); }, _ => work++, _ => work++);
                    var decision = coordinator.Decide();
                    Require(decision.State == state && decision.Cancel == (state != SessionEndState.Saved) && work == 0);
                }
            });
            check("세션 종료: 취소 응답 후 저장 큐·100회 중복 병합·완료 후에도 앱 종료 호출 없음", () =>
            {
                var queued = new Queue<Action>(); var gate = Gate(); int calls = 0; var notices = new List<SessionPreparationResult>();
                var state = SessionEndState.Dirty;
                async Task<bool> Flush() { calls++; await gate.Task; state = SessionEndState.Saved; return true; }
                using var c = new SessionEndingCoordinator(() => state, Flush, queued.Enqueue, notices.Add);
                for (int i = 0; i < 100; i++) { Require(c.Decide().Cancel); c.PrepareAfterCancellation(); }
                Require(calls == 0 && queued.Count == 1 && c.IsPending); queued.Dequeue()();
                for (int i = 0; i < 100; i++) c.PrepareAfterCancellation();
                Require(calls == 1 && queued.Count == 0 && !c.Preparation.IsCompleted && c.Decide().Cancel);
                gate.SetResult(true); Wait(c.Preparation);
                Require(!c.Decide().Cancel && !c.IsPending && notices.SequenceEqual([SessionPreparationResult.Ready]));
                // There is deliberately no application-shutdown callback in this coordinator.
                state = SessionEndState.Dirty; Require(c.Decide().Cancel); // A new edit invalidates readiness.
            });
            check("세션 종료: 준비/작업 중에는 저장을 시작하지 않고 다음 요청에서 재판단", () =>
            {
                using var f = new Fixture(); var gate = Gate();
                var initialization = f.Workspace.InitializeAsync(() => gate.Task);
                Require(f.Workspace.SessionEndState == SessionEndState.Preparing);
                var queued = new Queue<Action>(); int calls = 0;
                using var c = new SessionEndingCoordinator(() => f.Workspace.SessionEndState,
                    () => { calls++; return f.Workspace.FlushAllAsync(); }, queued.Enqueue, _ => { });
                Require(c.Decide().Cancel); c.PrepareAfterCancellation(); queued.Dequeue()(); Wait(c.Preparation); Require(calls == 0);
                gate.SetResult(true); Wait(initialization); Require(f.Workspace.SessionEndState == SessionEndState.Dirty);
                c.PrepareAfterCancellation(); queued.Dequeue()(); Wait(c.Preparation); Require(calls == 1 && !c.Decide().Cancel);
                Task? during = null;
                Require(Wait(f.Workspace.SaveThenAsync(() =>
                {
                    Require(c.Decide().State == SessionEndState.Busy); c.PrepareAfterCancellation(); queued.Dequeue()(); during = c.Preparation;
                })));
                Wait(during!); Require(calls == 1 && !c.Decide().Cancel);
            });
            check("세션 종료: 실제 저장 실패는 최신 편집기 보존·원인 해제 후 재요청 성공", () =>
            {
                using var f = new Fixture(); Wait(f.Workspace.InitializeAsync()); Require(Wait(f.Workspace.FlushAllAsync()));
                var view = f.Surfaces.Single().View; Guid id = f.Workspace.Items.Single().Id;
                view.InsertPlainText("실패 뒤에도 최신 메모를 유지합니다");
                var queued = new Queue<Action>(); var notices = new List<SessionPreparationResult>();
                using var c = new SessionEndingCoordinator(() => f.Workspace.SessionEndState, f.Workspace.FlushAllAsync, queued.Enqueue, notices.Add);
                using (var locked = new FileStream(new FileNoteRepository(f.Root, id).PrimaryPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    c.PrepareAfterCancellation(); queued.Dequeue()(); Wait(c.Preparation);
                    Require(c.Decide().State == SessionEndState.Failed && c.Decide().Cancel && view.IsEnabled && !f.Surfaces.Single().Disposed);
                    Require(new TextRange(view.TextEditor.Document.ContentStart, view.TextEditor.Document.ContentEnd).Text.Contains("최신 메모"));
                }
                c.PrepareAfterCancellation(); queued.Dequeue()(); Wait(c.Preparation);
                Require(!c.Decide().Cancel && notices.SequenceEqual([SessionPreparationResult.Failed, SessionPreparationResult.Ready]));
                var record = new FileNoteRepository(f.Root, id).Load(record => RtfDocumentCodec.Load(record.Rtf), out _)!;
                var document = RtfDocumentCodec.Load(record.Rtf);
                Require(new TextRange(document.ContentStart, document.ContentEnd).Text.Contains("최신 메모"));
            });
            check("세션 종료: 지연 저장은 질의를 막지 않음·폐기 후 알림/추가 저장 없음", () =>
            {
                var queued = new Queue<Action>(); var gate = Gate(); int calls = 0, notices = 0;
                var c = new SessionEndingCoordinator(() => SessionEndState.Saving, () => { calls++; return gate.Task; }, queued.Enqueue, _ => notices++);
                c.PrepareAfterCancellation(); queued.Dequeue()();
                var clock = Stopwatch.StartNew(); for (int i = 0; i < 1000; i++) Require(c.Decide().Cancel);
                Require(clock.Elapsed < TimeSpan.FromSeconds(2) && calls == 1 && !c.Preparation.IsCompleted);
                c.Dispose(); gate.SetResult(true); Wait(c.Preparation); Require(notices == 0 && !c.IsPending);
                c.PrepareAfterCancellation(); Require(queued.Count == 0);
                var queuedOnly = new SessionEndingCoordinator(() => SessionEndState.Dirty, () => { calls++; return Task.FromResult(true); }, queued.Enqueue, _ => notices++);
                queuedOnly.PrepareAfterCancellation(); queuedOnly.Dispose(); queued.Dequeue()(); Wait(queuedOnly.Preparation);
                Require(calls == 1 && notices == 0);
            });
            check("세션 종료: 저장/알림/큐 예외 관찰·다음 요청에서 재시도 가능", () =>
            {
                var queued = new Queue<Action>(); int calls = 0;
                using var c = new SessionEndingCoordinator(() => SessionEndState.Dirty,
                    () => { calls++; return Task.FromException<bool>(new InvalidOperationException("Synthetic failure.")); }, queued.Enqueue,
                    _ => throw new InvalidOperationException("Synthetic notification failure."));
                for (int i = 0; i < 2; i++) { c.PrepareAfterCancellation(); queued.Dequeue()(); Wait(c.Preparation); Require(!c.IsPending && c.Decide().Cancel); }
                Require(calls == 2);
                using var rejected = new SessionEndingCoordinator(() => SessionEndState.Dirty, () => Task.FromResult(true), _ => throw new InvalidOperationException("Queue closed."), _ => { });
                rejected.PrepareAfterCancellation(); Require(rejected.Decide().Cancel && !rejected.IsPending);
            });
            check("종료 저장: 여러 대기자·저장 중 새 편집은 마지막 버전까지 직렬 저장", () =>
            {
                string root = Path.Combine(AppContext.BaseDirectory, "session-ending-tests", Guid.NewGuid().ToString("N"));
                using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); int commits = 0;
                var repo = new FileNoteRepository(root, Guid.NewGuid(), checkpoint =>
                {
                    if (checkpoint != FileNoteRepository.SaveCheckpoint.TemporaryFlushed) return;
                    if (Interlocked.Increment(ref commits) == 1) { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(15))) throw new IOException("Gate timeout."); }
                });
                using var view = new NoteView(); using var saves = new AutosaveCoordinator(view, repo, () => new(48, 64, 320, 300), DateTimeOffset.UtcNow.AddMinutes(-1));
                view.InsertPlainText("첫 버전"); var first = saves.FlushAsync();
                try
                {
                    Wait(Until(() => entered.IsSet)); Require(saves.State == SaveState.Saving);
                    var second = saves.FlushAsync(); var third = saves.FlushAsync();
                    view.TextEditor.SelectAll(); view.InsertPlainText("마지막 편집 버전"); release.Set();
                    Wait(Task.WhenAll(first, second, third).WaitAsync(TimeSpan.FromSeconds(10)));
                    Require(first.Result && second.Result && third.Result && saves.State == SaveState.Saved && !saves.IsScheduled && commits == 2);
                    var record = repo.Load(record => RtfDocumentCodec.Load(record.Rtf), out _)!;
                    var doc = RtfDocumentCodec.Load(record.Rtf);
                    Require(new TextRange(doc.ContentStart, doc.ContentEnd).Text.Trim() == "마지막 편집 버전");
                }
                finally { release.Set(); }
            });
            check("트레이 종료 저장: 모든 메모 확정 후 차단 구간 안에서만 종료 콜백", () =>
            {
                using var f = new Fixture(); Wait(f.Workspace.InitializeAsync()); f.Workspace.Create();
                foreach (var surface in f.Surfaces) surface.View.InsertPlainText("종료 직전 저장 내용"); int callbacks = 0;
                Require(Wait(f.Workspace.SaveThenAsync(() =>
                {
                    callbacks++; Require(f.Workspace.IsBusy && f.Surfaces.All(surface => !surface.View.IsEnabled));
                    Require(!Wait(f.Workspace.FlushAllAsync()));
                    foreach (var item in f.Workspace.Items)
                    {
                        var record = new FileNoteRepository(f.Root, item.Id).Load(record => RtfDocumentCodec.Load(record.Rtf), out _)!;
                        var doc = RtfDocumentCodec.Load(record.Rtf); Require(new TextRange(doc.ContentStart, doc.ContentEnd).Text.Trim() == "종료 직전 저장 내용");
                    }
                })));
                Require(callbacks == 1 && f.Surfaces.All(surface => surface.View.IsEnabled) && f.Workspace.SessionEndState == SessionEndState.Saved);
                f.Surfaces[0].View.InsertPlainText(" 저장 실패 시 남겨둘 편집");
                using (var locked = new FileStream(new FileNoteRepository(f.Root, f.Workspace.Items[0].Id).PrimaryPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    Require(!Wait(f.Workspace.SaveThenAsync(() => callbacks++)) && callbacks == 1);
                    Require(f.Workspace.SessionEndState == SessionEndState.Failed && f.Surfaces.All(surface => surface.View.IsEnabled && !surface.Disposed));
                }
                Require(Wait(f.Workspace.SaveThenAsync(() => callbacks++)) && callbacks == 2 && f.Workspace.SessionEndState == SessionEndState.Saved);
            });
            check("종료 저장: 작업공간 준비 중 폐기는 새 창·타이머를 생성하지 않음", () =>
            {
                using var f = new Fixture(); var gate = Gate(); var initialize = f.Workspace.InitializeAsync(() => gate.Task);
                f.Workspace.Dispose(); gate.SetResult(true); Wait(initialize);
                Require(f.Surfaces.Count == 0 && f.Workspace.SessionEndState == SessionEndState.Disposed && !Wait(f.Workspace.FlushAllAsync()));
            });
            check("세션 종료: 비동기 준비 완료·폐기 뒤 조정 객체 참조 회수", () =>
            {
                var reference = CompletedCoordinator(); Wait(Task.Delay(25));
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Require(!reference.IsAlive);
            });
            check("세션 종료: 자체 비표시 창의 보류 사유 등록·조회·해제와 HWND 20회 폐기", () =>
            {
                for (int i = 0; i < 20; i++)
                {
                    using var reason = new SessionEndBlockReason();
                    Require(reason.Handle == 0 && reason.Register(SessionEndState.Dirty));
                    nint handle = reason.Handle; Require(IsWindow(handle));
                    uint length = 256; var text = new StringBuilder(256);
                    Require(ShutdownBlockReasonQuery(handle, text, ref length) && text.ToString().Contains("최신 메모"));
                    reason.Clear(); Require(!reason.IsRegistered);
                    length = 256; Require(!ShutdownBlockReasonQuery(handle, text, ref length));
                    Require(reason.Register(SessionEndState.Failed)); reason.Dispose();
                    Require(reason.Handle == 0 && !IsWindow(handle) && !reason.Register(SessionEndState.Dirty));
                }
            });
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CompletedCoordinator()
    {
        var queued = new Queue<Action>(); var gate = Gate();
        var c = new SessionEndingCoordinator(() => SessionEndState.Dirty, () => gate.Task, queued.Enqueue, _ => { });
        c.PrepareAfterCancellation(); queued.Dequeue()(); c.Dispose(); gate.SetResult(true); Wait(c.Preparation);
        return new(c);
    }
    private static async Task Until(Func<bool> condition)
    { var clock = Stopwatch.StartNew(); while (!condition()) { Require(clock.Elapsed < TimeSpan.FromSeconds(10)); await Task.Delay(10); } }
    private static T Wait<T>(Task<T> task) { Wait((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Wait(Task task)
    {
        var dispatcher = Dispatcher.CurrentDispatcher; var frame = new DispatcherFrame();
        task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
        if (!task.IsCompleted) Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
    }
    private static void Require(bool value) { if (!value) throw new InvalidOperationException("Session-ending assertion failed."); }
}
