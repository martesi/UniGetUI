using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using UniGetUI.PackageOperations;

namespace UniGetUI.Pages.DialogPages;

public sealed partial class OperationLiveLogPage : Page
{
    public event EventHandler<EventArgs>? Close;
    private readonly Paragraph _paragraph = new() { LineHeight = 4.8 };
    private readonly List<(Run Run, AbstractOperation.LineType Type)> _lines = [];
    private bool _lastLineWasProgress;

    public OperationLiveLogPage(AbstractOperation operation)
    {
        InitializeComponent();
        TextBlock.Blocks.Add(_paragraph);
        foreach (var line in operation.GetOutput()) AppendLine(line);
        Loaded += (_, _) => ScrollToEnd();
        ActualThemeChanged += (_, _) =>
        {
            foreach (var (run, type) in _lines) ApplyColor(run, type);
        };
    }

    private void AppendLine((string Text, AbstractOperation.LineType Type) line)
    {
        if (_lastLineWasProgress && _lines.Count > 0)
        {
            _paragraph.Inlines.RemoveAt(_paragraph.Inlines.Count - 1);
            _lines.RemoveAt(_lines.Count - 1);
        }
        _lastLineWasProgress = line.Type is not (AbstractOperation.LineType.Information or AbstractOperation.LineType.VerboseDetails or AbstractOperation.LineType.Error);
        Run run = new() { Text = line.Text + "\n" };
        ApplyColor(run, line.Type);
        _paragraph.Inlines.Add(run);
        _lines.Add((run, line.Type));
    }

    private static void ApplyColor(Run run, AbstractOperation.LineType type)
    {
        string? resource = type switch
        {
            AbstractOperation.LineType.Error => "SystemFillColorCriticalBrush",
            AbstractOperation.LineType.VerboseDetails => "SystemFillColorNeutralBrush",
            _ => null,
        };
        if (resource is null) run.ClearValue(TextElement.ForegroundProperty);
        else run.Foreground = (Brush)Application.Current.Resources[resource];
    }

    public void AddLine_ThreadSafe(object? sender, (string, AbstractOperation.LineType) line) =>
        MainApp.Dispatcher.TryEnqueue(() =>
        {
            bool follow = ScrollBar.VerticalOffset >= ScrollBar.ScrollableHeight - 24;
            AppendLine(line);
            if (follow) ScrollToEnd();
        });

    private void ScrollToEnd()
    {
        ScrollBar.UpdateLayout();
        ScrollBar.ChangeView(null, ScrollBar.ScrollableHeight, null, disableAnimation: true);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close?.Invoke(this, EventArgs.Empty);
}
