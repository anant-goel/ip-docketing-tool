using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IPDocketing.WinUI.Services;

/// <summary>
/// Shows a long text report in a dialog, and always writes it to a file as
/// well.
///
/// WHY THIS EXISTS
///
/// Every diagnostic report in this app was displayed as a read-only multi-line
/// TextBox with a fixed Height, wrapped in a ScrollViewer, inside a
/// ContentDialog. That is three nested scrolling containers, and it does not
/// render: a TextBox already hosts its own ScrollViewer, so putting it inside
/// another one with a fixed height gives the inner control an unconstrained
/// measure and it collapses to roughly one visible line. The result was
/// dialogs that showed a title, one line of text, and a large blank area -
/// which is exactly what "there is no log in the self-test" looks like.
///
/// Every report I have added over several rounds - the self-test, the name
/// search, the raw-HTML capture, the download log - was invisible for this
/// reason. The information was being produced correctly and then thrown away
/// at the last step.
///
/// The fix is a single ScrollViewer containing a selectable TextBlock, with no
/// fixed height on the inner element so it measures naturally.
///
/// The report is ALSO written to disk every time. A dialog is a rendering
/// path that can fail; a file on disk is not. If the dialog is ever blank
/// again, the file is still there and still complete.
/// </summary>
public static class TextReportDialog
{
    /// <summary>Where reports are written, so they survive a failed dialog.</summary>
    public static string ReportsDirectory =>
        System.IO.Path.Combine(App.AppDataDirectory, "Reports");

    /// <summary>
    /// Displays the report and writes it to Reports\{slug}.txt.
    /// Returns the file path, or null if writing failed.
    /// </summary>
    public static async Task<string?> ShowAsync(
        XamlRoot xamlRoot, string title, string body, string fileSlug)
    {
        string? savedPath = null;

        // File first. If the dialog fails to render, this still exists.
        try
        {
            System.IO.Directory.CreateDirectory(ReportsDirectory);
            savedPath = System.IO.Path.Combine(
                ReportsDirectory,
                $"{fileSlug}_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            await System.IO.File.WriteAllTextAsync(savedPath, body, System.Text.Encoding.UTF8);
        }
        catch
        {
            savedPath = null;
        }

        // One ScrollViewer, one TextBlock, no fixed height on the inner
        // element. TextBlock does not carry its own scroll host, so it
        // measures to its full content and the ScrollViewer scrolls it.
        var text = new TextBlock
        {
            Text = body,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            LineHeight = 17
        };

        var scroller = new ScrollViewer
        {
            Content = text,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            // Height on the SCROLLER, never on the content.
            Height = 420,
            Width = 620
        };

        var footer = new TextBlock
        {
            Text = savedPath is null
                ? "(Could not write a copy to disk.)"
                : $"Also saved to: {savedPath}",
            FontSize = 11,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
            IsTextSelectionEnabled = true
        };

        var panel = new StackPanel { Spacing = 0 };
        panel.Children.Add(scroller);
        panel.Children.Add(footer);

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = title,
            Content = panel,
            PrimaryButtonText = "Copy all",
            SecondaryButtonText = savedPath is null ? "" : "Open file",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Primary)
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(body);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        }
        else if (result == ContentDialogResult.Secondary && savedPath is not null)
        {
            try
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(savedPath);
                await Windows.System.Launcher.LaunchFileAsync(file);
            }
            catch
            {
                // Opening in Notepad is a convenience; the path is on screen.
            }
        }

        return savedPath;
    }
}
