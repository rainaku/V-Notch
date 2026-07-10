using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using VNotch.Services;

namespace VNotch.Windows;

public partial class ConfirmationDialog : Window
{
    public enum DialogIcon
    {
        Warning,
        Question,
        Error,
        Info
    }

    public enum DialogStyle
    {
        Normal,
        Danger
    }

    public bool Confirmed { get; private set; }

    public ConfirmationDialog()
    {
        InitializeComponent();
        AnimationPrimitives.ApplyFpsToTree(this);
    }

    /// <summary>
    /// Show a confirmation dialog with custom message
    /// </summary>
    public static bool Show(
        Window? owner,
        string message,
        string title = "",
        string confirmText = "",
        string cancelText = "",
        DialogIcon icon = DialogIcon.Warning,
        DialogStyle style = DialogStyle.Normal,
        string? detailText = null)
    {
        var dialog = new ConfirmationDialog();
        
        if (owner != null)
        {
            dialog.Owner = owner;
        }

        // Set title
        dialog.TitleText.Text = string.IsNullOrEmpty(title) ? Loc.Get("dialog.confirm.title") : title;
        
        // Set message
        dialog.MessageText.Text = message;

        // Set detail text if provided
        if (!string.IsNullOrEmpty(detailText))
        {
            dialog.DetailText.Text = detailText;
            dialog.DetailText.Visibility = Visibility.Visible;
        }

        // Set button text
        dialog.ConfirmButton.Content = string.IsNullOrEmpty(confirmText) ? Loc.Get("dialog.confirm") : confirmText;
        dialog.CancelButton.Content = string.IsNullOrEmpty(cancelText) ? Loc.Get("dialog.cancel") : cancelText;

        // Set button style
        if (style == DialogStyle.Danger)
        {
            dialog.ConfirmButton.Style = (Style)dialog.FindResource("DangerButton");
        }

        // Set icon
        dialog.SetIcon(icon);

        dialog.ShowDialog();
        return dialog.Confirmed;
    }

    private void SetIcon(DialogIcon icon)
    {
        Color iconColor;
        string pathData;

        switch (icon)
        {
            case DialogIcon.Warning:
                iconColor = Color.FromRgb(255, 165, 0); // Orange
                pathData = "M12,2 L22,20 L2,20 Z M12,8 L12,14 M12,16 L12,18";
                break;

            case DialogIcon.Error:
                iconColor = Color.FromRgb(220, 53, 69); // Red
                pathData = "M12,2 C6.48,2 2,6.48 2,12 C2,17.52 6.48,22 12,22 C17.52,22 22,17.52 22,12 C22,6.48 17.52,2 12,2 Z M12,8 L12,14 M12,16 L12,18";
                break;

            case DialogIcon.Question:
                iconColor = Color.FromRgb(0, 102, 255); // Blue
                pathData = "M12,2 C6.48,2 2,6.48 2,12 C2,17.52 6.48,22 12,22 C17.52,22 22,17.52 22,12 C22,6.48 17.52,2 12,2 Z M12,17 L12,17 M12,14 L12,10 C12,8.9 12.9,8 14,8 C15.1,8 16,8.9 16,10";
                break;

            case DialogIcon.Info:
                iconColor = Color.FromRgb(23, 162, 184); // Cyan
                pathData = "M12,2 C6.48,2 2,6.48 2,12 C2,17.52 6.48,22 12,22 C17.52,22 22,17.52 22,12 C22,6.48 17.52,2 12,2 Z M12,7 L12,7 M12,10 L12,17";
                break;

            default:
                iconColor = Color.FromRgb(255, 165, 0);
                pathData = "M12,2 L22,20 L2,20 Z M12,8 L12,14 M12,16 L12,18";
                break;
        }

        WarningIcon.Stroke = new SolidColorBrush(iconColor);
        WarningIcon.Data = Geometry.Parse(pathData);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Set WS_EX_TOOLWINDOW to prevent this window from appearing in Alt+Tab
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            int exStyle = Win32Interop.GetWindowLong(hwnd, Win32Interop.GWL_EXSTYLE);
            Win32Interop.SetWindowLong(hwnd, Win32Interop.GWL_EXSTYLE, exStyle | Win32Interop.WS_EX_TOOLWINDOW);
        }
    }
}
