using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using IOPath = System.IO.Path;

namespace VNotch;

internal interface ISetupEntryAwarePage
{
    void OnPageEntryCompleted();
}

internal interface ISetupAnimatedPage
{
    IReadOnlyList<UIElement> GetAnimatedElements();
}

public partial class SetupWindow : Window
{
    private const int SwShow = 5;

    private enum NavigationDirection
    {
        Forward,
        Backward
    }

    private int _currentPageIndex = 0;
    private readonly Func<UIElement>[] _pageFactories;
    private readonly DirectoryPage _directoryPage;
    private readonly StartupOptionsPage _startupOptionsPage;
    private readonly InstallProgressPage _installProgressPage;
    private readonly FinishPage _finishPage;
    private readonly string _sourceDirectory;
    private bool _isWelcomePage = true;
    private bool _isTransitioning;
    private bool _isInstalling;
    private bool _installationSucceeded;
    private bool _hasForcedForeground;
    public int ResultExitCode { get; private set; } = 1;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public SetupWindow(string? sourceDirectory = null)
    {
        InitializeComponent();

        _sourceDirectory = sourceDirectory ?? AppContext.BaseDirectory;
        _directoryPage = new DirectoryPage(IOPath.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "V-Notch"));
        _startupOptionsPage = new StartupOptionsPage(startWithWindows: true);
        _installProgressPage = new InstallProgressPage();
        _finishPage = new FinishPage(launchAfterInstall: true);

