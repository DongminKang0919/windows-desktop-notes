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
        check("저장 형식: Hidden 없는 기존 스키마 1 복원", () =>
        {
            var repo = Repository("legacy-schema"); var record = Record("이전 버전 메모");
            Directory.CreateDirectory(Path.GetDirectoryName(repo.PrimaryPath)!);
            var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(record))!;
            node.AsObject().Remove("Hidden");
            byte[] cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(node.ToJsonString()), null, DataProtectionScope.CurrentUser);
            File.WriteAllText(repo.PrimaryPath, JsonSerializer.Serialize(new { Version = 1, Protection = "DPAPI-CurrentUser", Ciphertext = cipher }));
            var restored = repo.Load(Validate, out bool recovered)!;
            Assert(restored.SchemaVersion == 1 && !restored.Hidden && !recovered && Text(restored) == "이전 버전 메모");
        });
        check("미래 스키마: 이전 백업으로 되돌리거나 원본 덮어쓰지 않음", () =>
        {
            var repo = Repository("future-schema"); repo.Save(Record("old-backup")); repo.Save(Record("current"));
            byte[] backup = File.ReadAllBytes(repo.BackupPath);
            byte[] cipher = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(Record("future") with { SchemaVersion = 5 }), null, DataProtectionScope.CurrentUser);
            File.WriteAllText(repo.PrimaryPath, JsonSerializer.Serialize(new { Version = 1, Protection = "DPAPI-CurrentUser", Ciphertext = cipher }));
            byte[] primary = File.ReadAllBytes(repo.PrimaryPath);
            Throws(() => repo.Load(Validate, out _));
            Assert(File.ReadAllBytes(repo.PrimaryPath).SequenceEqual(primary) && File.ReadAllBytes(repo.BackupPath).SequenceEqual(backup));
        });
        check("저장 형식 4: 기존 필드 기본값과 새 상태 검증", () =>
        {
            var repo = Repository("paper-schema"); var record = Record("종이색 보존");
            repo.Save(record with { SchemaVersion = 4, Paper = "Sage", Collapsed = true, Archived = true, Hidden = true });
            var restored = repo.Load(Validate, out _)!;
            Assert(restored.Paper == "Sage" && restored.Collapsed && restored.Archived && restored.Hidden);
            Throws(() => repo.Save(restored with { Paper = "Unknown" }));
            Throws(() => repo.Save(restored with { Hidden = false }));
            Throws(() => repo.Save(restored with { DeletedAtUtc = restored.UpdatedAtUtc }));
            Throws(() => repo.Save(restored with { SchemaVersion = 3 }));
            foreach (int schema in new[] { 1, 2, 3 })
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(record with { SchemaVersion = schema }))!;
                foreach (string field in new[] { "Paper", "Collapsed", "Archived" }) node.AsObject().Remove(field);
                byte[] cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(node.ToJsonString()), null, DataProtectionScope.CurrentUser);
                File.WriteAllText(repo.PrimaryPath, JsonSerializer.Serialize(new { Version = 1, Protection = "DPAPI-CurrentUser", Ciphertext = cipher }));
                restored = repo.Load(Validate, out bool recovered)!;
                Assert(!recovered && restored.Paper == "Butter" && !restored.Collapsed && !restored.Archived);
            }
        });
        check("삭제 상태: 이전 스키마·표시 상태·잘못된 시각 거부", () =>
        {
            var repo = Repository("deleted-validation"); var record = Record("keep");
            var deleted = record.UpdatedAtUtc;
            Throws(() => repo.Save(record with { SchemaVersion = 2, Hidden = true, DeletedAtUtc = deleted }));
            Throws(() => repo.Save(record with { SchemaVersion = 3, Hidden = false, DeletedAtUtc = deleted }));
            Throws(() => repo.Save(record with { SchemaVersion = 3, Hidden = true, DeletedAtUtc = record.CreatedAtUtc.AddDays(-1) }));
            Throws(() => repo.Save(record with { SchemaVersion = 3, Hidden = true, DeletedAtUtc = record.UpdatedAtUtc.AddDays(1) }));
            Assert(!File.Exists(repo.PrimaryPath));
        });
        check("여러 메모: 독립 저장·백업·손상 복구와 기존 ID 호환", () =>
        {
            string folder = Path.Combine(root, "multiple");
            var legacy = new FileNoteRepository(folder);
            Guid id = Guid.NewGuid();
            var other = new FileNoteRepository(folder, id);
            legacy.Save(Record("legacy-one")); legacy.Save(Record("legacy-two"));
            other.Save(Record("other-one") with { Id = id });
            other.Save(Record("other-two") with { Id = id });
            byte[] otherPrimary = File.ReadAllBytes(other.PrimaryPath), otherBackup = File.ReadAllBytes(other.BackupPath);
            File.WriteAllText(legacy.PrimaryPath, "damaged");
            var recovered = legacy.Load(Validate, out bool usedBackup)!;
            Assert(usedBackup && Text(recovered) == "legacy-one");
            legacy.Save(recovered);
            Assert(File.ReadAllBytes(other.PrimaryPath).SequenceEqual(otherPrimary));
            Assert(File.ReadAllBytes(other.BackupPath).SequenceEqual(otherBackup));
            Assert(Text(other.Load(Validate, out _)!) == "other-two");
            Assert(legacy.Id == FileNoteRepository.NoteId);
            Assert(Path.GetFileName(legacy.PrimaryPath) == FileNoteRepository.NoteId.ToString("N") + ".json");
        });
        check("메모 목록: 중복 제거·백업만 남은 메모·보존 파일 제외", () =>
        {
            string folder = Path.Combine(root, "discovery");
            Assert(FileNoteRepository.DiscoverIds(folder).Count == 0);
            var legacy = new FileNoteRepository(folder);
            Guid id = Guid.NewGuid(); var other = new FileNoteRepository(folder, id);
            legacy.Save(Record("legacy")); legacy.Save(Record("updated"));
            other.Save(Record("backup-only") with { Id = id });
            File.Move(other.PrimaryPath, other.BackupPath);
            File.WriteAllText(Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp"), "ignored");
            File.WriteAllText(legacy.PrimaryPath + ".damaged-" + Guid.NewGuid().ToString("N"), "ignored");
            File.WriteAllText(Path.Combine(folder, "unexpected.json"), "ignored");
            File.WriteAllText(Path.Combine(folder, Guid.Empty.ToString("N") + ".json"), "ignored");
            var ids = FileNoteRepository.DiscoverIds(folder);
            Assert(ids.Count == 2 && ids.Contains(legacy.Id) && ids.Contains(other.Id));
            Assert(ids.SequenceEqual(ids.OrderBy(value => value)));
            Assert(Text(other.Load(Validate, out bool recovered)!) == "backup-only" && recovered);
        });
        check("메모 ID 경계: 다른 ID 저장·파일 교체·빈 ID 거부", () =>
        {
            string folder = Path.Combine(root, "identity");
            Guid id = Guid.NewGuid(); var other = new FileNoteRepository(folder, id);
            Throws(() => new FileNoteRepository(folder, Guid.Empty));
            Throws(() => other.Save(Record("wrong-id")));
            Assert(!File.Exists(other.PrimaryPath));
            var legacy = new FileNoteRepository(folder); legacy.Save(Record("keep"));
            File.Copy(legacy.PrimaryPath, other.PrimaryPath);
            byte[] wrong = File.ReadAllBytes(other.PrimaryPath);
            Throws(() => other.Load(Validate, out _));
            Assert(File.ReadAllBytes(other.PrimaryPath).SequenceEqual(wrong));
            Assert(Text(legacy.Load(Validate, out _)!) == "keep");
        });
        check("자동 저장: 두 메모의 본문·ID·위치 분리", () =>
        {
            string folder = Path.Combine(root, "multi-autosave");
            Guid firstId = Guid.NewGuid(), secondId = Guid.NewGuid();
            var firstRepo = new FileNoteRepository(folder, firstId);
            var secondRepo = new FileNoteRepository(folder, secondId);
            using var firstView = new NoteView(); using var secondView = new NoteView();
            using var firstSaves = new AutosaveCoordinator(firstView, firstRepo, () => new NoteBounds(10,20,420,480), DateTimeOffset.UtcNow.AddMinutes(-1));
            using var secondSaves = new AutosaveCoordinator(secondView, secondRepo, () => new NoteBounds(80,90,320,360), DateTimeOffset.UtcNow.AddMinutes(-1));
            firstView.InsertPlainText("첫 번째 메모"); secondView.InsertPlainText("두 번째 메모");
            var firstSave = firstSaves.FlushAsync(); var secondSave = secondSaves.FlushAsync();
            Assert(Wait(firstSave) && Wait(secondSave));
            var firstRecord = firstRepo.Load(Validate, out _)!; var secondRecord = secondRepo.Load(Validate, out _)!;
            Assert(firstRecord.Id == firstId && Text(firstRecord) == "첫 번째 메모" && firstRecord.Bounds.X == 10);
            Assert(secondRecord.Id == secondId && Text(secondRecord) == "두 번째 메모" && secondRecord.Bounds.X == 80);
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
