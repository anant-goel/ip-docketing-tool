using IPDocketing.Core.Models;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace IPDocketing.WinUI.Views;

public sealed partial class DeadlinesPage : Page
{
    public DeadlinesPage()
    {
        InitializeComponent();
        LoadDeadlines();
    }

    private void LoadDeadlines()
    {
        var today = DateTime.Today;
        var rows = App.Deadlines.GetAll().Select(d => new DeadlineRow(d, today)).ToList();
        DeadlineList.ItemsSource = rows;
        CountText.Text = $"{rows.Count} deadlines";
    }

    public sealed class DeadlineRow
    {
        public string Description { get; }
        public string Matter { get; }
        public string DueDate { get; }
        public string NominalDate { get; }
        public string Owner { get; }
        public string Urgency { get; }
        public SolidColorBrush UrgencyBrush { get; }

        public DeadlineRow(Deadline deadline, DateTime today)
        {
            Description = deadline.Description;
            Matter = deadline.Matter?.MatterNumber ?? "Unlinked matter";
            DueDate = deadline.DueDate.ToString("ddd, dd MMM yyyy");
            NominalDate = $"Nominal {deadline.NominalDueDate:dd MMM yyyy}";
            Owner = string.IsNullOrWhiteSpace(deadline.ResponsibleUser) ? "Unassigned" : deadline.ResponsibleUser;

            (Urgency, UrgencyBrush) = deadline.GetUrgency(today) switch
            {
                UrgencyLevel.Overdue => ("Overdue", Brush(255, 91, 82)),
                UrgencyLevel.Upcoming => ("Upcoming", Brush(255, 170, 36)),
                UrgencyLevel.Completed => ("Completed", Brush(53, 208, 113)),
                _ => ("Pending", Brush(138, 125, 255))
            };
        }

        private static SolidColorBrush Brush(byte r, byte g, byte b) =>
            new(Color.FromArgb(255, r, g, b));
    }
}
