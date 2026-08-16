using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IPDocketing.WinUI.Views;

public sealed partial class ReportsPage : Page
{
    public ReportsPage() => InitializeComponent();

    private void ExportDeadlines_Click(object sender, RoutedEventArgs e)
    {
        var csv = new StringBuilder("MatterNumber,ApplicationNumber,Description,NominalDueDate,DueDate,Kind,Status,ResponsibleUser\n");
        foreach (var deadline in App.Deadlines.GetAll())
        {
            csv.AppendLine($"{Csv(deadline.Matter?.MatterNumber)},{Csv(deadline.Matter?.ApplicationNumber)},{Csv(deadline.Description)}," +
                           $"{deadline.NominalDueDate:yyyy-MM-dd},{deadline.DueDate:yyyy-MM-dd}," +
                           $"{deadline.Kind},{deadline.Status},{Csv(deadline.ResponsibleUser)}");
        }
        Save(csv, "deadlines");
    }

    private void ExportMatters_Click(object sender, RoutedEventArgs e)
    {
        var csv = new StringBuilder("MatterNumber,ApplicationNumber,Title,Client,Type,Country,Status,FilingDate," +
                                     "Proprietor,AttorneyOfRecord,State,MarkType,NiceClass,AssignedTo\n");
        foreach (var matter in App.Matters.GetAll())
        {
            csv.AppendLine($"{Csv(matter.MatterNumber)},{Csv(matter.ApplicationNumber)},{Csv(matter.Title)},{Csv(matter.ClientName)}," +
                           $"{matter.Type},{Csv(matter.Country)},{matter.Status},{matter.FilingDate:yyyy-MM-dd}," +
                           $"{Csv(matter.ProprietorName)},{Csv(matter.AttorneyOfRecord)},{Csv(matter.State)}," +
                           $"{matter.MarkType},{Csv(matter.NiceClass)},{Csv(matter.AssignedTo?.Name)}");
        }
        Save(csv, "matters");
    }

    private void ExportOppositions_Click(object sender, RoutedEventArgs e)
    {
        var csv = new StringBuilder("TrademarkNumber,MarkDetails,OpposingParty,Direction,Status,NoticeDate,HearingDate,AssignedTo\n");
        foreach (var opposition in App.Oppositions.GetAll())
        {
            csv.AppendLine($"{Csv(opposition.TrademarkNumber)},{Csv(opposition.MarkDetails)},{Csv(opposition.OpposingParty)}," +
                           $"{opposition.Direction},{opposition.Status},{opposition.NoticeDate:yyyy-MM-dd},{opposition.HearingDate:yyyy-MM-dd}," +
                           $"{Csv(opposition.AssignedTo?.Name)}");
        }
        Save(csv, "oppositions");
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