        _pageFactories = new Func<UIElement>[]
        {
            () => null!, // Welcome page uses built-in XAML
            () => _directoryPage,
            () => _startupOptionsPage,
            () => _installProgressPage,
            () => _finishPage
        };
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var storyboard = (Storyboard)FindResource("WindowEntranceStoryboard");
        storyboard.Begin();
        ForceWindowToFront();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        ForceWindowToFront();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isInstalling)
        {
            MessageBox.Show(
                "Installation is in progress. Please wait for it to finish.",
                "V-Notch Setup",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (_installationSucceeded)
        {
            ResultExitCode = 0;
            Close();
            return;
        }

        var result = MessageBox.Show(
            "Are you sure you want to cancel the installation?",
            "Cancel Setup",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        
        if (result == MessageBoxResult.Yes)
            Close();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isTransitioning)
        {
            return;
        }

        if (_currentPageIndex > 0)
        {
            ShowPage(_currentPageIndex - 1, NavigationDirection.Backward);
        }
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isTransitioning)
        {
            return;
        }

        if (!CommitCurrentStep())
        {
            return;
        }

        if (_currentPageIndex < _pageFactories.Length - 1)
        {
            ShowPage(_currentPageIndex + 1, NavigationDirection.Forward);
        }
        else
        {
            if (_installationSucceeded && _finishPage.LaunchAfterInstall)
            {
                SetupOperations.LaunchInstalledApp(_directoryPage.InstallPath);
            }

            ResultExitCode = 0;
            Close();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        CloseButton_Click(sender, e);
    }

    private void ShowPage(int index, NavigationDirection direction)
    {
        if (index < 0 || index >= _pageFactories.Length)
        {
            return;
        }

        if (_isTransitioning)
        {
            return;
        }

        _isTransitioning = true;
        SetNavigationEnabled(false);

        AnimateCurrentViewOut(direction, () =>
        {
            try
            {
                _currentPageIndex = index;

                if (index == 0)
                {
                    ShowWelcomePage(direction);
                }
                else
                {
                    ShowDynamicPage(index, direction);
                }

                UpdateStepIndicators(index);
                UpdateNavigationButtons(index);
            }
            catch (Exception ex)
            {
                CompleteTransition();
                MessageBox.Show($"Error loading page: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        });
    }

    private void ShowWelcomePage(NavigationDirection direction)
    {
        ResetContentPresenter();
        ContentPresenter.Content = null;
        ContentPresenter.Visibility = Visibility.Collapsed;

        _isWelcomePage = true;

        ResetElementForEntry(IconContainer, GetWelcomeOffset(direction));
        ResetElementForEntry(HeadlineText, GetWelcomeOffset(direction));
        ResetElementForEntry(BodyText, GetWelcomeOffset(direction));

        AnimateWelcomeIn(direction, CompleteTransition);
        AnimateButtonPanel();
    }

    private void ShowDynamicPage(int index, NavigationDirection direction)
    {
        HideWelcomeElements();

        ResetContentPresenter();
        ContentPresenter.Content = _pageFactories[index]();
        ContentPresenter.Visibility = Visibility.Visible;
        _isWelcomePage = false;

        AnimateDynamicPageIn(direction, () =>
        {
            CompleteTransition();

            if (index == _pageFactories.Length - 2)
            {
                BeginInstallationAsync();
            }
        });
        AnimateButtonPanel();
    }

    private void UpdateNavigationButtons(int index)
    {
        var showBack = index > 0 && index < _pageFactories.Length - 1;
        var showCancel = index < _pageFactories.Length - 1;
        var showNext = true;

        if (index == _pageFactories.Length - 2)
        {
            showBack = false;
            showCancel = false;
            showNext = false;
        }
        else if (index == _pageFactories.Length - 1)
        {
            NextButton.Content = "Finish";
        }
        else if (index == _pageFactories.Length - 3)
        {
            NextButton.Content = "Install";
        }
        else
        {
            NextButton.Content = "Continue";
        }

        BackButton.Visibility = showBack ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = showNext ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AnimateCurrentViewOut(NavigationDirection direction, Action onComplete)
    {
        ResetButtonPanelForEntry();

        if (_isWelcomePage)
        {
            AnimateWelcomeOut(direction, () =>
            {
                HideWelcomeElements();
                onComplete?.Invoke();
            });

            return;
        }

        if (ContentPresenter.Visibility != Visibility.Visible || ContentPresenter.Content == null)
        {
            ResetContentPresenter();
            onComplete?.Invoke();
            return;
        }

        AnimatePageOut(direction, () =>
        {
            ResetContentPresenter();
            onComplete?.Invoke();
        });
    }

    private void AnimatePageOut(NavigationDirection direction, Action onComplete)
    {
        var offset = direction == NavigationDirection.Forward ? -24 : 24;
        var slideOut = new DoubleAnimation
        {
            From = 0,
            To = offset,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };

        var fadeOut = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };

        var transform = EnsureTranslateTransform(ContentPresenter);
        StopAnimations(ContentPresenter);

        fadeOut.Completed += (s, e) =>
        {
            StopAnimations(ContentPresenter);
            transform.Y = offset;
            ContentPresenter.Opacity = 0;
            onComplete?.Invoke();
        };

        transform.BeginAnimation(TranslateTransform.YProperty, slideOut);
        ContentPresenter.BeginAnimation(OpacityProperty, fadeOut);
    }

    private void AnimatePageIn(NavigationDirection direction, Action? onComplete = null)
    {
        var offset = direction == NavigationDirection.Forward ? 24 : -24;
        var slideIn = new DoubleAnimation
        {
            From = offset,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(240),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        var fadeIn = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(240),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        var transform = EnsureTranslateTransform(ContentPresenter);
        StopAnimations(ContentPresenter);
        transform.Y = offset;
        ContentPresenter.Opacity = 0;

        fadeIn.Completed += (s, e) =>
        {
            StopAnimations(ContentPresenter);
            transform.Y = 0;
            ContentPresenter.Opacity = 1;
            onComplete?.Invoke();
        };

        transform.BeginAnimation(TranslateTransform.YProperty, slideIn);
        ContentPresenter.BeginAnimation(OpacityProperty, fadeIn);
    }

    private void AnimateDynamicPageIn(NavigationDirection direction, Action? onComplete = null)
    {
        var animatedElements = GetAnimatedElementsForCurrentPage();
        if (animatedElements.Count == 0)
        {
            AnimatePageIn(direction, onComplete);
            return;
        }

        StopAnimations(ContentPresenter);
        EnsureTranslateTransform(ContentPresenter).Y = 0;
        ContentPresenter.Opacity = 1;

        var offset = direction == NavigationDirection.Forward ? 12 : -12;
        var lastIndex = animatedElements.Count - 1;

        for (int i = 0; i < animatedElements.Count; i++)
        {
            var element = animatedElements[i];
            ResetElementForEntry(element, offset);

            Action? completion = null;
            if (i == lastIndex)
            {
                completion = () =>
                {
                    if (ContentPresenter.Content is ISetupEntryAwarePage entryAwarePage)
                    {
                        entryAwarePage.OnPageEntryCompleted();
                    }

                    onComplete?.Invoke();
                };
            }

            AnimateContentIn(element, i * 55, offset, completion);
        }
    }

    private void AnimateWelcomeIn(NavigationDirection direction, Action? onComplete = null)
    {
        var offset = GetWelcomeOffset(direction);
        AnimateContentIn(IconContainer, 0, offset);
        AnimateContentIn(HeadlineText, 50, offset);
        AnimateContentIn(BodyText, 100, offset, onComplete);
    }

    private void AnimateWelcomeOut(NavigationDirection direction, Action? onComplete = null)
    {
        var offset = direction == NavigationDirection.Forward ? -8 : 8;
        AnimateContentOut(IconContainer, offset);
        AnimateContentOut(HeadlineText, offset);
        AnimateContentOut(BodyText, offset, onComplete);
    }

    private void AnimateContentIn(UIElement element, int delay, double offsetY, Action? onComplete = null)
    {
        var slideIn = new DoubleAnimation
        {
            From = offsetY,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(260),
            BeginTime = TimeSpan.FromMilliseconds(delay),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        var fadeIn = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(260),
            BeginTime = TimeSpan.FromMilliseconds(delay),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        var transform = EnsureTranslateTransform(element);
        StopAnimations(element);
        transform.Y = offsetY;
        element.Opacity = 0;
        element.Visibility = Visibility.Visible;

        fadeIn.Completed += (s, e) =>
        {
            StopAnimations(element);
            transform.Y = 0;
            element.Opacity = 1;
            onComplete?.Invoke();
        };

        transform.BeginAnimation(TranslateTransform.YProperty, slideIn);
        element.BeginAnimation(OpacityProperty, fadeIn);
    }

    private void AnimateContentOut(UIElement element, double offsetY, Action? onComplete = null)
    {
        var slideOut = new DoubleAnimation
        {
            From = 0,
            To = offsetY,
            Duration = TimeSpan.FromMilliseconds(160),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };

        var fadeOut = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(160),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };

        var transform = EnsureTranslateTransform(element);
        StopAnimations(element);

        fadeOut.Completed += (s, e) =>
        {
            StopAnimations(element);
            transform.Y = offsetY;
            element.Opacity = 0;
            element.Visibility = Visibility.Collapsed;
            onComplete?.Invoke();
        };

        transform.BeginAnimation(TranslateTransform.YProperty, slideOut);
        element.BeginAnimation(OpacityProperty, fadeOut);
    }

    private void AnimateButtonPanel()
    {
        ResetButtonPanelForEntry();

        var fadeIn = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 3 }
        };

        var slideIn = new DoubleAnimation
        {
            From = 8,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        ButtonPanel.BeginAnimation(OpacityProperty, fadeIn);
        ButtonTranslate.BeginAnimation(TranslateTransform.YProperty, slideIn);
    }

    private List<UIElement> GetAnimatedElementsForCurrentPage()
    {
        if (ContentPresenter.Content is ISetupAnimatedPage animatedPage)
        {
            return new List<UIElement>(animatedPage.GetAnimatedElements());
        }

        var elements = new List<UIElement>();

        if (ContentPresenter.Content is Panel panel)
        {
            foreach (UIElement child in panel.Children)
            {
                elements.Add(child);
            }

            return elements;
        }

        if (ContentPresenter.Content is Decorator decorator && decorator.Child != null)
        {
            elements.Add(decorator.Child);
            return elements;
        }

        if (ContentPresenter.Content is UIElement element)
        {
            elements.Add(element);
        }

        return elements;
    }

    private void ResetContentPresenter()
    {
        StopAnimations(ContentPresenter);
        var transform = EnsureTranslateTransform(ContentPresenter);
        transform.Y = 0;
        ContentPresenter.Opacity = 0;
    }

    private void HideWelcomeElements()
    {
        HideAndResetElement(IconContainer);
        HideAndResetElement(HeadlineText);
        HideAndResetElement(BodyText);
    }

    private void HideAndResetElement(UIElement element)
    {
        StopAnimations(element);
        var transform = EnsureTranslateTransform(element);
        transform.Y = 0;
        element.Opacity = 0;
        element.Visibility = Visibility.Collapsed;
    }

    private void ResetElementForEntry(UIElement element, double offsetY)
    {
        StopAnimations(element);
        var transform = EnsureTranslateTransform(element);
        transform.Y = offsetY;
        element.Opacity = 0;
        element.Visibility = Visibility.Visible;
    }

    private void ResetButtonPanelForEntry()
    {
        ButtonPanel.BeginAnimation(OpacityProperty, null);
        ButtonTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        ButtonPanel.Opacity = 0;
        ButtonTranslate.Y = 8;
    }

    private TranslateTransform EnsureTranslateTransform(UIElement element)
    {
        if (element.RenderTransform is TranslateTransform translateTransform)
        {
            return translateTransform;
        }

        var newTransform = new TranslateTransform();
        element.RenderTransform = newTransform;
        return newTransform;
    }

    private void StopAnimations(UIElement element)
    {
        element.BeginAnimation(OpacityProperty, null);
        EnsureTranslateTransform(element).BeginAnimation(TranslateTransform.YProperty, null);
    }

    private static double GetWelcomeOffset(NavigationDirection direction)
    {
        return direction == NavigationDirection.Forward ? 8 : -8;
    }

    private void SetNavigationEnabled(bool isEnabled)
    {
        BackButton.IsEnabled = isEnabled;
        CancelButton.IsEnabled = isEnabled;
        NextButton.IsEnabled = isEnabled;
    }

    private void CompleteTransition()
    {
        _isTransitioning = false;
        SetNavigationEnabled(true);
    }

    private void UpdateStepIndicators(int currentStep)
    {
        var steps = new[] { Step1Text, Step2Text, Step3Text, Step4Text, Step5Text };
        
        for (int i = 0; i < steps.Length; i++)
        {
            if (i == currentStep)
            {
                // Current step - bright white
                steps[i].Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(255, 255, 255, 255));
                steps[i].FontWeight = FontWeights.SemiBold;
            }
            else if (i < currentStep)
            {
                // Completed steps - medium opacity
                steps[i].Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(153, 255, 255, 255));
                steps[i].FontWeight = FontWeights.Normal;
            }
            else
            {
                // Future steps - low opacity
                steps[i].Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(136, 255, 255, 255));
                steps[i].FontWeight = FontWeights.Normal;
            }
        }
    }
    
    public void AutoAdvanceToFinish()
    {
        if (_currentPageIndex == _pageFactories.Length - 2)
        {
            ShowPage(_currentPageIndex + 1, NavigationDirection.Forward);
        }
    }

    private void ForceWindowToFront()
    {
        if (_hasForcedForeground)
        {
            return;
        }

        _hasForcedForeground = true;
        BringToFront();

        var followUpTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(180)
        };
        followUpTimer.Tick += (_, _) =>
        {
            followUpTimer.Stop();
            BringToFront();
        };
        followUpTimer.Start();
    }

    private void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();
        Activate();
        Focus();

        Topmost = true;
        Topmost = false;

        if (ContentPresenter.IsVisible)
        {
            ContentPresenter.Focus();
        }

        var windowHandle = new WindowInteropHelper(this).Handle;
        if (windowHandle != IntPtr.Zero)
        {
            ShowWindow(windowHandle, SwShow);
            SetForegroundWindow(windowHandle);
        }
    }

    private bool CommitCurrentStep()
    {
        if (_currentPageIndex == 1)
        {
            return ValidateInstallDirectory();
        }

        return true;
    }

    private bool ValidateInstallDirectory()
    {
        var installPath = _directoryPage.InstallPath;
        if (string.IsNullOrWhiteSpace(installPath))
        {
            MessageBox.Show(
                "Choose an installation folder before continuing.",
                "V-Notch Setup",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        try
        {
            _directoryPage.InstallPath = IOPath.GetFullPath(installPath);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"The selected install path is not valid.\n\n{ex.Message}",
                "V-Notch Setup",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }
    }

    private async void BeginInstallationAsync()
    {
        if (_isInstalling || _installationSucceeded)
        {
            return;
        }

        _isInstalling = true;
        _installProgressPage.ShowIndeterminate("Preparing installation...");

        try
        {
            var installOptions = new SetupInstallOptions(
                _sourceDirectory,
                _directoryPage.InstallPath,
                _startupOptionsPage.StartWithWindows);

            await SetupOperations.InstallAsync(installOptions, progress =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (progress.IsIndeterminate)
                    {
                        _installProgressPage.ShowIndeterminate(progress.StatusText);
                    }
                    else
                    {
                        _installProgressPage.SetProgress(
                            progress.CurrentStep,
                            progress.TotalSteps,
                            progress.StatusText);
                    }
                });
            });

            _isInstalling = false;
            _installationSucceeded = true;
            _installProgressPage.SetProgress(1, 1, "Installation complete.");
            AutoAdvanceToFinish();
        }
        catch (Exception ex)
        {
            _isInstalling = false;
            _installProgressPage.ShowFailure(ex.Message);
            BackButton.Visibility = Visibility.Visible;
            CancelButton.Visibility = Visibility.Visible;
            NextButton.Visibility = Visibility.Collapsed;
            SetNavigationEnabled(true);
        }
    }
}

