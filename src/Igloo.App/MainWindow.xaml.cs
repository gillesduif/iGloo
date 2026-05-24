using System.Windows;
using Igloo.App.ViewModels;

namespace Igloo.App;

public sealed partial class MainWindow : Window
{
    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
