using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Threading;
using WindowsMemo.Infrastructure;
using WindowsMemo.Models;
using WindowsMemo.Services;
using WindowsMemo.Views;

namespace WindowsMemo.Tests;

// Opt-in diagnostics only: synthetic encrypted files, hidden native windows, no user data.
internal static class RecoveryLifetimeProbe
{
    private sealed record Resources(long ManagedBytes, long PrivateBytes, int Handles, uint Gdi, uint User);
    private sealed class Surface : INoteSurface
    {
        public NoteBounds CaptureBounds() => new(48, 64, 320, 300);
        public event EventHandler? BoundsChanged { add { } remove { } }
        public void Dispose() { }
    }
    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(nint process, uint flags);

    public static void Run(string[] args)
    {
        int cycles = 100, idleSeconds = 300;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || !seen.Add(args[i]) || !int.TryParse(args[i + 1], out int value))
                throw new ArgumentException("Use --cycles 1..1000 and --idle-seconds 0..86400.");
            switch (args[i])
            {
                case "--cycles" when value is >= 1 and <= 1000: cycles = value; break;
                case "--idle-seconds" when value is >= 0 and <= 86400: idleSeconds = value; break;
                default: throw new ArgumentException("Invalid recovery probe option.");
            }
        }
        var dispatcher = Dispatcher.CurrentDispatcher;
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        try
        {
            Task task = RunAsync(cycles, idleSeconds);
            var frame = new DispatcherFrame();
            task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
            if (!task.IsCompleted) Dispatcher.PushFrame(frame);
            task.GetAwaiter().GetResult();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static async Task RunAsync(int cycles, int idleSeconds)
    {
        string run = Path.Combine(AppContext.BaseDirectory, "recovery-probe", Guid.NewGuid().ToString("N"));
        string notes = Path.Combine(run, "Notes");
        using var log = new StreamWriter(CreateLog(run)) { AutoFlush = true };
        void Emit(object value) { string json = JsonSerializer.Serialize(value); log.WriteLine(json); Console.WriteLine(json); }
        var now = DateTimeOffset.UtcNow;
        var record = new NoteRecord(4, Guid.NewGuid(),
            RtfDocumentCodec.Save(new FlowDocument(new Paragraph(new Run("격리된 복구 수명 시험 메모")))),
            new(48, 64, 320, 300), now, now, Hidden: true, Paper: "Sage");
        var repository = new FileNoteRepository(notes, record.Id); repository.Save(record);
        for (int i = 0; i < 65; i++)
            File.Copy(repository.PrimaryPath, Path.Combine(notes, Guid.NewGuid().ToString("N") + ".tmp"));
        using var workspace = new NoteWorkspace(notes, (_, _) => new Surface());
        await workspace.InitializeAsync();
        string[] original = Fingerprint(notes);
        Emit(new { stage = "start", version = typeof(NoteView).Assembly.GetName().Version?.ToString(3),
            processId = Environment.ProcessId, cycles, idleSeconds, candidates = 65, run,
            windowMode = "EnsureHandle without Show; production controls and events" });
        var retired = new List<WeakReference>();
        for (int i = 0; i < 10; i++) await CycleAsync(workspace, notes, retired);
        await Task.Delay(30);
        var baseline = Sample();
        Require(retired.All(reference => !reference.IsAlive), "Warmup retained a closed recovery window/document.");
        retired.Clear(); Emit(new { stage = "baseline", resources = baseline });
        for (int cycle = 1; cycle <= cycles; cycle++)
        {
            await CycleAsync(workspace, notes, retired);
            if (cycle % 10 != 0 && cycle != cycles) continue;
            await Task.Delay(30);
            var resources = Sample(); int survivors = retired.Count(reference => reference.IsAlive);
            Emit(new { stage = "cycle", cycle, retiredSurvivors = survivors, resources });
            Require(survivors == 0, "Closed recovery window/document remains referenced.");
            // Bounded native growth guard; raw measurements remain available for trend review.
            VerifyNativeBudget(baseline, resources);
            Require(resources.ManagedBytes <= baseline.ManagedBytes + Math.Max(10 * 1024 * 1024, baseline.ManagedBytes / 5),
                "Managed heap exceeded the investigation budget.");
            retired.Clear();
        }
        Require(original.SequenceEqual(Fingerprint(notes)), "Repeated failed recovery changed fixture files.");
        await IdleAsync(workspace, notes, idleSeconds, retired, Emit);
        await Task.Delay(30);
        var final = Sample();
        VerifyNativeBudget(baseline, final);
        Require(retired.All(reference => !reference.IsAlive), "Idle window/document remains referenced after close.");
        Require(workspace.Items.Count == 2 && Directory.GetFiles(notes, "*.tmp").Length == 65,
            "Final retry failed or removed original candidates.");
        // The only additional file is the deliberately recovered copy after idle.
        string[] finalFiles = Fingerprint(notes);
        Require(finalFiles.Length == original.Length + 1 && original.All(value => finalFiles.Contains(value)),
            "Final retry changed original files or left unexpected files.");
        Emit(new { stage = "complete", cycles, warmupCycles = 10, retiredSurvivors = 0,
            originalsUnchanged = true, finalRetrySucceeded = true, resources = final });
    }

    private static string CreateLog(string run) { Directory.CreateDirectory(run); return Path.Combine(run, "metrics.jsonl"); }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task CycleAsync(NoteWorkspace workspace, string notes, List<WeakReference> retired)
    {
        var window = Open(workspace);
        string? blocked = null;
        try
        {
            await window.RefreshAsync(); var controls = Controls(window);
            Require(controls.List.Items.Count == 64 && controls.Next.IsEnabled, "First page mismatch.");
            controls.List.SelectedIndex = 0; await Until(() => controls.Recover.IsEnabled);
            retired.Add(new WeakReference(controls.Body.Document));
            blocked = BlockSelected(notes, controls.List);
            Click(controls.Recover); await Until(() => IsRetry(controls.Recover));
            Require(controls.List.SelectedItem is not null && Text(controls.Body).Contains("격리된 복구"), "Failed save lost the preview.");
            Click(controls.Recover); await Until(() => IsRetry(controls.Recover));
            Require(workspace.Items.Count == 1 && controls.Body.IsReadOnly, "Failed retry changed stored notes.");
            Click(controls.Next); await Until(() => controls.Refresh.IsEnabled);
            Require(controls.List.Items.Count == 1 && !controls.Next.IsEnabled && !controls.Recover.IsEnabled,
                "Next page did not clear preview.");
            Click(controls.Refresh); await Until(() => controls.Refresh.IsEnabled);
            Require(controls.List.Items.Count == 64 && controls.Next.IsEnabled, "Restart page mismatch.");
            retired.Add(new WeakReference(window));
        }
        finally { window.Close(); if (blocked is not null) Directory.Delete(blocked); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task IdleAsync(NoteWorkspace workspace, string notes, int seconds,
        List<WeakReference> retired, Action<object> emit)
    {
        var window = Open(workspace); string? blocked = null;
        try
        {
            await window.RefreshAsync(); var controls = Controls(window);
            controls.List.SelectedIndex = 0; await Until(() => controls.Recover.IsEnabled);
            blocked = BlockSelected(notes, controls.List);
            Click(controls.Recover); await Until(() => IsRetry(controls.Recover));
            string[] before = Fingerprint(notes);
            emit(new { stage = "idle-start", seconds, resources = Sample() });
            using var process = Process.GetCurrentProcess();
            TimeSpan cpuStart = process.TotalProcessorTime; var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(seconds))
            {
                var remaining = TimeSpan.FromSeconds(seconds) - watch.Elapsed;
                if (remaining <= TimeSpan.Zero) break;
                await Task.Delay(remaining < TimeSpan.FromSeconds(30) ? remaining : TimeSpan.FromSeconds(30));
                emit(new { stage = "idle", elapsedSeconds = watch.Elapsed.TotalSeconds, resources = Sample(collect: false) });
            }
            watch.Stop(); double cpuMs = (process.TotalProcessorTime - cpuStart).TotalMilliseconds;
            bool unchanged = before.SequenceEqual(Fingerprint(notes));
            emit(new { stage = "idle-end", elapsedSeconds = watch.Elapsed.TotalSeconds, cpuMs,
                oneCorePercent = seconds > 0 ? cpuMs / watch.Elapsed.TotalMilliseconds * 100 : 0,
                machinePercent = seconds > 0 ? cpuMs / watch.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100 : 0,
                processors = Environment.ProcessorCount, filesUnchanged = unchanged, resources = Sample() });
            Require(unchanged && IsRetry(controls.Recover) && Text(controls.Body).Contains("격리된 복구"),
                "Idle changed files or lost the retry preview.");
            retired.Add(new WeakReference(controls.Body.Document));
            Directory.Delete(blocked); blocked = null;
            Click(controls.Recover); await Until(() => !workspace.IsBusy && workspace.Items.Count == 2 && controls.Refresh.IsEnabled);
            Require(!controls.Recover.IsEnabled && controls.List.SelectedItem is null, "Successful retry did not clear selection.");
            retired.Add(new WeakReference(window));
        }
        finally { window.Close(); if (blocked is not null) Directory.Delete(blocked); }
    }

    private static PendingRecoveryWindow Open(NoteWorkspace workspace)
    {
        var window = new PendingRecoveryWindow(workspace, () => { }) { ShowActivated = false, ShowInTaskbar = false };
        try { Require(new WindowInteropHelper(window).EnsureHandle() != 0, "Native window creation failed."); return window; }
        catch { window.Close(); throw; }
    }
    private static (ListBox List, RichTextBox Body, Button Recover, Button Next, Button Refresh) Controls(Window window)
    {
        var grid = (Grid)window.Content;
        var buttons = grid.Children.OfType<WrapPanel>().Single().Children.OfType<Button>().ToArray();
        Button Find(string text) => buttons.Single(button => Equals(button.Content, text));
        return (grid.Children.OfType<ListBox>().Single(), grid.Children.OfType<RichTextBox>().Single(),
            Find("새 메모로 복구"), Find("다음 항목"), Find("처음부터 확인"));
    }
    private static string BlockSelected(string notes, ListBox list)
    {
        var file = (PendingNoteFile)list.SelectedItem;
        var preview = new PendingNoteRecovery(notes).Read(file.Token);
        string path = new FileNoteRepository(notes, PendingNoteRecovery.CopyId(preview.Token, preview.Fingerprint)).PrimaryPath;
        Directory.CreateDirectory(path); return path; // Empty directory blocks only this fixture's new file.
    }
    private static bool IsRetry(Button button) => button.IsEnabled && Equals(button.Content, "복구 다시 시도");
    private static string Text(RichTextBox body) => new TextRange(body.Document.ContentStart, body.Document.ContentEnd).Text;
    private static void Click(Button button) { Require(button.IsEnabled, "Clicked a disabled control."); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
    private static async Task Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition()) { Require(watch.Elapsed < TimeSpan.FromSeconds(15), "Recovery operation timed out."); await Task.Delay(10); }
    }
    private static string[] Fingerprint(string notes) => Directory.GetFiles(notes).OrderBy(path => path, StringComparer.Ordinal)
        .Select(path => $"{Path.GetFileName(path)}:{File.GetLastWriteTimeUtc(path).Ticks}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}").ToArray();
    private static Resources Sample(bool collect = true)
    {
        if (collect)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); // Probe only, never production mitigation.
        }
        using var process = Process.GetCurrentProcess();
        return new(GC.GetTotalMemory(false), process.PrivateMemorySize64, process.HandleCount,
            GetGuiResources(process.Handle, 0), GetGuiResources(process.Handle, 1));
    }
    private static void VerifyNativeBudget(Resources baseline, Resources current) =>
        Require(current.Handles <= baseline.Handles + 12 && current.Gdi <= baseline.Gdi + 4
            && current.User <= baseline.User + 4, "Native resources exceeded the post-warmup allowance.");
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