// Directory Selection Page
public class DirectoryPage : UserControl, ISetupAnimatedPage
{
    private TextBox? _pathBox;
    private readonly TextBlock _headline;
    private readonly TextBlock _description;
    private readonly Border _container;
    
    public DirectoryPage(string initialInstallPath)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        
        _headline = new TextBlock
        {
            Text = "Choose Install Location",
            FontSize = 28,
            FontWeight = FontWeights.Bold,
            Foreground = System.Windows.Media.Brushes.White,
            FontFamily = new System.Windows.Media.FontFamily("SF Pro Display, Segoe UI Variable Display, Segoe UI, Inter, Roboto, Sans-serif"),
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(_headline, 0);
        grid.Children.Add(_headline);
        
        _description = new TextBlock
        {
            Text = "Select the folder where V-Notch will be installed.",
            FontSize = 14,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(204, 255, 255, 255)),
            FontFamily = new System.Windows.Media.FontFamily("SF Pro Text, Segoe UI, Inter, Roboto, Sans-serif"),
            Margin = new Thickness(0, 0, 0, 24)
        };
        Grid.SetRow(_description, 1);
        grid.Children.Add(_description);
        
        // Container with folder icon and path input
        _container = new Border
        {
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 26, 26, 26)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 60, 60, 60)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10)
        };
        
        var pathPanel = new Grid();
        pathPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
        pathPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pathPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
        
        // Folder icon
        var folderIcon = new TextBlock
        {
            Text = "\uE8B7",
            FontSize = 16,
            FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(204, 255, 255, 255)),
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(folderIcon, 0);
        pathPanel.Children.Add(folderIcon);
        
        _pathBox = new TextBox
        {
            Text = initialInstallPath,
            FontSize = 13,
            FontFamily = new System.Windows.Media.FontFamily("SF Pro Text, Segoe UI, Inter, Roboto, Sans-serif"),
            Background = System.Windows.Media.Brushes.Transparent,
            Foreground = System.Windows.Media.Brushes.White,
            BorderThickness = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0)
        };
        Grid.SetColumn(_pathBox, 1);
        pathPanel.Children.Add(_pathBox);
        
        // Browse button with icon
        var browseButton = new Button
        {
            Height = 32,
            Padding = new Thickness(12, 0, 12, 0),
            Margin = new Thickness(12, 0, 0, 0),
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            FontFamily = new System.Windows.Media.FontFamily("SF Pro Text, Segoe UI, Inter, Roboto, Sans-serif"),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 10, 122, 255)),
            Foreground = System.Windows.Media.Brushes.White,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand
        };
        
        var buttonContent = new StackPanel { Orientation = Orientation.Horizontal };
        
        var buttonText = new TextBlock
        {
            Text = "Browse",
            VerticalAlignment = VerticalAlignment.Center
        };
        buttonContent.Children.Add(buttonText);
        
        browseButton.Content = buttonContent;
        browseButton.Click += BrowseButton_Click;
        
        // Simple button template with rounded corners
        var buttonTemplate = new ControlTemplate(typeof(Button));
        var buttonBorder = new FrameworkElementFactory(typeof(Border));
        buttonBorder.Name = "border";
        buttonBorder.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
        buttonBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        buttonBorder.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));
        
        var contentPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
        contentPresenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        contentPresenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        buttonBorder.AppendChild(contentPresenter);
        
        buttonTemplate.VisualTree = buttonBorder;
        browseButton.Template = buttonTemplate;
        
        Grid.SetColumn(browseButton, 2);
        pathPanel.Children.Add(browseButton);
        
        _container.Child = pathPanel;
        Grid.SetRow(_container, 2);
        grid.Children.Add(_container);
        
        Content = grid;
    }

    public IReadOnlyList<UIElement> GetAnimatedElements()
    {
        return new UIElement[] { _headline, _description, _container };
    }

    public string InstallPath
    {
        get => _pathBox?.Text ?? string.Empty;
        set
        {
            if (_pathBox != null)
            {
                _pathBox.Text = value;
            }
        }
    }
    
    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Select Installation Folder",
            FileName = "Select Folder",
            Filter = "Folder|*.none",
            CheckFileExists = false,
            CheckPathExists = false
        };
        
        if (dialog.ShowDialog() == true)
        {
            if (_pathBox != null)
            {
                var selectedPath = System.IO.Path.GetDirectoryName(dialog.FileName);
                if (!string.IsNullOrEmpty(selectedPath))
                {
                    _pathBox.Text = selectedPath;
                }
            }
        }
    }
}

