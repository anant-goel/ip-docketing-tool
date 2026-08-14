using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPDocketing.Core.Models;

namespace IPDocketing.App.ViewModels;

public partial class DeadlinesViewModel : ViewModelBase
{
    public ObservableCollection<Deadline> Deadlines { get; } = new();

    [ObservableProperty]
    private Deadline? selectedDeadline;

    [ObservableProperty]
    private string filterText = string.Empty;

    public ICommand MarkCompleteCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand ExtendCommand { get; }

    public DeadlinesViewModel()
    {
        MarkCompleteCommand = new RelayCommand<Deadline>(MarkComplete);
        RefreshCommand = new RelayCommand(Load);
        ExtendCommand = new RelayCommand<Deadline>(Extend);
        Load();
    }

    private void Load()
    {
        Deadlines.Clear();
        foreach (var d in App.Deadlines.GetAll())
            Deadlines.Add(d);
    }

    private void MarkComplete(Deadline? deadline)
    {
        if (deadline is null) return;
        App.Deadlines.MarkComplete(deadline.Id);
        Load();
    }

    private void Extend(Deadline? deadline)
    {
        if (deadline is null) return;

        var ok = App.RuleEngine.TryExtend(deadline.Id, 30, out var message);
        System.Windows.MessageBox.Show(message, "Extend Deadline",
            System.Windows.MessageBoxButton.OK,
            ok ? System.Windows.MessageBoxImage.Information : System.Windows.MessageBoxImage.Warning);
        Load();
    }
}
