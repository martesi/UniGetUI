using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using UniGetUI.Core.Tools;

namespace UniGetUI.Pages.SettingsPages;

internal static partial class SettingsSearch
{
    private sealed record Entry(Type Page, string Label, string Key)
    {
        public override string ToString() => CoreTools.Translate(Label);
    }

    private static string? _pendingLabel;

    public static void Attach(Panel content, Action<Type> navigate)
    {
        AutoSuggestBox search = new()
        {
            PlaceholderText = CoreTools.Translate("Search settings"),
            QueryIcon = new SymbolIcon(Symbol.Find),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        content.Children.Insert(0, search);
        search.TextChanged += (_, args) =>
        {
            if (args.Reason is not AutoSuggestionBoxTextChangeReason.UserInput) return;
            var words = search.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            search.ItemsSource = words.Length == 0 ? Array.Empty<Entry>() : Entries.Where(entry => words.All(word =>
                (entry.Label + " " + entry.Key + " " + entry.ToString()).Contains(word, StringComparison.CurrentCultureIgnoreCase))).Take(30).ToArray();
        };
        search.SuggestionChosen += (_, args) => search.Text = ((Entry)args.SelectedItem).ToString();
        search.QuerySubmitted += (_, args) =>
        {
            var entry = args.ChosenSuggestion as Entry ?? (search.ItemsSource as IEnumerable<Entry>)?.FirstOrDefault();
            if (entry is null) return;
            _pendingLabel = entry.ToString();
            navigate(entry.Page);
        };
    }

    public static void Highlight(Page page)
    {
        if (_pendingLabel is not { } label) return;
        _pendingLabel = null;
        void Reveal()
        {
            var target = Descendants(page).OfType<TextBlock>().FirstOrDefault(t => t.Text.Equals(label, StringComparison.CurrentCultureIgnoreCase));
            if (target is null) return;
            target.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true });
            for (DependencyObject? parent = target; parent is not null; parent = VisualTreeHelper.GetParent(parent))
                if (parent is Control control && control.Focus(FocusState.Programmatic)) break;
        }
        if (page.IsLoaded) page.DispatcherQueue.TryEnqueue(Reveal);
        else
        {
            void Loaded(object sender, RoutedEventArgs args)
            {
                page.Loaded -= Loaded;
                page.DispatcherQueue.TryEnqueue(Reveal);
            }
            page.Loaded += Loaded;
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
