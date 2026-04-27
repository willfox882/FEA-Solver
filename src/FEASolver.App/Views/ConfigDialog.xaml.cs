using System.Windows;
using FEASolver.ViewModels;

namespace FEASolver.Views;

public partial class ConfigDialog : Window
{
    public ConfigDialog()
    {
        InitializeComponent();
        if (DataContext is ConfigDialogViewModel vm)
            vm.CloseRequested += (_, saved) => { DialogResult = saved; Close(); };
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
