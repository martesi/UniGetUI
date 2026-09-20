from pathlib import Path
import sys

root = Path(sys.argv[1])
ui = root / 'src/UniGetUI'


def replace(path, old, new, count=1):
    text = path.read_text(encoding='utf-8-sig')
    if text.count(old) != count:
        raise ValueError(f'{path}: expected {count} occurrences of {old!r}, got {text.count(old)}')
    path.write_text(text.replace(old, new), encoding='utf-8', newline='\n')


replace(ui / 'App.xaml.cs', '                InitializeComponent();\n                ApplyThemeToApp();', '                InitializeComponent();\n                UniGetUI.Services.UiFontPolicy.Apply();\n                ApplyThemeToApp();')
replace(ui / 'Pages/SettingsPages/GeneralPages/Interface_P.xaml.cs', '            ThemeSelector.ShowAddedItems();', '''            ThemeSelector.ShowAddedItems();
            if (Scroller.Content is Microsoft.UI.Xaml.Controls.Panel cards)
            {
                var font = new UniGetUI.Interface.Widgets.CheckboxCard
                {
                    SettingName = Settings.K.UseSystemUIFont,
                    Text = "Use the font configured in Windows instead of the default interface font",
                    WarningText = "Restart UniGetUI to apply this change",
                };
                font.StateChanged += (_, _) => RestartRequired?.Invoke(this, EventArgs.Empty);
                cards.Children.Insert(0, font);
            }''')
