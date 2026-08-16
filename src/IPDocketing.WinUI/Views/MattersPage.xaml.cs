using IPDocketing.Core.Models;
using Microsoft.UI.Xaml.Controls;

namespace IPDocketing.WinUI.Views;

public sealed partial class MattersPage : Page
{
    public MattersPage()
    {
        InitializeComponent();
        try { LoadMatters(App.Matters.GetAll()); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"MattersPage.LoadMatters failed: {ex}"); }
    }

    private void LoadMatters(List<Matter> matters)
    {
        var rows = matters.Select(m => new MatterRow(m)).ToList();
        MatterList.ItemsSource = rows;
        CountText.Text = $"{rows.Count} matters";
    }

    private void Search_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        IEnumerable<Matter> results = App.Matters.GetAll();

        var mark = MarkSearchBox.Text?.Trim();
        if (!string.IsNullOrEmpty(mark))
            results = results.Where(m => m.Title.Contains(mark, StringComparison.OrdinalIgnoreCase));

        var proprietor = ProprietorSearchBox.Text?.Trim();
        if (!string.IsNullOrEmpty(proprietor))
            results = results.Where(m => m.ProprietorName != null &&
                m.ProprietorName.Contains(proprietor, StringComparison.OrdinalIgnoreCase));

        var attorney = AttorneySearchBox.Text?.Trim();
        if (!string.IsNullOrEmpty(attorney))
            results = results.Where(m => m.AttorneyOfRecord != null &&
                m.AttorneyOfRecord.Contains(attorney, StringComparison.OrdinalIgnoreCase));

        var state = StateSearchBox.Text?.Trim();
        if (!string.IsNullOrEmpty(state))
            results = results.Where(m => m.State != null &&
                m.State.Contains(state, StringComparison.OrdinalIgnoreCase));

        LoadMatters(results.ToList());
    }

    private async void AddMatter_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        await ShowMatterDialog(null);
    }

    private async void EditMatter_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is Button { Tag: int id })
        {
            var matter = App.Matters.GetById(id);
            if (matter is not null) await ShowMatterDialog(matter);
        }
    }

    private async void DeleteMatter_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int id }) return;

        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Delete matter?",
            Content = "This also removes its linked deadlines, events and documents. This can't be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        App.Matters.Delete(id);
        LoadMatters(App.Matters.GetAll());
    }

    /// <summary>Shared dialog for both Add (existing == null) and Edit (existing != null).</summary>
    private async System.Threading.Tasks.Task ShowMatterDialog(Matter? existing)
    {
        var numberBox = new TextBox { Header = "Matter number", PlaceholderText = "e.g. TM-2026-001", Text = existing?.MatterNumber ?? "" };
        var titleBox = new TextBox { Header = "Mark / title", Text = existing?.Title ?? "" };
        var clientBox = new TextBox { Header = "Client name", Text = existing?.ClientName ?? "" };
        var typePicker = new ComboBox
        {
            Header = "Type",
            ItemsSource = Enum.GetValues<MatterType>(),
            SelectedIndex = existing is null ? 1 : (int)existing.Type
        };
        var proprietorBox = new TextBox { Header = "Proprietor", Text = existing?.ProprietorName ?? "" };
        var attorneyBox = new TextBox { Header = "Attorney of record", Text = existing?.AttorneyOfRecord ?? "" };
        var stateBox = new TextBox { Header = "State", Text = existing?.State ?? "" };
        var classBox = new TextBox { Header = "Nice class", PlaceholderText = "e.g. 25", Text = existing?.NiceClass ?? "" };
        var markTypePicker = new ComboBox
        {
            Header = "Mark type",
            ItemsSource = Enum.GetValues<MarkType>(),
            SelectedIndex = existing?.MarkType is null ? 0 : (int)existing.MarkType.Value
        };
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
                 { numberBox, titleBox, clientBox, typePicker, proprietorBox, attorneyBox, stateBox, classBox, markTypePicker, assigneePicker })
            panel.Children.Add(control);

        var scroll = new ScrollViewer { Content = panel, MaxHeight = 480 };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = existing is null ? "Add matter" : "Edit matter",
            Content = scroll,
            PrimaryButtonText = existing is null ? "Create" : "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (string.IsNullOrWhiteSpace(numberBox.Text) || string.IsNullOrWhiteSpace(titleBox.Text)) return;

        var assignedToId = (assigneePicker.SelectedItem as TeamMember)?.Id;

        if (existing is null)
        {
            App.Matters.Add(new Matter
            {
                MatterNumber = numberBox.Text,
                Title = titleBox.Text,
                ClientName = clientBox.Text,
                Type = (MatterType)typePicker.SelectedItem,
                ProprietorName = string.IsNullOrWhiteSpace(proprietorBox.Text) ? null : proprietorBox.Text,
                AttorneyOfRecord = string.IsNullOrWhiteSpace(attorneyBox.Text) ? null : attorneyBox.Text,
                State = string.IsNullOrWhiteSpace(stateBox.Text) ? null : stateBox.Text,
                NiceClass = string.IsNullOrWhiteSpace(classBox.Text) ? null : classBox.Text,
                MarkType = (MarkType)markTypePicker.SelectedItem,
                AssignedToId = assignedToId
            });
        }
        else
        {
            existing.MatterNumber = numberBox.Text;
            existing.Title = titleBox.Text;
            existing.ClientName = clientBox.Text;
            existing.Type = (MatterType)typePicker.SelectedItem;
            existing.ProprietorName = string.IsNullOrWhiteSpace(proprietorBox.Text) ? null : proprietorBox.Text;
            existing.AttorneyOfRecord = string.IsNullOrWhiteSpace(attorneyBox.Text) ? null : attorneyBox.Text;
            existing.State = string.IsNullOrWhiteSpace(stateBox.Text) ? null : stateBox.Text;
            existing.NiceClass = string.IsNullOrWhiteSpace(classBox.Text) ? null : classBox.Text;
            existing.MarkType = (MarkType)markTypePicker.SelectedItem;
            existing.AssignedToId = assignedToId;
            App.Matters.Update(existing);
        }

        LoadMatters(App.Matters.GetAll());
    }

    public sealed class MatterRow
    {
        public int Id { get; }
        public string Number { get; }
        public string Title { get; }
        public string Client { get; }
        public string Type { get; }
        public string Country { get; }
        public string Status { get; }
        public string FilingDate { get; }
        public string Proprietor { get; }
        public string State { get; }
        public string AssignedTo { get; }

        public MatterRow(Matter matter)
        {
            Id = matter.Id;
            Number = matter.MatterNumber;
            Title = matter.Title;
            Client = matter.ClientName;
            Type = matter.Type.ToString();
            Country = matter.Country;
            Status = matter.Status.ToString();
            FilingDate = matter.FilingDate?.ToString("dd MMM yyyy") ?? "Not filed";
            Proprietor = matter.ProprietorName ?? "-";
            State = matter.State ?? "-";
            AssignedTo = matter.AssignedTo?.Name ?? "Unassigned";
        }
    }
}
