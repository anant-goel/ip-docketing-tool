using IPDocketing.Core.Models;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace IPDocketing.WinUI.Views;

public sealed partial class DashboardPage : Page
{
    public DashboardPage()
    {
        InitializeComponent();
        // A data-load exception here (constructor time) can otherwise abort page
        // construction entirely, leaving Frame.Content blank instead of this page's
        // static XAML chrome. Catching keeps navigation successful either way.
        try { Load(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"DashboardPage.Load failed: {ex}"); }
    }

    private void AddMatter_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        // Matters is the register that owns "new matter" creation; route there
        // rather than duplicating that flow on the dashboard.
        if (Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(this) is Microsoft.UI.Xaml.Controls.Frame frame)
            frame.Navigate(typeof(MattersPage));
    }

    private void AssignTeamMember_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(this) is Microsoft.UI.Xaml.Controls.Frame frame)
            frame.Navigate(typeof(MattersPage));
    }

    private void Load()
    {
        var all = App.Deadlines.GetAll().ToList();
        var overdue = App.Deadlines.GetOverdue();
        var upcoming = App.Deadlines.GetUpcoming(14);
        var asOf = DateTime.Today;
        var pending = all.Where(d => d.GetUrgency(asOf) == UrgencyLevel.PendingResponse).ToList();
        var completed = all.Where(d => d.Status == DeadlineStatus.Completed).ToList();

        OverdueText.Text = overdue.Count.ToString();
        UpcomingText.Text = upcoming.Count.ToString();
        PendingText.Text = pending.Count.ToString();
        CompletedText.Text = completed.Count.ToString();

        var top = all
            .Where(d => d.Status != DeadlineStatus.Completed && d.Status != DeadlineStatus.Waived)
            .OrderBy(d => d.DueDate)
            .Take(8)
            .Select(d => new DeadlineRow(d, asOf))
            .ToList();
        DeadlineList.ItemsSource = top;
    }

    public sealed class DeadlineRow
    {
        public string Description { get; }
        public string MatterNumber { get; }
        public string DueDateText { get; }
        public string StatusLabel { get; }
        public SolidColorBrush UrgencyBrush { get; }

        public DeadlineRow(Deadline d, DateTime asOf)
        {
            Description = d.Description ?? "";
            MatterNumber = d.Matter?.MatterNumber ?? "";
            DueDateText = d.DueDate.ToString("d");
            var u = d.GetUrgency(asOf);
            (StatusLabel, UrgencyBrush) = u switch
            {
                UrgencyLevel.Overdue => ("Overdue", Brush(255, 69, 58)),
                UrgencyLevel.Upcoming => ("Upcoming", Brush(255, 159, 10)),
                UrgencyLevel.PendingResponse => ("Pending", Brush(94, 92, 230)),
                _ => ("Open", Brush(153, 160, 179))
            };
        }

        private static SolidColorBrush Brush(byte r, byte g, byte b) =>
            new(Color.FromArgb(255, r, g, b));
    }
}
