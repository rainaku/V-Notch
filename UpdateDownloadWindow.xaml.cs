using System;
using System.Windows;

namespace VNotch;

public partial class UpdateDownloadWindow : Window
{
    public UpdateDownloadWindow()
    {
        InitializeComponent();
    }

    public void SetStatus(string text)
    {
        StatusText.Text = text;
    }

    public void SetIndeterminate(string status)
    {
        DownloadProgressBar.IsIndeterminate = true;
        PercentText.Text = "...";
        StatusText.Text = status;
    }

    public void SetProgress(double percent)
    {
        var value = Math.Clamp(percent, 0d, 100d);
        DownloadProgressBar.IsIndeterminate = false;
        DownloadProgressBar.Value = value;
        PercentText.Text = $"{value:0}%";
    }
}
