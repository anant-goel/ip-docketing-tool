using IPDocketing.Core.Models;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace IPDocketing.WinUI.Views;

public sealed partial class DocumentsPage : Page
{
    public DocumentsPage()
    {
        InitializeComponent();
        LoadDocuments();
    }

    private void LoadDocuments()
    {
        var matterNumbers = App.Matters.GetAll().ToDictionary(m => m.Id, m => m.MatterNumber);
        var rows = App.Database.Documents
            .AsEnumerable()
            .OrderByDescending(d => d.UploadedDate)
            .Select(d => new DocumentRow(d, matterNumbers.GetValueOrDefault(d.MatterId, "Unlinked")))
            .ToList();
        DocumentList.ItemsSource = rows;
    }

    public sealed class DocumentRow
    {
        public string FileName { get; }
        public string Matter { get; }
        public string Type { get; }
        public string Uploaded { get; }
        public string OcrStatus { get; }
        public SolidColorBrush OcrBrush { get; }

        public DocumentRow(Document document, string matter)
        {
            FileName = document.FileName;
            Matter = $"{matter} · version {document.Version}";
            Type = document.DocumentType;
            Uploaded = document.UploadedDate.ToLocalTime().ToString("dd MMM yyyy");
            OcrStatus = document.OcrProcessed ? "OCR ready" : "OCR pending";
            OcrBrush = document.OcrProcessed
                ? new SolidColorBrush(Color.FromArgb(255, 53, 208, 113))
                : new SolidColorBrush(Color.FromArgb(255, 255, 170, 36));
        }
    }
}
