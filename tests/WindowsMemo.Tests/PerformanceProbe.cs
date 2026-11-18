using System.Diagnostics;
using System.Text.Json;
using System.Windows.Documents;
using WindowsMemo.Views;

namespace WindowsMemo.Tests;

internal static class PerformanceProbe
{
    // Synthetic content only. Optional probe, not a timing assertion in the test suite.
    public static void Run()
    {
        using var view = new NoteView();
        view.InsertPlainText("입력 비용 측정\n" + new string('가', 80_000));
        void Edit()
        {
            view.TextEditor.CaretPosition = view.TextEditor.Document.ContentEnd;
            if (!view.InsertPlainText("나")) throw new InvalidOperationException("Probe input rejected.");
        }
        for (int i = 0; i < 20; i++) Edit();
        var edits = Measure(Edit, 200);
        var titles = Measure(view.RefreshTitle, 200);
        var palette = Measure(() => { _ = NoteTheme.Paper("Butter"); _ = NoteTheme.Font; }, 10_000);
        string output = JsonSerializer.Serialize(new
        {
            version = typeof(NoteView).Assembly.GetName().Version?.ToString(3),
            inputCharacters = 80_000,
            edit = edits, title = titles, theme = palette
        }, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(output);
    }
    private static object Measure(Action action, int iterations)
    {
        action(); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++) action();
        watch.Stop();
        return new { iterations, elapsedMs = Math.Round(watch.Elapsed.TotalMilliseconds, 2), allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before };
    }
}
