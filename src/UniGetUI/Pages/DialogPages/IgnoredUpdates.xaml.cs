using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.Classes.Packages.Classes;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Managers.WingetManager;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageEngine.PackageLoader;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace UniGetUI.Interface
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class IgnoredUpdatesManager : Page
    {
        public event EventHandler? Close;
        private readonly ObservableCollection<IgnoredPackageEntry> ignoredPackages = [];

        public IgnoredUpdatesManager()
        {
            InitializeComponent();
            IgnoredUpdatesList.ItemsSource = ignoredPackages;
            Unloaded += (_, _) =>
            {
                foreach (var entry in ignoredPackages)
                    entry.Detach();
            };
            UpdateData();
        }

        private void UpdateData()
        {
            Dictionary<string, IPackageManager> ManagerNameReference = [];

            foreach (IPackageManager Manager in PEInterface.Managers)
            {
                ManagerNameReference.Add(Manager.Name.ToLower(), Manager);
            }

            foreach (var entry in ignoredPackages)
                entry.Detach();
            ignoredPackages.Clear();

            var rawIgnoredPackages = IgnoredUpdatesDatabase.GetDatabase();

            foreach (var (ignoredId, version) in rawIgnoredPackages)
            {
                int separator = ignoredId.IndexOf('\\');
                string packageId = separator < 0 ? ignoredId : ignoredId[(separator + 1)..];
                IPackageManager? manager = PEInterface.WinGet; // Manager by default
                if (ManagerNameReference.ContainsKey(ignoredId.Split("\\")[0]))
                {
                    manager = ManagerNameReference[ignoredId.Split("\\")[0]];
                }

                if (manager is null)
                    continue;

                ignoredPackages.Add(
                    new IgnoredPackageEntry(
                        ignoredId,
                        packageId,
                        version,
                        manager,
                        ignoredPackages,
                        () => { DispatcherQueue.TryEnqueue(UpdateData); }
                    )
                );
            }
        }

        /*private async void IgnoredUpdatesList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (IgnoredUpdatesList.SelectedItem is IgnoredPackageEntry package)
            {
                await package.RemoveFromIgnoredUpdates();
            }
        }*/

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var entry in ignoredPackages)
                entry.Detach();
            Close?.Invoke(this, EventArgs.Empty);
        }

        private void YesResetButton_Click(object sender, RoutedEventArgs e) =>
            _ = _yesResetButton_Click();

        private async Task _yesResetButton_Click()
        {
            foreach (IgnoredPackageEntry package in ignoredPackages.ToArray())
            {
                await package.RemoveFromIgnoredUpdatesAsync();
            }
            ConfirmResetFlyout.Hide();
        }

        private void NoResetButton_Click(object sender, RoutedEventArgs e)
        {
            ConfirmResetFlyout.Hide();
        }
    }

    public class IgnoredPackageEntry : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public string Id { get; }
        public string Name { get; }
        public string Version { get; }
        public string NewVersion { get; }
        public IPackageManager Manager { get; }
        public string UpdateToolTip => CoreTools.Translate("Update {0} once, keeping its updates ignored", Name);
        public Visibility UpdateVisibility =>
            !_isUpdating && IgnoredUpdatesDatabase.GetAvailableUpdate(_ignoredId) is not null
                ? Visibility.Visible
                : Visibility.Collapsed;

        private readonly string _ignoredId;
        private readonly Action _refresh;
        private UpdatePackageOperation? _operation;
        private bool _isUpdating;
        private bool _detached;
        private int _operationFinished;
        private ObservableCollection<IgnoredPackageEntry> List { get; }

        public IgnoredPackageEntry(
            string ignoredId,
            string id,
            string version,
            IPackageManager manager,
            ObservableCollection<IgnoredPackageEntry> list,
            Action refresh
        )
        {
            _ignoredId = ignoredId;
            _refresh = refresh;
            Id = id;

            if (manager is WinGet && id.Contains('.'))
                Name = String.Join(' ', id.Split('.')[1..]);
            else
                Name = CoreTools.FormatAsName(id);

            if (version == "*")
            {
                Version = CoreTools.Translate("All versions");
            }
            else
            {
                Version = version;
            }

            string CurrentVersion =
                InstalledPackagesLoader.Instance.GetPackageForId(id)?.VersionString ?? "Unknown";

            if (
                IgnoredUpdatesDatabase.GetAvailableUpdate(_ignoredId) is { } package
            )
            {
                NewVersion = CurrentVersion + " \u27a4 " + package.NewVersionString;
            }
            else if (CurrentVersion != "Unknown")
            {
                NewVersion = CoreTools.Translate("Up to date") + $" ({CurrentVersion})";
                ;
            }
            else
            {
                NewVersion = CoreTools.Translate("Unknown");
            }

            Manager = manager;
            List = list;

            if (FindRunningUpdate() is { } running)
                Track(running);
        }

        private UpdatePackageOperation? FindRunningUpdate() =>
            MainApp.Operations._operationList
                .Select(control => control.Operation)
                .OfType<UpdatePackageOperation>()
                .FirstOrDefault(operation =>
                    (operation.Status is OperationStatus.InQueue or OperationStatus.Running)
                    && IgnoredUpdatesDatabase.GetIgnoredIdForPackage(operation.Package) == _ignoredId
                );

        public void UpdateOnce() => _ = UpdateOnceAsync();

        private async Task UpdateOnceAsync()
        {
            if (_isUpdating)
                return;

            if (FindRunningUpdate() is { } running)
            {
                Track(running);
                return;
            }

            if (IgnoredUpdatesDatabase.GetAvailableUpdate(_ignoredId) is not { } package)
            {
                RequestRefresh();
                return;
            }

            _isUpdating = true;
            PropertyChanged?.Invoke(this, new(nameof(UpdateVisibility)));
            try
            {
                if (await MainApp.Operations.Update(package, keepUpdatesPaused: true) is UpdatePackageOperation operation)
                    Track(operation);
                else
                    RequestRefresh();
            }
            catch (Exception ex)
            {
                Logger.Error("Could not update the ignored package:");
                Logger.Error(ex);
                RequestRefresh();
            }
        }

        private void Track(UpdatePackageOperation operation)
        {
            if (_detached)
                return;
            _operation = operation;
            _operationFinished = 0;
            _isUpdating = true;
            PropertyChanged?.Invoke(this, new(nameof(UpdateVisibility)));
            operation.OperationFinished += OnOperationFinished;
            if (operation.Status is OperationStatus.Succeeded or OperationStatus.Failed or OperationStatus.Canceled)
                OnOperationFinished(operation, EventArgs.Empty);
        }

        private async void OnOperationFinished(object? sender, EventArgs e)
        {
            if (sender is not UpdatePackageOperation operation || Interlocked.Exchange(ref _operationFinished, 1) == 1)
                return;

            operation.OperationFinished -= OnOperationFinished;
            try
            {
                await operation.SuccessHandled;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex);
            }
            if (ReferenceEquals(_operation, operation))
            {
                _operation = null;
                RequestRefresh();
            }
        }

        private void RequestRefresh()
        {
            if (!_detached)
                _refresh();
        }

        public void Detach()
        {
            _detached = true;
            var operation = _operation;
            _operation = null;
            if (operation is null)
                return;
            operation.OperationFinished -= OnOperationFinished;
        }

        public void RemoveFromIgnoredUpdates() => _ = RemoveFromIgnoredUpdatesAsync();

        public async Task RemoveFromIgnoredUpdatesAsync()
        {
            await Task.Run(() => IgnoredUpdatesDatabase.Remove(_ignoredId));

            // If possible, add the package to the software updates tab again
            if (
                UpgradablePackagesLoader.Instance.IgnoredPackages.TryRemove(
                    Id,
                    out IPackage? nativePackage
                )
                && nativePackage.NewVersionString != nativePackage.VersionString
            )
            {
                await UpgradablePackagesLoader.Instance.AddForeign(nativePackage);
            }

            foreach (IPackage package in InstalledPackagesLoader.Instance.Packages)
            {
                if (Manager == package.Manager && package.Id == Id)
                {
                    package.SetTag(PackageTag.Default);
                    break;
                }
            }

            Detach();
            List.Remove(this);
        }
    }
}
