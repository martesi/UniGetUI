using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using UniGetUI.Core.Classes;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.Interface;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Managers.WingetManager;

namespace UniGetUI.PackageEngine.PackageClasses
{
    /// <summary>
    /// A wrapper for packages to be able to show in ItemCollections
    /// </summary>
    public partial class PackageWrapper : IIndexableListItem, INotifyPropertyChanged, IDisposable
    {
        private static readonly ConcurrentDictionary<long, Uri?> CachedPackageIcons = new();

        public static void ResetIconCache()
        {
            CachedPackageIcons.Clear();
        }

        public bool IsChecked
        {
            get => Package.IsChecked;
            set
            {
                Package.IsChecked = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CheckedStatus)));
                _page.UpdatePackageCount();
            }
        }

        public string CheckedStatus => IsChecked ? CoreTools.Translate("Checked") : CoreTools.Translate("Unchecked");

        public bool IconWasLoaded;
        public bool AlternateIdIconVisible;
        public bool ShowCustomPackageIcon;
        public bool ShowDefaultPackageIcon = true;
        public string VersionComboString;
        public IconType MainIconId = IconType.Id;
        public IconType AlternateIconId = IconType.Id;
        public ImageSource? MainIconSource;

        public Uri? PackageIcon
        {
            set
            {
                CachedPackageIcons[Package.GetHash()] = value;
                UpdatePackageIcon();
            }
        }

        public string ListedNameTooltip = "";
        public readonly string ExtendedTooltip = "";
        public float ListedOpacity = 1.0f;

        public bool InstallerHostChanged { get; private set; }
        public string InstallerHostChangeTooltip { get; private set; } = "";

        public bool ShowUpgradeDownloadIcon => Package.IsUpgradable && !InstallerHostChanged;

        private CancellationTokenSource? _installerHostCheckCts;

        private static readonly SemaphoreSlim OptionalDetailsGate = new(8, 8);
        private readonly CancellationTokenSource _optionalDetailsCts = new();
        private Task? _optionalDetailsTask;
        private bool _listRowRealized;
        public string InstallerHostText { get; private set; } = "—";
        public string DownloadSizeText { get; private set; } = "—";
        public string ReleaseDateText { get; private set; } = "—";
        public long DownloadSizeBytes { get; private set; }
        public DateTime ReleaseDate { get; private set; }

        private GridLength WidthFor(string column) =>
            new(_page.IsColumnVisible(column) ? 1 : 0, GridUnitType.Star);
        private Visibility VisibilityFor(string column) =>
            _page.IsColumnVisible(column) ? Visibility.Visible : Visibility.Collapsed;

        public GridLength NameColumnWidth => WidthFor("Name");
        public GridLength IdColumnWidth => WidthFor("Id");
        public GridLength VersionColumnWidth => WidthFor("Version");
        public GridLength NewVersionColumnWidth => WidthFor("NewVersion");
        public GridLength SourceColumnWidth => WidthFor("Source");
        public GridLength InstallerHostColumnWidth => WidthFor("InstallerHost");
        public GridLength DownloadSizeColumnWidth => WidthFor("DownloadSize");
        public GridLength ReleaseDateColumnWidth => WidthFor("ReleaseDate");
        public double NameColumnMinWidth => _page.IsColumnVisible("Name") ? 125 : 0;
        public double IdColumnMinWidth => _page.IsColumnVisible("Id") ? 125 : 0;
        public Visibility NameColumnVisibility => VisibilityFor("Name");
        public Visibility IdColumnVisibility => VisibilityFor("Id");
        public Visibility VersionColumnVisibility => VisibilityFor("Version");
        public Visibility NewVersionColumnVisibility => VisibilityFor("NewVersion");
        public Visibility SourceColumnVisibility => VisibilityFor("Source");
        public Visibility InstallerHostColumnVisibility => VisibilityFor("InstallerHost");
        public Visibility DownloadSizeColumnVisibility => VisibilityFor("DownloadSize");
        public Visibility ReleaseDateColumnVisibility => VisibilityFor("ReleaseDate");

        public void NotifyColumnVisibilityChanged()
        {
            foreach (string property in new[]
            {
                nameof(NameColumnWidth), nameof(IdColumnWidth), nameof(VersionColumnWidth),
                nameof(NewVersionColumnWidth), nameof(SourceColumnWidth),
                nameof(InstallerHostColumnWidth), nameof(DownloadSizeColumnWidth),
                nameof(ReleaseDateColumnWidth), nameof(NameColumnMinWidth), nameof(IdColumnMinWidth),
                nameof(NameColumnVisibility), nameof(IdColumnVisibility), nameof(VersionColumnVisibility),
                nameof(NewVersionColumnVisibility), nameof(SourceColumnVisibility),
                nameof(InstallerHostColumnVisibility), nameof(DownloadSizeColumnVisibility),
                nameof(ReleaseDateColumnVisibility),
            })
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
            if (_listRowRealized)
                _ = EnsureOptionalDetailsLoadedAsync();
        }

        public void PackageListRow_Loaded(object sender, RoutedEventArgs e)
        {
            _listRowRealized = true;
            _ = EnsureOptionalDetailsLoadedAsync();
        }

        public void PackageListRow_Unloaded(object sender, RoutedEventArgs e) => _listRowRealized = false;

        public Task EnsureOptionalDetailsLoadedAsync()
        {
            if (!_page.OptionalColumnsVisible || _optionalDetailsCts.IsCancellationRequested)
                return Task.CompletedTask;
            return _optionalDetailsTask ??= LoadOptionalDetailsAsync();
        }

        private async Task LoadOptionalDetailsAsync()
        {
            CancellationToken token = _optionalDetailsCts.Token;
            try
            {
                await OptionalDetailsGate.WaitAsync(token);
                IPackageDetails details;
                try
                {
                    IPackage target = Package.IsUpgradable && Package.NewVersionString != Package.VersionString
                        ? new Package(Package.Name, Package.Id, Package.NewVersionString,
                            Package.NewVersionString, Package.Source, Package.Manager, Package.OverridenOptions)
                        : Package;
                    details = target.Details;
                    if (!details.IsPopulated)
                        await Task.Run(() => details.Load(), token);
                }
                finally
                {
                    OptionalDetailsGate.Release();
                }
                token.ThrowIfCancellationRequested();
                InstallerHostText = details.InstallerUrl?.Host is { Length: > 0 } host ? host : "—";
                DownloadSizeBytes = details.InstallerSize;
                DownloadSizeText = DownloadSizeBytes > 0 ? CoreTools.FormatAsSize(DownloadSizeBytes) : "—";
                ReleaseDate = DateTime.TryParse(details.UpdateDate, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out DateTime date) ? date : default;
                ReleaseDateText = ReleaseDate != default ? ReleaseDate.ToString("d", CultureInfo.CurrentCulture) : "—";
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InstallerHostText)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DownloadSizeText)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReleaseDateText)));
                _page.NotifyOptionalDetailsLoaded();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Logger.Warn($"Could not load list details for {Package.Id}: {ex.Message}");
            }
        }

        public int NewVersionLabelWidth
        {
            get => Package.IsUpgradable ? 125 : 0;
        }
        public int NewVersionIconWidth
        {
            get => Package.IsUpgradable ? 24 : 0;
        }

        public int Index { get; set; }
        public event PropertyChangedEventHandler? PropertyChanged;

        public IPackage Package { get; private set; }
        public PackageWrapper Self { get; private set; }

        private readonly AbstractPackagesPage _page;

        public PackageWrapper(IPackage package, AbstractPackagesPage page)
        {
            Package = package;
            Self = this;
            _page = page;
            WhenTagHasChanged();
            Package.PropertyChanged += Package_PropertyChanged;
            UpdatePackageIcon();
            VersionComboString = package.IsUpgradable
                ? $"{package.VersionString} -> {package.NewVersionString}"
                : package.VersionString;

            if (package.Name.ToLower() != package.Id.ToLower())
                ExtendedTooltip =
                    $"{package.Name} ({package.Id} from {package.Source.AsString_DisplayName})";
            else
                ExtendedTooltip = $"{package.Name} (from {package.Source.AsString_DisplayName})";

            MaybeStartInstallerHostCheck();
        }

        /// <summary>
        /// For upgradable WinGet packages, asynchronously fetches the installer URL host for
        /// both the installed and the new version, and flags the row when the hosts differ.
        /// See issue #4617 — defense-in-depth signal that an upgrade may be redirecting the
        /// download to a different domain than the user originally trusted.
        /// </summary>
        private void MaybeStartInstallerHostCheck()
        {
            if (!Package.IsUpgradable) return;
            if (Package.Manager is not WinGet) return;
            if (Settings.Get(Settings.K.DisableInstallerHostChangeWarning)) return;

            string installedVersion = Package.VersionString;
            string newVersion = Package.NewVersionString;
            if (string.IsNullOrWhiteSpace(installedVersion) || string.IsNullOrWhiteSpace(newVersion))
                return;
            if (installedVersion == newVersion) return;

            DispatcherQueue? dispatcher = _page.DispatcherQueue
                ?? DispatcherQueue.GetForCurrentThread();
            if (dispatcher is null) return;

            _installerHostCheckCts?.Cancel();
            _installerHostCheckCts = new CancellationTokenSource();
            CancellationToken token = _installerHostCheckCts.Token;

            Task.Run(() =>
            {
                try
                {
                    if (token.IsCancellationRequested) return;
                    var oldHosts = WinGet.TryGetInstallerHostsForVersion(Package, installedVersion);
                    if (token.IsCancellationRequested) return;
                    var newHosts = WinGet.TryGetInstallerHostsForVersion(Package, newVersion);
                    if (token.IsCancellationRequested) return;

                    if (oldHosts is null || newHosts is null) return;
                    // Only flag when the two host sets are fully disjoint. If they share even
                    // one host, the publisher hasn't moved hosting — adding/removing CDN mirrors
                    // or architectures shouldn't trigger the warning.
                    if (oldHosts.Overlaps(newHosts)) return;

                    string tooltip = CoreTools.Translate(
                        "Installer host changed since the installed version.\n"
                        + "Old: {0}\n"
                        + "New: {1}\n\n"
                        + "This is usually harmless (the publisher moved hosting), "
                        + "but can also indicate a hijacked package manifest. "
                        + "Verify the new source before upgrading.",
                        string.Join(", ", oldHosts),
                        string.Join(", ", newHosts)
                    );

                    dispatcher.TryEnqueue(() =>
                    {
                        if (token.IsCancellationRequested) return;
                        InstallerHostChanged = true;
                        InstallerHostChangeTooltip = tooltip;
                        PropertyChanged?.Invoke(
                            this,
                            new PropertyChangedEventArgs(nameof(InstallerHostChanged))
                        );
                        PropertyChanged?.Invoke(
                            this,
                            new PropertyChangedEventArgs(nameof(InstallerHostChangeTooltip))
                        );
                        PropertyChanged?.Invoke(
                            this,
                            new PropertyChangedEventArgs(nameof(ShowUpgradeDownloadIcon))
                        );
                    });
                }
                catch (Exception ex)
                {
                    Logger.Warn(
                        $"Installer-host check failed for {Package.Id}: {ex.Message}"
                    );
                }
            }, token);
        }

        public void PackageItemContainer_DoubleTapped(
            object sender,
            DoubleTappedRoutedEventArgs e
        ) => _page.PackageItemContainer_DoubleTapped(sender, e);

        public void PackageItemContainer_PreviewKeyDown(object sender, KeyRoutedEventArgs e) =>
            _page.PackageItemContainer_PreviewKeyDown(sender, e);

        public void PackageItemContainer_RightTapped(object sender, RightTappedRoutedEventArgs e) =>
            _page.PackageItemContainer_RightTapped(sender, e);

        public void RightClick() => _ = RightClickAsync();

        private async Task RightClickAsync()
        {
            await _page.ShowContextMenu(this);
        }

        public void Package_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            try
            {
                if (e.PropertyName == nameof(Package.Tag))
                {
                    WhenTagHasChanged();
                    PropertyChanged?.Invoke(
                        this,
                        new PropertyChangedEventArgs(nameof(ListedOpacity))
                    );
                    PropertyChanged?.Invoke(
                        this,
                        new PropertyChangedEventArgs(nameof(AlternateIconId))
                    );
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MainIconId)));
                    PropertyChanged?.Invoke(
                        this,
                        new PropertyChangedEventArgs(nameof(AlternateIdIconVisible))
                    );
                    PropertyChanged?.Invoke(
                        this,
                        new PropertyChangedEventArgs(nameof(ListedNameTooltip))
                    );
                }
                else if (e.PropertyName == nameof(Package.IsChecked))
                {
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
                }
                else
                {
                    PropertyChanged?.Invoke(this, e);
                }
            }
            catch (COMException)
            {
                // ignore
            }
        }

        public void Dispose()
        {
            Package.PropertyChanged -= Package_PropertyChanged;
            _optionalDetailsCts.Cancel();
            _installerHostCheckCts?.Cancel();
            _installerHostCheckCts?.Dispose();
            _installerHostCheckCts = null;
        }

        /// <summary>
        /// Updates the fields that change how the item template is rendered.
        /// </summary>
        public void WhenTagHasChanged()
        {
            MainIconId = Package.Tag switch
            {
                PackageTag.Default => IconType.Id,
                PackageTag.AlreadyInstalled => IconType.Installed,
                PackageTag.IsUpgradable => IconType.Upgradable,
                PackageTag.Pinned => IconType.Pin,
                PackageTag.OnQueue => IconType.SandClock,
                PackageTag.BeingProcessed => IconType.Loading,
                PackageTag.Failed => IconType.Warning,
                PackageTag.Unavailable => IconType.Help,
                _ => throw new ArgumentException($"Unknown tag {Package.Tag}"),
            };

            AlternateIconId = Package.Tag switch
            {
                PackageTag.Default => IconType.Empty,
                PackageTag.AlreadyInstalled => IconType.Installed_Filled,
                PackageTag.IsUpgradable => IconType.Upgradable_Filled,
                PackageTag.Pinned => IconType.Pin_Filled,
                PackageTag.OnQueue => IconType.Empty,
                PackageTag.BeingProcessed => IconType.Loading_Filled,
                PackageTag.Failed => IconType.Warning_Filled,
                PackageTag.Unavailable => IconType.Empty,
                _ => throw new ArgumentException($"Unknown tag {Package.Tag}"),
            };
            AlternateIdIconVisible = AlternateIconId != IconType.Empty;

            ListedNameTooltip =
                Package.Tag switch
                {
                    PackageTag.Default => "",
                    PackageTag.AlreadyInstalled => CoreTools.Translate(
                        "This package is already installed"
                    ) + " - ",
                    PackageTag.IsUpgradable => CoreTools.Translate(
                        "This package can be upgraded to version {0}",
                        Package.GetUpgradablePackage()?.NewVersionString ?? "-1"
                    ) + " - ",
                    PackageTag.Pinned => CoreTools.Translate("Updates for this package are ignored")
                        + " - ",
                    PackageTag.OnQueue => CoreTools.Translate(
                        "This package is on the queue" + " - "
                    ),
                    PackageTag.BeingProcessed => CoreTools.Translate(
                        "This package is being processed"
                    ) + " - ",
                    PackageTag.Failed => CoreTools.Translate(
                        "An error occurred while processing this package"
                    ) + " - ",
                    PackageTag.Unavailable => CoreTools.Translate("This package is not available")
                        + " - ",
                    _ => throw new ArgumentException($"Unknown tag {Package.Tag}"),
                } + Package.Name;

            ListedOpacity = Package.Tag switch
            {
                PackageTag.Default => 1,
                PackageTag.AlreadyInstalled => 1,
                PackageTag.IsUpgradable => 1,
                PackageTag.Pinned => 1,
                PackageTag.OnQueue => .5F,
                PackageTag.BeingProcessed => .5F,
                PackageTag.Failed => 1,
                PackageTag.Unavailable => .5F,
                _ => throw new ArgumentException($"Unknown tag {Package.Tag}"),
            };
#pragma warning restore CS8524
        }

        public void UpdatePackageIcon()
        {
            if (CachedPackageIcons.TryGetValue(Package.GetHash(), out Uri? icon))
            {
                MainIconSource = new BitmapImage
                {
                    UriSource = icon,
                    DecodePixelWidth = 64,
                    DecodePixelType = DecodePixelType.Logical,
                };
                ShowCustomPackageIcon = true;
                ShowDefaultPackageIcon = false;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MainIconSource)));
            }
            else
            {
                ShowCustomPackageIcon = false;
                ShowDefaultPackageIcon = true;
            }
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(ShowCustomPackageIcon))
            );
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(ShowDefaultPackageIcon))
            );
        }
    }
}
