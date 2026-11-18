using System.Text.Json;

namespace WindowsMemo.Monitor;

internal static class MonitorChecks
{
    internal static void Run()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "self-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        // Linear growth with an exaggerated warmup spike and two distinct starts sharing a PID.
        var samples = Enumerable.Range(0, 71).Select(i => new Sample(start.AddSeconds(i * 10), i * 10, 123, start,
            i < 35 ? "active" : "idle", 80 * 1048576, (i < 30 ? 1000 : 100 + i) * 1048576L,
            100 + i, 10, 8, 9, i == 0 ? null : i < 35 ? 2 : 0.5, i == 0 ? null : 10, true)).ToArray();
        var summary = Report.Summarize(samples);
        Assert(summary.WarmupExcluded && Math.Abs(summary.PrivateTrendMiBPerHour!.Value - 360) < 0.001, "warmup excluded slope");
        Assert(summary.IdleCpuMeanPercent == 0.5 && summary.PrivatePeakMiB == 1000, "phase CPU and peak");
        var shortSummary = Report.Summarize(samples.Take(3).ToArray());
        Assert(shortSummary.PrivateTrendMiBPerHour is null && !shortSummary.WarmupExcluded, "short run has no invented trend");
        File.WriteAllLines(Path.Combine(folder, "samples.jsonl"), samples.Select(s => JsonSerializer.Serialize(s, Program.Json))
            .Concat([JsonSerializer.Serialize(samples[0] with { ProcessStartUtc = start.AddHours(1), ElapsedSeconds = 3600 }, Program.Json), "{incomplete"]));
        File.WriteAllText(Path.Combine(folder, "events.jsonl"), "<script>untrusted marker</script>");
        Report.Write(folder);
        var parsed = Report.Read(folder, out int incomplete);
        Assert(parsed.Count == 72 && incomplete == 1, "partial record recovery");
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "summary.json")));
        Assert(json.RootElement.GetProperty("processes").GetArrayLength() == 2, "PID reuse stays separate");
        string html = File.ReadAllText(Path.Combine(folder, "report.html"));
        Assert(!html.Contains("<script>") && html.Contains("&lt;script&gt;"), "HTML escaping");
        Assert(Program.ToCsv(samples[0]).Split(',').Length == 15, "CSV schema");
        Assert(TrackedProcess.CpuPercent(12, 10, 1, 8, false) == 25, "system-normalized CPU");
        Assert(TrackedProcess.CpuPercent(12, 10, 60, 8, true) is null
            && TrackedProcess.CpuPercent(12, null, 1, 8, false) is null, "sleep gaps and fresh process baselines");
        bool rejected = false;
        try { Settings.Parse(["--exe", Environment.ProcessPath!, "--interval-seconds", "NaN"]); }
        catch (ArgumentException) { rejected = true; }
        Assert(rejected, "non-finite interval rejected");
        Console.WriteLine("PASS monitor: warmup/trend, phase CPU, CPU normalization/gaps, short runs, partial JSONL, PID reuse, HTML escaping, CSV, argument bounds.");
        Console.WriteLine("Synthetic report: " + Path.Combine(folder, "report.html"));
    }
    private static void Assert(bool condition, string label)
    { if (!condition) throw new InvalidOperationException("Monitor check failed: " + label); }
}
