using Microsoft.UI.Xaml.Controls;
using UniGetUI.Core.Tools;
using UniGetUI.Pages.DialogPages;

namespace UniGetUI.Services;

internal static class ConfirmationDialog
{
    public static async Task<bool> ShowAsync(string message)
    {
        var dialog = DialogFactory.Create_AsWindow(true);
        dialog.Title = CoreTools.Translate("Are you sure?");
        dialog.Content = new TextBlock { Text = message, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap, MaxWidth = 600 };
        dialog.PrimaryButtonText = CoreTools.Translate("Continue");
        dialog.CloseButtonText = CoreTools.Translate("Cancel");
        dialog.DefaultButton = ContentDialogButton.Close;
        return await DialogHelper.ShowDialogAsync(dialog) is ContentDialogResult.Primary;
    }
}
