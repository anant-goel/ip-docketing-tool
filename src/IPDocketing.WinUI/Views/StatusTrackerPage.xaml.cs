using System.IO;
using IPDocketing.Core.Models;
using IPDocketing.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;
using Windows.UI;

namespace IPDocketing.WinUI.Views;

/// <summary>
/// docx section 5 - Trademark Status Tracker, including opposition status.
///
/// The print button writes the dossier out as a self-contained HTML sheet in
/// %LocalAppData%\IPDocketing\Print and hands it to the shell, which opens it
/// in the default browser with the print dialog already up. WinUI 3's
/// PrintManager needs package identity and a window-handle interop path this
/// deliberately unpackaged app doesn't have, so this is the route that actually
/// produces paper - and it gives Save-as-PDF for free.
/// </summary>
public sealed partial class StatusTrackerPage : Page
{
    private StatusTrackerService.StatusDossier? _dossier;

    public StatusTrackerPage()
    {
        InitializeComponent();
        try { LoadMatterPicker(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"StatusTrackerPage init failed: {ex}"); }
    }

    /// <summary>
    /// Accepts a matter id passed by another page (the search results double-click)
    /// and opens straight onto that mark. The trademarks-only filter is dropped if
    /// the requested matter isn't a trademark, so a patent handed over from
    /// elsewhere still resolves instead of silently showing nothing.
    /// </summary>
    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not int matterId) return;

        try
        {
            var matter = App.Matters.GetById(matterId);
            if (matter is not null && matter.Type != MatterType.Trademark)
            {
                TrademarksOnlyToggle.IsChecked = false;
                LoadMatterPicker();
            }

            foreach (var choice in (MatterPicker.ItemsSource as List<MatterChoice>) ?? new List<MatterChoice>())
            {
                if (choice.Id != matterId) continue;
                MatterPicker.SelectedItem = choice;
                return;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"StatusTrackerPage.OnNavigatedTo failed: {ex}");
        }
    }

    private void LoadMatterPicker()
    {
        var trademarksOnly = TrademarksOnlyToggle.IsChecked == true;
        var matters = App.Matters.GetAll()
            .Where(m => !trademarksOnly || m.Type == MatterType.Trademark)
            .OrderBy(m => m.MatterNumber, StringComparer.OrdinalIgnoreCase)
            .Select(m => new MatterChoice(m.Id, BuildLabel(m)))
            .ToList();

        MatterPicker.ItemsSource = matters;
        MatterPicker.DisplayMemberPath = nameof(MatterChoice.Label);

        if (matters.Count == 0)
        {
            ShowEmpty();
            MatterPicker.PlaceholderText = trademarksOnly
                ? "No trademark matters yet - untick the filter or add one on the Matters page"
                : "No matters yet - add one on the Matters page";
        }
    }

    private static string BuildLabel(Matter m)
    {
        var appNumber = string.IsNullOrWhiteSpace(m.ApplicationNumber) ? "no app#" : m.ApplicationNumber;
        return $"{m.MatterNumber} · {m.Title} · {appNumber}";
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        // Fires during InitializeComponent before the picker exists.
        if (MatterPicker is null) return;
        LoadMatterPicker();
    }

    private void MatterPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MatterPicker.SelectedItem is not MatterChoice choice) return;
        LoadDossier(choice.Id);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        var selectedId = (MatterPicker.SelectedItem as MatterChoice)?.Id;
        LoadMatterPicker();
        if (selectedId is null) return;

        foreach (var item in (MatterPicker.ItemsSource as List<MatterChoice>) ?? new List<MatterChoice>())
        {
            if (item.Id != selectedId.Value) continue;
            MatterPicker.SelectedItem = item;
            return;
        }
    }

    private void LoadDossier(int matterId)
    {
        _dossier = App.StatusTracker.GetDossier(matterId);
        if (_dossier is null)
        {
            ShowEmpty();
            return;
        }

        var m = _dossier.Matter;
        DossierScroll.Visibility = Visibility.Visible;
        EmptyState.Visibility = Visibility.Collapsed;
        PrintButton.IsEnabled = true;
        CopyButton.IsEnabled = true;

        MarkTitleText.Text = m.Title;
        StatusBadgeText.Text = m.Status.ToString();

        AlertBadge.Visibility = string.IsNullOrWhiteSpace(m.PortalAlert)
            ? Visibility.Collapsed
            : Visibility.Visible;
        AlertBadgeText.Text = m.PortalAlert ?? string.Empty;

        OppositionBadge.Visibility = _dossier.HasOpenOpposition ? Visibility.Visible : Visibility.Collapsed;

        FactList.ItemsSource = new List<Fact>
        {
            new("Matter number", m.MatterNumber),
            new("Client", m.ClientName),
            new("Application number", m.ApplicationNumber),
            new("Registration number", m.GrantNumber),
            new("Proprietor", m.ProprietorName),
            new("Class", m.NiceClass),
            new("Mark type", m.MarkType?.ToString()),
            new("Attorney of record", m.AttorneyOfRecord),
            new("State", m.State),
            new("Jurisdiction", m.Country),
            new("Filing date", Format(m.FilingDate)),
            new("Registration date", Format(m.RegistrationDate)),
            new("Renewal due", Format(m.RenewalDueDate)),
            new("Assigned to", m.AssignedTo?.Name ?? "Unassigned"),
            new("Next deadline", _dossier.NextDeadline is null
                ? "None open"
                : $"{_dossier.NextDeadline.Description} — {_dossier.NextDeadline.DueDate:dd MMM yyyy}"),
        };

        var events = _dossier.Events.Select(e => new EventRow(e)).ToList();
        EventList.ItemsSource = events;
        EventsEmptyText.Visibility = events.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var today = DateTime.Today;
        var deadlines = _dossier.Deadlines.Select(d => new DeadlineRow(d, today)).ToList();
        DeadlineList.ItemsSource = deadlines;
        DeadlinesEmptyText.Visibility = deadlines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var oppositions = _dossier.Oppositions.Select(o => new OppositionRow(o)).ToList();
        OppositionList.ItemsSource = oppositions;
        OppositionsEmptyText.Visibility = oppositions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var documents = _dossier.Documents
            .OrderBy(d => d.DocumentType, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(d => d.UploadedDate)
            .Select(d => new DocumentRow(d))
            .ToList();
        DocumentList.ItemsSource = documents;
        DocumentsEmptyText.Visibility = documents.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        OpenDocumentButton.IsEnabled = false;
    }

    private void ShowEmpty()
    {
        _dossier = null;
        DossierScroll.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Visible;
        PrintButton.IsEnabled = false;
        CopyButton.IsEnabled = false;
    }

    private async void Print_Click(object sender, RoutedEventArgs e)
    {
        if (_dossier is null) return;

        try
        {
            var directory = Path.Combine(App.AppDataDirectory, "Print");
            Directory.CreateDirectory(directory);

            var safeName = string.Concat(_dossier.Matter.MatterNumber
                .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var path = Path.Combine(directory, $"status_{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}.html");

            await File.WriteAllTextAsync(path, App.StatusTracker.BuildPrintableHtml(_dossier),
                System.Text.Encoding.UTF8);

            var launched = await Launcher.LaunchUriAsync(new Uri(new Uri("file:///"), path.Replace('\\', '/')));
            if (!launched)
            {
                // Fall back to opening it as a file, which uses a different
                // shell path and works where the file: URI association doesn't.
                var file = await StorageFile.GetFileFromPathAsync(path);
                await Launcher.LaunchFileAsync(file);
            }

            App.Audit.Log("Print", "Matter", _dossier.Matter.Id,
                $"Status sheet generated at {path}.");
        }
        catch (Exception ex)
        {
            await ShowMessage("Could not produce the status sheet", ex.Message);
        }
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_dossier is null) return;

        var package = new DataPackage();
        package.SetText(App.StatusTracker.BuildPlainText(_dossier));
        Clipboard.SetContent(package);

        await ShowMessage("Copied", "The full status is on the clipboard as plain text.");
    }

    private void DocumentList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        OpenDocumentButton.IsEnabled = DocumentList.SelectedItem is DocumentRow;

    private async void OpenDocument_Click(object sender, RoutedEventArgs e)
    {
        if (DocumentList.SelectedItem is not DocumentRow row) return;

        if (!File.Exists(row.FilePath))
        {
            await ShowMessage("File not found",
                "The original file has been moved, renamed or deleted. The docket record is still here, but the file it points at is gone.");
            return;
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(row.FilePath);
            if (!await Launcher.LaunchFileAsync(file))
                await ShowMessage("Could not open", "Windows has no app associated with this file type.");
        }
        catch (Exception ex)
        {
            await ShowMessage("Could not open", ex.Message);
        }
    }

    private async System.Threading.Tasks.Task ShowMessage(string title, string content)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = content,
            CloseButtonText = "OK"
        };
        await dialog.ShowAsync();
    }

    private static string Format(DateTime? value) => value?.ToString("dd MMM yyyy") ?? "-";

    private sealed record MatterChoice(int Id, string Label);

    public sealed record Fact(string Label, string ValueText)
    {
        public string Value => string.IsNullOrWhiteSpace(ValueText) ? "-" : ValueText;
    }

    public sealed class EventRow
    {
        public string Date { get; }
        public string Type { get; }
        public string Notes { get; }

        public EventRow(Event e)
        {
            Date = e.EventDate.ToString("dd MMM yyyy");
            Type = e.Type.ToString();
            Notes = e.Notes ?? "";
        }
    }

    public sealed class DeadlineRow
    {
        public string Description { get; }
        public string RuleLabel { get; }
        public string DueDate { get; }
        public string NominalDate { get; }
        public string StatusLabel { get; }
        public SolidColorBrush UrgencyBrush { get; }

        public DeadlineRow(Deadline d, DateTime today)
        {
            Description = d.Description;
            RuleLabel = d.CountryRule?.Citation ?? d.RuleVersionApplied ?? "Manually docketed";
            DueDate = d.DueDate.ToString("dd MMM yyyy");
            NominalDate = $"Nominal {d.NominalDueDate:dd MMM yyyy}";

            (StatusLabel, UrgencyBrush) = d.Status switch
            {
                DeadlineStatus.Completed => ("Completed", Brush(53, 208, 113)),
                DeadlineStatus.Waived => ("Waived", Brush(153, 160, 179)),
                _ => d.GetUrgency(today) switch
                {
                    UrgencyLevel.Overdue => ("Overdue", Brush(255, 91, 82)),
                    UrgencyLevel.Upcoming => ("Upcoming", Brush(255, 170, 36)),
                    _ => ("Open", Brush(138, 125, 255))
                }
            };
        }

        private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Color.FromArgb(255, r, g, b));
    }

    public sealed class OppositionRow
    {
        public string Headline { get; }
        public string OpposingParty { get; }
        public string Direction { get; }
        public string Dates { get; }
        public string Status { get; }

        public OppositionRow(Opposition o)
        {
            Headline = string.IsNullOrWhiteSpace(o.MarkDetails)
                ? $"TM {o.TrademarkNumber}"
                : $"TM {o.TrademarkNumber} — {o.MarkDetails}";
            OpposingParty = string.IsNullOrWhiteSpace(o.OpposingParty)
                ? "Opposing party not recorded"
                : o.OpposingParty;
            Direction = o.Direction == OppositionDirection.FiledByUs ? "Filed by us" : "Filed against us";
            Status = o.Status.ToString();

            var parts = new List<string>();
            if (o.NoticeDate is not null) parts.Add($"Notice {o.NoticeDate:dd MMM yyyy}");
            if (o.CounterStatementDueDate is not null) parts.Add($"Counter-stmt {o.CounterStatementDueDate:dd MMM yyyy}");
            if (o.HearingDate is not null) parts.Add($"Hearing {o.HearingDate:dd MMM yyyy}");
            Dates = parts.Count == 0 ? "No dates recorded" : string.Join(" · ", parts);
        }
    }

    public sealed class DocumentRow
    {
        public string Category { get; }
        public string FileName { get; }
        public string Uploaded { get; }
        public string VersionLabel { get; }
        public string FilePath { get; }

        public DocumentRow(Document d)
        {
            Category = string.IsNullOrWhiteSpace(d.DocumentType) ? "General" : d.DocumentType;
            FileName = d.FileName;
            Uploaded = d.UploadedDate.ToLocalTime().ToString("dd MMM yyyy");
            VersionLabel = $"v{d.Version}";
            FilePath = d.FilePath;
        }
    }
}
