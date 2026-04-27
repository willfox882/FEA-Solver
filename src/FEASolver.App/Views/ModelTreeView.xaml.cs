using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FEASolver.ViewModels;

namespace FEASolver.Views;

public partial class ModelTreeView : UserControl
{
    public ModelTreeView()
    {
        InitializeComponent();
    }

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is ModelTreeViewModel vm)
            vm.SelectedNode = e.NewValue as ModelTreeNode;
    }

    private void Tree_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && DataContext is ModelTreeViewModel vm)
        {
            if (vm.DeleteSelectedCommand.CanExecute(null))
                vm.DeleteSelectedCommand.Execute(null);
            e.Handled = true;
        }
    }
}
