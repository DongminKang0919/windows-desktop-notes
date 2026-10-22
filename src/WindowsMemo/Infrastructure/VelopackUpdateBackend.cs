using System.IO;
using Velopack;
using Velopack.Locators;
using WindowsMemo.Services;

namespace WindowsMemo.Infrastructure;

internal sealed class VelopackUpdateBackend : IUpdateBackend
{
    private readonly UpdateManager _manager;
    private readonly string _root;
    private UpdateInfo? _prepared;
    public VelopackUpdateBackend(string root)
    {
        _root = root;
        _manager = new UpdateManager(new LocalTestUpdateSource(root), new UpdateOptions
            { ExplicitChannel = "win", AllowVersionDowngrade = false, MaximumDeltasBeforeFallback = -1 });
        if (!_manager.IsInstalled || _manager.IsPortable || _manager.AppId != "WindowsMemoApp")
            throw new InvalidOperationException("업데이트 시험은 Windows 메모 설치본에서 실행하세요.");
    }
    public async Task<string?> PrepareAsync(CancellationToken cancellationToken)
    {
        _prepared = null;
        var update = await _manager.CheckForUpdatesAsync();
        cancellationToken.ThrowIfCancellationRequested();
        if (update is null) return null;
        LocalTestUpdateSource.Validate(update.TargetFullRelease);
        if (update.IsDowngrade || update.TargetFullRelease.Version <= _manager.CurrentVersion)
            throw new InvalidDataException("업데이트 버전이 현재 버전보다 높지 않습니다.");
        await _manager.DownloadUpdatesAsync(update, cancelToken: cancellationToken);
        await Task.Run(() => UpdatePackageValidator.Validate(CachedPath(update.TargetFullRelease), update.TargetFullRelease), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _prepared = update;
        return update.TargetFullRelease.Version.ToString();
    }
    public void Apply()
    {
        if (_prepared is null) throw new InvalidOperationException("다운로드한 업데이트가 없습니다.");
        UpdatePackageValidator.Validate(CachedPath(_prepared.TargetFullRelease), _prepared.TargetFullRelease);
        _manager.ApplyUpdatesAndRestart(_prepared.TargetFullRelease, ["--update-test-source", _root]);
    }
    private static string CachedPath(VelopackAsset asset)
    {
        LocalTestUpdateSource.Validate(asset);
        string packages = VelopackLocator.Current.PackagesDir ?? throw new InvalidOperationException("설치 패키지 경로가 없습니다.");
        return Path.Combine(packages, asset.FileName);
    }
}
