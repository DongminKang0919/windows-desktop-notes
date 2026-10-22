using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WindowsMemo.Infrastructure;
using WindowsMemo.Models;
using WindowsMemo.Services;
using WindowsMemo.Views;

namespace WindowsMemo.Tests;

internal static class NoteListTests
{
    private sealed class Surface(NoteView view) : INoteSurface
    {
        internal NoteView View { get; } = view;
        public NoteBounds CaptureBounds() => new(48, 64, 320, 300);
        public event EventHandler? BoundsChanged { add { } remove { } }
        public void Dispose() { }
    }
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(AppContext.BaseDirectory, "note-list-tests", Guid.NewGuid().ToString("N"));
        internal List<Surface> Surfaces { get; } = [];
        internal Guid First { get; } = Guid.NewGuid();
        internal Guid Second { get; } = Guid.NewGuid();
        internal Guid Archive { get; } = Guid.NewGuid();
        internal Guid Trash { get; } = Guid.NewGuid();
        internal NoteWorkspace Workspace { get; }
        internal Fixture()
        {
            Seed(First, "☐ 주말 산책길 찾아보기\n둘째 줄은 제목에 포함하지 않음");
            Seed(Second, "☑ 주말 산책길 찾아보기\n다른 메모");
            Seed(Archive, "읽고 싶은 책\n이번 달 독서", archived: true);
            Seed(Trash, "정리할 생각\n버린 메모", trashed: true);
            Workspace = Open(); Wait(Workspace.InitializeAsync());
        }
        internal NoteWorkspace Open() => new(Root, (view, _) => { var surface = new Surface(view); Surfaces.Add(surface); return surface; });
        private void Seed(Guid id, string text, bool archived = false, bool trashed = false)
        {
            var now = DateTimeOffset.UtcNow;
            // Use the editor's paragraph/line-break insertion, as real saved notes do.
            using var editor = new NoteView(); Require(editor.InsertPlainText(text));
            var record = new NoteRecord(4, id, RtfDocumentCodec.Save(editor.TextEditor.Document),
                new(48, 64, 320, 300), now, now, Hidden: true, Archived: archived, DeletedAtUtc: trashed ? now : null);
            new FileNoteRepository(Root, id).Save(record);
        }
        public void Dispose() => Workspace.Dispose();
    }
    internal static void Run(Action<string, Action> check)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            check("메모 제목: 숨김·보관·휴지통 복원 시 첫 줄 요약·길이·손상 파일 보존", () =>
            {
                using var f = new Fixture(); Require(f.Surfaces.Count == 0);
                Require(f.Workspace.Items.All(item => item.Title == "주말 산책길 찾아보기"));
                Require(f.Workspace.ArchiveItems.Single().Title == "읽고 싶은 책" && f.Workspace.TrashItems.Single().Title == "정리할 생각");
                string text = "\r\n☐ " + new string('한', 79) + "😀끝\n둘째 줄";
                using var view = new NoteView(); view.InsertPlainText(text);
                Require(NoteTitleSummary.FromText(text) == view.NoteTitle && view.NoteTitle.Length == 79);
                Require(NoteTitleSummary.FromText(" \r\n☑ ") == "메모");
                Guid broken = Guid.NewGuid(); string path = new FileNoteRepository(f.Root, broken).PrimaryPath;
                File.WriteAllText(path, "broken encrypted note"); string[] before = Fingerprint(f.Root);
                using var restarted = f.Open(); Wait(restarted.InitializeAsync());
                Require(restarted.Items.Single(item => item.Id == broken).Title == "확인할 수 없는 메모");
                Require(restarted.Items.Single(item => item.Id == broken).Error is not null && before.SequenceEqual(Fingerprint(f.Root)) && f.Surfaces.Count == 0);
            });
            check("메모 목록: 제목 실시간 변경·선택 유지·본문만 편집 시 목록 알림 없음·숨김/재실행", () =>
            {
                using var f = new Fixture(); var window = new NoteListWindow(f.Workspace);
                try
                {
                    var layout = (Grid)window.Content;
                    layout.Measure(new Size(432, 376)); layout.Arrange(new Rect(0, 0, 432, 376)); layout.UpdateLayout();
                    var list = List(window); list.SelectedItem = list.Items.OfType<NoteListItem>().Single(item => item.Id == f.First);
                    Click(Action(window, "선택한 메모 열기")); Wait(Until(() => !f.Workspace.IsBusy && f.Surfaces.Count == 1));
                    var view = f.Surfaces.Single().View; view.TextEditor.SelectAll(); view.InsertPlainText("새로운 첫 줄");
                    Require(((NoteListItem)list.SelectedItem).Id == f.First && ((NoteListItem)list.SelectedItem).Title == "새로운 첫 줄");
                    layout.UpdateLayout(); Require(Visuals(layout).OfType<TextBlock>().Any(text => text.Text == "새로운 첫 줄"));
                    int changes = 0; f.Workspace.Changed += (_, _) => changes++;
                    view.InsertPlainText("\n본문만 추가"); Require(changes == 0);
                    Require(Wait(f.Workspace.HideAsync(f.First)));
                    Require(f.Workspace.Items.Single(item => item.Id == f.First).Title == "새로운 첫 줄");
                    using var restarted = f.Open(); Wait(restarted.InitializeAsync());
                    Require(restarted.Items.Single(item => item.Id == f.First).Title == "새로운 첫 줄");
                }
                finally { window.Close(); }
            });
            check("메모 목록: 같은 제목의 ID별 Enter 열기·빈 영역 더블클릭 무시·보관/휴지통 복원", () =>
            {
                using var f = new Fixture(); var window = new NoteListWindow(f.Workspace);
                try
                {
                    var list = List(window); list.SelectedItem = list.Items.OfType<NoteListItem>().Single(item => item.Id == f.Second);
                    list.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent });
                    Require(f.Surfaces.Count == 0);
                    using var source = new HwndSource(new HwndSourceParameters("Isolated list keyboard check") { WindowStyle = unchecked((int)0x80000000) });
                    list.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
                    Wait(Until(() => !f.Workspace.IsBusy && f.Surfaces.Count == 1));
                    Require(f.Workspace.Items.Single(item => item.Id == f.First).Hidden && !f.Workspace.Items.Single(item => item.Id == f.Second).Hidden);
                    var archive = new NoteListWindow(f.Workspace, archive: true); var trash = new NoteListWindow(f.Workspace, trash: true);
                    try
                    {
                        List(archive).SelectedIndex = 0; Click(Action(archive, "선택한 메모 복원")); Wait(Until(() => !f.Workspace.IsBusy && f.Workspace.ArchiveItems.Count == 0));
                        List(trash).SelectedIndex = 0; Click(Action(trash, "선택한 메모 복원")); Wait(Until(() => !f.Workspace.IsBusy && f.Workspace.TrashItems.Count == 0));
                        Require(f.Workspace.Items.Single(item => item.Id == f.Archive).Title == "읽고 싶은 책");
                        Require(f.Workspace.Items.Single(item => item.Id == f.Trash).Title == "정리할 생각");
                        Require(!Action(archive, "선택한 메모 복원").IsEnabled && !Action(trash, "영구 삭제").IsEnabled);
                    }
                    finally { archive.Close(); trash.Close(); }
                }
                finally { window.Close(); }
            });
            check("메모 목록: 잠긴 파일에서도 추가 읽기·쓰기 없는 목록·빈 상태·배율 렌더", () =>
            {
                using var f = new Fixture(); string[] before = Fingerprint(f.Root);
                using (var locked = new FileStream(new FileNoteRepository(f.Root, f.First).PrimaryPath, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    foreach (var mode in new[] { "notes", "archive", "trash" })
                    {
                        var window = new NoteListWindow(f.Workspace, trash: mode == "trash", archive: mode == "archive");
                        try
                        {
                            Require(List(window).Items.Count == (mode == "notes" ? 2 : 1) && f.Surfaces.Count == 0);
                            var layout = (Grid)window.Content; string images = Path.Combine(AppContext.BaseDirectory, "ui-previews"); Directory.CreateDirectory(images);
                            foreach (double scale in new[] { 1.0, 1.5, 2.0 })
                            {
                                layout.Measure(new Size(432, 376)); layout.Arrange(new Rect(0, 0, 432, 376)); layout.UpdateLayout();
                                Require(Action(window, mode == "notes" ? "새 메모" : "선택한 메모 복원").ActualHeight >= 28);
                                NoteDesignTests.Save(layout, 432, 376, scale, Path.Combine(images, $"{mode}-list-{(int)(scale * 100)}.png"));
                            }
                            // The root's 22-DIP margin is included in its available size.
                            layout.Measure(new Size(336, 340)); layout.Arrange(new Rect(0, 0, 336, 340)); layout.UpdateLayout();
                            Require(List(window).ActualHeight >= 52, $"Narrow {mode}: root {layout.ActualWidth}x{layout.ActualHeight}, list height {List(window).ActualHeight}.");
                        }
                        finally { window.Close(); }
                    }
                }
                Require(before.SequenceEqual(Fingerprint(f.Root)));
                Require(Wait(f.Workspace.UnarchiveAsync(f.Archive)));
                var empty = new NoteListWindow(f.Workspace, archive: true);
                try { Require(List(empty).Items.Count == 0 && ((Grid)empty.Content).Children.OfType<TextBlock>().Any(text => text.Visibility == Visibility.Visible && text.Text.StartsWith("보관한 메모가 없습니다."))); }
                finally { empty.Close(); }
            });
            check("메모 제목: 복구 사본 요약·편집 사본 재인식·닫은 목록 참조 회수", () =>
            {
                using var f = new Fixture(); Guid token = Guid.NewGuid();
                File.Copy(new FileNoteRepository(f.Root, f.First).PrimaryPath, Path.Combine(f.Root, token.ToString("N") + ".tmp"));
                var preview = new PendingNoteRecovery(f.Root).Read(token);
                var recovered = Wait(f.Workspace.RecoverPendingAsync(preview)); Require(recovered.Succeeded);
                Require(f.Workspace.Items.Single(item => item.Id == recovered.Id).Title == "주말 산책길 찾아보기");
                Require(Wait(f.Workspace.ShowAsync(recovered.Id))); var view = f.Surfaces.Single().View;
                view.TextEditor.SelectAll(); view.InsertPlainText("편집한 복구 사본"); Require(Wait(f.Workspace.HideAsync(recovered.Id)));
                using var restarted = f.Open(); Wait(restarted.InitializeAsync());
                Require(Wait(restarted.RecoverPendingAsync(preview)).AlreadyRecovered && restarted.Items.Single(item => item.Id == recovered.Id).Title == "편집한 복구 사본");
                var references = Enumerable.Range(0, 20).Select(_ => ClosedWindow(restarted)).ToArray();
                Wait(Task.Delay(25)); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Require(references.All(reference => !reference.IsAlive));
            });
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ClosedWindow(NoteWorkspace workspace)
    {
        var window = new NoteListWindow(workspace); var layout = (Grid)window.Content;
        layout.Measure(new Size(432, 376)); layout.Arrange(new Rect(0, 0, 432, 376)); layout.UpdateLayout();
        window.Close(); return new(window);
    }
    private static ListBox List(Window window) => ((Grid)window.Content).Children.OfType<ListBox>().Single();
    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static Button Action(Window window, string text) => ((Grid)window.Content).Children.OfType<WrapPanel>().Single().Children.OfType<Button>().Single(button => Equals(button.Content, text));
    private static void Click(Button button) { Require(button.IsEnabled); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
    private static string[] Fingerprint(string root) => Directory.GetFiles(root).OrderBy(path => path, StringComparer.Ordinal)
        .Select(path => $"{Path.GetFileName(path)}:{File.GetLastWriteTimeUtc(path).Ticks}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}").ToArray();
    private static async Task Until(Func<bool> condition)
    { var deadline = DateTime.UtcNow.AddSeconds(10); while (!condition()) { Require(DateTime.UtcNow < deadline); await Task.Delay(10); } }
    private static T Wait<T>(Task<T> task) { Wait((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Wait(Task task)
    {
        var dispatcher = Dispatcher.CurrentDispatcher; var frame = new DispatcherFrame();
        task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
        if (!task.IsCompleted) Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
    }
    private static void Require(bool condition, string? message = null) { if (!condition) throw new InvalidOperationException(message ?? "Note list assertion failed."); }
}
