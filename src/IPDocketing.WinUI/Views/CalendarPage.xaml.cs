using IPDocketing.Core.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace IPDocketing.WinUI.Views;

public sealed partial class CalendarPage : Page
{
    private List<Deadline> _deadlines;
    private bool _loaded;

    public CalendarPage()
    {
        InitializeComponent();
        try
        {
            _deadlines = App.Deadlines.GetAll();
        }
        catch (Exception ex)
        {
            _deadlines = new List<Deadline>();
            System.Diagnostics.Debug.WriteLine($"CalendarPage.GetAll failed: {ex}");
        }
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        LoadCalendar(selectToday: true);
    }

    private void LoadCalendar(bool selectToday)
    {
        _deadlines = App.Deadlines.GetAll();
        var today = DateTime.Today;
        OverdueCountText.Text = _deadlines.Count(d =>
            d.Status != DeadlineStatus.Completed && d.Status != DeadlineStatus.Waived && d.DueDate.Date < today).ToString();
        UpcomingCountText.Text = _deadlines.Count(d =>
            d.Status != DeadlineStatus.Completed && d.Status != DeadlineStatus.Waived
            && d.DueDate.Date >= today && d.DueDate.Date <= today.AddDays(7)).ToString();
        CompletedCountText.Text = _deadlines.Count(d =>
            d.Status == DeadlineStatus.Completed || d.Status == DeadlineStatus.Waived).ToString();

        if (selectToday)
        {
            DeadlineCalendar.SelectedDates.Clear();
            DeadlineCalendar.SelectedDates.Add(new DateTimeOffset(today));
            DeadlineCalendar.SetDisplayDate(new DateTimeOffset(today));
        }
        else
        {
            ShowDate(DeadlineCalendar.SelectedDates.FirstOrDefault(DateTimeOffset.Now).Date);
        }
    }

    private void DeadlineCalendar_DayItemChanging(
        CalendarView sender,
        CalendarViewDayItemChangingEventArgs args)
    {
        var date = args.Item.Date.Date;
        var colors = _deadlines
            .Where(d => d.DueDate.Date == date)
            .Take(10)
            .Select(GetUrgencyColor)
            .ToList();
        args.Item.SetDensityColors(colors);
    }

    private void DeadlineCalendar_SelectedDatesChanged(
        CalendarView sender,
        CalendarViewSelectedDatesChangedEventArgs args)
    {
        if (sender.SelectedDates.Count > 0)
            ShowDate(sender.SelectedDates[0].Date);
    }

    private void ShowDate(DateTime date)
    {
        var rows = _deadlines
            .Where(d => d.DueDate.Date == date)
            .OrderBy(d => d.DueDate)
            .Select(d => new CalendarDeadlineRow(d, DateTime.Today))
            .ToList();
        SelectedDateText.Text = date.ToString("dddd, dd MMMM yyyy");
        SelectedDateSummary.Text = rows.Count == 0
            ? "No deadlines on this date."
            : $"{rows.Count} deadline{(rows.Count == 1 ? string.Empty : "s")} on this date.";
        SelectedDeadlineList.ItemsSource = rows;
        MarkCompleteButton.IsEnabled = false;
    }

    private void Today_Click(object sender, RoutedEventArgs e) => LoadCalendar(selectToday: true);

    private void ShowOverdue_Click(object sender, RoutedEventArgs e)
    {
        var rows = _deadlines
            .Where(d => d.Status != DeadlineStatus.Completed && d.Status != DeadlineStatus.Waived
                        && d.DueDate.Date < DateTime.Today)
            .OrderBy(d => d.DueDate)
            .Select(d => new CalendarDeadlineRow(d, DateTime.Today))
            .ToList();
        SelectedDateText.Text = "Overdue deadlines";
        SelectedDateSummary.Text = rows.Count == 0
            ? "Nothing is overdue."
            : $"{rows.Count} item{(rows.Count == 1 ? string.Empty : "s")} need attention.";
        SelectedDeadlineList.ItemsSource = rows;
        MarkCompleteButton.IsEnabled = false;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => LoadCalendar(selectToday: false);

    private void SelectedDeadlineList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        MarkCompleteButton.IsEnabled = SelectedDeadlineList.SelectedItem is CalendarDeadlineRow { CanComplete: true };

    private void MarkComplete_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDeadlineList.SelectedItem is not CalendarDeadlineRow { CanComplete: true } row) return;
        App.Deadlines.MarkComplete(row.Id);
        LoadCalendar(selectToday: false);
        App.MainWindow.RefreshReminders(showSystemNotification: false);
    }

    private static Color GetUrgencyColor(Deadline deadline) => deadline.GetUrgency(DateTime.Today) switch
    {
        UrgencyLevel.Overdue => Colors.Red,
        UrgencyLevel.Upcoming => Colors.Orange,
        UrgencyLevel.Completed => Colors.LimeGreen,
        _ => Colors.MediumSlateBlue
    };

    public sealed class CalendarDeadlineRow
    {
        public int Id { get; }
        public string Description { get; }
        public string Matter { get; }
        public string DueDate { get; }
        public string Urgency { get; }
        public SolidColorBrush UrgencyBrush { get; }
        public bool CanComplete { get; }

        public CalendarDeadlineRow(Deadline deadline, DateTime today)
        {
            Id = deadline.Id;
            Description = deadline.Description;
            Matter = deadline.Matter?.MatterNumber ?? "Unlinked matter";
            DueDate = deadline.DueDate.ToString("dd MMM");
            CanComplete = deadline.Status != DeadlineStatus.Completed && deadline.Status != DeadlineStatus.Waived;
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
