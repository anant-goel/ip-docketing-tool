using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IPDocketing.WinUI.Views;

public sealed partial class ReportsPage : Page
{
    public ReportsPage() => InitializeComponent();

    private void ExportDeadlines_Click(object sender, RoutedEventArgs e)
    {
        var csv = new StringBuilder("MatterNumber,Description,DueDate,Kind,Status,ResponsibleUser\n");
        foreach (var deadline in App.Deadlines.GetAll())
        {
            csv.AppendLine($"{Csv(deadline.Matter?.MatterNumber)},{Csv(deadline.Description)},{deadline.DueDate:yyyy-MM-dd}," +
                           $"{deadline.Kind},{deadline.Status},{Csv(deadline.ResponsibleUser)}");
        }
        Save(csv, "deadlines");
    }

    private void ExportMatters_Click(object sender, RoutedEventArgs e)
    {
        var csv = new StringBuilder("MatterNumber,Title,Client,Type,Country,Status,FilingDate\n");
        foreach (var matter in App.Matters.GetAll())
        {
            csv.AppendLine($"{Csv(matter.MatterNumber)},{Csv(matter.Title)},{Csv(matter.ClientName)}," +
                           $"{matter.Type},{Csv(matter.Country)},{matter.Status},{matter.FilingDate:yyyy-MM-dd}");
        }
        Save(csv, "matters");
    }

    private void Save(StringBuilder content, string reportName)
    {
        try
        {
            var directory = Path.Combine(App.AppDataDirectory, "Exports");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{reportName}_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            File.WriteAllText(path, content.ToString(), Encoding.UTF8);
            App.Audit.Log("Export", "Report", 0, $"Exported {reportName} to {path}.");
            ExportStatusText.Text = $"Saved {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            ExportStatusText.Text = $"Export failed: {ex.Message}";
        }
    }

    private static string Csv(string? value) => $"\"{(value ?? string.Empty).Replace("\"", "\"\"")}\"";
}
