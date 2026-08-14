using System.Windows;
using IPDocketing.App.ViewModels;
using IPDocketing.Core.Models;

// UseWindowsForms is enabled (for the tray icon on MainWindow), which pulls
// System.Windows.Forms into scope alongside WPF's System.Windows.* namespaces.
// Several type names exist in both (UserControl, DragEventArgs, DataFormats,
// MessageBox), so those specific ones are aliased explicitly to the WPF
// version rather than left to a plain `using`, which would stay ambiguous.
using UserControl = System.Windows.Controls.UserControl;
using DragEventArgs = System.Windows.DragEventArgs;
using DataFormats = System.Windows.DataFormats;
using MessageBox = System.Windows.MessageBox;

namespace IPDocketing.App.Views;

public partial class DocumentsView : UserControl
{
    public DocumentsView()
    {
        InitializeComponent();
    }

    private void UserControl_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
        if (DataContext is not DocumentsViewModel) return;

        var firstMatter = App.Matters.GetAll().FirstOrDefault();
        if (firstMatter is null)
        {
            MessageBox.Show("Create a matter first before filing documents against it.", "IP Docketing",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        foreach (var file in files)
        {
            var doc = new Document
            {
                MatterId = firstMatter.Id,
                FileName = System.IO.Path.GetFileName(file),
                FilePath = file,
                DocumentType = "General",
                UploadedDate = DateTime.UtcNow
            };
            App.Database.Documents.Add(doc);
        }
        App.Database.SaveChanges();
        App.Audit.Log("Create", "Document", 0, $"Filed {files.Length} document(s) via drag-and-drop.");

        // Re-run the view model's load logic by replacing the DataContext instance.
        DataContext = new DocumentsViewModel();
    }
}
