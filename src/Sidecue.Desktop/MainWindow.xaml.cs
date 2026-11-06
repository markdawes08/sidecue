using System.Windows;
using Sidecue.Desktop.ViewModels;

namespace Sidecue.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel();
    }
}
