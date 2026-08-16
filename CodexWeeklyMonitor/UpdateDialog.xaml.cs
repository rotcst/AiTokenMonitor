using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using CodexWeeklyMonitor.Services;

namespace CodexWeeklyMonitor;

public partial class UpdateDialog : Window
{
    private readonly UpdateRelease? _release;
    private readonly IUpdateService? _updateService;
    private readonly CancellationTokenSource _cancellation = new();
    private bool _downloading;

    /// <param name="title">
    /// Overrides the heading for callers that reuse this chrome for a non-update notice.
    /// </param>
    private UpdateDialog(
        string message,
        UpdateRelease? release = null,
        IUpdateService? updateService = null,
        string? title = null)
    {
        InitializeComponent();
        _release = release;
        _updateService = updateService;

        Title = title ?? Loc.T("update.title");
        TitleText.Text = Title;
        MessageText.Text = message;
        VersionText.Text = release is null
            ? AppVersion.Display
            : Loc.T(
                "update.versionPair",
                AppVersion.Display,
                $"v{AppVersion.ToDisplayString(release.Version)}");

        if (release is null)
        {
            SecondaryButton.Visibility = Visibility.Collapsed;
            PrimaryButton.Content = Loc.T("update.ok");
        }
        else
        {
            SecondaryButton.Content = Loc.T("update.later");
            PrimaryButton.Content = Loc.T("update.installNow");
        }
    }

    internal static void ShowInformation(Window? owner, string message, bool topmost, string? title = null)
    {
        var dialog = new UpdateDialog(message, title: title);
        ConfigureOwner(dialog, owner, topmost);
        dialog.ShowDialog();
    }

    internal static void ShowAvailable(
        Window? owner,
        UpdateRelease release,
        IUpdateService updateService,
        bool topmost)
    {
        var message = Loc.T(
            "update.available",
            $"v{AppVersion.ToDisplayString(release.Version)}");
        var dialog = new UpdateDialog(message, release, updateService);
        ConfigureOwner(dialog, owner, topmost);
        dialog.ShowDialog();
    }

    private static void ConfigureOwner(UpdateDialog dialog, Window? owner, bool topmost)
    {
        if (owner is { IsVisible: true })
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        dialog.Topmost = topmost;
    }

    private async void PrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_release is null || _updateService is null)
        {
            Close();
            return;
        }

        if (_downloading)
        {
            return;
        }

        _downloading = true;
        PrimaryButton.IsEnabled = false;
        SecondaryButton.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;
        MessageText.Text = Loc.T("update.downloading");
        ProgressText.Text = Loc.T("update.progress", 0);

        var progress = new Progress<double>(value =>
        {
            var percent = (int)Math.Round(Math.Clamp(value, 0, 1) * 100);
            var availableWidth = Math.Max(0, ProgressPanel.ActualWidth);
            ProgressFill.Width = availableWidth * percent / 100d;
            ProgressText.Text = Loc.T("update.progress", percent);
        });

        try
        {
            var downloaded = await _updateService.DownloadAsync(
                _release,
                progress,
                _cancellation.Token);
            MessageText.Text = Loc.T("update.restarting");
            ProgressText.Text = Loc.T("update.progress", 100);
            ProgressFill.Width = Math.Max(0, ProgressPanel.ActualWidth);
            _updateService.LaunchInstaller(_release, downloaded);
            System.Windows.Application.Current.Shutdown();
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            // Closing the prompt deliberately cancels the transfer.
        }
        catch (Exception exception)
        {
            var message = exception is UpdateServiceException updateException
                ? Loc.T(updateException.ResourceKey)
                : Loc.T("update.installFailed");
            MessageText.Text = Loc.T("update.errorDetail", message);
            ProgressPanel.Visibility = Visibility.Collapsed;
            PrimaryButton.Content = Loc.T("update.retry");
            PrimaryButton.IsEnabled = true;
            SecondaryButton.IsEnabled = true;
            _downloading = false;
        }
    }

    private void SecondaryButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The button may be released between the event and DragMove.
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _cancellation.Cancel();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _cancellation.Dispose();
        base.OnClosed(e);
    }
}