// Startup Options Page
public class StartupOptionsPage : UserControl, ISetupAnimatedPage
{
    private readonly TextBlock _headline;
    private readonly TextBlock _description;
    private readonly CheckBox _checkbox;

    public StartupOptionsPage(bool startWithWindows)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        
        _headline = new TextBlock
        {
            Text = "Startup Options",
            FontSize = 28,
            FontWeight = FontWeights.Bold,
            Foreground = System.Windows.Media.Brushes.White,
            FontFamily = new System.Windows.Media.FontFamily("SF Pro Display, Segoe UI Variable Display, Segoe UI, Inter, Roboto, Sans-serif"),
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(_headline, 0);
        grid.Children.Add(_headline);
        
        _description = new TextBlock
        {
            Text = "Configure how V-Notch starts with your system.",
            FontSize = 14,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(204, 255, 255, 255)),
            FontFamily = new System.Windows.Media.FontFamily("SF Pro Text, Segoe UI, Inter, Roboto, Sans-serif"),
            Margin = new Thickness(0, 0, 0, 32)
        };
        Grid.SetRow(_description, 1);
        grid.Children.Add(_description);
        
        _checkbox = new CheckBox
        {
            Content = "Launch V-Notch when Windows starts",
            IsChecked = startWithWindows,
            FontSize = 14,
            Foreground = System.Windows.Media.Brushes.White,
            FontFamily = new System.Windows.Media.FontFamily("SF Pro Text, Segoe UI, Inter, Roboto, Sans-serif")
        };
        Grid.SetRow(_checkbox, 2);
        grid.Children.Add(_checkbox);
        
