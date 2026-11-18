using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WindowsMemo.Infrastructure;
using WindowsMemo.Models;
using WindowsMemo.Services;
using WindowsMemo.Views;

namespace WindowsMemo.Tests;

internal static class SettingsTitleTests
{
    private sealed class Surface(NoteBounds bounds) : INoteSurface
    {
        public NoteBounds CaptureBounds() => bounds;
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
    private static string Root() => Path.Combine(AppContext.BaseDirectory, "settings-title-tests", Guid.NewGuid().ToString("N"));
    private static void RunCore(Action<string, Action> check)
    {
        check("메모 제목: 본문과 독립·자동 제목 복귀·길이/줄바꿈/잘못된 유니코드 거부", () =>
        {
            using var view = new NoteView();
            view.InsertPlainText("본문 첫 줄");
            byte[] body = RtfDocumentCodec.Save(view.TextEditor.Document);
            view.SetCustomTitle("  직접 정한 제목  ");
            Assert(view.CustomTitle == "직접 정한 제목" && view.NoteTitle == "직접 정한 제목");
            Assert(body.SequenceEqual(RtfDocumentCodec.Save(view.TextEditor.Document)));
            view.InsertPlainText(" 본문 수정"); Assert(view.NoteTitle == "직접 정한 제목");
            view.SetCustomTitle(" "); Assert(view.CustomTitle == "" && view.NoteTitle.StartsWith("본문 첫 줄"));
            foreach (string invalid in new[] { new string('가', 81), "제목\n둘째 줄", "앞\t뒤", "줄\u2028바꿈", "잘못\ud800" })
                Throws(() => view.SetCustomTitle(invalid));
        });
        check("제목 저장 형식 5: 암호화·구형 기본값·구형 형식의 제목 거부", () =>
        {
            string root = Root(); using var view = new NoteView(); view.InsertPlainText("비공개 본문");
            var now = DateTimeOffset.UtcNow;
            var repo = new FileNoteRepository(root);
            var old = new NoteRecord(4, repo.Id, RtfDocumentCodec.Save(view.TextEditor.Document), new(20, 30, 320, 300), now, now);
            repo.Save(old);
            var legacy = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(old))!;
            legacy.AsObject().Remove("Title");
            byte[] legacyCipher = ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(legacy.ToJsonString()), null, DataProtectionScope.CurrentUser);
            File.WriteAllText(repo.PrimaryPath, JsonSerializer.Serialize(new { Version = 1, Protection = "DPAPI-CurrentUser", Ciphertext = legacyCipher }));
            Assert(repo.Load(_ => { }, out _)!.Title == "");
            var titled = old with { SchemaVersion = NoteRecord.CurrentSchema, Title = "나만의 제목" };
            repo.Save(titled);
            Assert(repo.Load(_ => { }, out _)!.Title == titled.Title);
            using var envelope = JsonDocument.Parse(File.ReadAllBytes(repo.PrimaryPath));
            Assert(envelope.RootElement.EnumerateObject().Count() == 3);
            byte[] plain = ProtectedData.Unprotect(envelope.RootElement.GetProperty("Ciphertext").GetBytesFromBase64(), null, DataProtectionScope.CurrentUser);
            try { using var document = JsonDocument.Parse(plain); Assert(document.RootElement.GetProperty("Title").GetString() == titled.Title); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            Throws(() => repo.Save(titled with { SchemaVersion = 4 }));
            Throws(() => repo.Save(titled with { Title = "줄\n바꿈" }));
        });
        check("제목 수명: 목록 즉시 갱신·숨김/보관/휴지통 복원·재실행 보존", () =>
        {
            string root = Root(); var views = new List<NoteView>();
            NoteWorkspace Open() => new(root, (view, bounds) => { views.Add(view); return new Surface(bounds!); });
            using var workspace = Open(); Wait(workspace.InitializeAsync());
            Guid id = workspace.Items.Single().Id;
            views[^1].SetCustomTitle("고정 제목"); Assert(workspace.Items.Single().Title == "고정 제목");
            Assert(Wait(workspace.HideAsync(id)) && Wait(workspace.ShowAsync(id)));
            Assert(views[^1].CustomTitle == "고정 제목");
            Assert(Wait(workspace.ArchiveAsync(id))); Assert(workspace.ArchiveItems.Single().Title == "고정 제목");
            Assert(Wait(workspace.UnarchiveAsync(id)) && Wait(workspace.ShowAsync(id)));
            Assert(Wait(workspace.TrashAsync(id))); Assert(workspace.TrashItems.Single().Title == "고정 제목");
            Assert(Wait(workspace.RestoreAsync(id)));
            workspace.Dispose();
            using var reopened = Open(); Wait(reopened.InitializeAsync());
            Assert(reopened.Items.Single().Title == "고정 제목");
        });
        check("제목 저장 실패: 변경한 제목과 편집기를 유지하고 재시도", () =>
        {
            string root = Root(); NoteView? editor = null;
            using var workspace = new NoteWorkspace(root, (view, bounds) => { editor = view; return new Surface(bounds!); });
            Wait(workspace.InitializeAsync()); Assert(Wait(workspace.FlushAllAsync()));
            Guid id = workspace.Items.Single().Id; string file = new FileNoteRepository(root, id).PrimaryPath;
            editor!.SetCustomTitle("저장 재시도 제목");
            using (var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
            { Assert(!Wait(workspace.HideAsync(id))); Assert(editor.CustomTitle == "저장 재시도 제목" && !workspace.Items.Single().Hidden); }
            Assert(Wait(workspace.HideAsync(id)));
            Assert(new FileNoteRepository(root, id).Load(_ => { }, out _)!.Title == "저장 재시도 제목");
        });
        check("제목 복구: 임시 원본 제목 보존·제목만 다른 파일은 자동 정리 금지", () =>
        {
            string root = Root(); Directory.CreateDirectory(root); using var view = new NoteView(); view.InsertPlainText("복구 본문");
            var now = DateTimeOffset.UtcNow;
            var record = new NoteRecord(5, Guid.NewGuid(), RtfDocumentCodec.Save(view.TextEditor.Document), new(30, 40, 320, 300), now.AddDays(-3), now.AddDays(-2), Title: "원래 제목");
            var seed = new FileNoteRepository(Root(), record.Id); seed.Save(record);
            Guid token = Guid.NewGuid(); string pending = Path.Combine(root, token.ToString("N") + ".tmp");
            File.Copy(seed.PrimaryPath, pending); File.SetLastWriteTimeUtc(pending, now.AddDays(-2).UtcDateTime);
            new FileNoteRepository(root, record.Id).Save(record with { Title = "다른 제목", UpdatedAtUtc = now });
            var maintenance = TemporaryNoteMaintenance.Run(root, now, _ => { });
            Assert(maintenance.Removed == 0 && File.Exists(pending));
            using var workspace = new NoteWorkspace(root, (_, bounds) => new Surface(bounds!)); Wait(workspace.InitializeAsync());
            var result = Wait(workspace.RecoverPendingAsync(new PendingNoteRecovery(root).Read(token)));
            Assert(result.Succeeded && workspace.Items.Single(i => i.Id == result.Id).Title == "원래 제목");
        });
        check("설정: 기본값 조회는 파일 생성 없음·저장/재시작·기존 메모 기본값 유지", () =>
        {
            string root = Root(); var settings = new SettingsService(root);
            Assert(settings.Current == new AppSettings() && !Directory.Exists(root));
            var custom = new AppSettings(DefaultPaper: "Peach", NoteWidth: 480, NoteHeight: 360, ShowListOnLaunch: false);
            Wait(settings.SaveAsync(custom)); Assert(new SettingsService(root).Current == custom);
            var views = new List<NoteView>(); var boundsList = new List<NoteBounds>();
            using var workspace = new NoteWorkspace(root, (view, bounds) => { views.Add(view); boundsList.Add(bounds!); return new Surface(bounds!); });
            Wait(workspace.InitializeAsync());
            Assert(views[^1].Paper == "Peach" && boundsList[^1].Width == 480 && boundsList[^1].Height == 360);
            Wait(workspace.Settings.SaveAsync(new(DefaultPaper: "Sage", NoteWidth: 400, NoteHeight: 280)));
            workspace.Create(); Assert(views[^1].Paper == "Sage" && boundsList[^1].Width == 400);
            Assert(views[0].Paper == "Peach" && boundsList[0].Width == 480);
        });
        check("설정 실패: 범위 거부·잠금 시 이전 설정 보존·손상 파일은 기본값과 안내", () =>
        {
            string root = Root(); var settings = new SettingsService(root); var store = new FileSettingsStore(root);
            Wait(settings.SaveAsync(new(DefaultPaper: "Sage")));
            var previous = settings.Current; byte[] bytes = File.ReadAllBytes(store.FilePath);
            foreach (var invalid in new[] { previous with { NoteWidth = double.NaN }, previous with { NoteHeight = 1 }, previous with { DefaultPaper = "Unknown" } })
                Throws(() => Wait(settings.SaveAsync(invalid)));
            using (var locked = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
                Throws(() => Wait(settings.SaveAsync(new(DefaultPaper: "Peach"))));
            Assert(settings.Current == previous && bytes.SequenceEqual(File.ReadAllBytes(store.FilePath)));
            File.WriteAllText(store.FilePath, "damaged settings");
            var damaged = new SettingsService(root); Assert(damaged.LoadFailed && damaged.Current == new AppSettings());
            Assert(File.ReadAllText(store.FilePath) == "damaged settings");
        });
        check("설정 화면: 저장 버튼·진단 모드 자동 실행 비활성·제목 입력 창", () =>
        {
            string root = Root(); var settings = new SettingsService(root);
            var window = new SettingsWindow(settings, null);
            var layout = (Grid)window.Content; layout.Measure(new Size(416, 470)); layout.Arrange(new Rect(0, 0, 416, 470)); layout.UpdateLayout();
            var children = Visuals(layout).ToArray();
            var width = children.OfType<TextBox>().Single(t => System.Windows.Automation.AutomationProperties.GetName(t) == "새 메모 너비");
            width.Text = "500";
            var save = children.OfType<Button>().Single(b => Equals(b.Content, "설정 저장"));
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Wait(Until(() => save.IsEnabled));
            Assert(settings.Current.NoteWidth == 500);
            Assert(children.OfType<Button>().Single(b => Equals(b.Content, "진단 모드에서는 자동 실행 변경 불가")).IsEnabled == false);
            window.Close();
            var title = new NoteTitleWindow("시험 제목"); title.Close();
        });
    }
    internal static void Render()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "settings-title-renders"); Directory.CreateDirectory(folder);
        var settings = new SettingsWindow(new SettingsService(Root()), null);
        RenderElement((FrameworkElement)settings.Content, 416, 480, Path.Combine(folder, "settings.png")); settings.Close();
        var title = new NoteTitleWindow("이번 주 할 일");
        RenderElement((FrameworkElement)title.Content, 352, 280, Path.Combine(folder, "title.png")); title.Close();
        using var view = new NoteView(); view.SetCustomTitle("이번 주 할 일"); view.InsertPlainText("본문 첫 줄과 제목은 별개입니다.\n☐ 디자인 확인\n☐ 메모 정리");
        RenderElement(view, 320, 300, Path.Combine(folder, "note.png"));
        Console.WriteLine(folder);
    }
    private static void RenderElement(FrameworkElement element, double width, double height, string path)
    {
        element.Measure(new Size(width, height)); element.Arrange(new Rect(0, 0, width, height)); element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)(width * 1.5), (int)(height * 1.5), 144, 144, PixelFormats.Pbgra32); bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream);
    }
    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Visuals(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static async Task Until(Func<bool> condition)
    { var until = DateTime.UtcNow.AddSeconds(5); while (!condition()) { Assert(DateTime.UtcNow < until); await Task.Delay(10); } }
    private static T Wait<T>(Task<T> task) { Wait((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Wait(Task task)
    {
        var frame = new DispatcherFrame(); var dispatcher = Dispatcher.CurrentDispatcher;
        task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
        if (!task.IsCompleted) Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
    }
    private static void Throws(Action action)
    { try { action(); } catch (Exception ex) when (FileNoteRepository.IsStorageError(ex)) { return; } throw new InvalidOperationException("Expected validation/save failure."); }
    private static void Assert(bool condition) { if (!condition) throw new InvalidOperationException("Settings/title assertion failed."); }
}
