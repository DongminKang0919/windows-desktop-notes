using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using WindowsMemo.Infrastructure;
using WindowsMemo.Models;
using WindowsMemo.Services;
using WindowsMemo.Views;

namespace WindowsMemo.Tests;

internal static class PreviewSchedulingTests
{
    private sealed class Surface : INoteSurface
    {
        public NoteBounds CaptureBounds() => new(48, 64, 320, 300);
        public event EventHandler? BoundsChanged { add { } remove { } }
        public void Dispose() { }
    }
    // Completion gates make concurrent reads and obsolete results deterministic, without slow disks/sleeps.
    private sealed class Reader
    {
        internal sealed record Request(Guid Token, TaskCompletionSource<PendingNotePreview> Completion);
        internal List<Request> Requests { get; } = [];
        internal int Active { get; private set; }
        internal int Peak { get; private set; }
        internal Task<PendingNotePreview> Read(Guid token)
        {
            var completion = new TaskCompletionSource<PendingNotePreview>(TaskCreationOptions.RunContinuationsAsynchronously);
            Requests.Add(new(token, completion)); Active++; Peak = Math.Max(Peak, Active);
            return FinishAsync(completion.Task);
        }
        private async Task<PendingNotePreview> FinishAsync(Task<PendingNotePreview> task)
        { try { return await task; } finally { Active--; } }
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(AppContext.BaseDirectory, "preview-scheduling-tests", Guid.NewGuid().ToString("N"));
        private readonly string[] _before;
        private readonly Dictionary<Guid, PendingNotePreview> _previews = [];
        private readonly NoteWorkspace _workspace;
        internal Reader Reader { get; } = new();
        internal PendingRecoveryWindow Window { get; }
        internal ListBox List { get; }
        internal RichTextBox Body { get; }
        internal Button Recover { get; }
        internal Button Next { get; }
        internal Button Refresh { get; }
        internal TextBlock Status { get; }
        internal Fixture(int count = 3)
        {
            var now = DateTimeOffset.UtcNow;
            var record = new NoteRecord(4, Guid.NewGuid(),
                RtfDocumentCodec.Save(new FlowDocument(new Paragraph(new Run("선택 순서 시험")))),
                new(48, 64, 320, 300), now, now, Hidden: true);
            var repository = new FileNoteRepository(_root, record.Id); repository.Save(record);
            for (int i = 0; i < count; i++)
            {
                Guid token = Guid.NewGuid(); File.Copy(repository.PrimaryPath, Path.Combine(_root, token.ToString("N") + ".tmp"));
                _previews.Add(token, new PendingNoteRecovery(_root).Read(token));
            }
            _workspace = new(_root, (_, _) => new Surface()); Wait(_workspace.InitializeAsync());
            Window = new(_workspace, () => { }, Reader.Read); Wait(Window.RefreshAsync());
            var grid = (Grid)Window.Content;
            List = grid.Children.OfType<ListBox>().Single(); Body = grid.Children.OfType<RichTextBox>().Single();
            Status = grid.Children.OfType<TextBlock>().Single(text => Grid.GetRow(text) == 2);
            var buttons = grid.Children.OfType<WrapPanel>().Single().Children.OfType<Button>().ToArray();
            Recover = buttons.Single(button => Equals(button.Content, "새 메모로 복구"));
            Next = buttons.Single(button => Equals(button.Content, "다음 항목"));
            Refresh = buttons.Single(button => Equals(button.Content, "처음부터 확인"));
            _before = Fingerprint();
        }
        internal Guid SelectedToken => ((PendingNoteFile)List.SelectedItem).Token;
        internal void Complete(int index) => Reader.Requests[index].Completion.SetResult(_previews[Reader.Requests[index].Token]);
        private string[] Fingerprint() => Directory.GetFiles(_root).OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => $"{Path.GetFileName(path)}:{File.GetLastWriteTimeUtc(path).Ticks}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}").ToArray();
        public void Dispose()
        {
            Window.Close();
            foreach (var request in Reader.Requests) request.Completion.TrySetException(new IOException("Isolated reader released."));
            Wait(Until(() => Reader.Active == 0)); Wait(Task.Delay(25));
            _workspace.Dispose(); Require(_before.SequenceEqual(Fingerprint()), "Preview scheduling changed stored files.");
        }
    }
    internal static void Run(Action<string, Action> check)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            check("복구 미리보기: 빠른 200회 선택은 읽기 1개·마지막 대기 1개로 병합", () =>
            {
                using var f = new Fixture(); f.List.SelectedIndex = 0;
                for (int i = 0; i < 200; i++) f.List.SelectedIndex = (i + 1) % 3;
                Guid last = f.SelectedToken;
                Require(f.Reader.Requests.Count == 1 && f.Reader.Active == 1 && !f.Recover.IsEnabled, "Selections started concurrent reads.");
                f.Complete(0); Wait(Until(() => f.Reader.Requests.Count == 2));
                Require(f.Reader.Requests[1].Token == last && !f.Recover.IsEnabled && Empty(f.Body), "Obsolete result was displayed or wrong selection read.");
                f.Complete(1); Wait(Until(() => f.Recover.IsEnabled));
                Require(f.Reader.Peak == 1 && f.Reader.Requests.Count == 2 && f.SelectedToken == last && !Empty(f.Body), "Latest preview did not settle.");
            });
            check("복구 미리보기: 오래된 실패 무시·현재 실패 후 같은 행 재선택", () =>
            {
                using var f = new Fixture(); f.List.SelectedIndex = 0; f.List.SelectedIndex = 1; Guid latest = f.SelectedToken;
                f.Reader.Requests[0].Completion.SetException(new InvalidDataException("Obsolete failure."));
                Wait(Until(() => f.Reader.Requests.Count == 2));
                Require(f.SelectedToken == latest && !f.Recover.IsEnabled && Empty(f.Body), "Obsolete failure cleared current selection.");
                f.Reader.Requests[1].Completion.SetException(new IOException("Current failure."));
                Wait(Until(() => f.List.SelectedItem is null));
                Require(!f.Recover.IsEnabled && f.Status.Text.StartsWith("임시 파일을 읽지 못했습니다."), "Current failure was not reported.");
                f.List.SelectedIndex = 1; Wait(Until(() => f.Reader.Requests.Count == 3)); f.Complete(2);
                Wait(Until(() => f.Recover.IsEnabled)); Require(f.Reader.Peak == 1 && f.SelectedToken == latest, "Reselection failed.");
            });
            check("복구 미리보기: 다음 페이지·재조회는 대기 선택과 이전 결과 무효화", () =>
            {
                using var f = new Fixture(65); f.List.SelectedIndex = 0; f.List.SelectedIndex = 1;
                Click(f.Next); Wait(Until(() => f.Refresh.IsEnabled));
                Require(f.List.Items.Count == 1, "Page did not advance.");
                f.Complete(0); Wait(Until(() => f.Reader.Active == 0)); Wait(Task.Delay(25));
                Require(f.Reader.Requests.Count == 1 && Empty(f.Body) && !f.Recover.IsEnabled, "Page change retained queued read.");
                f.List.SelectedIndex = 0; Wait(Until(() => f.Reader.Requests.Count == 2));
                Click(f.Refresh); Wait(Until(() => f.Refresh.IsEnabled));
                f.Complete(1); Wait(Until(() => f.Reader.Active == 0)); Wait(Task.Delay(25));
                Require(f.List.Items.Count == 64 && f.List.SelectedItem is null && Empty(f.Body) && !f.Recover.IsEnabled, "Refresh restored obsolete preview.");
                f.List.SelectedIndex = 0; Wait(Until(() => f.Reader.Requests.Count == 3)); f.Complete(2);
                Wait(Until(() => f.Recover.IsEnabled)); Require(f.Reader.Peak == 1, "Page reads overlapped.");
            });
            check("복구 미리보기: 읽는 중 창 닫기는 대기 작업 폐기·완료 후 창 참조 회수", () =>
            {
                WeakReference reference = CloseDuringRead();
                Wait(Task.Delay(25)); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                Require(!reference.IsAlive, "Completed preview retained the closed window.");
            });
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CloseDuringRead()
    {
        using var f = new Fixture(); f.List.SelectedIndex = 0; f.List.SelectedIndex = 1;
        var reference = new WeakReference(f.Window); f.Window.Close(); f.Complete(0);
        Wait(Until(() => f.Reader.Active == 0)); Wait(Task.Delay(25));
        Require(f.Reader.Requests.Count == 1 && Empty(f.Body) && !f.Recover.IsEnabled, "Closed window started queued read or restored content.");
        return reference;
    }
    private static bool Empty(RichTextBox body) => string.IsNullOrWhiteSpace(new TextRange(body.Document.ContentStart, body.Document.ContentEnd).Text);
    private static void Click(Button button) { Require(button.IsEnabled, "Disabled button."); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
    private static async Task Until(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(10);
        while (!condition()) { Require(DateTime.UtcNow < timeout, "Preview scheduling timed out."); await Task.Delay(10); }
    }
    private static void Wait(Task task)
    {
        var dispatcher = Dispatcher.CurrentDispatcher; var frame = new DispatcherFrame();
        task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