        Content = grid;
    }

    public IReadOnlyList<UIElement> GetAnimatedElements()
    {
        return new UIElement[] { _headline, _description, _checkbox };
    }

    public bool StartWithWindows => _checkbox.IsChecked == true;
}

// Install Progress Page
public class InstallProgressPage : UserControl, ISetupAnimatedPage
{
    private readonly TextBlock _headline;
    private readonly TextBlock _status;
    private readonly ProgressBar _progressBar;
    
    public InstallProgressPage()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        
        _headline = new TextBlock
        {
            Text = "Installing V-Notch",
            FontSize = 28,
            FontWeight = FontWeights.Bold,
            Foreground = System.Windows.Media.Brushes.White,
            FontFamily = new System.Windows.Media.FontFamily("SF Pro Display, Segoe UI Variable Display, Segoe UI, Inter, Roboto, Sans-serif"),
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(_headline, 0);
        grid.Children.Add(_headline);
        
        _status = new TextBlock
        {
            Text = "Copying files...",
            FontSize = 14,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(204, 255, 255, 255)),
            FontFamily = new System.Windows.Media.FontFamily("SF Pro Text, Segoe UI, Inter, Roboto, Sans-serif"),
            Margin = new Thickness(0, 0, 0, 24)
        };
        Grid.SetRow(_status, 1);
        grid.Children.Add(_status);
        
