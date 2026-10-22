using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Velopack;
using WindowsMemo.Infrastructure;

namespace WindowsMemo.Tests;

internal static class UpdateDownloadTests
{
    private sealed class Fixture
    {
        internal string Root { get; } = Path.Combine(AppContext.BaseDirectory, "update-download-tests", Guid.NewGuid().ToString("N"));
        internal string FeedRoot => Path.Combine(Root, "feed");
        internal string CacheRoot => Path.Combine(Root, "cache");
        internal string SourcePath => Path.Combine(FeedRoot, "WindowsMemoApp-0.9.14-full.nupkg");
        internal string Target => Path.Combine(CacheRoot, "download.partial");
        internal byte[] Content { get; } = new byte[180_000];
        internal byte[] Previous { get; } = [9, 8, 7, 6];
        internal VelopackAsset Asset { get; }
        internal Fixture(bool existing = true)
        {
            Directory.CreateDirectory(FeedRoot); Directory.CreateDirectory(CacheRoot);
            new Random(1234).NextBytes(Content); File.WriteAllBytes(SourcePath, Content);
            if (existing) File.WriteAllBytes(Target, Previous);
            Asset = VelopackAssetFeed.FromJson(JsonSerializer.Serialize(new { Assets = new[] { new
            {
                PackageId = "WindowsMemoApp", Version = "0.9.14", Type = "Full", FileName = Path.GetFileName(SourcePath),
                SHA256 = Convert.ToHexString(SHA256.HashData(Content)), Size = Content.Length
            } } })).Assets.Single();
        }
        internal string[] CacheSnapshot() => Directory.GetFiles(CacheRoot).OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => $"{Path.GetFileName(path)}:{File.GetLastWriteTimeUtc(path).Ticks}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}").ToArray();
    }
    internal static void Run(Action<string, Action> check)
    {
        check("업데이트 캐시: 해시/크기 실패·사전 취소는 기존 파일과 신규 대상 부재 보존", () =>
        {
            foreach (bool existing in new[] { false, true })
            for (int failure = 0; failure < 3; failure++)
            {
                var f = new Fixture(existing); string[] before = f.CacheSnapshot(); int progress = 0;
                if (failure == 0) File.WriteAllBytes(f.SourcePath, new byte[f.Content.Length]);
                if (failure == 1) File.WriteAllBytes(f.SourcePath, [1]);
                using var cancel = new CancellationTokenSource(); if (failure == 2) cancel.Cancel();
                var source = new LocalTestUpdateSource(f.FeedRoot);
                Throws(() => source.DownloadReleaseEntry(null!, f.Asset, f.Target, _ => progress++, cancel.Token).GetAwaiter().GetResult());
                Require(before.SequenceEqual(f.CacheSnapshot()) && progress == 0);
            }
        });
        check("업데이트 캐시: 복사 중·검증 후 교체 직전 취소는 캐시 보존·임시 파일 해제", () =>
        {
            foreach (bool existing in new[] { false, true })
            foreach (var stop in Enum.GetValues<LocalTestUpdateSource.DownloadCheckpoint>())
            {
                var f = new Fixture(existing); string[] before = f.CacheSnapshot(); int progress = 0, stops = 0;
                using var cancel = new CancellationTokenSource();
                var source = new LocalTestUpdateSource(f.FeedRoot, point =>
                {
                    if (point != stop) return;
                    Require(existing ? File.ReadAllBytes(f.Target).SequenceEqual(f.Previous) : !File.Exists(f.Target));
                    stops++; cancel.Cancel();
                });
                Throws(() => source.DownloadReleaseEntry(null!, f.Asset, f.Target, _ => progress++, cancel.Token).GetAwaiter().GetResult());
                Require(stops == 1 && progress == 0 && before.SequenceEqual(f.CacheSnapshot()));
                // Also proves source and staging handles have closed before failure is returned.
                using var sourceLock = new FileStream(f.SourcePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
        });
        check("업데이트 캐시: 검증 완료 전 기존 파일 유지·교체 후에만 완료 알림", () =>
        {
            foreach (bool existing in new[] { false, true })
            {
                var f = new Fixture(existing); int chunks = 0, commits = 0, progress = 0;
                var source = new LocalTestUpdateSource(f.FeedRoot, point =>
                {
                    if (point == LocalTestUpdateSource.DownloadCheckpoint.ChunkWritten) chunks++; else commits++;
                    Require(existing ? File.ReadAllBytes(f.Target).SequenceEqual(f.Previous) : !File.Exists(f.Target));
                });
                source.DownloadReleaseEntry(null!, f.Asset, f.Target, value =>
                {
                    Require(value == 100 && File.ReadAllBytes(f.Target).SequenceEqual(f.Content)); progress++;
                    Require(Directory.GetFiles(f.CacheRoot).SequenceEqual([f.Target]));
                }).GetAwaiter().GetResult();
                Require(chunks >= 2 && commits == 1 && progress == 1 && File.ReadAllBytes(f.SourcePath).SequenceEqual(f.Content));
            }
        });
        check("업데이트 캐시: 교체 대상 잠금 실패 후 기존 파일 보존·원인 해제 재시도", () =>
        {
            var f = new Fixture(); string[] before = f.CacheSnapshot(); int progress = 0;
            var source = new LocalTestUpdateSource(f.FeedRoot);
            using (var locked = new FileStream(f.Target, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Throws(() => source.DownloadReleaseEntry(null!, f.Asset, f.Target, _ => progress++).GetAwaiter().GetResult());
                Require(before.SequenceEqual(f.CacheSnapshot()) && progress == 0);
            }
            source.DownloadReleaseEntry(null!, f.Asset, f.Target, _ => progress++).GetAwaiter().GetResult();
            Require(File.ReadAllBytes(f.Target).SequenceEqual(f.Content) && progress == 1 && Directory.GetFiles(f.CacheRoot).Length == 1);
        });
        check("업데이트 캐시: 원본/피드/하위 폴더·상대/원격/스트림 대상 거부", () =>
        {
            var f = new Fixture(); string feed = Path.Combine(f.FeedRoot, "releases.win.json"); File.WriteAllText(feed, "synthetic feed");
            string[] before = Directory.GetFiles(f.FeedRoot).Select(path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
            var source = new LocalTestUpdateSource(f.FeedRoot);
            foreach (string target in new[] { f.SourcePath, feed, Path.Combine(f.FeedRoot, "nested", "copy.partial"),
                Path.Combine(f.FeedRoot, "new.partial"), "relative.partial", @"\\server\share\cache.partial", f.Target + ":stream" })
                Throws(() => source.DownloadReleaseEntry(null!, f.Asset, target, _ => throw new InvalidOperationException("Premature progress.")).GetAwaiter().GetResult());
            Require(before.SequenceEqual(Directory.GetFiles(f.FeedRoot).Select(path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))));
            Require(!Directory.Exists(Path.Combine(f.FeedRoot, "nested")) && File.ReadAllBytes(f.Target).SequenceEqual(f.Previous));
        });
    }
    private static void Throws(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or OperationCanceledException) { return; }
        throw new InvalidOperationException("Expected a download failure.");
    }
    private static void Require(bool condition) { if (!condition) throw new InvalidOperationException("Update cache preservation assertion failed."); }
}
