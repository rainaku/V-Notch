using System.Windows;

namespace VNotch;

public partial class SetupWindowTest : Window
{
    public SetupWindowTest()
    {
        InitializeComponent();
    }

    private void LaunchSetup_Click(object sender, RoutedEventArgs e)
    {
        var setupWindow = new SetupWindow();
        setupWindow.ShowDialog();
    }
}