        _progressBar = new ProgressBar
        {
            Height = 8,
            IsIndeterminate = true,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 26, 26, 26)),
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 48, 209, 88)),
            BorderThickness = new Thickness(0)
        };
        Grid.SetRow(_progressBar, 2);
        grid.Children.Add(_progressBar);
        
        Content = grid;
    }

    public IReadOnlyList<UIElement> GetAnimatedElements()
    {
        return new UIElement[] { _headline, _status, _progressBar };
    }

    public void ShowIndeterminate(string status)
    {
        _status.Text = status;
        _progressBar.IsIndeterminate = true;
        _progressBar.Value = 0;
        _progressBar.Foreground = new SolidColorBrush(Color.FromArgb(255, 48, 209, 88));
    }

    public void SetProgress(int current, int total, string status)
    {
        _status.Text = status;
        _progressBar.IsIndeterminate = false;
        _progressBar.Minimum = 0;
        _progressBar.Maximum = Math.Max(1, total);
        _progressBar.Value = Math.Min(Math.Max(0, current), _progressBar.Maximum);
        _progressBar.Foreground = new SolidColorBrush(Color.FromArgb(255, 48, 209, 88));
    }

    public void ShowFailure(string errorMessage)
    {
        _status.Text = $"Installation failed: {errorMessage}";
        _progressBar.IsIndeterminate = false;
        _progressBar.Minimum = 0;
        _progressBar.Maximum = 1;
        _progressBar.Value = 0;
        _progressBar.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 69, 58));
    }
}

