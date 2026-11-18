using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WindowsMemo.Infrastructure;
using WindowsMemo.Interop;
using WindowsMemo.Models;
using WindowsMemo.Services;
using WindowsMemo.Views;

namespace WindowsMemo.Tests;

// Explicit, isolated probe. Never uses FileNoteRepository.DefaultRoot or visible windows.
internal static class WorkspaceProbe
{
    private sealed record Resources(long ManagedBytes, long PrivateBytes, int Handles, uint Gdi, uint User);
    private sealed class Surface : INoteSurface
    {
        private readonly DesktopNoteHost _host;
        public NoteView? View { get; private set; }
        public bool Disposed { get; private set; }
        public Surface(NoteView view, nint parent, NoteBounds? bounds)
        {
            View = view;
            _host = new DesktopNoteHost(view, () => parent, watchChanges: true);
            try { _host.Open(visible: false, restoredBounds: bounds); }
            catch { _host.Dispose(); throw; }
        }
        public NoteBounds CaptureBounds() => _host.CaptureBounds();
        public event EventHandler? BoundsChanged { add => _host.BoundsChanged += value; remove => _host.BoundsChanged -= value; }
        public void Dispose() { if (Disposed) return; Disposed = true; _host.Dispose(); View = null; }
    }
    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(nint process, uint flags);

