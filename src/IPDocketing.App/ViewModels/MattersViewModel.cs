using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPDocketing.Core.Models;

namespace IPDocketing.App.ViewModels;

public partial class MattersViewModel : ViewModelBase
{
    public ObservableCollection<Matter> Matters { get; } = new();

    [ObservableProperty]
    private Matter? selectedMatter;

    [ObservableProperty]
    private string newMatterNumber = string.Empty;
    [ObservableProperty]
    private string newTitle = string.Empty;
    [ObservableProperty]
    private string newClientName = string.Empty;
    [ObservableProperty]
    private MatterType newType = MatterType.Patent;
    [ObservableProperty]
    private string newCountry = "US";

    public Array MatterTypes => Enum.GetValues(typeof(MatterType));

    public ICommand AddMatterCommand { get; }
    public ICommand RefreshCommand { get; }

    public MattersViewModel()
    {
        AddMatterCommand = new RelayCommand(AddMatter);
        RefreshCommand = new RelayCommand(Load);
        Load();
    }

    private void Load()
    {
        Matters.Clear();
        foreach (var m in App.Matters.GetAll())
            Matters.Add(m);
    }

    private void AddMatter()
    {
        if (string.IsNullOrWhiteSpace(NewMatterNumber) || string.IsNullOrWhiteSpace(NewTitle))
        {
            System.Windows.MessageBox.Show("Matter number and title are required.", "IP Docketing",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        var matter = new Matter
        {
            MatterNumber = NewMatterNumber.Trim(),
            Title = NewTitle.Trim(),
            ClientName = NewClientName.Trim(),
            Type = NewType,
            Country = string.IsNullOrWhiteSpace(NewCountry) ? "US" : NewCountry.Trim().ToUpperInvariant(),
            Status = MatterStatus.Pending
        };

        App.Matters.Add(matter);
        Matters.Add(matter);

        NewMatterNumber = string.Empty;
        NewTitle = string.Empty;
        NewClientName = string.Empty;
        NewCountry = "US";
    }
}
