using System.IO;
using System.Text;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

namespace IPDocketing.App.ViewModels;

public class ReportsViewModel : ViewModelBase
{
    public ICommand ExportDeadlinesCsvCommand { get; }
    public ICommand ExportMattersCsvCommand { get; }

    public string? LastExportPath { get; private set; }

    public ReportsViewModel()
    {
        ExportDeadlinesCsvCommand = new RelayCommand(ExportDeadlinesCsv);
        ExportMattersCsvCommand = new RelayCommand(ExportMattersCsv);
    }

    private void ExportDeadlinesCsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine("MatterNumber,Description,DueDate,Kind,Status,ResponsibleUser");
        foreach (var d in App.Deadlines.GetAll())
        {
            sb.AppendLine($"{Csv(d.Matter?.MatterNumber)},{Csv(d.Description)},{d.DueDate:yyyy-MM-dd}," +
                           $"{d.Kind},{d.Status},{Csv(d.ResponsibleUser)}");
        }
        SaveAndNotify(sb.ToString(), "deadlines_export.csv");
    }

    private void ExportMattersCsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine("MatterNumber,Title,Client,Type,Country,Status,FilingDate");
        foreach (var m in App.Matters.GetAll())
        {
            sb.AppendLine($"{Csv(m.MatterNumber)},{Csv(m.Title)},{Csv(m.ClientName)},{m.Type},{m.Country}," +
                           $"{m.Status},{m.FilingDate:yyyy-MM-dd}");
        }
        SaveAndNotify(sb.ToString(), "matters_export.csv");
    }

    private static string Csv(string? s) => $"\"{(s ?? string.Empty).Replace("\"", "\"\"")}\"";

    private void SaveAndNotify(string content, string suggestedName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = suggestedName,
            Filter = "CSV files (*.csv)|*.csv"
        };

        if (dialog.ShowDialog() != true) return;

        File.WriteAllText(dialog.FileName, content);
        LastExportPath = dialog.FileName;

        App.Audit.Log("Export", "Report", 0, $"Exported {suggestedName} to {dialog.FileName}.");

        System.Windows.MessageBox.Show($"Exported to:\n{dialog.FileName}", "Export complete",
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
    }
}
