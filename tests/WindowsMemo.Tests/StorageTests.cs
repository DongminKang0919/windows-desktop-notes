using System.IO;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using WindowsMemo.Infrastructure;
using WindowsMemo.Models;
using WindowsMemo.Services;
using WindowsMemo.Views;

namespace WindowsMemo.Tests;

internal static class StorageTests
{
    internal static void Run(Action<string, Action> check)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try { RunCore(check); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private static void RunCore(Action<string, Action> check)
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "storage-tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        FileNoteRepository Repository(string name) => new(Path.Combine(root, name));
        NoteRecord Record(string text)
        {
            using var view = new NoteView();
            view.InsertPlainText(text);
            return new NoteRecord(1, FileNoteRepository.NoteId, RtfDocumentCodec.Save(view.TextEditor.Document),
                new NoteBounds(-320, 64, 420, 480, 1.5), DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow);
        }
        void Validate(NoteRecord record) => RtfDocumentCodec.Load(record.Rtf);
        string Text(NoteRecord record)
        {
            var document = RtfDocumentCodec.Load(record.Rtf);
            return new TextRange(document.ContentStart, document.ContentEnd).Text.Trim();
        }
        check("RTF: 한글·굵게·색상·목록 왕복 및 리터럴 제어어", () =>
        {
            var run = new Run(@"한글 \object {메모}") { FontWeight = FontWeights.Bold, Foreground = Brushes.RoyalBlue };
            var list = new List(new ListItem(new Paragraph(run))) { MarkerStyle = TextMarkerStyle.Decimal };
            var document = new FlowDocument(list);
            var restored = RtfDocumentCodec.Load(RtfDocumentCodec.Save(document));
            Assert(new TextRange(restored.ContentStart, restored.ContentEnd).Text.Contains(@"한글 \object {메모}"));
            Assert(restored.Blocks.FirstBlock is List restoredList && restoredList.MarkerStyle == TextMarkerStyle.Decimal);
            var range = new TextRange(restored.ContentStart, restored.ContentEnd.GetPositionAtOffset(-1)!);
            Assert(range.GetPropertyValue(TextElement.FontWeightProperty).Equals(FontWeights.Bold));
            Assert(range.GetPropertyValue(TextElement.ForegroundProperty) is SolidColorBrush brush && brush.Color == Colors.RoyalBlue);
        });
        check("암호화 저장·위치 복원·이전 정상본 백업", () =>
        {
            var repo = Repository("encrypted");
            repo.Save(Record("first-secret")); repo.Save(Record("second-secret"));
            var restored = repo.Load(Validate, out bool recovered)!;
            Assert(!recovered && Text(restored) == "second-secret" && restored.Bounds.X == -320 && restored.Bounds.Scale == 1.5);
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(repo.PrimaryPath)!))
            {
                string data = File.ReadAllText(file);
                Assert(!data.Contains("first-secret") && !data.Contains("second-secret") && !data.Contains("rtf1"));
            }
            Assert(File.Exists(repo.BackupPath));
            Assert(!Directory.GetFiles(Path.GetDirectoryName(repo.PrimaryPath)!, "*.tmp").Any());
        });
        check("손상된 원본: 백업 복구 후 원본·정상 백업 보존", () =>
        {
            var repo = Repository("recovery");
            repo.Save(Record("first")); repo.Save(Record("second"));
            byte[] backup = File.ReadAllBytes(repo.BackupPath);
            File.WriteAllText(repo.PrimaryPath, "damaged");
            var restored = repo.Load(Validate, out bool recovered)!;
            Assert(recovered && Text(restored) == "first");
            repo.Save(restored);
            Assert(File.ReadAllBytes(repo.BackupPath).SequenceEqual(backup));
            Assert(Directory.GetFiles(Path.GetDirectoryName(repo.PrimaryPath)!, "*.damaged-*").Single() is string damaged && File.ReadAllText(damaged) == "damaged");
            Assert(Text(repo.Load(Validate, out _)!) == "first");
        });
        check("복구 불가·변조·초과 파일: 덮어쓰지 않고 거부", () =>
        {
            var repo = Repository("invalid");
            repo.Save(Record("keep"));
            byte[] bytes = File.ReadAllBytes(repo.PrimaryPath);
            var node = System.Text.Json.Nodes.JsonNode.Parse(bytes)!;
            byte[] cipher = Convert.FromBase64String(node["Ciphertext"]!.GetValue<string>());
            cipher[^1] ^= 0xff; node["Ciphertext"] = Convert.ToBase64String(cipher);
            File.WriteAllText(repo.PrimaryPath, node.ToJsonString());
            byte[] damaged = File.ReadAllBytes(repo.PrimaryPath);
            Throws(() => repo.Load(Validate, out _));
            Assert(File.ReadAllBytes(repo.PrimaryPath).SequenceEqual(damaged));
            File.WriteAllBytes(repo.PrimaryPath, new byte[FileNoteRepository.MaxFileBytes + 1]);
            Throws(() => repo.Load(Validate, out _));
        });
        check("스키마·ID·좌표·RTF 한도 거부", () =>
        {
            var repo = Repository("schema"); var record = Record("keep");
            Throws(() => repo.Save(record with { SchemaVersion = 99 }));
            Throws(() => repo.Save(record with { Id = Guid.NewGuid() }));
            Throws(() => repo.Save(record with { Bounds = record.Bounds with { X = double.NaN } }));
            Throws(() => RtfDocumentCodec.Load(Encoding.ASCII.GetBytes(@"{\rtf1{\object unsafe}}")));
            Throws(() => RtfDocumentCodec.Load(Encoding.ASCII.GetBytes(@"{\rtf1{\pict unsafe}}")));
            Throws(() => RtfDocumentCodec.Load(new byte[RtfDocumentCodec.MaxBytes + 1]));
            Throws(() => RtfDocumentCodec.Load(Encoding.ASCII.GetBytes(@"{\rtf1" + new string('{', 65) + new string('}', 66))));
            Assert(!File.Exists(repo.PrimaryPath));
        });
        check("RTF 해석 실패 시 백업 복구", () =>
        {
            var repo = Repository("bad-rtf"); repo.Save(Record("good"));
            repo.Save(Record("bad") with { Rtf = Encoding.ASCII.GetBytes(@"{\rtf1{\object unsafe}}") });
            Assert(Text(repo.Load(Validate, out bool recovered)!) == "good" && recovered);
        });
        check("쓰기 실패: 원본·백업 보호 및 재시도", () =>
        {
            var repo = Repository("failure");repo.Save(Record("one"));repo.Save(Record("two"));
            byte[] before = File.ReadAllBytes(repo.PrimaryPath), backup = File.ReadAllBytes(repo.BackupPath);
            using (var held = new FileStream(repo.PrimaryPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                Throws(() => repo.Save(Record("three")));
            Assert(File.ReadAllBytes(repo.PrimaryPath).SequenceEqual(before) && File.ReadAllBytes(repo.BackupPath).SequenceEqual(backup));
            repo.Save(Record("three"));Assert(Text(repo.Load(Validate, out _)!) == "three");
        });
        check("자동 저장: 저장 중 새 변경·실패 재시도·서식만 변경", () =>
        {
            var repo = Repository("autosave");
            using var view = new NoteView();
            using var saves = new AutosaveCoordinator(view, repo, () => new NoteBounds(48,64,420,480), DateTimeOffset.UtcNow.AddMinutes(-1));
            view.InsertPlainText("old");
            var first = saves.FlushAsync();
            view.TextEditor.SelectAll();view.InsertPlainText("new");
            Assert(Wait(first)); Assert(Text(repo.Load(Validate, out _)!) == "new");
            view.TextEditor.SelectAll();view.ApplyColor(Brushes.RoyalBlue);
            Assert(Wait(saves.FlushAsync()));
            var doc = RtfDocumentCodec.Load(repo.Load(Validate, out _)!.Rtf);
            Assert(new TextRange(doc.ContentStart, doc.ContentEnd.GetPositionAtOffset(-1)!).GetPropertyValue(TextElement.ForegroundProperty) is SolidColorBrush brush && brush.Color == Colors.RoyalBlue);
            using (var held = new FileStream(repo.PrimaryPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            { view.TextEditor.SelectAll();view.InsertPlainText("retry");Assert(!Wait(saves.FlushAsync())); }
            Assert(Wait(saves.FlushAsync()));Assert(Text(repo.Load(Validate, out _)!) == "retry");
        });
    }
    private static bool Wait(Task<bool> task)
    {
        var frame = new DispatcherFrame();
        var dispatcher = Dispatcher.CurrentDispatcher;
        task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        return task.GetAwaiter().GetResult();
    }
    private static void Assert(bool value) { if (!value) throw new InvalidOperationException("Storage assertion failed."); }
    private static void Throws(Action action)
    {
        try { action(); } catch (Exception ex) when (FileNoteRepository.IsStorageError(ex)) { return; }
        throw new InvalidOperationException("Expected a storage error.");
    }
}
