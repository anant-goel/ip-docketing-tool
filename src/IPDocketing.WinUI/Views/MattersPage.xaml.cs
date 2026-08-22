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

    private async void LogEvent_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int matterId }) return;
        var matter = App.Matters.GetById(matterId);
        if (matter is null) return;

        var typePicker = new ComboBox
        {
            Header = "Event type",
            ItemsSource = Enum.GetValues<EventType>(),
            SelectedIndex = 0
        };
        var dateBox = new DatePicker { Header = "Event date" };
        var notesBox = new TextBox { Header = "Notes (optional)", AcceptsReturn = true, Height = 60 };

        var panel = new StackPanel { Spacing = 10, Width = 360 };
        panel.Children.Add(typePicker);
        panel.Children.Add(dateBox);
        panel.Children.Add(notesBox);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Log event for {matter.MatterNumber}",
            Content = panel,
            PrimaryButtonText = "Log event",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var ev = new Event
        {
            MatterId = matter.Id,
            Type = (EventType)typePicker.SelectedItem,
            EventDate = dateBox.Date.DateTime,
            Notes = string.IsNullOrWhiteSpace(notesBox.Text) ? null : notesBox.Text
        };
        App.Database.Events.Add(ev);
        App.Database.SaveChanges();

        // This is where the deadline rule engine actually fires - it matches
        // (matter.Country, matter.Type, event.Type) against CountryRules and,
        // if a rule exists, auto-creates the resulting statutory deadline.
        var deadline = App.RuleEngine.CalculateAndCreateDeadline(ev);

        var resultDialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = deadline is null ? "Event logged" : "Deadline created",
            Content = deadline is null
                ? $"No deadline rule matches {matter.Country}/{matter.Type}/{ev.Type} yet. " +
                  "The event was still recorded against the matter."
                : $"{deadline.Description}\ndue {deadline.DueDate:dd MMM yyyy} " +
                  $"(nominal {deadline.NominalDueDate:dd MMM yyyy}).",
            CloseButtonText = "OK"
        };
        await resultDialog.ShowAsync();

        LoadMatters(App.Matters.GetAll());
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
        var numberBox = new TextBox { Header = "Matter number (your internal docket reference)", PlaceholderText = "e.g. TM-2026-001", Text = existing?.MatterNumber ?? "" };
        var applicationNumberBox = new TextBox { Header = "Application number (as filed with IP India)", PlaceholderText = "e.g. 4567890", Text = existing?.ApplicationNumber ?? "" };
        var countryBox = new ComboBox
        {
            Header = "Country / jurisdiction",
            ItemsSource = new[] { "IN", "US", "EP", "PCT", "CN" },
            SelectedItem = existing?.Country ?? "IN"
        };
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
        var pincodeBox = new TextBox { Header = "PIN code (optional - auto-fills State)", PlaceholderText = "e.g. 171001", MaxLength = 6, Width = 260 };
        var pincodeLookupButton = new Button { Content = "Look up", Margin = new Microsoft.UI.Xaml.Thickness(0, 22, 0, 0) };
        var pincodeStatusText = new TextBlock { FontSize = 11, Opacity = 0.7 };
        pincodeLookupButton.Click += async (_, _) =>
        {
            pincodeStatusText.Text = "Looking up...";
            var result = await App.Pincode.LookupAsync(pincodeBox.Text);
            if (result is null)
            {
                pincodeStatusText.Text = "Not found - enter State manually.";
            }
            else
            {
                stateBox.Text = result.State;
                pincodeStatusText.Text = $"Found: {result.District}, {result.State}";
            }
        };
        var pincodeRow = new StackPanel { Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal, Spacing = 10 };
        pincodeRow.Children.Add(pincodeBox);
        pincodeRow.Children.Add(pincodeLookupButton);
        var classBox = new TextBox { Header = "Nice class", PlaceholderText = "e.g. 25", Text = existing?.NiceClass ?? "" };

        // Phase 30: Status, FilingDate, GrantNumber, RegistrationDate,
        // RenewalDueDate and PortalAlert were all in the model but had no way in
        // through the UI - a matter could only ever be Pending with no filing
        // date, which made the dashboard metrics and the docx status tracker
        // structurally unable to show anything real. All six are editable now.
        var statusPicker = new ComboBox
        {
            Header = "Current status",
            ItemsSource = Enum.GetValues<MatterStatus>(),
            SelectedIndex = existing is null ? 0 : (int)existing.Status
        };
        var registrationNumberBox = new TextBox
        {
            Header = "Registration number (once registered)",
            PlaceholderText = "e.g. 4567890",
            Text = existing?.GrantNumber ?? ""
        };
        var attorneyCodeBox = new TextBox
        {
            Header = "Agent / attorney registration code",
            PlaceholderText = "The code the Registry files under",
            Text = existing?.AttorneyCode ?? ""
        };
        var alertBox = new TextBox
        {
            Header = "Portal alert (as shown on the TMR status page)",
            PlaceholderText = "e.g. Opposed / Objected / Abandoned",
            Text = existing?.PortalAlert ?? ""
        };

        // CalendarDatePicker, unlike DatePicker, has a genuine null state - so a
        // matter with no filing date stays that way instead of silently taking
        // today's date the moment the dialog opens.
        var filingDatePicker = new CalendarDatePicker
        {
            Header = "Filing date",
            PlaceholderText = "Not filed",
            Date = existing?.FilingDate
        };
        var registrationDatePicker = new CalendarDatePicker
        {
            Header = "Registration date",
            PlaceholderText = "Not registered",
            Date = existing?.RegistrationDate
        };
        var renewalDatePicker = new CalendarDatePicker
        {
            Header = "Renewal due",
            PlaceholderText = "Not set",
            Date = existing?.RenewalDueDate
        };

        // India: registration + 10 years, Section 25. Offered rather than
        // applied, because the renewal anchor differs by jurisdiction and a
        // silently computed date on a renewal is exactly the kind of thing that
        // becomes a malpractice claim.
        var deriveRenewalButton = new Button
        {
            Content = "Set renewal to registration + 10 years",
            Margin = new Microsoft.UI.Xaml.Thickness(0, 4, 0, 0)
        };
        deriveRenewalButton.Click += (_, _) =>
        {
            if (registrationDatePicker.Date is { } registered)
                renewalDatePicker.Date = registered.AddYears(10);
        };
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
                 {
                     numberBox, applicationNumberBox, registrationNumberBox, countryBox, titleBox, clientBox,
                     typePicker, statusPicker, proprietorBox, attorneyBox, stateBox, pincodeRow, pincodeStatusText,
                     classBox, markTypePicker, attorneyCodeBox, filingDatePicker, registrationDatePicker, renewalDatePicker,
                     deriveRenewalButton, alertBox, assigneePicker
                 })
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

        // Word/device only means something for a trademark. Previously every
        // patent and copyright matter got stamped MarkType.Word, which then
        // showed up as noise in the docx section 6 word/device split.
        var selectedType = (MatterType)typePicker.SelectedItem;
        MarkType? markTypeValue = selectedType == MatterType.Trademark
            ? (MarkType)markTypePicker.SelectedItem
            : null;

        if (existing is null)
        {
            App.Matters.Add(new Matter
            {
                MatterNumber = numberBox.Text,
                ApplicationNumber = string.IsNullOrWhiteSpace(applicationNumberBox.Text) ? null : applicationNumberBox.Text,
                Country = countryBox.SelectedItem as string ?? "IN",
                Title = titleBox.Text,
                ClientName = clientBox.Text,
                Type = (MatterType)typePicker.SelectedItem,
                ProprietorName = string.IsNullOrWhiteSpace(proprietorBox.Text) ? null : proprietorBox.Text,
                AttorneyOfRecord = string.IsNullOrWhiteSpace(attorneyBox.Text) ? null : attorneyBox.Text,
                State = string.IsNullOrWhiteSpace(stateBox.Text) ? null : stateBox.Text,
                NiceClass = string.IsNullOrWhiteSpace(classBox.Text) ? null : classBox.Text,
                MarkType = markTypeValue,
                Status = (MatterStatus)statusPicker.SelectedItem,
                GrantNumber = Trimmed(registrationNumberBox.Text),
                PortalAlert = Trimmed(alertBox.Text),
                AttorneyCode = Trimmed(attorneyCodeBox.Text),
                FilingDate = filingDatePicker.Date?.DateTime,
                RegistrationDate = registrationDatePicker.Date?.DateTime,
                RenewalDueDate = renewalDatePicker.Date?.DateTime,
                AssignedToId = assignedToId
            });
        }
        else
        {
            existing.MatterNumber = numberBox.Text;
            existing.ApplicationNumber = string.IsNullOrWhiteSpace(applicationNumberBox.Text) ? null : applicationNumberBox.Text;
            existing.Country = countryBox.SelectedItem as string ?? "IN";
            existing.Title = titleBox.Text;
            existing.ClientName = clientBox.Text;
            existing.Type = (MatterType)typePicker.SelectedItem;
            existing.ProprietorName = string.IsNullOrWhiteSpace(proprietorBox.Text) ? null : proprietorBox.Text;
            existing.AttorneyOfRecord = string.IsNullOrWhiteSpace(attorneyBox.Text) ? null : attorneyBox.Text;
            existing.State = string.IsNullOrWhiteSpace(stateBox.Text) ? null : stateBox.Text;
            existing.NiceClass = string.IsNullOrWhiteSpace(classBox.Text) ? null : classBox.Text;
            existing.MarkType = markTypeValue;
            existing.Status = (MatterStatus)statusPicker.SelectedItem;
            existing.GrantNumber = Trimmed(registrationNumberBox.Text);
            existing.PortalAlert = Trimmed(alertBox.Text);
            existing.AttorneyCode = Trimmed(attorneyCodeBox.Text);
            existing.FilingDate = filingDatePicker.Date?.DateTime;
            existing.RegistrationDate = registrationDatePicker.Date?.DateTime;
            existing.RenewalDueDate = renewalDatePicker.Date?.DateTime;
            existing.AssignedToId = assignedToId;
            App.Matters.Update(existing);
        }

        LoadMatters(App.Matters.GetAll());
    }

    /// <summary>
    /// CSV import. Two-phase on purpose: the file is parsed and reported on
    /// first, and nothing is written until the preview is accepted. A bulk
    /// import that half-succeeds on a malformed sheet is worse than one that
    /// refuses, because the damage is spread across hundreds of rows nobody
    /// will re-check afterwards.
    /// </summary>
    private async void Import_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".csv");
        picker.FileTypeFilter.Add(".txt");

        // Unpackaged WinUI pickers need an owning window handle, or they throw.
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        try
        {
            var text = await Windows.Storage.FileIO.ReadTextAsync(file);
            var report = App.PortfolioImport.Validate(text);

            var summary = new System.Text.StringBuilder();
            summary.AppendLine($"{report.NewCount} new matter(s), {report.UpdateCount} existing matter(s) would be updated.");
            summary.AppendLine();

            if (report.Issues.Count > 0)
            {
                var fatal = report.Issues.Where(i => i.IsFatal).ToList();
                if (fatal.Count > 0)
                {
                    summary.AppendLine("ERRORS — these must be fixed before importing:");
                    foreach (var issue in fatal.Take(10))
                        summary.AppendLine($"  Line {issue.LineNumber} [{issue.Column}]: {issue.Message}");
                    summary.AppendLine();
                }

                var warnings = report.Issues.Where(i => !i.IsFatal).ToList();
                if (warnings.Count > 0)
                {
                    summary.AppendLine($"Warnings ({warnings.Count}) — the import can proceed, but check these:");
                    foreach (var issue in warnings.Take(25))
                        summary.AppendLine($"  Line {issue.LineNumber} [{issue.Column}]: {issue.Message}");
                    if (warnings.Count > 25) summary.AppendLine($"  ...and {warnings.Count - 25} more.");
                }
            }
            else
            {
                summary.AppendLine("No problems found.");
            }

            // Shown through the shared report dialog, which renders long text
            // correctly and saves a copy to disk. The fixed-height TextBox this
            // replaces collapsed to about one line, hiding every warning the
            // validator produced - so an import could look clean when it wasn't.
            await IPDocketing.WinUI.Services.TextReportDialog.ShowAsync(
                XamlRoot,
                report.HasFatalIssues ? "Import blocked" : "Import preview",
                summary.ToString(),
                "importpreview");

            if (report.HasFatalIssues) return;

            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Import now?",
                Content = new TextBlock
                {
                    Text = $"{report.NewCount} new matter(s), {report.UpdateCount} update(s), " +
                           $"{report.WarningCount} warning(s) - all listed in the preview you just saw.",
                    TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap
                },
                PrimaryButtonText = "Import",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary
            };

            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

            var (created, updated) = App.PortfolioImport.Import(report);

            // Newly imported registrations need their s.25 dates straight away -
            // an imported portfolio with no renewal deadlines is the exact
            // failure this app exists to prevent.
            var renewalResult = App.Renewals.DocketRenewals();

            LoadMatters(App.Matters.GetAll());

            await ShowInfo("Import complete",
                $"{created} matter(s) created, {updated} updated.\n" +
                $"{renewalResult.DeadlinesCreated} renewal deadline(s) docketed automatically.");
        }
        catch (Exception ex)
        {
            await ShowInfo("Import failed", ex.Message);
        }
    }

    private async void Export_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        try
        {
            var directory = System.IO.Path.Combine(App.AppDataDirectory, "Exports");
            System.IO.Directory.CreateDirectory(directory);

            var path = System.IO.Path.Combine(directory, $"portfolio_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            await System.IO.File.WriteAllTextAsync(path, App.PortfolioImport.ExportCsv(),
                System.Text.Encoding.UTF8);

            // A blank template alongside it, so the round trip is obvious.
            var templatePath = System.IO.Path.Combine(directory, "import-template.csv");
            await System.IO.File.WriteAllTextAsync(templatePath, App.PortfolioImport.BuildTemplateCsv(),
                System.Text.Encoding.UTF8);

            App.Audit.Log("Export", "Portfolio", 0, $"Portfolio exported to {path}.");
            await ShowInfo("Exported",
                $"Portfolio written to:\n{path}\n\nA blank import template was saved alongside it as import-template.csv.");
        }
        catch (Exception ex)
        {
            await ShowInfo("Export failed", ex.Message);
        }
    }

    private async System.Threading.Tasks.Task ShowInfo(string title, string content)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new ScrollViewer
            {
                Content = new TextBlock { Text = content, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap },
                MaxHeight = 400
            },
            CloseButtonText = "OK"
        };
        await dialog.ShowAsync();
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// docx sections 2 and 3 both call for "a tool to assign a particular TM to
    /// a team member". Reassignment was previously buried inside the full edit
    /// dialog, which is far too much friction for the one field that changes
    /// most often - this is a single dropdown.
    /// </summary>
    private async void AssignMatter_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int id }) return;

        var matter = App.Matters.GetById(id);
        if (matter is null) return;

        var teamMembers = App.Team.GetActive();
        if (teamMembers.Count == 0)
        {
            var noTeam = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "No team members yet",
                Content = "There are no active team members to assign to. Add them first — " +
                          "the seed database ships with two as examples.",
                CloseButtonText = "OK"
            };
            await noTeam.ShowAsync();
            return;
        }

        var picker = new ComboBox
        {
            Header = "Assign to",
            ItemsSource = teamMembers,
            DisplayMemberPath = "Name",
            MinWidth = 300,
            SelectedIndex = matter.AssignedToId is null
                ? -1
                : teamMembers.FindIndex(t => t.Id == matter.AssignedToId)
        };
        var unassign = new CheckBox { Content = "Leave unassigned", IsChecked = matter.AssignedToId is null };

        var panel = new StackPanel { Spacing = 12, Width = 320 };
        panel.Children.Add(new TextBlock
        {
            Text = $"{matter.MatterNumber} — {matter.Title}",
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
            Opacity = 0.75
        });
        panel.Children.Add(picker);
        panel.Children.Add(unassign);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Assign matter",
            Content = panel,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        matter.AssignedToId = unassign.IsChecked == true
            ? null
            : (picker.SelectedItem as TeamMember)?.Id;
        App.Matters.Update(matter);
        App.Audit.Log("Assign", "Matter", matter.Id,
            matter.AssignedToId is null
                ? "Left unassigned."
                : $"Assigned to team member {matter.AssignedToId}.");

        LoadMatters(App.Matters.GetAll());
    }

    /// <summary>Jumps to the full prosecution/opposition history for this mark.</summary>
    private void TraceMatter_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is Button { Tag: int id })
            Frame?.Navigate(typeof(StatusTrackerPage), id);
    }

    public sealed class MatterRow
    {
        public int Id { get; }
        public string Number { get; }
        public string TypeAndApplicationNumber { get; }
        public string Title { get; }
        public string Client { get; }
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
            TypeAndApplicationNumber = string.IsNullOrWhiteSpace(matter.ApplicationNumber)
                ? $"{matter.Type} · no application number"
                : $"{matter.Type} · App# {matter.ApplicationNumber}";
            Title = matter.Title;
            Client = matter.ClientName;
            Country = matter.Country;
            Status = matter.Status.ToString();
            FilingDate = matter.FilingDate?.ToString("dd MMM yyyy") ?? "Not filed";
            Proprietor = matter.ProprietorName ?? "-";
            State = matter.State ?? "-";
            AssignedTo = matter.AssignedTo?.Name ?? "Unassigned";
        }
    }
}
