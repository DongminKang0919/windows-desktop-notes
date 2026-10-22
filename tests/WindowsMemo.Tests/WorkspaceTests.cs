using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Threading;
using WindowsMemo.Interop;
using WindowsMemo.Infrastructure;
using WindowsMemo.Models;
using WindowsMemo.Services;
using WindowsMemo.Views;

namespace WindowsMemo.Tests;

internal static class WorkspaceTests
{
    private sealed class StartupSurface : INoteSurface
    {
        public DesktopNoteHost Host { get; }
        public NoteView View { get; }
        public StartupSurface(NoteView view, NoteBounds? bounds, Func<nint> locator)
        {
            View = view;
            Host = new DesktopNoteHost(view, locator, watchChanges: true);
            try { Host.Open(visible: false, restoredBounds: bounds); }
            catch { Host.Dispose(); throw; }
        }
        public NoteBounds CaptureBounds() => Host.CaptureBounds();
        public event EventHandler? BoundsChanged { add => Host.BoundsChanged += value; remove => Host.BoundsChanged -= value; }
        public void Dispose() => Host.Dispose();
    }
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
        check("바탕화면 없는 초기화: 새 메모 저장·재실행 복원·늦은 재부착", () =>
        {
            string folder = Path.Combine(root, "desktop-not-ready");
            var surfaces = new List<StartupSurface>();
            nint parent = 0;
            NoteWorkspace CreateWorkspace() => new(folder, (view, bounds) =>
            {
                var surface = new StartupSurface(view, bounds, () => parent);
                surfaces.Add(surface); return surface;
            });
            Guid id;
            using (var workspace = CreateWorkspace())
            {
                Wait(workspace.InitializeAsync());
                id = workspace.Items.Single().Id;
                Assert(workspace.Items.Single().Error is null && surfaces[0].Host.HasFallback);
                var view = surfaces[0].View;
                view.InsertPlainText("첫 로그인 임시 창에서 저장한 메모");
                view.TextEditor.SelectAll(); view.ApplyColor(Brushes.RoyalBlue);
                view.TextEditor.Selection.ApplyPropertyValue(TextElement.FontWeightProperty, System.Windows.FontWeights.Bold);
                view.SetPaper("Sage");
                Assert(Wait(workspace.FlushAllAsync()));
            }
            using (var workspace = CreateWorkspace())
            {
                Wait(workspace.InitializeAsync());
                Assert(workspace.Items.Single().Id == id && workspace.Items.Single().Error is null);
                var surface = surfaces[^1];
                Assert(surface.Host.HasFallback && Text(surface.View) == "첫 로그인 임시 창에서 저장한 메모");
                Assert(surface.View.Paper == "Sage");
                var restoredRange = new TextRange(surface.View.TextEditor.Document.ContentStart,
                    surface.View.TextEditor.Document.ContentEnd.GetPositionAtOffset(-1)!);
                Assert(restoredRange.GetPropertyValue(TextElement.FontWeightProperty).Equals(System.Windows.FontWeights.Bold));
                Assert(restoredRange.GetPropertyValue(TextElement.ForegroundProperty) is SolidColorBrush restoredBrush
                    && restoredBrush.Color == Colors.RoyalBlue);
                surface.View.TextEditor.SelectAll(); surface.View.InsertPlainText("늦게 연결되어도 최신 편집 보존");
                using var desktop = new HwndSource(new HwndSourceParameters("Startup test desktop", 1600, 900)
                    { WindowStyle = unchecked((int)0x80000000) });
                parent = desktop.Handle; surface.Host.CheckConnection();
                Assert(surface.Host.Handle != 0 && !surface.Host.HasFallback);
                Assert(Text(surface.View) == "늦게 연결되어도 최신 편집 보존");
                Assert(Wait(workspace.FlushAllAsync()));
                var stored = new FileNoteRepository(folder, id).Load(value => RtfDocumentCodec.Load(value.Rtf), out bool recovered)!;
                Assert(!recovered && stored.Paper == "Sage" && !stored.Hidden && !stored.Archived);
                Assert(stored.Bounds == surface.Host.CaptureBounds());
                var doc = RtfDocumentCodec.Load(stored.Rtf);
                Assert(new TextRange(doc.ContentStart, doc.ContentEnd).Text.Trim() == "늦게 연결되어도 최신 편집 보존");
            }
            Assert(surfaces.All(surface => surface.Host.Handle == 0 && !surface.Host.HasFallback));
        });
        check("임시 창 위치·크기: 저장·숨김 복원·재실행·재부착", () =>
        {
            string folder = Path.Combine(root, "fallback-geometry");
            var surfaces = new List<StartupSurface>(); nint parent = 0;
            NoteWorkspace CreateWorkspace() => new(folder, (view, bounds) =>
            { var surface = new StartupSurface(view, bounds, () => parent); surfaces.Add(surface); return surface; });
            Guid id; NoteBounds saved;
            using (var workspace = CreateWorkspace())
            {
                Wait(workspace.InitializeAsync()); id = workspace.Items.Single().Id;
                var surface = surfaces[^1]; surface.View.InsertPlainText("임시 창에서 바꾼 배치 보존");
                RecoveryTests.DragGrip(surface.View, "MoveGrip", 20, 15);
                RecoveryTests.DragGrip(surface.View, "ResizeGrip", 60, 50);
                Assert(NativeMethods.SetWindowPos(surface.Host.FallbackHandle, 0, 120, 130, 420, 380,
                    NativeMethods.SwpNoActivate | NativeMethods.SwpNoZOrder));
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                saved = surface.Host.CaptureBounds();
                Assert(Wait(workspace.FlushAllAsync()));
                var record = new FileNoteRepository(folder, id).Load(value => RtfDocumentCodec.Load(value.Rtf), out _)!;
                Assert(record.Bounds == saved && saved.X == 120 && saved.Y == 130);
                nint fallback = surface.Host.FallbackHandle;
                Assert(Wait(workspace.HideAsync(id)) && !NativeMethods.IsWindow(fallback));
                Assert(Wait(workspace.ShowAsync(id)) && surfaces[^1].Host.CaptureBounds() == saved);
            }
            using (var workspace = CreateWorkspace())
            {
                Wait(workspace.InitializeAsync());
                Assert(workspace.Items.Single().Id == id && workspace.Items.Single().Error is null);
                var surface = surfaces[^1];
                Assert(surface.Host.CaptureBounds() == saved && Text(surface.View) == "임시 창에서 바꾼 배치 보존");
                using var desktop = new HwndSource(new HwndSourceParameters("Fallback geometry desktop", 1600, 900)
                    { WindowStyle = unchecked((int)0x80000000) });
                parent = desktop.Handle; surface.Host.CheckConnection();
                Assert(surface.Host.Handle != 0 && !surface.Host.HasFallback && surface.Host.CaptureBounds() == saved);
                Assert(Wait(workspace.FlushAllAsync()));
            }
            Assert(surfaces.All(surface => !surface.Host.HasFallback && surface.Host.Handle == 0));
        });
        check("임시 창 저장 실패: 실제 닫기 취소·재부착·최신 내용·배치 보존", () =>
        {
            string folder = Path.Combine(root, "fallback-save-failure");
            const string latestText = "저장 실패 뒤 계속 편집한 최종 내용";
            NoteBounds? latestBounds = null;
            var surfaces = new List<StartupSurface>(); nint parent = 0;
            NoteWorkspace CreateWorkspace() => new(folder, (view, bounds) =>
            { var surface = new StartupSurface(view, bounds, () => parent); surfaces.Add(surface); return surface; });
            using var desktop = new HwndSource(new HwndSourceParameters("Save failure test desktop", 1600, 900)
                { WindowStyle = unchecked((int)0x80000000) });
            Guid id;
            using (var workspace = CreateWorkspace())
            {
                Wait(workspace.InitializeAsync()); id = workspace.Items.Single().Id;
                var surface = surfaces[^1]; var view = surface.View;
                view.InsertPlainText("이전 정상 저장"); view.SetPaper("Peach");
                RecoveryTests.DragGrip(view, "ResizeGrip", 30, 20);
                Assert(Wait(workspace.FlushAllAsync()));
                var repository = new FileNoteRepository(folder, id);
                byte[] before = File.ReadAllBytes(repository.PrimaryPath);
                view.TextEditor.SelectAll(); view.InsertPlainText("닫기 실패 뒤에도 지킬 최신 한글 내용");
                view.TextEditor.SelectAll(); view.ApplyColor(Brushes.RoyalBlue);
                view.TextEditor.Selection.ApplyPropertyValue(TextElement.FontWeightProperty, System.Windows.FontWeights.Bold);
                RecoveryTests.DragGrip(view, "ResizeGrip", 30, 20);
                nint fallback = surface.Host.FallbackHandle;
                using (var held = new FileStream(repository.PrimaryPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var closeAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    bool busySeen = false;
                    void OnChanged(object? sender, EventArgs e)
                    {
                        if (workspace.IsBusy) busySeen = true;
                        else if (busySeen) closeAttempt.TrySetResult();
                    }
                    workspace.Changed += OnChanged;
                    try
                    {
                        // WM_CLOSE targets only our hidden test window, exercising the real Closing handler.
                        RecoveryTests.CloseTestWindow(fallback);
                        Wait(closeAttempt.Task.WaitAsync(TimeSpan.FromSeconds(10)));
                    }
                    finally { workspace.Changed -= OnChanged; }
                    Assert(surface.Host.HasFallback && NativeMethods.IsWindow(fallback));
                    Assert(!workspace.Items.Single().Hidden && view.IsEnabled && Text(view) == "닫기 실패 뒤에도 지킬 최신 한글 내용");
                    Assert(File.ReadAllBytes(repository.PrimaryPath).SequenceEqual(before));
                    view.TextEditor.SelectAll(); view.InsertPlainText(latestText);
                    view.TextEditor.SelectAll(); view.ApplyColor(Brushes.RoyalBlue);
                    view.TextEditor.Selection.ApplyPropertyValue(TextElement.FontWeightProperty, System.Windows.FontWeights.Bold);
                    RecoveryTests.DragGrip(view, "ResizeGrip", 10, 10);
                    latestBounds = surface.Host.CaptureBounds();
                    view.SetCollapsed(true); Assert(!view.IsCollapsed);
                    bool exited = false;
                    Assert(!Wait(workspace.SaveThenAsync(() => exited = true)) && !exited);
                    parent = desktop.Handle; surface.Host.CheckConnection();
                    Assert(surface.Host.Handle != 0 && !surface.Host.HasFallback && !NativeMethods.IsWindow(fallback));
                    Assert(surface.Host.CaptureBounds() == latestBounds);
                    Assert(Text(view) == latestText && view.Paper == "Peach" && view.IsEnabled);
                    Assert(((System.Windows.Controls.Button)view.FindName("SaveButton")).Visibility == System.Windows.Visibility.Visible);
                    view.SetCollapsed(true); Assert(!view.IsCollapsed);
                }
                Assert(Wait(workspace.FlushAllAsync()));
                Assert(((System.Windows.Controls.Button)view.FindName("SaveButton")).Visibility == System.Windows.Visibility.Collapsed);
                view.SetCollapsed(true); Assert(view.IsCollapsed);
                Assert(Wait(workspace.HideAsync(id)));
                var stored = repository.Load(value => RtfDocumentCodec.Load(value.Rtf), out _)!;
                Assert(stored.Hidden && stored.Collapsed && stored.Paper == "Peach");
                Assert(stored.Bounds == latestBounds);
                var doc = RtfDocumentCodec.Load(stored.Rtf);
                Assert(new TextRange(doc.ContentStart, doc.ContentEnd).Text.Trim() == latestText);
                Assert(Directory.GetFiles(folder, "*.tmp").Length == 0);
                Assert(Wait(workspace.ShowAsync(id)));
            }
            using (var workspace = CreateWorkspace())
            {
                Wait(workspace.InitializeAsync());
                var view = surfaces[^1].View;
                Assert(workspace.Items.Single().Id == id && view.IsCollapsed && view.Paper == "Peach");
                Assert(surfaces[^1].Host.CaptureBounds() == latestBounds);
                Assert(Text(view) == latestText);
                var range = new TextRange(view.TextEditor.Document.ContentStart, view.TextEditor.Document.ContentEnd.GetPositionAtOffset(-1)!);
                Assert(range.GetPropertyValue(TextElement.FontWeightProperty).Equals(System.Windows.FontWeights.Bold));
                Assert(range.GetPropertyValue(TextElement.ForegroundProperty) is SolidColorBrush brush && brush.Color == Colors.RoyalBlue);
                Assert(Wait(workspace.FlushAllAsync()));
            }
            Assert(surfaces.All(surface => !surface.Host.HasFallback && surface.Host.Handle == 0));
        });
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
                Assert(record.Hidden && record.SchemaVersion == 4 && record.Bounds.X == -120);
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
                Assert(record.SchemaVersion == 4 && record.Hidden && record.DeletedAtUtc is not null);
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
        check("보관: 색상·접힘·체크리스트 보존, 실행 취소와 재실행 복원", () =>
        {
            var surfaces = new List<Surface>(); Guid id;
            using (var workspace = Workspace("archive", surfaces))
            {
                Wait(workspace.InitializeAsync()); id = workspace.Items.Single().Id;
                var view = surfaces[0].View!; view.InsertPlainText("보관할 생각"); view.ToggleChecklist(); view.ToggleChecklistDone();
                view.SetPaper("Peach"); view.SetCollapsed(true);
                Assert(Wait(workspace.ArchiveAsync(id)) && surfaces[0].Disposed);
                Assert(workspace.Items.Count == 0 && workspace.ArchiveItems.Single().Id == id && workspace.CanUndoArchive);
                Assert(!Wait(workspace.ShowAsync(id)));
                Assert(Wait(workspace.UndoArchiveAsync()));
                var restored = surfaces[^1].View!;
                Assert(restored.Paper == "Peach" && restored.IsCollapsed && Text(restored) == "☑ 보관할 생각");
                Assert(Wait(workspace.ArchiveAsync(id)));
            }
            var nextSurfaces = new List<Surface>(); using var next = Workspace("archive", nextSurfaces);
            Wait(next.InitializeAsync()); Assert(next.ArchiveItems.Single().Id == id && nextSurfaces.Count == 0);
            Assert(Wait(next.UnarchiveAsync(id)) && next.Items.Single().Hidden);
            Assert(Wait(next.ShowAsync(id)) && nextSurfaces[0].View!.Paper == "Peach");
        });
        check("보관 실패: 최신 편집기·색상 보존, 복원 실패 시 보관 상태 유지", () =>
        {
            var surfaces = new List<Surface>(); using var workspace = Workspace("archive-failure", surfaces);
            Wait(workspace.InitializeAsync()); var id = workspace.Items.Single().Id;
            var view = surfaces[0].View!; view.InsertPlainText("원본"); Assert(Wait(workspace.FlushAllAsync()));
            view.InsertPlainText(" 최신"); view.SetPaper("Sage");
            var repo = new FileNoteRepository(Path.Combine(root, "archive-failure"), id);
            using (var held = new FileStream(repo.PrimaryPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert(!Wait(workspace.ArchiveAsync(id)));
            Assert(view.IsEnabled && view.Paper == "Sage" && Text(view).Contains("최신") && workspace.ArchiveItems.Count == 0);
            Assert(Wait(workspace.ArchiveAsync(id)));
            using (var held = new FileStream(repo.PrimaryPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert(!Wait(workspace.UnarchiveAsync(id)));
            Assert(workspace.ArchiveItems.Single().Id == id);
            Assert(Wait(workspace.TrashAsync(id)) && workspace.ArchiveItems.Count == 0 && workspace.TrashItems.Count == 1);
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