// Finish Page
public class FinishPage : UserControl, ISetupEntryAwarePage, ISetupAnimatedPage
{
    private readonly Grid _icon;
    private readonly Ellipse _glow;
    private readonly System.Windows.Shapes.Path _checkPath;
    private readonly double _checkDashUnits;
    private readonly TextBlock _headline;
    private readonly TextBlock _description;
    private readonly CheckBox _checkbox;

    public FinishPage(bool launchAfterInstall)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        
        _icon = new Grid
        {
            Width = 84,
            Height = 84,
            Margin = new Thickness(0, 0, 0, 24),
            HorizontalAlignment = HorizontalAlignment.Left,
            RenderTransformOrigin = new Point(0.5, 0.5)
        };

        _glow = new Ellipse
        {
            Width = 84,
            Height = 84,
            Fill = new RadialGradientBrush(
                Color.FromArgb(90, 48, 209, 88),
                Color.FromArgb(0, 48, 209, 88)),
            Opacity = 0
        };
        _icon.Children.Add(_glow);

        var badge = new Ellipse
        {
            Width = 72,
            Height = 72,
            Fill = new LinearGradientBrush(
                Color.FromArgb(255, 16, 34, 22),
                Color.FromArgb(255, 12, 23, 16),
                90),
            Stroke = new SolidColorBrush(Color.FromArgb(48, 48, 209, 88)),
            StrokeThickness = 1.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _icon.Children.Add(badge);

        var ring = new Ellipse
        {
            Width = 62,
            Height = 62,
            Stroke = new SolidColorBrush(Color.FromArgb(32, 255, 255, 255)),
            StrokeThickness = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _icon.Children.Add(ring);

        var checkContainer = new Viewbox
        {
            Width = 44,
            Height = 34,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0),
            Stretch = Stretch.Uniform
        };

        _checkPath = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 4 18 L 16 30 L 40 4"),
            Stroke = new SolidColorBrush(Color.FromArgb(255, 48, 209, 88)),
            StrokeThickness = 5.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Stretch = Stretch.Fill
        };

