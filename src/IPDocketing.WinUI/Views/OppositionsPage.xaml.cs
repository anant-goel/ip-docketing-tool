using IPDocketing.Core.Models;
using Microsoft.UI.Xaml.Controls;

namespace IPDocketing.WinUI.Views;

public sealed partial class OppositionsPage : Page
{
    public OppositionsPage()
    {
        InitializeComponent();
        try { Load(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"OppositionsPage.Load failed: {ex}"); }
    }

    private void Load()
    {
        var rows = App.Oppositions.GetAll().Select(o => new OppositionRow(o)).ToList();
        OppositionList.ItemsSource = rows;
        CountText.Text = $"{rows.Count} oppositions";
    }

    private async void AddOpposition_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        await ShowOppositionDialog(null);
    }

    private async void EditOpposition_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is Button { Tag: int id })
        {
            var opposition = App.Oppositions.GetById(id);
            if (opposition is not null) await ShowOppositionDialog(opposition);
        }
    }

    private async void DeleteOpposition_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int id }) return;

        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Delete opposition?",
            Content = "This can't be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        App.Oppositions.Delete(id);
        Load();
    }

    private async System.Threading.Tasks.Task ShowOppositionDialog(Opposition? existing)
    {
        var tmNumberBox = new TextBox { Header = "Trademark number", PlaceholderText = "e.g. 4567890", Text = existing?.TrademarkNumber ?? "" };
        var markBox = new TextBox { Header = "Mark details", Text = existing?.MarkDetails ?? "" };
        var partyBox = new TextBox { Header = "Opposing party", Text = existing?.OpposingParty ?? "" };
        var directionPicker = new ComboBox
        {
            Header = "Direction",
            ItemsSource = Enum.GetValues<OppositionDirection>(),
            SelectedIndex = existing is null ? 0 : (int)existing.Direction
        };
        var statusPicker = new ComboBox
        {
            Header = "Status",
            ItemsSource = Enum.GetValues<OppositionStatus>(),
            SelectedIndex = existing is null ? 0 : (int)existing.Status
        };
        var hearingDateBox = new DatePicker { Header = "Hearing date" };
        if (existing?.HearingDate is not null) hearingDateBox.Date = existing.HearingDate.Value;

        var teamMembers = App.Team.GetActive();
        var assigneePicker = new ComboBox
        {
            Header = "Assign to",
            ItemsSource = teamMembers,
            DisplayMemberPath = "Name",
            SelectedIndex = existing?.AssignedToId is null ? -1 : teamMembers.FindIndex(t => t.Id == existing.AssignedToId)
        };

        var panel = new StackPanel { Spacing = 10, Width = 380 };
        foreach (var control in new Microsoft.UI.Xaml.FrameworkElement[]
                 { tmNumberBox, markBox, partyBox, directionPicker, statusPicker, hearingDateBox, assigneePicker })
            panel.Children.Add(control);

        var scroll = new ScrollViewer { Content = panel, MaxHeight = 480 };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = existing is null ? "New opposition" : "Edit opposition",
            Content = scroll,
            PrimaryButtonText = existing is null ? "Create" : "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (string.IsNullOrWhiteSpace(tmNumberBox.Text)) return;

        var assignedToId = (assigneePicker.SelectedItem as TeamMember)?.Id;
        // Note: DatePicker.Date always has a value (defaults to today if the
        // user never touches it) — there's no built-in "no date" state, so
        // this will set today's date if left untouched rather than staying
        // null. A "no hearing scheduled" checkbox would be needed to make
        // this a true optional field; not added yet.
        var hearingDate = hearingDateBox.Date.DateTime;

        if (existing is null)
        {
            App.Oppositions.Add(new Opposition
            {
                TrademarkNumber = tmNumberBox.Text,
                MarkDetails = markBox.Text,
                OpposingParty = partyBox.Text,
                Direction = (OppositionDirection)directionPicker.SelectedItem,
                Status = (OppositionStatus)statusPicker.SelectedItem,
                HearingDate = hearingDate,
                AssignedToId = assignedToId
            });
        }
        else
        {
            existing.TrademarkNumber = tmNumberBox.Text;
            existing.MarkDetails = markBox.Text;
            existing.OpposingParty = partyBox.Text;
            existing.Direction = (OppositionDirection)directionPicker.SelectedItem;
            existing.Status = (OppositionStatus)statusPicker.SelectedItem;
            existing.HearingDate = hearingDate;
            existing.AssignedToId = assignedToId;
            App.Oppositions.Update(existing);
        }

        Load();
    }

    public sealed class OppositionRow
    {
        public int Id { get; }
        public string TrademarkNumber { get; }
        public string MarkDetails { get; }
        public string OpposingParty { get; }
        public string Direction { get; }
        public string Status { get; }
        public string HearingDate { get; }
        public string AssignedTo { get; }

        public OppositionRow(Opposition o)
        {
            Id = o.Id;
            TrademarkNumber = o.TrademarkNumber;
            MarkDetails = o.MarkDetails;
            OpposingParty = o.OpposingParty;
            Direction = o.Direction == OppositionDirection.FiledByUs ? "Filed by us" : "Filed against us";
            Status = o.Status.ToString();
            HearingDate = o.HearingDate?.ToString("dd MMM yyyy") ?? "Not scheduled";
            AssignedTo = o.AssignedTo?.Name ?? "Unassigned";
        }
    }
}
