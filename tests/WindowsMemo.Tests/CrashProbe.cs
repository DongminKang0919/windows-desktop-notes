using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using WindowsMemo.Infrastructure;
using WindowsMemo.Models;
using WindowsMemo.Services;
using WindowsMemo.Views;

namespace WindowsMemo.Tests;

// Explicit probe: terminates only child processes created here, using synthetic repositories.
internal static class CrashProbe
{
    private sealed record Case(string Name, string Seed, string Stop, int Version, bool Recovered, int Temps, int Damaged);
    private static readonly Case[] Cases =
    [
        new("first-before-commit", "first", "TemporaryFlushed", -1, false, 1, 0),
        new("first-after-commit", "first", "Committed", 1, false, 0, 0),
        new("replace-before-commit", "replace", "TemporaryFlushed", 0, false, 1, 0),
        new("replace-after-commit", "replace", "Committed", 1, false, 0, 0),
        new("recover-before-preserve", "recover", "TemporaryFlushed", 0, true, 1, 0),
        new("recover-after-preserve", "recover", "DamagedPreserved", 0, true, 1, 1),
        new("recover-after-commit", "recover", "Committed", 1, false, 0, 1),
        new("autosave-acknowledged", "replace", "Acknowledged", 1, false, 0, 0)
    ];
    private static NoteBounds Bounds(int version) => version == 0 ? new(48, 64, 320, 300) : new(80, 90, 360, 340, 1.5);
    private static void CreateProbeDirectory(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Linked probe output paths are not allowed.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        Directory.CreateDirectory(path);
    }
    private static string Body(int version) => $"☑ crash-probe revision {version}\n저장된 한글과 서식";
    private static string Folder(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("A probe run GUID is required.");
        return Path.Combine(AppContext.BaseDirectory, "crash-probe", id, "Notes");
    }
    private static NoteView View(int version)
    {
        var view = new NoteView(); view.InsertPlainText(Body(version)); view.TextEditor.SelectAll();
        view.TextEditor.Selection.ApplyPropertyValue(TextElement.FontWeightProperty, FontWeights.Bold);
        view.ApplyColor(Brushes.RoyalBlue); view.SetPaper(version == 0 ? "Sage" : "Peach"); view.SetCollapsed(version == 1);
        return view;
    }
    private static NoteRecord Record(int version)
    {
        using var view = View(version);
        return new(4, FileNoteRepository.NoteId, RtfDocumentCodec.Save(view.TextEditor.Document), Bounds(version),
            new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), DateTimeOffset.UtcNow, Paper: view.Paper, Collapsed: view.IsCollapsed);
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Validate(NoteRecord record) => RtfDocumentCodec.Load(record.Rtf);
    private static void VerifyRecord(NoteRecord record, int version)
    {
        Require(record.Id == FileNoteRepository.NoteId && record.SchemaVersion == 4 && record.Bounds == Bounds(version)
            && record.Paper == (version == 0 ? "Sage" : "Peach") && record.Collapsed == (version == 1)
            && !record.Hidden && !record.Archived && record.DeletedAtUtc is null, "Restored metadata mismatch.");
        var doc = RtfDocumentCodec.Load(record.Rtf);
        var range = new TextRange(doc.ContentStart, doc.ContentEnd.GetPositionAtOffset(-1)!);
        Require(range.Text.Trim() == Body(version).Replace("\n", "\r\n"), "Restored body mismatch.");
        Require(range.GetPropertyValue(TextElement.FontWeightProperty).Equals(FontWeights.Bold)
            && range.GetPropertyValue(TextElement.ForegroundProperty) is SolidColorBrush brush && brush.Color == Colors.RoyalBlue,
            "Restored formatting mismatch.");
    }
    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame(); var dispatcher = Dispatcher.CurrentDispatcher;
        task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }
    public static void Run()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try { Pump(RunAsync()); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private static Process StartChild(params string[] args)
    {
        string host = Environment.ProcessPath ?? throw new InvalidOperationException("Missing process host.");
        var start = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(CrashProbe).Assembly.Location);
        start.ArgumentList.Add("--crash-probe-child");
        foreach (string arg in args) start.ArgumentList.Add(arg);
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start isolated probe child.");
    }
    private static async Task StopOwnedChild(Process child)
    {
        // Never enumerate processes, use names, or accept external PIDs.
        if (!child.HasExited) child.Kill();
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }
    private static async Task VerifyChild(string id, int version, bool recovered)
    {
        using var reader = StartChild(id, "verify", version.ToString(System.Globalization.CultureInfo.InvariantCulture), recovered.ToString());
        Task<string> output = reader.StandardOutput.ReadToEndAsync(), errors = reader.StandardError.ReadToEndAsync();
        try
        {
            await reader.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Require(reader.ExitCode == 0 && (await output).Trim() == "VERIFIED", "Fresh reader failed: " + await errors);
        }
        finally { await StopOwnedChild(reader); }
    }
    private static async Task RunAsync()
    {
        string report = Path.Combine(AppContext.BaseDirectory, "crash-probe", Guid.NewGuid().ToString("N"));
        CreateProbeDirectory(report);
        using var log = new StreamWriter(Path.Combine(report, "results.jsonl")) { AutoFlush = true };
        void Emit(object value) { string json = JsonSerializer.Serialize(value); Console.WriteLine(json); log.WriteLine(json); }
        Emit(new { stage = "start", version = typeof(NoteView).Assembly.GetName().Version?.ToString(3), report, cases = Cases.Length });
        string? duplicateRun = null;
        foreach (var test in Cases)
        {
            string id = Guid.NewGuid().ToString("N"), folder = Folder(id);
            CreateProbeDirectory(folder); var repo = new FileNoteRepository(folder);
            byte[]? baseline = null;
            if (test.Seed != "first")
            {
                repo.Save(Record(0)); baseline = File.ReadAllBytes(repo.PrimaryPath);
                if (test.Seed == "recover")
                {
                    repo.Save(Record(1)); File.WriteAllText(repo.PrimaryPath, "damaged-probe-fixture");
                }
            }
            int writerId;
            using (var child = StartChild(id, "write", test.Stop))
            {
                writerId = child.Id; Task<string> errors = child.StandardError.ReadToEndAsync();
                try
                {
                    string? signal = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
                    if (signal != "CHECKPOINT " + test.Stop)
                        throw new InvalidOperationException("Writer did not reach checkpoint: " + await errors.WaitAsync(TimeSpan.FromSeconds(5)));
                }
                finally { await StopOwnedChild(child); }
            }
            await VerifyChild(id, test.Version, test.Recovered);
            Require(Directory.GetFiles(folder, "*.tmp").Length == test.Temps, "Unexpected temporary file count.");
            string[] damaged = Directory.GetFiles(folder, "*.damaged-*");
            Require(damaged.Length == test.Damaged && damaged.All(path => File.ReadAllText(path) == "damaged-probe-fixture"), "Damaged primary not preserved.");
            bool expectedBackup = test.Seed == "recover" || (test.Seed == "replace" && test.Stop is "Committed" or "Acknowledged");
            Require(File.Exists(repo.BackupPath) == expectedBackup, "Unexpected backup presence.");
            if (expectedBackup) Require(baseline is not null && File.ReadAllBytes(repo.BackupPath).SequenceEqual(baseline), "Last good backup changed.");
            foreach (string path in Directory.GetFiles(folder, "*.tmp")) File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-2));
            var cleanup = TemporaryNoteMaintenance.Run(folder, DateTimeOffset.UtcNow, Validate);
            Require(cleanup.Removed == 0 && Directory.GetFiles(folder, "*.tmp").Length == test.Temps, "Unique interrupted snapshot was deleted.");
            if (test.Name == "replace-before-commit") duplicateRun = id;
            Emit(new { stage = "pass", test.Name, test.Stop, writerId, expectedVersion = test.Version, test.Recovered, test.Temps, test.Damaged, folder });
        }
        // Once that same interrupted snapshot is durably saved, only its old redundant temporary can go.
        string duplicateFolder = Folder(duplicateRun!);
        string pendingPath = Directory.GetFiles(duplicateFolder, "*.tmp").Single();
        NoteRecord pending;
        using (var stream = File.OpenRead(pendingPath)) pending = FileNoteRepository.ReadSnapshot(stream);
        new FileNoteRepository(duplicateFolder).Save(pending with { UpdatedAtUtc = DateTimeOffset.UtcNow });
        var removed = TemporaryNoteMaintenance.Run(duplicateFolder, DateTimeOffset.UtcNow, Validate);
        Require(removed.Removed == 1 && !File.Exists(pendingPath), "Redundant interrupted snapshot was not removed.");
        await VerifyChild(duplicateRun!, 1, false);
        Emit(new { stage = "complete", passed = Cases.Length, redundantCleanup = true });
    }
    public static void RunChild(string[] args)
    {
        if (args.Length < 3) throw new ArgumentException("Invalid isolated child options.");
        string folder = Folder(args[0]);
        if (args[1] == "verify" && args.Length == 4 && int.TryParse(args[2], out int version) && version is >= -1 and <= 1
            && bool.TryParse(args[3], out bool expectedRecovered))
        {
            var repository = new FileNoteRepository(folder);
            var record = repository.Load(Validate, out bool recovered);
            if (version < 0) Require(record is null && FileNoteRepository.DiscoverIds(folder).Count == 0, "Uncommitted first note was promoted.");
            else { Require(record is not null && recovered == expectedRecovered, "Recovery state mismatch."); VerifyRecord(record!, version); }
            Console.WriteLine("VERIFIED"); return;
        }
        if (args[1] != "write" || args.Length != 3 || args[2] is not ("TemporaryFlushed" or "DamagedPreserved" or "Committed" or "Acknowledged"))
            throw new ArgumentException("Invalid isolated write checkpoint.");
        void Pause(string stage)
        {
            Console.WriteLine("CHECKPOINT " + stage); Console.Out.Flush();
            // Parent terminates this exact child; the watchdog prevents an orphan if the parent dies.
            Thread.Sleep(TimeSpan.FromSeconds(45));
            Environment.Exit(124); // No finally/Dispose even on watchdog expiry.
        }
        var repo = new FileNoteRepository(folder, FileNoteRepository.NoteId, stage => { if (stage.ToString() == args[2]) Pause(args[2]); });
        _ = repo.Load(Validate, out _); // Set damaged-primary preservation state using the real reader.
        if (args[2] == "Acknowledged")
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            using var view = View(1);
            using var saves = new AutosaveCoordinator(view, repo, () => Bounds(1), new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
            saves.MarkChanged(); var flush = saves.FlushAsync(); Pump(flush);
            Require(flush.Result, "Autosave was not acknowledged."); Pause("Acknowledged");
        }
        else repo.Save(Record(1));
        throw new InvalidOperationException("Expected checkpoint was not reached.");
    }
}