        var checkPathLength = CalculatePathLength(_checkPath.Data);
        _checkDashUnits = checkPathLength / _checkPath.StrokeThickness;
        _checkPath.StrokeDashArray = new DoubleCollection { _checkDashUnits, _checkDashUnits };
        _checkPath.StrokeDashOffset = _checkDashUnits;

        checkContainer.Child = _checkPath;
        _icon.Children.Add(checkContainer);

        Grid.SetRow(_icon, 0);
        grid.Children.Add(_icon);
        
        _headline = new TextBlock
        {
            Text = "Installation Complete",
            FontSize = 28,
            FontWeight = FontWeights.Bold,
            Foreground = System.Windows.Media.Brushes.White,
            FontFamily = new System.Windows.Media.FontFamily("SF Pro Display, Segoe UI Variable Display, Segoe UI, Inter, Roboto, Sans-serif"),
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(_headline, 1);
        grid.Children.Add(_headline);
        
        _description = new TextBlock
        {
            Text = "V-Notch has been successfully installed on your computer.\n\nClick Finish to close the installer and launch V-Notch.",
            FontSize = 14,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(204, 255, 255, 255)),
            FontFamily = new System.Windows.Media.FontFamily("SF Pro Text, Segoe UI, Inter, Roboto, Sans-serif"),
            LineHeight = 22,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 24)
        };
        Grid.SetRow(_description, 2);
        grid.Children.Add(_description);
        
        _checkbox = new CheckBox
        {
            Content = "Launch V-Notch now",
            IsChecked = launchAfterInstall,
            FontSize = 14,
            Foreground = System.Windows.Media.Brushes.White,
            FontFamily = new System.Windows.Media.FontFamily("SF Pro Text, Segoe UI, Inter, Roboto, Sans-serif")
        };
        Grid.SetRow(_checkbox, 3);
        grid.Children.Add(_checkbox);
        
        Content = grid;
    }

    public IReadOnlyList<UIElement> GetAnimatedElements()
    {
        return new UIElement[] { _icon, _headline, _description, _checkbox };
    }

    public bool LaunchAfterInstall => _checkbox.IsChecked == true;

    public void OnPageEntryCompleted()
    {
        StartFinishIconAnimation(_glow, _checkPath, _checkDashUnits);
    }

    private static void StartFinishIconAnimation(Ellipse glow, System.Windows.Shapes.Path checkPath, double dashUnits)
    {
        glow.BeginAnimation(OpacityProperty, null);
        checkPath.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
        glow.Opacity = 0;
        checkPath.StrokeDashOffset = dashUnits;

        var glowFade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            BeginTime = TimeSpan.FromMilliseconds(40),
            Duration = TimeSpan.FromMilliseconds(260),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        var drawCheck = new DoubleAnimation
        {
            From = dashUnits,
            To = 0,
            BeginTime = TimeSpan.FromMilliseconds(10),
            Duration = TimeSpan.FromMilliseconds(520),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        glow.BeginAnimation(OpacityProperty, glowFade);
        checkPath.BeginAnimation(Shape.StrokeDashOffsetProperty, drawCheck);
    }

    private static double CalculatePathLength(Geometry geometry)
    {
        var pathGeometry = geometry.GetFlattenedPathGeometry();
        double length = 0;

        foreach (var figure in pathGeometry.Figures)
        {
            var currentPoint = figure.StartPoint;

            foreach (var segment in figure.Segments)
            {
                if (segment is PolyLineSegment polyLineSegment)
                {
                    foreach (var point in polyLineSegment.Points)
                    {
                        length += (point - currentPoint).Length;
                        currentPoint = point;
                    }
                }
                else if (segment is LineSegment lineSegment)
                {
                    length += (lineSegment.Point - currentPoint).Length;
                    currentPoint = lineSegment.Point;
                }
            }
        }

        return length;
    }
}
