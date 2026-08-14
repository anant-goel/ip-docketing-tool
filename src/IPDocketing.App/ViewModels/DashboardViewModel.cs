using System.Collections.ObjectModel;
using IPDocketing.Core.Models;

namespace IPDocketing.App.ViewModels;

public class DashboardViewModel : ViewModelBase
{
    public int OverdueCount { get; }
    public int UpcomingCount { get; }
    public int PendingCount { get; }
    public int CompletedCount { get; }

    public ObservableCollection<Deadline> TopDeadlines { get; } = new();

    public DashboardViewModel()
    {
        var all = App.Deadlines.GetAll();
        var now = DateTime.Now;

        OverdueCount = all.Count(d => d.GetUrgency(now) == UrgencyLevel.Overdue);
        UpcomingCount = all.Count(d => d.GetUrgency(now) == UrgencyLevel.Upcoming);
        PendingCount = all.Count(d => d.GetUrgency(now) == UrgencyLevel.PendingResponse);
        CompletedCount = all.Count(d => d.GetUrgency(now) == UrgencyLevel.Completed);

        foreach (var d in all.Where(d => d.Status != DeadlineStatus.Completed)
                              .OrderBy(d => d.DueDate)
                              .Take(8))
        {
            TopDeadlines.Add(d);
        }
    }
}
