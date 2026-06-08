using System;
using System.Windows;
using System.Windows.Controls;
using VNotch.Modules;
using VNotch.Presenters;
using VNotch.Services;

namespace VNotch;

public partial class MainWindow
{
    private CalendarPresenter? _calendarPresenter;


    internal void InitializeCalendarPresenter()
    {
        if (_calendarPresenter != null) return;

        var refs = new CalendarViewRefs
        {
            WeekDaysPanel = WeekDaysPanel,
            WeekNumbers = WeekNumbers,
            CalendarStripTranslate = CalendarStripTranslate,
            CalendarHighlightTranslate = CalendarHighlightTranslate,
            CalendarHighlightScale = CalendarHighlightScale,
            MonthText = MonthText,
            MonthTextScale = MonthTextScale,
            MonthTextTranslate = MonthTextTranslate,
            EventText = EventText,
            BatterySection = BatterySection,
            SettingsButton = SettingsButton,
            GreetingSection = GreetingSection,
            CalendarGreetingContextBlur = CalendarGreetingContextBlur,
            CalendarWidgetScale = CalendarWidgetScale,
            CalendarWidgetTranslate = CalendarWidgetTranslate,
            SmallTextStyle = (Style)FindResource("SmallText"),
            TitleTextStyle = (Style)FindResource("TitleText"),
            BrushBlack = _brushBlack,
            BrushWhite = _brushWhite,
            BrushTransparent = _brushTransparent,
            SettingsProvider = () => _settings,
            IsNonCalendarWidgetMode = () => IsNonCalendarWidgetMode,
            OnCalendarTick = now =>
            {
                if (_clockViewCalendarBuilt)
                {
                    UpdateClockViewCalendar(now);
                }
            }
        };

        _calendarPresenter = new CalendarPresenter(_calendarModule, new DispatcherService(Dispatcher), refs);
    }

    // JOIN: call in PerformCleanup (single idempotent teardown path).
    internal void DisposeCalendarPresenter()
    {
        _calendarPresenter?.Dispose();
        _calendarPresenter = null;
    }

    // --- XAML-wired event handlers (CalendarWidget in MainWindow.xaml) forward to presenter ---

    private void CalendarWidget_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        => _calendarPresenter?.HandleMouseEnter();

    private void CalendarWidget_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        => _calendarPresenter?.HandleMouseLeave();

    private void CalendarWidget_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        => _calendarPresenter?.HandleMouseWheel(e);

    // --- Methods consumed by other (non-editable) partials ---

    private void UpdateCalendarInfo() => _calendarPresenter?.UpdateCalendarInfo();

    public void ResetCalendarScroll() => _calendarPresenter?.ResetCalendarScroll();

    private void ResetCalendarHoverFocusVisualState() => _calendarPresenter?.ResetHoverFocusVisualState();
}
