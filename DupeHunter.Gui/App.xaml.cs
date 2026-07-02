using System.IO;
using System.Windows;
using DupeHunter.Gui.Services;
using DupeHunter.Gui.ViewModels;
using DupeHunter.Gui.Views;

namespace DupeHunter.Gui;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.ToString(), "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        var viewModel = new MainViewModel(new DialogService(), new SettingsService());

        // First run (no saved path): offer the CLI's default database if it's sitting next to us.
        if (string.IsNullOrWhiteSpace(viewModel.DatabasePath) && File.Exists("dupehunter.db"))
        {
            viewModel.DatabasePath = Path.GetFullPath("dupehunter.db");
        }

        var window = new MainWindow { DataContext = viewModel };
        window.Show();

        if (viewModel.RefreshCommand.CanExecute(null) && File.Exists(viewModel.DatabasePath))
        {
            viewModel.RefreshCommand.Execute(null);
        }
    }
}
