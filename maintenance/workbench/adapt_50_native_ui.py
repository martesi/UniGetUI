from pathlib import Path
import shutil
import sys

root = Path(sys.argv[1])
ui = root / 'src/UniGetUI'


def write(path, text):
    path.write_text(text, encoding='utf-8', newline='\n')


def replace(path, old, new, count=1):
    text = path.read_text(encoding='utf-8-sig')
    if text.count(old) != count:
        raise ValueError(f'{path}: expected {count} occurrences of {old!r}, got {text.count(old)}')
    write(path, text.replace(old, new))


# Reuse the upstream artwork in the native empty state without replacing its text.
for name in ['Empty_inbox.png', 'Trophee.png', 'launcher.png', 'maurice_penseur.png']:
    shutil.copy2(root / 'src/UniGetUI.Avalonia/Assets/Images' / name, ui / 'Assets/Images' / name)
page = ui / 'Pages/SoftwarePages/AbstractPackagesPage.xaml.cs'
replace(page, 'public string QueryBackup { get; set; } = "";', '''public string QueryBackup { get; set; } = "";

        public void ClearSearch()
        {
            QueryBackup = "";
            FilteredPackages.Query = "";
        }

        private void RefreshIllustration()
        {
            EmptyIllustration.Visibility = !Settings.Get(Settings.K.DisablePackageIllustrations)
                && BackgroundText.Visibility is Visibility.Visible && !Loader.IsLoading
                    ? Visibility.Visible : Visibility.Collapsed;
        }
''')
# The image follows the existing loading/filter state rather than duplicating it.
replace(page, '            NoPackages_BackgroundText = data.NoPackages_BackgroundText;', '''            NoPackages_BackgroundText = data.NoPackages_BackgroundText;''')
replace(page, '            InitializeComponent();', '''            InitializeComponent();
            string illustration = PAGE_ROLE is OperationType.Update ? "Trophee.png"
                : PAGE_ROLE is OperationType.Install ? "maurice_penseur.png" : "Empty_inbox.png";
            EmptyIllustration.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/Images/" + illustration));
            BackgroundText.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => RefreshIllustration());
            Loaded += (_, _) => RefreshIllustration();''')
xaml = ui / 'Pages/SoftwarePages/AbstractPackagesPage.xaml'
text = xaml.read_text()
start = text.index('            <TextBlock\n              x:Name="BackgroundText"')
end = text.index('            />', start) + len('            />')
block = text[start:end].replace('              Grid.Row="1"\n', '').replace('              Grid.Column="1"\n', '')
text = text[:start] + '''            <StackPanel VerticalAlignment="Center" HorizontalAlignment="Center" Spacing="16" Padding="16">
              <Image x:Name="EmptyIllustration" Width="160" Height="140" Stretch="Uniform" Visibility="Collapsed" />
''' + block + '\n            </StackPanel>' + text[end:]
write(xaml, text)

# Keep tray reopening predictable and release search results held by all pages.
main = ui / 'Pages/MainView.xaml.cs'
replace(main, 'public void ShowHelp(string uriAttachment = "")', '''public void ClearSearches()
        {
            DiscoverPage.ClearSearch();
            UpdatesPage.ClearSearch();
            InstalledPage.ClearSearch();
            BundlesPage.ClearSearch();
            MainTextBlock.Text = "";
        }

        public void ShowHelp(string uriAttachment = "")''')
replace(ui / 'MainWindow.xaml.cs', '                    MainContentFrame.Content = null;\n                    AppWindow.Hide();', '                    NavigationPage?.ClearSearches();\n                    MainContentFrame.Content = null;\n                    AppWindow.Hide();')
replace(main, 'new GridLength(Math.Min(maxHeight, 200))', 'new GridLength(Math.Min(maxHeight, (3 * 58) - 7))')

# Clicking a card opens the same details/log action as its live output line.
operation = ui / 'Controls/OperationWidgets/OperationControl.cs'
replace(operation, '    public void LiveLineClick() => _ = LiveLineClickAsync();', '''    public void CardTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs args)
    {
        for (var node = args.OriginalSource as Microsoft.UI.Xaml.DependencyObject;
             node is not null && !ReferenceEquals(node, sender);
             node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
            if (node is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase) return;
        args.Handled = true;
        LiveLineClick();
    }

    public void LiveLineClick() => _ = LiveLineClickAsync();''')
replace(ui / 'Pages/MainView.xaml', '            Background="{x:Bind Background, Mode=OneWay}"', '            Background="{x:Bind Background, Mode=OneWay}"\n            Tapped="{x:Bind CardTapped}"')

# Reset is irreversible. Make it explicit instead of executing on the first click.
replace(ui / 'Pages/SettingsPages/GeneralPages/General.xaml.cs', '''        private void ResetWingetUI(object sender, EventArgs e)
        {
            try''', '''        private async void ResetWingetUI(object sender, EventArgs e)
        {
            if (!await UniGetUI.Services.ConfirmationDialog.ShowAsync(CoreTools.Translate("Reset all UniGetUI settings? This cannot be undone."))) return;
            try''')

# Command profiles stay independent, with explicit copy actions and a visible hint.
optionsXaml = ui / 'Pages/DialogPages/InstallOptions_Package.xaml'
text = optionsXaml.read_text()
start = text.index('                      <widgets:TranslatedTextBlock\n                        x:Name="CustomParametersLabel1"')
end = text.index('                    </Grid>', start) + len('                    </Grid>')
text = text[:end] + '''
                    <widgets:TranslatedTextBlock Text="Install, update and uninstall arguments are independent. Copy them explicitly when they should match." TextWrapping="Wrap" />
                    <HyperlinkButton Click="CopyInstallArguments" Padding="0" HorizontalAlignment="Left">
                      <widgets:TranslatedTextBlock Text="Copy install arguments to update and uninstall" />
                    </HyperlinkButton>
''' + text[end:]
anchor = '                Name="CommandBox"'
start = text.index(anchor)
end = text.index('          </StackPanel>', start)
text = text[:end] + '''            <Button Click="OpenManualConsole" HorizontalAlignment="Left">
              <widgets:TranslatedTextBlock Text="Open in a terminal" />
            </Button>
''' + text[end:]
write(optionsXaml, text)
options = ui / 'Pages/DialogPages/InstallOptions_Package.xaml.cs'
replace(options, '        private readonly OperationType Operation;', '''        private void CopyInstallArguments(object sender, RoutedEventArgs args)
        {
            CustomParameters2.Text = CustomParameters1.Text;
            CustomParameters3.Text = CustomParameters1.Text;
        }

        private async void OpenManualConsole(object sender, RoutedEventArgs args)
        {
            if (!string.IsNullOrWhiteSpace(CommandBox.Text))
                await UniGetUI.Services.ManualInstallHelper.LaunchManualAsync(CommandBox.Text);
        }

        private readonly OperationType Operation;''')
