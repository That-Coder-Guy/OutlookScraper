using System.ComponentModel;
using System.Runtime.Versioning;
using System.Windows;
using OutlookScraper.App.ViewModels;

namespace OutlookScraper.App.Views;

[SupportedOSPlatform("windows10.0.19041.0")]
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;
    private bool _shuttingDown;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        Loaded += async (_, _) => await _viewModel.LoadAsync();
    }

    /// <summary>Hide rather than close, same as the review window.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_shuttingDown)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        Hide();
    }

    public async Task ShowRefreshedAsync()
    {
        Show();

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        await _viewModel.LoadAsync();
    }

    public void AllowClose()
    {
        _shuttingDown = true;
        Close();
    }
}
