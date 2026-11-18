using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Documents;
using System.Windows.Threading;
using WindowsMemo.Infrastructure;
using WindowsMemo.Models;
using WindowsMemo.Services;

namespace WindowsMemo.Tests;

internal static class RecoveryFeedbackTests
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
        string root = Path.Combine(AppContext.BaseDirectory, "recovery-feedback-tests", Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.UtcNow;
        byte[] rtf = RtfDocumentCodec.Save(new FlowDocument(new Paragraph(new Run("private-fixture-body"))));
        NoteRecord Record() => new(4, Guid.NewGuid(), rtf, new(48, 64, 320, 300), now.AddDays(-2), now.AddDays(-1), Hidden: true);
        (string Folder, Guid Token, string Source, NoteRecord Record) Fixture(string name)
        {
            string folder = Path.Combine(root, name); var baseline = Record(); new FileNoteRepository(folder, baseline.Id).Save(baseline);
            var pending = Record(); var seed = new FileNoteRepository(Path.Combine(folder, "seed"), pending.Id); seed.Save(pending);
            Guid token = Guid.NewGuid(); string path = Path.Combine(folder, token.ToString("N") + ".tmp"); File.Copy(seed.PrimaryPath, path);
            return (folder, token, path, pending);
        }
        NoteWorkspace Workspace(string folder) => new(folder, (_, _) => throw new InvalidOperationException("All test notes must remain hidden."));
        PendingRecoveryResult Recover(NoteWorkspace workspace, PendingNotePreview preview) => Result(workspace.RecoverPendingAsync(preview));
        check("복구 실패 안내: 변경·없음·사용 중을 구분하고 원본 보존", () =>
        {
            var fixture = Fixture("source"); using var workspace = Workspace(fixture.Folder); Wait(workspace.InitializeAsync());
            var preview = Result(workspace.PreviewPendingAsync(fixture.Token)); byte[] bytes = File.ReadAllBytes(fixture.Source);
            using (var held = new FileStream(fixture.Source, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var locked = Recover(workspace, preview); Require(locked.Status == PendingRecoveryStatus.SourceUnavailable && locked.CanRetry && !locked.Succeeded);
            }
            File.WriteAllText(fixture.Source, "changed"); var changed = Recover(workspace, preview);
            Require(changed.Status == PendingRecoveryStatus.SourceChanged && !changed.CanRetry && File.ReadAllText(fixture.Source) == "changed");
            File.Delete(fixture.Source); var missing = Recover(workspace, preview);
            Require(missing.Status == PendingRecoveryStatus.SourceMissing && !missing.CanRetry && workspace.Items.Count == 1);
            File.WriteAllBytes(fixture.Source, bytes); Require(Recover(workspace, preview).Status == PendingRecoveryStatus.Created);
        });
        check("복구 실패 안내: 손상/미래 형식 구분·오류에 경로/본문 미노출", () =>
        {
            foreach (bool future in new[] { false, true })
            {
                var fixture = Fixture(future ? "future" : "invalid"); using var workspace = Workspace(fixture.Folder); Wait(workspace.InitializeAsync());
                var record = future ? fixture.Record with { SchemaVersion = 5 } : fixture.Record with { Rtf = [1, 2, 3] };
                byte[] plain = JsonSerializer.SerializeToUtf8Bytes(record); byte[] cipher;
                try { cipher = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser); }
                finally { CryptographicOperations.ZeroMemory(plain); }
                File.WriteAllBytes(fixture.Source, JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, Protection = "DPAPI-CurrentUser", Ciphertext = cipher }));
                byte[] bytes = File.ReadAllBytes(fixture.Source);
                var preview = new PendingNotePreview(fixture.Token, Convert.ToHexString(SHA256.HashData(bytes)), null!);
                var result = Recover(workspace, preview);
                Require(result.Status == (future ? PendingRecoveryStatus.UnsupportedSource : PendingRecoveryStatus.InvalidSource));
                Require(!result.CanRetry && !result.Succeeded && result.Id == Guid.Empty && File.ReadAllBytes(fixture.Source).SequenceEqual(bytes));
                Require(!result.Message.Contains(root) && !result.Message.Contains("private-fixture-body"));
            }
            var feedback = PendingRecoveryResult.SourceFailure(new IOException(root + " private-fixture-body"));
            Require(!feedback.Message.Contains(root) && !feedback.Message.Contains("private-fixture-body"));
        });
        check("복구 실패 안내: 실제 새 사본 저장 실패 후 같은 미리보기로 재시도", () =>
        {
            var fixture = Fixture("save"); using var workspace = Workspace(fixture.Folder); Wait(workspace.InitializeAsync());
            var preview = Result(workspace.PreviewPendingAsync(fixture.Token)); byte[] before = File.ReadAllBytes(fixture.Source);
            Guid id = PendingNoteRecovery.CopyId(preview.Token, preview.Fingerprint);
            string blocked = new FileNoteRepository(fixture.Folder, id).PrimaryPath; Directory.CreateDirectory(blocked);
            var failed = Recover(workspace, preview);
            Require(failed.Status == PendingRecoveryStatus.SaveFailed && failed.CanRetry && !failed.Succeeded && !workspace.IsBusy && workspace.Items.Count == 1);
            Require(File.ReadAllBytes(fixture.Source).SequenceEqual(before));
            Directory.Delete(blocked); var retried = Recover(workspace, preview);
            Require(retried.Status == PendingRecoveryStatus.Created && retried.Id == id && workspace.Items.Count == 2);
        });
        check("복구 실패 안내: 손상 사본을 고치면 재실행 없이 다시 인식", () =>
        {
            var fixture = Fixture("existing"); Guid id;
            using (var workspace = Workspace(fixture.Folder))
            {
                Wait(workspace.InitializeAsync()); id = Recover(workspace, Result(workspace.PreviewPendingAsync(fixture.Token))).Id;
            }
            var repo = new FileNoteRepository(fixture.Folder, id); byte[] before = File.ReadAllBytes(repo.PrimaryPath);
            File.WriteAllText(repo.PrimaryPath, "damaged"); using var restarted = Workspace(fixture.Folder); Wait(restarted.InitializeAsync());
            Require(restarted.Items.Single(item => item.Id == id).Error is not null);
            var preview = Result(restarted.PreviewPendingAsync(fixture.Token)); var failed = Recover(restarted, preview);
            Require(failed.Status == PendingRecoveryStatus.ExistingCopyUnavailable && failed.CanRetry && File.ReadAllText(repo.PrimaryPath) == "damaged");
            File.WriteAllBytes(repo.PrimaryPath, before); var success = Recover(restarted, preview);
            Require(success.AlreadyRecovered && success.Id == id && restarted.Items.Count == 2 && restarted.Items.Single(item => item.Id == id).Error is null);
            Require(File.ReadAllBytes(repo.PrimaryPath).SequenceEqual(before));
        });
        check("복구 실패 안내: 1,000개 한도에서도 기존 사본 확인·새 생성만 제한", () =>
        {
            var fixture = Fixture("limit"); var preview = new PendingNoteRecovery(fixture.Folder).Read(fixture.Token);
            Guid existingId = PendingNoteRecovery.CopyId(preview.Token, preview.Fingerprint);
            new FileNoteRepository(fixture.Folder, existingId).Save(fixture.Record with { Id = existingId });
            for (int i = 0; i < 998; i++) { var record = Record(); new FileNoteRepository(fixture.Folder, record.Id).Save(record); }
            using var workspace = Workspace(fixture.Folder); Wait(workspace.InitializeAsync());
            Require(workspace.Items.Count == 1000 && Recover(workspace, preview).Status == PendingRecoveryStatus.AlreadyRecovered);
            var replacement = new FileNoteRepository(Path.Combine(fixture.Folder, "replacement"), fixture.Record.Id);
            replacement.Save(fixture.Record with { UpdatedAtUtc = now }); File.Copy(replacement.PrimaryPath, fixture.Source, overwrite: true);
            var fresh = Result(workspace.PreviewPendingAsync(fixture.Token)); var limited = Recover(workspace, fresh);
            Require(limited.Status == PendingRecoveryStatus.LimitReached && limited.CanRetry && workspace.Items.Count == 1000 && FileNoteRepository.DiscoverIds(fixture.Folder).Count == 1000);
        });
    }
    private static void Wait(Task task)
    {
        var frame = new DispatcherFrame(); var dispatcher = Dispatcher.CurrentDispatcher;
        task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
        if (!task.IsCompleted) Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
    }
    private static T Result<T>(Task<T> task) { Wait(task); return task.GetAwaiter().GetResult(); }
    private static void Require(bool value) { if (!value) throw new InvalidOperationException("Recovery feedback assertion failed."); }
}
