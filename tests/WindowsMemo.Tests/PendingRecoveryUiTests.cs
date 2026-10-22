using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using WindowsMemo.Infrastructure;
using WindowsMemo.Models;
using WindowsMemo.Services;
using WindowsMemo.Views;

namespace WindowsMemo.Tests;

internal static class PendingRecoveryUiTests
{
    private sealed class Surface : INoteSurface
    {
        public NoteBounds CaptureBounds() => new(48, 64, 320, 300);
        public event EventHandler? BoundsChanged { add { } remove { } }
        public void Dispose() { }
    }
    internal static void Run(Action<string, Action> check)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            check("미완료 복구 UI: 선택·읽기 전용 미리보기·복구 버튼·오류 시 비활성", () =>
            {
                string root = Path.Combine(AppContext.BaseDirectory, "pending-ui-tests", Guid.NewGuid().ToString("N"));
                using var workspace = new NoteWorkspace(root, (_, _) => new Surface());
                Wait(workspace.InitializeAsync()); Require(Wait(workspace.FlushAllAsync()));
                var now = DateTimeOffset.UtcNow;
                var sample = new FlowDocument { FontFamily = NoteTheme.Font, FontSize = NoteTheme.BodySize };
                sample.Blocks.Add(new Paragraph(new Run("산책하며 떠오른 생각")));
                sample.Blocks.Add(new Paragraph(new Run("☐ 주말 산책길 찾아보기")));
                sample.Blocks.Add(new Paragraph(new Run("☑ 읽고 싶은 책 적어두기")));
                var record = new NoteRecord(4, Guid.NewGuid(), RtfDocumentCodec.Save(sample),
                    new(48, 64, 320, 300), now.AddDays(-1), now, Paper: "Sage");
                var seed = new FileNoteRepository(Path.Combine(root, "seed"), record.Id); seed.Save(record);
                Guid token = Guid.NewGuid(); string path = Path.Combine(root, token.ToString("N") + ".tmp"); File.Copy(seed.PrimaryPath, path);
                Guid badToken = Guid.NewGuid(); File.WriteAllText(Path.Combine(root, badToken.ToString("N") + ".tmp"), "broken");
                var window = new PendingRecoveryWindow(workspace, () => { });
                try
                {
                    Wait(window.RefreshAsync());
                    var layout = (Grid)window.Content;
                    var list = layout.Children.OfType<ListBox>().Single();
                    var preview = layout.Children.OfType<RichTextBox>().Single();
                    var buttons = layout.Children.OfType<WrapPanel>().Single().Children.OfType<Button>();
                    var recover = buttons.Single(button => Equals(button.Content, "새 메모로 복구"));
                    Require(preview.IsReadOnly && !preview.AllowDrop && !preview.IsDocumentEnabled && !recover.IsEnabled);
                    list.SelectedItem = list.Items.OfType<PendingNoteFile>().Single(file => file.Token == token);
                    Wait(Until(() => recover.IsEnabled));
                    Require(new TextRange(preview.Document.ContentStart, preview.Document.ContentEnd).Text.Contains("산책하며"));
                    layout.Measure(new Size(576, 536)); layout.Arrange(new Rect(0, 0, 576, 536)); layout.UpdateLayout();
                    string images = Path.Combine(AppContext.BaseDirectory, "ui-previews"); Directory.CreateDirectory(images);
                    NoteDesignTests.Save(layout, 576, 536, 1.5, Path.Combine(images, "pending-recovery-150.png"));
                    recover.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Wait(Until(() => !workspace.IsBusy && workspace.Items.Count == 2));
                    Require(workspace.Items.Count(item => item.Hidden) == 1 && File.Exists(path));
                    list.SelectedItem = list.Items.OfType<PendingNoteFile>().Single(file => file.Token == badToken);
                    Wait(Until(() => layout.Children.OfType<TextBlock>().Any(text => text.Text.StartsWith("내용을 읽을 수 없습니다."))));
                    Require(!recover.IsEnabled && new TextRange(preview.Document.ContentStart, preview.Document.ContentEnd).Text.Trim().Length == 0);
                }
                finally { window.Close(); }
            });
            check("미완료 복구 UI: 반복 열기·닫기 후 창 참조 회수", () =>
            {
                string root = Path.Combine(AppContext.BaseDirectory, "pending-ui-tests", Guid.NewGuid().ToString("N"));
                using var workspace = new NoteWorkspace(root, (_, _) => new Surface());
                Wait(workspace.InitializeAsync());
                var references = Enumerable.Range(0, 20).Select(_ => ClosedWindow(workspace)).ToArray();
                Wait(Task.Delay(25));
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                Require(references.All(reference => !reference.IsAlive));
            });
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private static async Task Until(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeout) throw new TimeoutException("Recovery UI did not finish.");
            await Task.Delay(10);
        }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ClosedWindow(NoteWorkspace workspace)
    {
        var window = new PendingRecoveryWindow(workspace, () => { });
        Wait(window.RefreshAsync()); window.Close();
        return new WeakReference(window);
    }
    private static void Wait(Task task)
    {
        var frame = new DispatcherFrame();
        var dispatcher = Dispatcher.CurrentDispatcher;
        task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }
    private static bool Wait(Task<bool> task) { Wait((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Require(bool condition) { if (!condition) throw new InvalidOperationException("Pending recovery UI assertion failed."); }
}
