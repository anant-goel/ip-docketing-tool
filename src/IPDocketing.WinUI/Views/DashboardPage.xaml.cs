using IPDocketing.Core.Models;
using IPDocketing.Core.Services;
using Windows.ApplicationModel.DataTransfer;
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

    private List<TeamNotificationService.TeamDigest> _digests = new();

    /// <summary>
    /// docx section 1 - the automated deadline notification for internal team
    /// members. Building the digests is automatic and happens on every dashboard
    /// load; this button just recomputes on demand.
    ///
    /// It stops short of sending. There is no SMTP host, no Graph token and no
    /// service account anywhere in this app, so nothing can leave the machine
    /// unattended. Each row gets Copy (paste anywhere) and Email (opens the
    /// default mail client pre-filled). Windows toast reminders, which DO fire
    /// on their own, are a separate mechanism - see MainWindow.RefreshReminders.
    /// </summary>
    private void NotifyTeam_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => LoadDigests();

    private void LoadDigests()
    {
        try
        {
            _digests = App.TeamNotifications.BuildDigests();
            NotifySummaryText.Text = App.TeamNotifications.BuildSummaryLine(_digests);
            DigestList.ItemsSource = _digests
                .Select((d, index) => new DigestRow(index, d))
                .ToList();
        }
        catch (Exception ex)
        {
            NotifySummaryText.Text = $"Could not build the digests: {ex.Message}";
            DigestList.ItemsSource = null;
        }
    }

    private void CopyDigest_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int index }) return;
        if (index < 0 || index >= _digests.Count) return;

        var package = new DataPackage();
        package.SetText(App.TeamNotifications.BuildDigestText(_digests[index]));
        Clipboard.SetContent(package);
        NotifySummaryText.Text = $"Digest for {_digests[index].RecipientName} copied to the clipboard.";
    }

    private async void EmailDigest_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int index }) return;
        if (index < 0 || index >= _digests.Count) return;

        var digest = _digests[index];
        var uri = App.TeamNotifications.BuildMailtoUri(digest);
        if (uri is null)
        {
            NotifySummaryText.Text = $"{digest.RecipientName} has no email address on file - copy the digest instead.";
            return;
        }

        try
        {
            if (await Windows.System.Launcher.LaunchUriAsync(new Uri(uri)))
            {
                NotifySummaryText.Text = $"Opened a draft to {digest.RecipientName}. Review it and send.";
                App.Audit.Log("Notify", "TeamMember", digest.TeamMemberId ?? 0,
                    $"Deadline digest drafted for {digest.RecipientName} " +
                    $"({digest.OverdueCount} overdue, {digest.ApproachingCount} approaching).");
            }
            else
            {
                NotifySummaryText.Text = "Windows has no default mail client configured - copy the digest instead.";
            }
        }
        catch (Exception ex)
        {
            NotifySummaryText.Text = $"Could not open a mail draft: {ex.Message}";
        }
    }

    public sealed class DigestRow
    {
        public int Key { get; }
        public string Name { get; }
        public string EmailLabel { get; }
        public string Summary { get; }
        public bool CanEmail { get; }

        public DigestRow(int key, TeamNotificationService.TeamDigest digest)
        {
            Key = key;
            Name = digest.RecipientName;
            EmailLabel = digest.CanEmail ? digest.RecipientEmail! : "No email on file";
            CanEmail = digest.CanEmail;

            var parts = new List<string>();
            if (digest.OverdueCount > 0) parts.Add($"{digest.OverdueCount} overdue");
            if (digest.ApproachingCount > 0) parts.Add($"{digest.ApproachingCount} approaching");

            var nearest = digest.Notices.FirstOrDefault();
            Summary = string.Join(" · ", parts) +
                      (nearest is null ? "" : $" — next: {nearest.Description} ({nearest.UrgencyLabel})");
        }
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

        OppositionsText.Text = App.Oppositions.GetAll()
            .Count(o => o.Status != OppositionStatus.Decided && o.Status != OppositionStatus.Withdrawn && o.Status != OppositionStatus.Settled)
            .ToString();
        WatchAlertsText.Text = App.Watch.GetAll().Count.ToString();
        UnassignedText.Text = App.Matters.GetAll().Count(m => m.AssignedToId == null).ToString();

        var top = all
            .Where(d => d.Status != DeadlineStatus.Completed && d.Status != DeadlineStatus.Waived)
            .OrderBy(d => d.DueDate)
            .Take(8)
            .Select(d => new DeadlineRow(d, asOf))
            .ToList();
        DeadlineList.ItemsSource = top;

        LoadDigests();
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