    public static void Run(string[] args)
    {
        int cycles = 100, idleSeconds = 300;
        var seen = new HashSet<string>();
        for (int i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || !seen.Add(args[i]) || !int.TryParse(args[i + 1], out int value))
                throw new ArgumentException("Use --cycles 1..1000 and --idle-seconds 0..86400.");
            switch (args[i])
            {
                case "--cycles" when value is >= 1 and <= 1000: cycles = value; break;
                case "--idle-seconds" when value is >= 0 and <= 86400: idleSeconds = value; break;
                default: throw new ArgumentException("Invalid workspace probe option.");
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
        string run = Path.Combine(AppContext.BaseDirectory, "workspace-probe", Guid.NewGuid().ToString("N"));
        string notes = Path.Combine(run, "Notes"); Directory.CreateDirectory(notes);
        using var log = new StreamWriter(Path.Combine(run, "metrics.jsonl")) { AutoFlush = true };
        void Emit(object value) { string json = JsonSerializer.Serialize(value); log.WriteLine(json); Console.WriteLine(json); }
        using var parent = new HwndSource(new HwndSourceParameters("Isolated saved workspace probe", 1600, 900)
            { WindowStyle = unchecked((int)0x80000000) });
        var surfaces = new List<Surface>();
        var retired = new List<WeakReference>();
        NoteWorkspace CreateWorkspace() => new(notes, (view, bounds) =>
        {
            var surface = new Surface(view, parent.Handle, bounds); surfaces.Add(surface); return surface;
        });
        Emit(new { stage = "start", version = typeof(NoteView).Assembly.GetName().Version?.ToString(3),
            processId = Environment.ProcessId, cycles, idleSeconds, noteCount = 10, run });
        using (var workspace = CreateWorkspace())
        {
            await workspace.InitializeAsync();
            for (int i = 1; i < 10; i++) workspace.Create();
            Guid[] ids = workspace.Items.Select(item => item.Id).ToArray();
            NoteBounds[] originalBounds = surfaces.Select(surface => surface.CaptureBounds()).ToArray();
            for (int warmup = -5; warmup < 0; warmup++) await CycleAsync(workspace, surfaces, retired, ids, notes, originalBounds, warmup);
            Emit(new { stage = "baseline", resources = Sample(), retiredSurvivors = CountSurvivors(retired) });
            retired.Clear();
            var watch = Stopwatch.StartNew();
            for (int cycle = 1; cycle <= cycles; cycle++)
            {
                await CycleAsync(workspace, surfaces, retired, ids, notes, originalBounds, cycle);
                if (cycle % 10 == 0 || cycle == cycles)
                {
                    var resources = Sample(); int survivors = CountSurvivors(retired);
                    Emit(new { stage = "cycle", cycle, elapsedMs = watch.ElapsedMilliseconds,
                        activeSurfaces = surfaces.Count, retiredSurvivors = survivors, resources });
                    Require(survivors == 0, "Disposed editors remain referenced."); retired.Clear();
                }
            }
            Require(await workspace.FlushAllAsync(), "Pre-idle save failed.");
            var beforeFiles = Fingerprint(notes);
            Emit(new { stage = "idle-start", seconds = idleSeconds, resources = Sample() });
            using var process = Process.GetCurrentProcess();
            TimeSpan cpuStart = process.TotalProcessorTime;
            var idle = Stopwatch.StartNew();
            while (true)
            {
                TimeSpan remaining = TimeSpan.FromSeconds(idleSeconds) - idle.Elapsed;
                if (remaining <= TimeSpan.Zero) break;
                await Task.Delay(remaining < TimeSpan.FromSeconds(30) ? remaining : TimeSpan.FromSeconds(30));
                Emit(new { stage = "idle", elapsedSeconds = Math.Round(idle.Elapsed.TotalSeconds, 1), resources = Sample(collect: false) });
            }
            idle.Stop();
            double cpuMs = (process.TotalProcessorTime - cpuStart).TotalMilliseconds;
            bool unchanged = beforeFiles.SequenceEqual(Fingerprint(notes));
            Emit(new { stage = "idle-end", elapsedSeconds = idle.Elapsed.TotalSeconds, cpuMs,
                oneCorePercent = idle.ElapsedMilliseconds > 0 ? cpuMs / idle.Elapsed.TotalMilliseconds * 100 : 0,
                machinePercent = idle.ElapsedMilliseconds > 0 ? cpuMs / idle.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100 : 0,
                processors = Environment.ProcessorCount, filesUnchanged = unchanged, resources = Sample() });
            Require(unchanged, "Saved note files changed while idle.");
            VerifySaved(notes, ids, cycles);
            VerifyViews(surfaces, notes, ids, cycles);
        }
        surfaces.RemoveAll(surface => surface.Disposed);
        Require(surfaces.Count == 0, "Workspace disposal left surfaces alive.");
        Emit(new { stage = "disposed", resources = Sample() });
        using (var restarted = CreateWorkspace())
        {
            await restarted.InitializeAsync();
            Require(restarted.Items.Count == 10 && surfaces.Count == 10, "Restart did not restore ten notes.");
            VerifySaved(notes, restarted.Items.Select(item => item.Id).ToArray(), cycles);
            VerifyViews(surfaces, notes, restarted.Items.Select(item => item.Id).ToArray(), cycles);
            Require(await restarted.FlushAllAsync(), "Restart flush failed.");
        }
        surfaces.RemoveAll(surface => surface.Disposed);
        Emit(new { stage = "complete", cycles, noteFiles = Directory.GetFiles(notes, "*.json").Length,
            backupFiles = Directory.GetFiles(notes, "*.json.bak").Length, temporaryFiles = Directory.GetFiles(notes, "*.tmp").Length,
            activeSurfaces = surfaces.Count, resources = Sample() });
        Require(surfaces.Count == 0 && Directory.GetFiles(notes, "*.tmp").Length == 0, "Probe cleanup failed.");
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Edit(List<Surface> surfaces, int cycle)
    {
        foreach (var surface in surfaces)
        {
            var view = surface.View!;
            view.TextEditor.SelectAll();
            Require(view.InsertPlainText($"저장 반복 시험 {cycle}\n☐ 상태와 서식을 유지합니다."), "Input failed.");
            view.TextEditor.SelectAll(); view.ApplyColor(Brushes.RoyalBlue);
            view.TextEditor.Selection.ApplyPropertyValue(TextElement.FontWeightProperty, FontWeights.Bold);
            view.SetPaper(cycle % 2 == 0 ? "Sage" : "Peach"); view.SetCollapsed(cycle % 2 == 0);
        }
    }
    private static void Track(List<Surface> surfaces, List<WeakReference> retired)
    { foreach (var surface in surfaces) retired.Add(new WeakReference(surface.View!)); }
    private static async Task CycleAsync(NoteWorkspace workspace, List<Surface> surfaces, List<WeakReference> retired,
        Guid[] ids, string notes, NoteBounds[] originalBounds, int cycle)
    {
        Require(surfaces.Count == 10, "Expected ten active surfaces.");
        Edit(surfaces, cycle); Require(await workspace.FlushAllAsync(), "Save failed.");
        Track(surfaces, retired);
        foreach (Guid id in ids) Require(await workspace.HideAsync(id), "Hide failed.");
        surfaces.RemoveAll(surface => surface.Disposed);
        Require(surfaces.Count == 0 && workspace.Items.All(item => item.Hidden), "Hidden notes retained surfaces.");
        foreach (Guid id in ids) Require(await workspace.ShowAsync(id), "Show failed.");
        Track(surfaces, retired);
        foreach (Guid id in ids) Require(await workspace.ArchiveAsync(id), "Archive failed.");
        surfaces.RemoveAll(surface => surface.Disposed);
        Require(surfaces.Count == 0 && workspace.ArchiveItems.Count == 10 && workspace.Items.Count == 0, "Archive state mismatch.");
        foreach (Guid id in ids) Require(await workspace.UnarchiveAsync(id), "Unarchive failed.");
        foreach (Guid id in ids) Require(await workspace.ShowAsync(id), "Restore failed.");
        Require(workspace.Items.Count == 10 && workspace.ArchiveItems.Count == 0 && workspace.TrashItems.Count == 0, "List state mismatch.");
        VerifySaved(notes, ids, cycle);
        for (int i = 0; i < ids.Length; i++)
        {
            var stored = new FileNoteRepository(notes, ids[i]).Load(record => RtfDocumentCodec.Load(record.Rtf), out _)!;
            Require(stored.Bounds == originalBounds[i], "Saved position or expanded size changed.");
        }
        VerifyViews(surfaces, notes, ids, cycle);
    }
    private static void VerifyViews(List<Surface> surfaces, string notes, Guid[] ids, int cycle)
    {
        foreach (var surface in surfaces)
        {
            var view = surface.View!;
            Require(view.Paper == (cycle % 2 == 0 ? "Sage" : "Peach") && view.IsCollapsed == (cycle % 2 == 0), "Restored editor state mismatch.");
            var range = new TextRange(view.TextEditor.Document.ContentStart, view.TextEditor.Document.ContentEnd.GetPositionAtOffset(-1)!);
            VerifyBody(range, cycle);
        }
        var saved = ids.Select(id => new FileNoteRepository(notes, id).Load(record => RtfDocumentCodec.Load(record.Rtf), out _)!.Bounds)
            .OrderBy(bounds => bounds.X).ThenBy(bounds => bounds.Y).ToArray();
        var shown = surfaces.Select(surface => surface.CaptureBounds()).OrderBy(bounds => bounds.X).ThenBy(bounds => bounds.Y).ToArray();
        Require(saved.SequenceEqual(shown), "Restored window geometry mismatch.");
    }
    private static void VerifySaved(string notes, Guid[] ids, int cycle)
    {
        foreach (Guid id in ids)
        {
            var record = new FileNoteRepository(notes, id).Load(value => RtfDocumentCodec.Load(value.Rtf), out bool recovered);
            Require(record is not null && !recovered && !record.Hidden && !record.Archived && record.DeletedAtUtc is null
                && record.Paper == (cycle % 2 == 0 ? "Sage" : "Peach") && record.Collapsed == (cycle % 2 == 0), "Saved state mismatch.");
            var document = RtfDocumentCodec.Load(record!.Rtf);
            var range = new TextRange(document.ContentStart, document.ContentEnd.GetPositionAtOffset(-1)!);
            VerifyBody(range, cycle);
        }
    }
    private static void VerifyBody(TextRange range, int cycle)
    {
        Require(range.Text.Trim() == $"저장 반복 시험 {cycle}\n☐ 상태와 서식을 유지합니다.".Replace("\n", "\r\n"), "Body mismatch.");
        Require(range.GetPropertyValue(TextElement.ForegroundProperty) is SolidColorBrush brush && brush.Color == Colors.RoyalBlue
            && range.GetPropertyValue(TextElement.FontWeightProperty).Equals(FontWeights.Bold), "Formatting mismatch.");
    }
    private static string[] Fingerprint(string notes) => Directory.GetFiles(notes).OrderBy(path => path, StringComparer.Ordinal)
        .Select(path => $"{Path.GetFileName(path)}:{File.GetLastWriteTimeUtc(path).Ticks}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}").ToArray();
    private static int CountSurvivors(List<WeakReference> retired) => retired.Count(reference => reference.IsAlive);
    private static Resources Sample(bool collect = true)
    {
        if (collect)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        }
        using var process = Process.GetCurrentProcess();
        return new(GC.GetTotalMemory(false), process.PrivateMemorySize64, process.HandleCount,
            GetGuiResources(process.Handle, 0), GetGuiResources(process.Handle, 1));
    }
    private static void Require(bool condition, string error) { if (!condition) throw new InvalidOperationException(error); }
}
