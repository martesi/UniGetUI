using System.ComponentModel;
using Microsoft.UI.Xaml;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Managers.PipManager;
using UniGetUI.PackageEngine.Managers.ScoopManager;
using UniGetUI.PackageEngine.Managers.WingetManager;

namespace UniGetUI.PackageEngine.PackageClasses;

public partial class PackageWrapper
{
    public string InstallerHostText { get; private set; } = "";
    public string? InstallerHostTooltip { get; private set; }
    public string DownloadSizeText { get; private set; } = "";
    public long DownloadSizeBytes { get; private set; }
    public string? InstalledVersionTooltip => InstalledVersionNotice.BuildTooltip(Package) ?? Package.VersionString;
    public GridLength InstallerHostWidth => new(Settings.Get(Settings.K.ShowInstallerHostColumn) ? 140 : 0);
    public GridLength DownloadSizeWidth => new(Settings.Get(Settings.K.ShowDownloadSizeColumn) ? 100 : 0);
    private readonly CancellationTokenSource _lifetimeCts = new();

    public void RefreshColumns()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InstallerHostWidth)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DownloadSizeWidth)));
    }

    private const string UnknownInstallerHost = "\u2014";
    private const int MaxInstallerHostCacheEntries = 1024;
    private const int MaxUnresolvedInstallerHostEntries = 512;
    private static readonly TimeSpan InstallerHostRetryInterval = TimeSpan.FromMinutes(5);
    private static readonly SemaphoreSlim _installerHostSemaphore = new(4, 4);
    private static readonly object _installerHostCacheLock = new();
    private static readonly Dictionary<long, (string Host, string Urls)> _installerHostCache = new();
    private static readonly Dictionary<long, long> _unresolvedInstallerHosts = new();

    private const string UnknownDownloadSize = "\u2014";
    private const int MaxDownloadSizeCacheEntries = 1024;
    private static readonly TimeSpan DownloadSizeRetryInterval = TimeSpan.FromMinutes(5);
    private static readonly SemaphoreSlim _downloadSizeSemaphore = new(8, 8);
    private static readonly object _downloadSizeCacheLock = new();
    private static readonly Dictionary<long, (long Size, long ResolvedAt)> _downloadSizeCache = new();

    private int _installerHostLoadStarted;

    public void EnsureInstallerHostLoaded()
    {
        if (!Settings.Get(Settings.K.ShowInstallerHostColumn)) return;
        if (Interlocked.Exchange(ref _installerHostLoadStarted, 1) != 0) return;
        _ = LoadInstallerHostAsync();
    }

    private string TargetInstallerVersion =>
        Package.IsUpgradable ? Package.NewVersionString : Package.VersionString;

    private async Task LoadInstallerHostAsync()
    {
        CancellationToken token = _lifetimeCts.Token;
        long hash = CoreTools.HashStringAsLong(
            $"{Package.GetVersionedHash()}|{TargetInstallerVersion}"
        );
        try
        {
            if (TryGetCachedInstallerHost(hash, out var cached))
            {
                ApplyInstallerHost(cached.Host, cached.Urls);
                return;
            }

            if (HasRecentInstallerHostFailure(hash))
            {
                ApplyInstallerHost("", "");
                return;
            }

            await _installerHostSemaphore.WaitAsync(token).ConfigureAwait(false);
            (string Host, string Urls) resolved;
            try
            {
                if (!TryGetCachedInstallerHost(hash, out resolved))
                {
                    IReadOnlyList<string>? urls = await ResolveInstallerUrlsAsync(token)
                        .ConfigureAwait(false);
                    resolved = (
                        InstallerHostDisplay.FromUrls(urls),
                        InstallerHostDisplay.JoinUrls(urls)
                    );
                    if (resolved.Host.Length > 0)
                        CacheInstallerHost(hash, resolved.Host, resolved.Urls);
                    else
                        MarkInstallerHostUnresolved(hash);
                }
            }
            finally
            {
                _installerHostSemaphore.Release();
            }

            if (token.IsCancellationRequested) return;
            _page.DispatcherQueue.TryEnqueue(() =>
            {
                if (!token.IsCancellationRequested)
                    ApplyInstallerHost(resolved.Host, resolved.Urls);
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Warn($"Could not resolve the installer host for {Package.Id}: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _installerHostLoadStarted, 0);
        }
    }

    private async Task<IReadOnlyList<string>?> ResolveInstallerUrlsAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
#if WINDOWS
        if (Package.Manager is WinGet)
        {
            string version = TargetInstallerVersion;
            return await Task.Run(() => WinGet.TryGetInstallerUrls(Package, version), token)
                .ConfigureAwait(false);
        }
#endif
        if (!Package.Details.IsPopulated)
            await Package.Details.Load().ConfigureAwait(false);

        return Package.Details.InstallerUrl is { } url ? [url.ToString()] : null;
    }

    private void ApplyInstallerHost(string host, string urls)
    {
        InstallerHostText = host.Length > 0 ? host : UnknownInstallerHost;
        InstallerHostTooltip = urls.Length > 0 ? urls : null;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InstallerHostText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InstallerHostTooltip)));
    }

    private static bool TryGetCachedInstallerHost(long hash, out (string Host, string Urls) entry)
    {
        lock (_installerHostCacheLock)
            return _installerHostCache.TryGetValue(hash, out entry);
    }

    private static bool HasRecentInstallerHostFailure(long hash)
    {
        lock (_installerHostCacheLock)
        {
            if (!_unresolvedInstallerHosts.TryGetValue(hash, out long failedAt))
                return false;

            if (Environment.TickCount64 - failedAt < (long)InstallerHostRetryInterval.TotalMilliseconds)
                return true;

            _unresolvedInstallerHosts.Remove(hash);
            return false;
        }
    }

    private static void MarkInstallerHostUnresolved(long hash)
    {
        lock (_installerHostCacheLock)
        {
            long now = Environment.TickCount64;
            _unresolvedInstallerHosts[hash] = now;
            if (_unresolvedInstallerHosts.Count <= MaxUnresolvedInstallerHostEntries)
                return;

            long retryMs = (long)InstallerHostRetryInterval.TotalMilliseconds;
            foreach (var expired in _unresolvedInstallerHosts.Where(e => now - e.Value >= retryMs).ToArray())
                _unresolvedInstallerHosts.Remove(expired.Key);

            int excess = _unresolvedInstallerHosts.Count - MaxUnresolvedInstallerHostEntries;
            if (excess <= 0)
                return;

            foreach (var oldest in _unresolvedInstallerHosts.OrderBy(e => e.Value).Take(excess).ToArray())
                _unresolvedInstallerHosts.Remove(oldest.Key);
        }
    }

    private static void CacheInstallerHost(long hash, string host, string urls)
    {
        lock (_installerHostCacheLock)
        {
            if (_installerHostCache.Count >= MaxInstallerHostCacheEntries
                && !_installerHostCache.ContainsKey(hash))
            {
                _installerHostCache.Clear();
            }

            _installerHostCache[hash] = (host, urls);
        }
    }

    private readonly object _downloadSizeLoadLock = new();
    private Task? _downloadSizeLoadTask;

    private bool ManagerReportsDownloadSize
    {
        get
        {
            if (Package.Manager is Pip) return true;
#if WINDOWS
            if (Package.Manager is WinGet or Scoop) return true;
#endif
            return false;
        }
    }

    public void EnsureDownloadSizeLoaded() => _ = EnsureDownloadSizeLoadedAsync();

    public Task EnsureDownloadSizeLoadedAsync()
    {
        if (!Settings.Get(Settings.K.ShowDownloadSizeColumn)) return Task.CompletedTask;

        if (!ManagerReportsDownloadSize)
        {
            if (DownloadSizeText.Length == 0) ApplyDownloadSize(0);
            return Task.CompletedTask;
        }

        lock (_downloadSizeLoadLock)
        {
            if (_downloadSizeLoadTask is { IsCompleted: false }) return _downloadSizeLoadTask;
            return _downloadSizeLoadTask = LoadDownloadSizeAsync();
        }
    }

    private async Task LoadDownloadSizeAsync()
    {
        CancellationToken token = _lifetimeCts.Token;
        long hash = CoreTools.HashStringAsLong(
            $"{Package.GetVersionedHash()}|{TargetInstallerVersion}"
        );
        try
        {
            if (TryGetCachedDownloadSize(hash, out long cached))
            {
                ApplyDownloadSize(cached);
                return;
            }

            await _downloadSizeSemaphore.WaitAsync(token).ConfigureAwait(false);
            long size;
            try
            {
                if (!TryGetCachedDownloadSize(hash, out size))
                {
                    size = await ResolveDownloadSizeAsync(token).ConfigureAwait(false);
                    CacheDownloadSize(hash, size);
                }
            }
            finally
            {
                _downloadSizeSemaphore.Release();
            }

            if (token.IsCancellationRequested) return;
            ApplyDownloadSize(size);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Warn($"Could not resolve the download size for {Package.Id}: {ex.Message}");
        }
    }

    private async Task<long> ResolveDownloadSizeAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Package.Details.IsPopulated)
            await Package.Details.Load().ConfigureAwait(false);

        token.ThrowIfCancellationRequested();
        return Package.Details.InstallerSize;
    }

    private void ApplyDownloadSize(long size)
    {
        if (!_page.DispatcherQueue.HasThreadAccess)
        {
            _page.DispatcherQueue.TryEnqueue(() => ApplyDownloadSize(size));
            return;
        }

        DownloadSizeBytes = size;
        DownloadSizeText = size > 0 ? CoreTools.FormatAsSize(size) : UnknownDownloadSize;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DownloadSizeBytes)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DownloadSizeText)));
    }

    private static bool TryGetCachedDownloadSize(long hash, out long size)
    {
        lock (_downloadSizeCacheLock)
        {
            size = 0;
            if (!_downloadSizeCache.TryGetValue(hash, out var entry))
                return false;

            if (entry.Size > 0)
            {
                size = entry.Size;
                return true;
            }

            if (Environment.TickCount64 - entry.ResolvedAt < (long)DownloadSizeRetryInterval.TotalMilliseconds)
                return true;

            _downloadSizeCache.Remove(hash);
            return false;
        }
    }

    private static void CacheDownloadSize(long hash, long size)
    {
        lock (_downloadSizeCacheLock)
        {
            if (_downloadSizeCache.Count >= MaxDownloadSizeCacheEntries
                && !_downloadSizeCache.ContainsKey(hash))
            {
                _downloadSizeCache.Clear();
            }

            _downloadSizeCache[hash] = (size, Environment.TickCount64);
        }
    }


}
