using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Controls;
using System.Windows.Threading;
using WindowsMemo.Infrastructure;
using WindowsMemo.Models;
using WindowsMemo.Services;
using WindowsMemo.Views;

namespace WindowsMemo.Tests;

internal static class WorkspaceDisposalTests
{
    private sealed class Surface : INoteSurface
    {
        internal bool Disposed { get; private set; }
        public NoteBounds CaptureBounds() => new(48, 64, 320, 300);
        public event EventHandler? BoundsChanged { add { } remove { } }
        public void Dispose() => Disposed = true;
    }
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(AppContext.BaseDirectory, "workspace-disposal-tests", Guid.NewGuid().ToString("N"));
        internal Guid Id { get; } = Guid.NewGuid();
        internal List<Surface> Surfaces { get; } = [];
        internal NoteWorkspace Workspace { get; }
        internal Fixture(bool archived = false, bool trashed = false)
        {
            using var view = new NoteView(); view.InsertPlainText("폐기 경합에서도 보존할 합성 메모");
            var now = DateTimeOffset.UtcNow;
            new FileNoteRepository(Root, Id).Save(new(4, Id, RtfDocumentCodec.Save(view.TextEditor.Document),
                new(48, 64, 320, 300), now, now, Hidden: true, Archived: archived, DeletedAtUtc: trashed ? now : null));
            Workspace = new(Root, (_, _) => { var surface = new Surface(); Surfaces.Add(surface); return surface; });
            Wait(Workspace.InitializeAsync());
        }
        public void Dispose() => Workspace.Dispose();
    }
    internal static void Run(Action<string, Action> check)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            check("작업공간 폐기: 숨긴 메모 읽기 완료 후 새 창·타이머 재생성 금지", () =>
            {
                using var f = new Fixture(); string[] before = Fingerprint(f.Root);
                var opening = f.Workspace.ShowAsync(f.Id);
                Require(!opening.IsCompleted); f.Workspace.Dispose();
                Require(!Wait(opening) && f.Surfaces.Count == 0 && !f.Workspace.Items.Any());
                Require(before.SequenceEqual(Fingerprint(f.Root)));
            });
            check("작업공간 폐기: 보관·휴지통·복원 읽기 중 폐기는 추가 저장 없이 종료", () =>
            {
                for (int operation = 0; operation < 4; operation++)
                {
                    using var f = new Fixture(archived: operation == 2, trashed: operation == 3);
                    string[] before = Fingerprint(f.Root);
                    var task = operation switch
                    {
                        0 => f.Workspace.ArchiveAsync(f.Id),
                        1 => f.Workspace.TrashAsync(f.Id),
                        2 => f.Workspace.UnarchiveAsync(f.Id),
                        _ => f.Workspace.RestoreAsync(f.Id)
                    };
                    Require(!task.IsCompleted); f.Workspace.Dispose();
                    // Re-subscribing makes a late Publish observable even though Dispose cleared old handlers.
                    int notifications = 0; f.Workspace.Changed += (_, _) => notifications++;
                    Require(!Wait(task) && notifications == 0 && f.Surfaces.Count == 0);
                    Require(before.SequenceEqual(Fingerprint(f.Root)));
                }
            });
            check("작업공간 폐기: 종료 인계 콜백의 자체 폐기·후속 요청 거부", () =>
            {
                using var f = new Fixture(); Require(Wait(f.Workspace.ShowAsync(f.Id)));
                int callbacks = 0;
                // No edits: the handoff itself disposes the workspace, as application Shutdown can do.
                Require(!Wait(f.Workspace.SaveThenAsync(() => { callbacks++; f.Workspace.Dispose(); })));
                Require(callbacks == 1 && f.Surfaces.All(surface => surface.Disposed));
                Require(!Wait(f.Workspace.SaveThenAsync(() => callbacks++)) && callbacks == 1);
                Require(!Wait(f.Workspace.ShowAsync(f.Id)) && !Wait(f.Workspace.PermanentlyDeleteAsync(f.Id, true)));
            });
            check("메모 폐기: 숨김·보관·휴지통 저장 완료/실패 후 폐기 UI 변경 없음", () =>
            {
                for (int operation = 0; operation < 3; operation++)
                foreach (bool fail in new[] { false, true })
                {
                    string root = Path.Combine(AppContext.BaseDirectory, "workspace-disposal-tests", Guid.NewGuid().ToString("N"));
                    using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
                    var repository = new FileNoteRepository(root, Guid.NewGuid(), checkpoint =>
                    {
                        if (checkpoint != FileNoteRepository.SaveCheckpoint.TemporaryFlushed) return;
                        entered.Set();
                        if (!release.Wait(TimeSpan.FromSeconds(15))) throw new IOException("Synthetic gate timeout.");
                        if (fail) throw new IOException("Synthetic write failure.");
                    });
                    var surface = new Surface();
                    using var session = new NoteSession(repository, null, (_, _) => surface);
                    session.View.InsertPlainText("진행 중 스냅샷은 원자적 저장을 마칩니다");
                    var closing = operation switch { 0 => session.HideAsync(), 1 => session.ArchiveAsync(), _ => session.TrashAsync() };
                    try
                    {
                        Wait(Until(() => entered.IsSet)); session.Dispose();
                        var label = (TextBlock)session.View.FindName("SaveLabel"); string status = label.Text;
                        release.Set(); Require(!Wait(closing) && surface.Disposed && label.Text == status);
                        Require(session.SaveState == SaveState.Disposed && !Wait(session.FlushAsync()));
                        if (!fail)
                        {
                            var record = repository.Load(record => RtfDocumentCodec.Load(record.Rtf), out _)!;
                            Require(record.Hidden && record.Archived == (operation == 1) && (record.DeletedAtUtc is not null) == (operation == 2));
                        }
                    }
                    finally { release.Set(); }
                }
            });
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private static string[] Fingerprint(string root) => Directory.GetFiles(root).OrderBy(path => path, StringComparer.Ordinal)
        .Select(path => $"{Path.GetFileName(path)}:{File.GetLastWriteTimeUtc(path).Ticks}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}").ToArray();
    private static T Wait<T>(Task<T> task) { Wait((Task)task); return task.GetAwaiter().GetResult(); }
    private static async Task Until(Func<bool> condition)
    { var clock = Stopwatch.StartNew(); while (!condition()) { Require(clock.Elapsed < TimeSpan.FromSeconds(10)); await Task.Delay(10); } }
    private static void Wait(Task task)
    {
        // Test-only message pump with a deadline; production code never pumps to await I/O.
        var frame = new DispatcherFrame(); var dispatcher = Dispatcher.CurrentDispatcher;
        var clock = Stopwatch.StartNew();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(20), DispatcherPriority.Send,
            (_, _) => { if (task.IsCompleted || clock.Elapsed > TimeSpan.FromSeconds(20)) frame.Continue = false; }, dispatcher);
        try { if (!task.IsCompleted) Dispatcher.PushFrame(frame); Require(task.IsCompleted); task.GetAwaiter().GetResult(); }
        finally { timer.Stop(); }
    }
    private static void Require(bool condition) { if (!condition) throw new InvalidOperationException("Workspace disposal assertion failed."); }
}
