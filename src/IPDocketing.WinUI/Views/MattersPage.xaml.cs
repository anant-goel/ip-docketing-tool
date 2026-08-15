using IPDocketing.Core.Models;
using Microsoft.UI.Xaml.Controls;

namespace IPDocketing.WinUI.Views;

public sealed partial class MattersPage : Page
{
    public MattersPage()
    {
        InitializeComponent();
        try { LoadMatters(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"MattersPage.LoadMatters failed: {ex}"); }
    }

    private void LoadMatters()
    {
        var rows = App.Matters.GetAll().Select(m => new MatterRow(m)).ToList();
        MatterList.ItemsSource = rows;
        CountText.Text = $"{rows.Count} matters";
    }

    public sealed class MatterRow
    {
        public string Number { get; }
        public string Title { get; }
        public string Client { get; }
        public string Type { get; }
        public string Country { get; }
        public string Status { get; }
        public string FilingDate { get; }

        public MatterRow(Matter matter)
        {
            Number = matter.MatterNumber;
            Title = matter.Title;
            Client = matter.ClientName;
            Type = matter.Type.ToString();
            Country = matter.Country;
            Status = matter.Status.ToString();
            FilingDate = matter.FilingDate?.ToString("dd MMM yyyy") ?? "Not filed";
        }
    }
}
