using System.IO;
using IPDocketing.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;
using WinRT.Interop;

namespace IPDocketing.WinUI.Views;

public sealed partial class DocumentsPage : Page
{
    public DocumentsPage()
    {
        InitializeComponent();
        try
        {
            LoadDocuments();
        }
        catch (Exception ex)
        {
            // Keep the page's chrome (title, buttons, empty card) on screen even if
            // the initial document query fails, instead of leaving Frame.Content blank.
            ShowStatus($"Documents could not be loaded: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private void LoadDocuments()
    {
        var matterNumbers = App.Matters.GetAll().ToDictionary(m => m.Id, m => m.MatterNumber);
        var rows = App.Database.Documents
            .AsEnumerable()
            .OrderByDescending(d => d.UploadedDate)
            .Select(d => new DocumentRow(d, d.MatterId.HasValue
                ? matterNumbers.GetValueOrDefault(d.MatterId.Value, "Unlinked")
                : "Unlinked"))
            .ToList();
        DocumentList.ItemsSource = rows;
        DocumentList.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyState.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        OpenFileButton.IsEnabled = false;
    }

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var matters = App.Matters.GetAll().OrderBy(m => m.MatterNumber).ToList();
        if (matters.Count == 0)
        {
            ShowStatus("Create a matter before adding documents.", InfoBarSeverity.Warning);
            return;
        }

        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                ViewMode = PickerViewMode.List
            };
            foreach (var extension in new[] { ".pdf", ".doc", ".docx", ".tif", ".tiff", ".png", ".jpg", ".jpeg", ".txt", ".eml", ".msg" })
                picker.FileTypeFilter.Add(extension);

            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
            var files = await picker.PickMultipleFilesAsync();
            if (files.Count == 0) return;

            var matterPicker = new ComboBox
            {
                Header = "File under matter",
                ItemsSource = matters.Select(m => new MatterChoice(m.Id, $"{m.MatterNumber} · {m.Title}")).ToList(),
                SelectedIndex = 0,
                MinWidth = 380
            };
            var typePicker = new ComboBox
            {
                Header = "Document type",
                ItemsSource = new[] { "General", "PTO Notice", "Correspondence", "Evidence", "Draft" },
                SelectedIndex = 0,
                MinWidth = 380
            };
            var dialogContent = new StackPanel { Spacing = 12 };
            dialogContent.Children.Add(matterPicker);
            dialogContent.Children.Add(typePicker);

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = $"Add {files.Count} file{(files.Count == 1 ? string.Empty : "s")}",
                Content = dialogContent,
                PrimaryButtonText = "Add files",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary
                || matterPicker.SelectedItem is not MatterChoice matter)
                return;

            var documentType = typePicker.SelectedItem?.ToString() ?? "General";
            var latestVersions = App.Database.Documents
                .AsEnumerable()
                .Where(d => d.MatterId == matter.Id)
                .GroupBy(d => d.FileName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Max(d => d.Version), StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                var nextVersion = latestVersions.GetValueOrDefault(file.Name) + 1;
                latestVersions[file.Name] = nextVersion;
                App.Database.Documents.Add(new Document
                {
                    MatterId = matter.Id,
                    FileName = file.Name,
                    FilePath = file.Path,
                    DocumentType = documentType,
                    Version = nextVersion,
                    UploadedDate = DateTime.UtcNow,
                    OcrProcessed = false
                });
            }

            App.Database.SaveChanges();
            App.Audit.Log("Create", "Document", 0,
                $"Filed {files.Count} document(s) against {matter.Label}.");
            LoadDocuments();
            ShowStatus($"Added {files.Count} file{(files.Count == 1 ? string.Empty : "s")}.",
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus($"Files could not be added: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private async void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (DocumentList.SelectedItem is not DocumentRow row) return;
        if (!File.Exists(row.FilePath))
        {
            ShowStatus("The original file has been moved or is no longer available.", InfoBarSeverity.Warning);
            return;
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(row.FilePath);
            if (!await Launcher.LaunchFileAsync(file))
                ShowStatus("Windows could not find an app to open this file.", InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            ShowStatus($"The file could not be opened: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        LoadDocuments();
        ShowStatus("Document list refreshed.", InfoBarSeverity.Informational);
    }

    private void DocumentList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        OpenFileButton.IsEnabled = DocumentList.SelectedItem is DocumentRow;

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        DocumentInfoBar.Message = message;
        DocumentInfoBar.Severity = severity;
        DocumentInfoBar.IsOpen = true;
    }

    private async void DeleteDocument_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int id }) return;

        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Delete document record?",
            Content = "This removes the record from the docket. The underlying file on disk is not deleted.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        var doc = App.Database.Documents.Find(id);
        if (doc is not null)
        {
            App.Database.Documents.Remove(doc);
            App.Database.SaveChanges();
        }
        LoadDocuments();
    }

    public sealed class DocumentRow
    {
        public int Id { get; }
        public string FileName { get; }
        public string Matter { get; }
        public string Type { get; }
        public string Uploaded { get; }
        public string OcrStatus { get; }
        public SolidColorBrush OcrBrush { get; }
        public string FilePath { get; }

        public DocumentRow(Document document, string matter)
        {
            Id = document.Id;
            FileName = document.FileName;
            FilePath = document.FilePath;
            Matter = $"{matter} · version {document.Version}";
            Type = document.DocumentType;
            Uploaded = document.UploadedDate.ToLocalTime().ToString("dd MMM yyyy");
            OcrStatus = document.OcrProcessed ? "OCR ready" : "OCR pending";
            OcrBrush = document.OcrProcessed
                ? new SolidColorBrush(Color.FromArgb(255, 53, 208, 113))
                : new SolidColorBrush(Color.FromArgb(255, 255, 170, 36));
        }
    }

    private sealed record MatterChoice(int Id, string Label)
    {
        public override string ToString() => Label;
    }
}
