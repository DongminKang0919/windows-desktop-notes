using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Interop;
using System.Windows.Threading;
using WindowsMemo.Infrastructure;
using WindowsMemo.Interop;
using WindowsMemo.Models;
using WindowsMemo.Services;
using WindowsMemo.Views;

namespace WindowsMemo.Tests;

internal static class StabilityProbe
{
    private sealed class Surface : INoteSurface
    {
        private readonly DesktopNoteHost _host;
        public Surface(NoteView view, nint parent)
        {
            _host = new DesktopNoteHost(view, () => parent, watchChanges: true);
            _host.Open(visible: false);
        }
        public NoteBounds CaptureBounds() => _host.CaptureBounds();
        public event EventHandler? BoundsChanged { add => _host.BoundsChanged += value; remove => _host.BoundsChanged -= value; }
        public void Dispose() => _host.Dispose();
    }
    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(nint process, uint flag);

    public static void Run()
    {
        using var parent = new HwndSource(new HwndSourceParameters("Isolated stability probe", 1600, 900)
            { WindowStyle = unchecked((int)0x80000000) });
        Console.WriteLine("Warming up 20 cycles of 10 hidden note sessions...");
        RunBatch(parent.Handle, 20);
        var baseline = Sample();
        Console.WriteLine("Baseline: " + JsonSerializer.Serialize(baseline));
        for (int batch = 1; batch <= 2; batch++)
        {
            var watch = Stopwatch.StartNew();
            int survivors = RunBatch(parent.Handle, 100);
            var sample = Sample();
            Console.WriteLine(JsonSerializer.Serialize(new { batch, cycles = 100, notesPerCycle = 10,
                survivors, elapsedMs = watch.ElapsedMilliseconds, resources = sample }));
            if (survivors != 0) throw new InvalidOperationException("Disposed note sessions remain referenced.");
        }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] Cycle(nint parent)
    {
        var sessions = new List<NoteSession>();
        var references = new List<WeakReference>();
        try
        {
            for (int i = 0; i < 10; i++)
            {
                var repository = new FileNoteRepository(Path.Combine(AppContext.BaseDirectory, "stability-probe"), Guid.NewGuid());
                var session = new NoteSession(repository, null, (view, _) => new Surface(view, parent));
                sessions.Add(session);
                session.View.InsertPlainText("가짜 메모 — 자원 수명 시험");
                session.View.SetPaper(i % 2 == 0 ? "Sage" : "Peach");
                session.View.SetCollapsed(true); session.View.SetCollapsed(false);
                references.Add(new WeakReference(session)); references.Add(new WeakReference(session.View));
            }
        }
        finally { foreach (var session in sessions) session.Dispose(); }
        // Deliberately discard synthetic unsaved data; no user repository is accessed.
        return references.ToArray();
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunBatch(nint parent, int cycles)
    {
        var references = new List<WeakReference>();
        for (int cycle = 0; cycle < cycles; cycle++)
        {
            references.AddRange(Cycle(parent));
            if ((cycle + 1) % 20 == 0) Console.WriteLine($"Completed {cycle + 1}/{cycles} cycles");
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        }
        Collect();
        return references.Count(reference => reference.IsAlive);
    }
    private static void Collect()
    {
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    }
    private static object Sample()
    {
        Collect(); using var process = Process.GetCurrentProcess();
        return new { managedBytes = GC.GetTotalMemory(false), privateBytes = process.PrivateMemorySize64,
            handles = process.HandleCount, gdi = GetGuiResources(process.Handle, 0), user = GetGuiResources(process.Handle, 1) };
    }
}
