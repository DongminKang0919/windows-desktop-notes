using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
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
            check("날짜별 메모: 재사용·제목 변경·재시작·휴지통 복원", () =>
            {
                using var f = new Fixture();
                var date = new DateOnly(2026, 11, 20);
                var create = f.Workspace.OpenCalendarNoteAsync(date);
                Wait(create);
                Require(create.Result);
                var id = f.Workspace.CalendarNote(date)!.Id;
                var view = f.Surfaces.Last().View;
                var text = new TextRange(view.TextEditor.Document.ContentStart, view.TextEditor.Document.ContentEnd);
                Require(text.Text.StartsWith("☐ ", StringComparison.Ordinal));
                view.SetCustomTitle("오늘 할 일");
                var reopen = f.Workspace.OpenCalendarNoteAsync(date);
                Wait(reopen);
                Require(reopen.Result && f.Workspace.CalendarDates.Count(day => day == date) == 1);
                var trash = f.Workspace.TrashAsync(id);
                Wait(trash);
                Require(trash.Result);
                f.Workspace.Dispose();

                using var restarted = f.Open();
                Wait(restarted.InitializeAsync());
                Require(restarted.CalendarNote(date) is { Trashed: true, Title: "오늘 할 일" } item && item.Id == id);
                var restore = restarted.OpenCalendarNoteAsync(date);
                Wait(restore);
                Require(restore.Result && restarted.CalendarNote(date) is { Trashed: false } active && active.Id == id);
            });

            check("사용자 백업: 기존 메모 보존·상태 복원·중복과 잘린 백업 차단", () =>
            {
                using var f = new Fixture();
                string path = Path.Combine(f.Root, "notes.wmbackup");
                var original = Fingerprint(f.Root);
                var export = f.Workspace.ExportBackupAsync(path);
                Wait(export);
                Require(export.Result);
                var malformed = Path.Combine(f.Root, "truncated.wmbackup");
                byte[] bytes = File.ReadAllBytes(path);
                File.WriteAllBytes(malformed, bytes[..^1]);
                try
                {
                    Wait(f.Workspace.RestoreBackupAsync(malformed));
                    throw new InvalidOperationException("잘린 백업을 복원했습니다.");
                }
                catch (InvalidDataException) { }
                Require(f.Workspace.Items.Count == 2 && f.Workspace.ArchiveItems.Count == 1 && f.Workspace.TrashItems.Count == 1);

                var restore = f.Workspace.RestoreBackupAsync(path);
                Wait(restore);
                Require(restore.Result == 4 && f.Workspace.Items.Count == 4
                    && f.Workspace.ArchiveItems.Count == 2 && f.Workspace.TrashItems.Count == 2);
                Require(original.All(Fingerprint(f.Root).Contains));
                using var reopened = f.Open();
                Wait(reopened.InitializeAsync());
                var repeat = reopened.RestoreBackupAsync(path);
                Wait(repeat);
                Require(repeat.Result == 0 && reopened.Items.All(item => item.Hidden));
            });
            check("통합 관리: 제목 검색·상태 필터·탭 전환은 파일 읽기 없이 선택과 범위 유지", () =>
            {
                using var f = new Fixture(); var window = new NoteListWindow(f.Workspace);
                try
                {
                    string[] before = Fingerprint(f.Root);
                    var search = Visuals((Grid)window.Content).OfType<TextBox>().Single();
                    var filter = Visuals((Grid)window.Content).OfType<ComboBox>().Single();
                    using (var locked = new FileStream(new FileNoteRepository(f.Root, f.First).PrimaryPath, FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        search.Text = "  주말  "; Require(List(window).Items.Count == 2);
                        SaveManagerPreview(window, "manager-search-150.png");
                        List(window).SelectedIndex = 0; Guid selected = ((NoteListItem)List(window).SelectedItem).Id;
                        window.Navigate(NoteListWindow.Section.Archive); Require(List(window).Items.Count == 0);
                        Require(Visuals((Grid)window.Content).OfType<TextBlock>().Any(text => text.Text.StartsWith("일치하는 메모가 없습니다.") && text.Visibility == Visibility.Visible));
                        SaveManagerPreview(window, "manager-no-results-150.png");
                        window.Navigate(NoteListWindow.Section.Notes); Require(((NoteListItem)List(window).SelectedItem).Id == selected);
                        filter.SelectedIndex = 1; Require(List(window).Items.Count == 0);
                        filter.SelectedIndex = 2; Require(List(window).Items.Count == 2);
                        filter.SelectedIndex = 3; Require(List(window).Items.Count == 0);
                        filter.SelectedIndex = 0; search.Clear();
                        window.Navigate(NoteListWindow.Section.Archive); Require(List(window).Items.Count == 1);
                        search.Text = "읽고"; Require(List(window).Items.Count == 1);
                        window.Navigate(NoteListWindow.Section.Trash); Require(List(window).Items.Count == 0);
                        search.Text = "정리"; Require(List(window).Items.Count == 1);
                    }
                    Require(before.SequenceEqual(Fingerprint(f.Root)) && f.Surfaces.Count == 0);
                    window.Navigate(NoteListWindow.Section.Notes); search.Clear();
                    Require(Wait(f.Workspace.ShowAsync(f.First)));
                    search.Text = "바뀐 제목"; Require(List(window).Items.Count == 0);
                    f.Surfaces.Single().View.SetCustomTitle("바뀐 제목"); Require(List(window).Items.Count == 1);
                    Require(Wait(f.Workspace.HideAsync(f.First)));
                    Require(List(window).Items.Count == 1);
                }
                finally { window.Close(); }
            });
            check("삭제 실행 취소: 실패 시 휴지통/취소 대상 보존·재시도·보관함 복귀·새 삭제 우선", () =>
            {
                using var f = new Fixture(); var window = new NoteListWindow(f.Workspace);
                try
                {
                    List(window).SelectedItem = List(window).Items.OfType<NoteListItem>().Single(item => item.Id == f.First);
                    Click(Action(window, "휴지통으로 이동")); Wait(Until(() => !f.Workspace.IsBusy));
                    Require(f.Workspace.CanUndoTrash && f.Workspace.LastTrashedId == f.First);
                    var undo = Visuals((Grid)window.Content).OfType<Button>().Single(button => Equals(button.Content, "삭제 실행 취소"));
                    Require(undo.Visibility == Visibility.Visible);
                    SaveManagerPreview(window, "manager-undo-150.png");
                    string[] before = Fingerprint(f.Root);
                    using (var locked = new FileStream(new FileNoteRepository(f.Root, f.First).PrimaryPath, FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        Click(undo); window.Navigate(NoteListWindow.Section.Archive);
                        Require(window.CurrentSection == NoteListWindow.Section.Notes);
                        Wait(Until(() => Feedback(window).Text.Contains("취소하지 못했습니다")));
                        Require(f.Workspace.CanUndoTrash);
                    }
                    Require(before.SequenceEqual(Fingerprint(f.Root)));
                    Click(undo); Wait(Until(() => Feedback(window).Text.Contains("취소했습니다")));
                    Require(!f.Workspace.CanUndoTrash && ((NoteListItem)List(window).SelectedItem).Id == f.First);
                    Require(Wait(f.Workspace.TrashAsync(f.Archive)));
                    Require(Wait(f.Workspace.UndoTrashAsync()));
                    Require(f.Workspace.ArchiveItems.Any(item => item.Id == f.Archive));
                    Require(Wait(f.Workspace.TrashAsync(f.First)) && Wait(f.Workspace.TrashAsync(f.Second)));
                    Require(f.Workspace.LastTrashedId == f.Second && Wait(f.Workspace.UndoTrashAsync()));
                    Require(f.Workspace.TrashItems.Any(item => item.Id == f.First) && !f.Workspace.CanUndoTrash);
                    using var restarted = f.Open(); Wait(restarted.InitializeAsync());
                    Require(restarted.Items.Any(item => item.Id == f.Second) && restarted.ArchiveItems.Any(item => item.Id == f.Archive));
                    Require(!restarted.CanUndoTrash);
                }
                finally { window.Close(); }
            });
            check("휴지통 접근: 목록에서 삭제·휴지통 열기·복원·목록 복귀·재실행 보존", () =>
            {
                using var f = new Fixture();
                var notes = new NoteListWindow(f.Workspace);
                try
                {
                    Require(Wait(f.Workspace.ShowAsync(f.First)));
                    var view = f.Surfaces.Single().View;
                    view.SetCustomTitle("실수로 삭제한 제목");
                    byte[] body = RtfDocumentCodec.Save(view.TextEditor.Document);
                    List(notes).SelectedItem = List(notes).Items.OfType<NoteListItem>().Single(item => item.Id == f.First);
                    Click(Action(notes, "휴지통으로 이동"));
                    Wait(Until(() => !f.Workspace.IsBusy && f.Workspace.TrashItems.Any(item => item.Id == f.First)));
                    Require(!f.Workspace.Items.Any(item => item.Id == f.First));
                    var openTrash = Visuals((Grid)notes.Content).OfType<Button>().Single(button => Equals(button.Content, "휴지통"));
                    Click(openTrash); Require(notes.CurrentSection == NoteListWindow.Section.Trash);
                    List(notes).SelectedItem = List(notes).Items.OfType<NoteListItem>().Single(item => item.Id == f.First);
                    Click(Action(notes, "선택한 메모 복원"));
                    Wait(Until(() => !f.Workspace.IsBusy && f.Workspace.Items.Any(item => item.Id == f.First)));
                    Require(f.Workspace.Items.Single(item => item.Id == f.First).Hidden);
                    Require(!f.Workspace.TrashItems.Any(item => item.Id == f.First));
                    Require(Feedback(notes).Text.Contains("복원했습니다"));
                    SaveManagerPreview(notes, "manager-restored-150.png");
                    Visuals((Grid)notes.Content).OfType<TextBox>().Single().Text = "일치하지 않는 검색";
                    Click(Visuals((Grid)notes.Content).OfType<Button>().Single(button => Equals(button.Content, "복원한 메모 열기")));
                    Wait(Until(() => !f.Workspace.IsBusy && !f.Workspace.Items.Single(item => item.Id == f.First).Hidden));
                    Require(notes.CurrentSection == NoteListWindow.Section.Notes && ((NoteListItem)List(notes).SelectedItem).Id == f.First);
                    Require(Wait(f.Workspace.FlushAllAsync()));
                    using var restarted = f.Open(); Wait(restarted.InitializeAsync());
                    Require(restarted.Items.Single(item => item.Id == f.First).Title == "실수로 삭제한 제목");
                    var saved = new FileNoteRepository(f.Root, f.First).Load(record => RtfDocumentCodec.Load(record.Rtf), out _)!;
                    Require(saved.DeletedAtUtc is null && saved.Bounds == new NoteBounds(48, 64, 320, 300));
                    Require(saved.Rtf.SequenceEqual(body));
                    Require(Wait(restarted.ShowAsync(f.First)));
                    Require(f.Surfaces.Last().View.NoteTitle == "실수로 삭제한 제목");
                    notes.Close(); Click(openTrash); Require(notes.CurrentSection == NoteListWindow.Section.Notes);
                }
                finally { notes.Close(); }
            });
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
                    layout.Measure(new Size(432, 496)); layout.Arrange(new Rect(0, 0, 432, 496)); layout.UpdateLayout();
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
                    Click(Action(window, "숨기기"));
                    Wait(Until(() => !f.Workspace.IsBusy && f.Workspace.Items.Single(item => item.Id == f.Second).Hidden));
                    Require(!Action(window, "숨기기").IsEnabled);
                    Click(Action(window, "보관하기"));
                    Wait(Until(() => !f.Workspace.IsBusy && f.Workspace.ArchiveItems.Any(item => item.Id == f.Second)));
                    Wait(f.Workspace.UnarchiveAsync(f.Second));
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
                                layout.Measure(new Size(432, 496)); layout.Arrange(new Rect(0, 0, 432, 496)); layout.UpdateLayout();
                                Require(Action(window, mode == "notes" ? "새 메모" : "선택한 메모 복원").ActualHeight >= 28);
                                NoteDesignTests.Save(layout, 432, 496, scale, Path.Combine(images, $"{mode}-list-{(int)(scale * 100)}.png"));
                            }
                            // The root's 22-DIP margin is included in its available size.
                            layout.Measure(new Size(336, 460)); layout.Arrange(new Rect(0, 0, 336, 460)); layout.UpdateLayout();
                            Require(List(window).ActualHeight >= 52, $"Narrow {mode}: root {layout.ActualWidth}x{layout.ActualHeight}, list height {List(window).ActualHeight}.");
                            NoteDesignTests.Save(layout, 336, 460, 1.5, Path.Combine(images, $"{mode}-narrow-150.png"));
                            List(window).SelectedIndex = 0;
                            layout.Measure(new Size(432, 496)); layout.Arrange(new Rect(0, 0, 432, 496)); layout.UpdateLayout();
                            NoteDesignTests.Save(layout, 432, 496, 1.5, Path.Combine(images, $"{mode}-selected-150.png"));
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
            check("목록 작업 안내: 열기·보관/휴지통 복원 실패는 파일 보존·목록 안에서 재시도", () =>
            {
                foreach (string mode in new[] { "notes", "archive", "trash" })
                {
                    using var f = new Fixture(); Guid id = mode == "notes" ? f.First : mode == "archive" ? f.Archive : f.Trash;
                    string[] before = Fingerprint(f.Root);
                    var window = new NoteListWindow(f.Workspace, archive: mode == "archive", trash: mode == "trash");
                    try
                    {
                        List(window).SelectedItem = List(window).Items.OfType<NoteListItem>().Single(item => item.Id == id);
                        string action = mode == "notes" ? "선택한 메모 열기" : "선택한 메모 복원";
                        using (var locked = new FileStream(new FileNoteRepository(f.Root, id).PrimaryPath, FileMode.Open, FileAccess.Read, FileShare.None))
                        {
                            Click(Action(window, action)); Wait(Until(() => Feedback(window).Text.Contains("다시 시도")));
                            Require(!f.Workspace.IsBusy && Action(window, action).IsEnabled && f.Surfaces.Count == 0);
                            var feedback = Feedback(window);
                            Require(AutomationProperties.GetName(feedback) == feedback.Text && AutomationProperties.GetLiveSetting(feedback) == AutomationLiveSetting.Polite);
                            Require(!feedback.Text.Contains(f.Root) && !feedback.Text.Contains("Exception") && !feedback.Text.Contains("주말 산책"));
                            var layout = (Grid)window.Content; string images = Path.Combine(AppContext.BaseDirectory, "ui-previews"); Directory.CreateDirectory(images);
                            foreach (double scale in new[] { 1.0, 1.5, 2.0 })
                            {
                                layout.Measure(new Size(432, 496)); layout.Arrange(new Rect(0, 0, 432, 496)); layout.UpdateLayout();
                                NoteDesignTests.Save(layout, 432, 496, scale, Path.Combine(images, $"{mode}-feedback-{(int)(scale * 100)}.png"));
                            }
                            layout.Measure(new Size(336, 460)); layout.Arrange(new Rect(0, 0, 336, 460)); layout.UpdateLayout();
                            Require(List(window).ActualHeight >= 52 && Action(window, action).ActualHeight >= 28 && feedback.ActualHeight > 0);
                        }
                        Require(before.SequenceEqual(Fingerprint(f.Root)));
                        Click(Action(window, action)); Wait(Until(() => Feedback(window).Text.Contains(mode == "notes" ? "열었습니다" : "복원했습니다")));
                        Require(mode == "notes" ? f.Surfaces.Count == 1 : f.Workspace.Items.Any(item => item.Id == id && item.Hidden));
                    }
                    finally { window.Close(); }
                }
            });
            check("목록 작업 안내: 휴지통 저장 실패의 최신 편집·선택 유지와 재시도", () =>
            {
                using var f = new Fixture(); Require(Wait(f.Workspace.ShowAsync(f.First)));
                var view = f.Surfaces.Single().View; view.TextEditor.SelectAll(); view.InsertPlainText("저장 실패 후 최신 본문");
                var window = new NoteListWindow(f.Workspace);
                try
                {
                    List(window).SelectedItem = List(window).Items.OfType<NoteListItem>().Single(item => item.Id == f.First);
                    using (var locked = new FileStream(new FileNoteRepository(f.Root, f.First).PrimaryPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        Click(Action(window, "휴지통으로 이동")); Wait(Until(() => Feedback(window).Text.Contains("옮기지 못했습니다")));
                        Require(((NoteListItem)List(window).SelectedItem).Id == f.First && view.IsEnabled);
                        Require(new TextRange(view.TextEditor.Document.ContentStart, view.TextEditor.Document.ContentEnd).Text.Contains("최신 본문"));
                    }
                    Click(Action(window, "휴지통으로 이동")); Wait(Until(() => Feedback(window).Text.Contains("옮겼습니다")));
                    Require(f.Workspace.TrashItems.Any(item => item.Id == f.First));
                    var saved = new FileNoteRepository(f.Root, f.First).Load(record => RtfDocumentCodec.Load(record.Rtf), out _)!;
                    var document = RtfDocumentCodec.Load(saved.Rtf);
                    Require(new TextRange(document.ContentStart, document.ContentEnd).Text.Contains("최신 본문"));
                }
                finally { window.Close(); }
            });
            check("목록 작업 안내: 읽기 중 닫힌 창은 뒤늦은 안내 없이 완료 후 참조 회수", () =>
            {
                using var f = new Fixture(); string[] before = Fingerprint(f.Root); var references = new List<WeakReference>();
                using (var locked = new FileStream(new FileNoteRepository(f.Root, f.First).PrimaryPath, FileMode.Open, FileAccess.Read, FileShare.None))
                    for (int i = 0; i < 20; i++) references.Add(CloseWhileOpening(f));
                Require(f.Surfaces.Count == 0 && before.SequenceEqual(Fingerprint(f.Root)));
                Wait(Task.Delay(25)); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                Require(references.All(reference => !reference.IsAlive));
            });
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CloseWhileOpening(Fixture f)
    {
        var window = new NoteListWindow(f.Workspace);
        List(window).SelectedItem = List(window).Items.OfType<NoteListItem>().Single(item => item.Id == f.First);
        Click(Action(window, "선택한 메모 열기")); Require(f.Workspace.IsBusy);
        var feedback = Feedback(window); Require(feedback.Text.Contains("열고 있습니다"));
        window.Close(); Wait(Until(() => !f.Workspace.IsBusy)); Wait(Task.Delay(10));
        Require(feedback.Text == "" && AutomationProperties.GetName(feedback) == "" && List(window).Items.Count == 0);
        return new(window);
    }
    private static TextBlock Feedback(Window window) => Visuals((Grid)window.Content).OfType<TextBlock>()
        .Single(text => AutomationProperties.GetAutomationId(text) == "NoteListFeedback");
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ClosedWindow(NoteWorkspace workspace)
    {
        var window = new NoteListWindow(workspace); var layout = (Grid)window.Content;
        layout.Measure(new Size(432, 496)); layout.Arrange(new Rect(0, 0, 432, 496)); layout.UpdateLayout();
        window.Close(); return new(window);
    }
    private static ListBox List(Window window) => ((Grid)window.Content).Children.OfType<ListBox>().Single();
    private static void SaveManagerPreview(Window window, string name)
    {
        var layout = (Grid)window.Content;
        layout.Measure(new Size(432, 496)); layout.Arrange(new Rect(0, 0, 432, 496)); layout.UpdateLayout();
        Wait(Task.Delay(10)); layout.UpdateLayout();
        string images = Path.Combine(AppContext.BaseDirectory, "ui-previews"); Directory.CreateDirectory(images);
        NoteDesignTests.Save(layout, 432, 496, 1.5, Path.Combine(images, name));
    }
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
