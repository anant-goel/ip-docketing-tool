using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using IPDocketing.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace IPDocketing.App.ViewModels;

public class DocumentsViewModel : ViewModelBase
{
    public ObservableCollection<Document> Documents { get; } = new();
    public ICommand ImportCommand { get; }
    public ICommand RefreshCommand { get; }

    public DocumentsViewModel()
    {
        ImportCommand = new RelayCommand(Import);
        RefreshCommand = new RelayCommand(Load);
        Load();
    }

    private void Load()
    {
        Documents.Clear();
        foreach (var d in App.Database.Documents.Include(d => d.Matter).OrderByDescending(d => d.UploadedDate))
            Documents.Add(d);
    }

    private void Import()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import PTO notice or matter document",
            Filter = "Supported files (*.pdf;*.docx;*.tif;*.png;*.jpg)|*.pdf;*.docx;*.tif;*.png;*.jpg|All files (*.*)|*.*",
            Multiselect = false
        };

        if (dialog.ShowDialog() != true) return;

        var firstMatter = App.Matters.GetAll().FirstOrDefault();
        if (firstMatter is null)
        {
            System.Windows.MessageBox.Show("Create a matter first before filing documents against it.",
                "IP Docketing", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }

        var doc = new Document
        {
            MatterId = firstMatter.Id,
            FileName = System.IO.Path.GetFileName(dialog.FileName),
            FilePath = dialog.FileName,
            DocumentType = "PTO Notice",
            UploadedDate = DateTime.UtcNow,
            // OCR is not wired to an engine in this scaffold - swap in Tesseract
            // (or a cloud OCR service) behind IOcrService and set OcrText/OcrProcessed here.
            OcrProcessed = false
        };

        App.Database.Documents.Add(doc);
        App.Database.SaveChanges();
        App.Audit.Log("Create", "Document", doc.Id, $"Filed '{doc.FileName}' against {firstMatter.MatterNumber}.");

        Load();
    }
}
