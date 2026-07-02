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

        // First run (no saved path): offer the newest CLI report if one is sitting next to us.
        if (string.IsNullOrWhiteSpace(viewModel.ReportPath))
        {
            var newest = Directory.EnumerateFiles(Environment.CurrentDirectory, "duplicates-*.yml")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (newest is not null)
            {
                viewModel.ReportPath = Path.GetFullPath(newest);
            }
        }

        var window = new MainWindow { DataContext = viewModel };
        window.Show();

        if (viewModel.RefreshCommand.CanExecute(null) && File.Exists(viewModel.ReportPath))
        {
            viewModel.RefreshCommand.Execute(null);
        }
    }
}
