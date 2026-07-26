using System.ComponentModel;
using System.Windows;
using OutlookScraper.App.ViewModels;

namespace OutlookScraper.App.Views;

public partial class ReviewWindow : Window
{
    private readonly ReviewViewModel _viewModel;

    public ReviewWindow(ReviewViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        Loaded += async (_, _) => await _viewModel.LoadAsync();
    }

    private bool _shuttingDown;

    /// <summary>
    /// Closing hides rather than exits. This is a tray application: the window is a
    /// view onto it, not the app itself. Only a real shutdown gets through.
    /// </summary>
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

    /// <summary>Brings the window back, refreshed, from the tray or a toast.</summary>
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

    /// <summary>Lets the window actually close, for application shutdown.</summary>
    public void AllowClose()
    {
        _shuttingDown = true;
        Close();
    }
}
