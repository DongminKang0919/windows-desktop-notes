using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Velopack;
using WindowsMemo.Infrastructure;
using WindowsMemo.Services;

namespace WindowsMemo.Tests;

internal static class UpdateTests
{
    private sealed class Backend : IUpdateBackend
    {
        public int Checks, Applies;
        public bool FailCheck, FailApply;
        public TaskCompletionSource<string?>? Pending;
        public CancellationToken Token;
        public Task<string?> PrepareAsync(CancellationToken token)
        {
            Checks++; Token = token;
            if (FailCheck) throw new IOException("offline");
            return Pending?.Task ?? Task.FromResult<string?>("0.8.0");
        }
        public void Apply() { Applies++; if (FailApply) throw new IOException("apply failed"); }
    }
    internal static void Run(Action<string, Action> check)
    {
        check("업데이트: 다운로드와 적용 분리·저장 실패 차단·재시도", () =>
        {
            var backend = new Backend(); using var service = new UpdaterService(backend);
            service.CheckAsync().GetAwaiter().GetResult();
            Assert(service.State == UpdateState.Ready && backend.Applies == 0);
            Assert(!service.ApplyAsync(_ => Task.FromResult(false)).GetAwaiter().GetResult());
            Assert(backend.Applies == 0 && service.State == UpdateState.Ready);
            Assert(service.ApplyAsync(action => { action(); return Task.FromResult(true); }).GetAwaiter().GetResult());
            Assert(backend.Applies == 1);
        });
        check("업데이트: 오프라인·적용 오류 후 현재 앱 유지와 재시도", () =>
        {
            var backend = new Backend { FailCheck = true }; using var service = new UpdaterService(backend);
            service.CheckAsync().GetAwaiter().GetResult(); Assert(service.State == UpdateState.Failed);
            backend.FailCheck = false; service.CheckAsync().GetAwaiter().GetResult();
            backend.FailApply = true;
            Assert(!service.ApplyAsync(action => { action(); return Task.FromResult(true); }).GetAwaiter().GetResult());
            Assert(service.State == UpdateState.Ready);
            backend.FailApply = false;
            Assert(service.ApplyAsync(action => { action(); return Task.FromResult(true); }).GetAwaiter().GetResult());
        });
        check("업데이트: 중복 확인·적용 차단과 종료 시 취소", () =>
        {
            var backend = new Backend { Pending = new() }; var service = new UpdaterService(backend);
            var pending = service.CheckAsync(); service.CheckAsync().GetAwaiter().GetResult();
            Assert(backend.Checks == 1 && service.IsBusy);
            service.Dispose(); service.Dispose(); Assert(backend.Token.IsCancellationRequested);
            backend.Pending.SetResult("0.8.0"); pending.GetAwaiter().GetResult();
            Assert(!service.ApplyAsync(action => { action(); return Task.FromResult(true); }).GetAwaiter().GetResult());
            Assert(backend.Applies == 0);
            var nextBackend = new Backend(); using var next = new UpdaterService(nextBackend);
            next.CheckAsync().GetAwaiter().GetResult();
            var saving = new TaskCompletionSource<bool>();
            var applying = next.ApplyAsync(_ => saving.Task);
            Assert(!next.ApplyAsync(_ => Task.FromResult(true)).GetAwaiter().GetResult());
            next.CheckAsync().GetAwaiter().GetResult(); Assert(nextBackend.Checks == 1);
            saving.SetResult(false); applying.GetAwaiter().GetResult();
            Assert(next.State == UpdateState.Ready);
        });
        string root = Path.Combine(AppContext.BaseDirectory, "update-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string feedRoot = Path.Combine(root, "feed"); Directory.CreateDirectory(feedRoot);
        string feedPath = Path.Combine(feedRoot, "releases.win.json");
        string name = "WindowsMemoApp-0.8.0-full.nupkg";
        byte[] content = [1, 2, 3, 4];
        string sha = Convert.ToHexString(SHA256.HashData(content));
        void Feed(string file = "WindowsMemoApp-0.8.0-full.nupkg", string id = "WindowsMemoApp",
            string version = "0.8.0", long size = 4, string? hash = null) => File.WriteAllText(feedPath,
                JsonSerializer.Serialize(new { Assets = new[] { new { PackageId = id, Version = version, Type = "Full", FileName = file, SHA256 = hash ?? sha, Size = size } } }));
        File.WriteAllBytes(Path.Combine(feedRoot, name), content); Feed();
        var source = new LocalTestUpdateSource(feedRoot);
        Task<VelopackAssetFeed> Read() => source.GetReleaseFeed(null!, "WindowsMemoApp", "win");
        check("업데이트 소스: 정상 피드·해시 확인 후 복사", () =>
        {
            var asset = Read().GetAwaiter().GetResult().Assets.Single();
            string target = Path.Combine(root, "download.partial");
            source.DownloadReleaseEntry(null!, asset, target, _ => { }).GetAwaiter().GetResult();
            Assert(File.ReadAllBytes(target).SequenceEqual(content));
        });
        check("업데이트 소스: traversal·다른 제품·시험판·과대 크기·해시 거부", () =>
        {
            foreach (string file in new[] { "../outside.nupkg", @"C:\outside.nupkg", "https://host/package.nupkg", "bad:stream.nupkg" })
            { Feed(file: file); Throws(() => Read().GetAwaiter().GetResult()); }
            Feed(id: "OtherApp"); Throws(() => Read().GetAwaiter().GetResult());
            Feed(version: "0.8.0-beta"); Throws(() => Read().GetAwaiter().GetResult());
            Feed(size: LocalTestUpdateSource.MaxPackageBytes + 1); Throws(() => Read().GetAwaiter().GetResult());
            Feed(hash: "invalid"); Throws(() => Read().GetAwaiter().GetResult());
            Feed();
        });
        check("업데이트 소스: 변조·크기 불일치·취소 거부", () =>
        {
            var asset = Read().GetAwaiter().GetResult().Assets.Single();
            string target = Path.Combine(root, "bad.partial");
            File.WriteAllBytes(Path.Combine(feedRoot, name), [4, 3, 2, 1]);
            Throws(() => source.DownloadReleaseEntry(null!, asset, target, _ => { }).GetAwaiter().GetResult());
            File.WriteAllBytes(Path.Combine(feedRoot, name), [1]);
            Throws(() => source.DownloadReleaseEntry(null!, asset, target, _ => { }).GetAwaiter().GetResult());
            File.WriteAllBytes(Path.Combine(feedRoot, name), content);
            Throws(() => source.DownloadReleaseEntry(null!, asset, target, _ => { }, new CancellationToken(true)).GetAwaiter().GetResult());
        });
        check("업데이트 소스: 상대·원격 경로·과대 피드·잘못된 채널 거부", () =>
        {
            foreach (string path in new[] { "relative", @"\\server\share", "https://host/updates" })
                Throws(() => _ = new LocalTestUpdateSource(path));
            Throws(() => source.GetReleaseFeed(null!, "WindowsMemoApp", "other").GetAwaiter().GetResult());
            File.WriteAllText(feedPath, new string(' ', 1024 * 1024 + 1));
            Throws(() => Read().GetAwaiter().GetResult());
            File.WriteAllText(feedPath, "{broken}"); Throws(() => Read().GetAwaiter().GetResult());
        });
    }
    private static void Assert(bool value) { if (!value) throw new InvalidOperationException("Update assertion failed."); }
    private static void Throws(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or JsonException or OperationCanceledException) { return; }
        throw new InvalidOperationException("Expected an update error.");
    }
}
