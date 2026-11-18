using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace WindowsMemo.Infrastructure;

// Explicit local prototype testing only; hashes do not authenticate a publisher.
internal sealed class LocalTestUpdateSource : IUpdateSource
{
    internal enum DownloadCheckpoint { ChunkWritten, BeforeCommit }
    private readonly Action<DownloadCheckpoint>? _checkpoint;
    private readonly string _root;
    internal const long MaxPackageBytes = 256 * 1024 * 1024;
    public LocalTestUpdateSource(string root) : this(root, null) { }
    // Instance-scoped test observation; production never installs a callback.
    internal LocalTestUpdateSource(string root, Action<DownloadCheckpoint>? checkpoint)
    {
        if (!Path.IsPathFullyQualified(root) || root.StartsWith(@"\\") || root.StartsWith("//"))
            throw new ArgumentException("로컬 절대 폴더 경로를 지정하세요.", nameof(root));
        _root = Path.GetFullPath(root);
        _checkpoint = checkpoint;
        CheckLinks(_root);
    }
    private static void CheckLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("링크된 업데이트 경로는 사용할 수 없습니다.");
    }
    private string FilePath(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name)
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..")
            throw new InvalidDataException("잘못된 업데이트 파일명입니다.");
        string path = Path.Combine(_root, name);
        CheckLinks(path);
        return path;
    }
    internal static void Validate(VelopackAsset? asset)
    {
        if (asset is null || asset.PackageId != "WindowsMemoApp" || asset.Version is null || asset.Version.IsPrerelease
            || asset.Type != VelopackAssetType.Full || asset.Size <= 0 || asset.Size > MaxPackageBytes
            || !Regex.IsMatch(asset.SHA256 ?? "", "\\A[0-9a-fA-F]{64}\\z")
            || asset.FileName != $"WindowsMemoApp-{asset.Version}-full.nupkg")
            throw new InvalidDataException("지원하지 않는 시험 업데이트 패키지입니다.");
    }
    public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId,
        string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
    {
        if (appId != "WindowsMemoApp" || channel != "win") throw new InvalidDataException("잘못된 업데이트 채널입니다.");
        using var stream = File.OpenRead(FilePath("releases.win.json"));
        if (stream.Length > 1024 * 1024) throw new InvalidDataException("업데이트 목록이 너무 큽니다.");
        // Read a bounded snapshot even if another process grows the file after opening it.
        var bytes = new byte[1024 * 1024 + 1];
        int count = await stream.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false);
        if (count == bytes.Length) throw new InvalidDataException("업데이트 목록이 너무 큽니다.");
        using var json = JsonDocument.Parse(bytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 16 });
        var feed = VelopackAssetFeed.FromJson(json.RootElement.GetRawText());
        if (feed.Assets is null || feed.Assets.Length > 100) throw new InvalidDataException("잘못된 업데이트 목록입니다.");
        foreach (var asset in feed.Assets) { Validate(asset); _ = FilePath(asset.FileName); }
        return feed;
    }
    public async Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry,
        string localFile, Action<int> progress, CancellationToken cancelToken = default)
    {
        cancelToken.ThrowIfCancellationRequested();
        Validate(releaseEntry);
        string destination = DestinationPath(localFile); // provided by Velopack, never by the feed
        using var source = File.OpenRead(FilePath(releaseEntry.FileName));
        if (source.Length != releaseEntry.Size) throw new InvalidDataException("업데이트 크기가 일치하지 않습니다.");
        string temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".windowsmemo-download-{Guid.NewGuid():N}.tmp");
        bool ownsTemporary = false;
        try
        {
            using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous))
            {
                ownsTemporary = true;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancelToken)) > 0)
                {
                    total += read;
                    if (total > releaseEntry.Size) throw new InvalidDataException("업데이트 크기가 초과되었습니다.");
                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), cancelToken);
                    _checkpoint?.Invoke(DownloadCheckpoint.ChunkWritten);
                }
                if (total != releaseEntry.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(releaseEntry.SHA256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("업데이트 해시가 일치하지 않습니다.");
                await target.FlushAsync(cancelToken);
            }
            _checkpoint?.Invoke(DownloadCheckpoint.BeforeCommit);
            _ = DestinationPath(destination); // Recheck links immediately before publishing the cache.
            FileNoteRepository.CheckLink(temporary);
            cancelToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true); // Same directory; never truncate the old cache.
            ownsTemporary = false;
            progress?.Invoke(100);
        }
        finally
        {
            if (ownsTemporary)
            {
                FileNoteRepository.CheckRoot(temporary);
                File.Delete(temporary); // Only this call's staging file, never the existing cache or feed.
            }
        }
    }
    private string DestinationPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\") || path.StartsWith("//")
            || Path.GetFileName(path).IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("로컬 캐시 파일 경로를 지정하세요.");
        string destination = Path.GetFullPath(path);
        if (destination.Equals(_root, StringComparison.OrdinalIgnoreCase)
            || destination.StartsWith(_root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("업데이트 원본 폴더에는 다운로드할 수 없습니다.");
        FileNoteRepository.CheckRoot(destination);
        return destination;
    }
}
