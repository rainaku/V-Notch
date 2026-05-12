using System;
using System.Collections.Generic;

namespace VNotch.Services;

/// <summary>
/// Lightweight localization service for V-Notch.
/// Supports English ("en") and Vietnamese ("vi").
/// </summary>
public static class Loc
{
    private static string _currentLanguage = "en";
    private static readonly Dictionary<string, Dictionary<string, string>> _strings = new();

    public static string CurrentLanguage => _currentLanguage;

    public static void SetLanguage(string language)
    {
        _currentLanguage = language == "vi" ? "vi" : "en";
    }

    /// <summary>
    /// Gets the localized string for the given key.
    /// Falls back to English if the key is not found in the current language.
    /// Falls back to the key itself if not found at all.
    /// </summary>
    public static string Get(string key)
    {
        if (_strings.TryGetValue(_currentLanguage, out var langDict) && langDict.TryGetValue(key, out var value))
            return value;

        if (_currentLanguage != "en" && _strings.TryGetValue("en", out var enDict) && enDict.TryGetValue(key, out var enValue))
            return enValue;

        return key;
    }

    /// <summary>
    /// Gets a formatted localized string.
    /// </summary>
    public static string Get(string key, params object[] args)
    {
        var template = Get(key);
        try { return string.Format(template, args); }
        catch { return template; }
    }

    static Loc()
    {
        InitializeEnglish();
        InitializeVietnamese();
    }

    private static void InitializeEnglish()
    {
        _strings["en"] = new Dictionary<string, string>
        {
            // ─── Settings Window ───
            ["settings.title"] = "Notch Settings",
            ["settings.subtitle"] = "Tune behavior, visuals, and system integration.",
            ["settings.appearance"] = "APPEARANCE",
            ["settings.behavior"] = "BEHAVIOR",
            ["settings.updates"] = "UPDATES",
            ["settings.display"] = "DISPLAY",
            ["settings.system"] = "SYSTEM",
            ["settings.width"] = "Width",
            ["settings.width.hint"] = "How wide the compact notch should sit on screen.",
            ["settings.height"] = "Height",
            ["settings.height.hint"] = "Controls the compact profile thickness.",
            ["settings.cornerRadius"] = "Corner Radius",
            ["settings.cornerRadius.hint"] = "Sharper for utility, softer for an island-like feel.",
            ["settings.opacity"] = "Opacity",
            ["settings.opacity.hint"] = "Blend the notch into the desktop or keep it crisp and solid.",
            ["settings.blurBrightness"] = "Blur Brightness",
            ["settings.blurBrightness.hint"] = "Brighten the expanded media blur without changing the notch body opacity.",
            ["settings.hoverExpand"] = "Expand on hover",
            ["settings.hoverExpand.hint"] = "Open the notch automatically when the pointer lingers nearby.",
            ["settings.expandDelay"] = "Expand Delay",
            ["settings.expandDelay.hint"] = "Delay before hover opens the notch.",
            ["settings.activeMonitor"] = "Active Monitor",
            ["settings.activeMonitor.hint"] = "Choose which screen anchors the notch.",
            ["settings.autoStart"] = "Start with Windows",
            ["settings.autoStart.hint"] = "Launch automatically when you sign in.",
            ["settings.musicNotify"] = "Show music notifications",
            ["settings.systemNotify"] = "Show system notifications",
            ["settings.shelfUnlock"] = "Unlock shelf upload limit",
            ["settings.checkingUpdates"] = "Checking for updates...",
            ["settings.upToDate"] = "You're up to date",
            ["settings.updateAvailable"] = "Update available: v{0}",
            ["settings.currentVersion"] = "Current version: {0}",
            ["settings.checkUpdate"] = "Check",
            ["settings.download"] = "Download",
            ["settings.reset"] = "Reset to defaults",

            // ─── Tray Menu ───
            ["tray.hide"] = "Hide Notch",
            ["tray.show"] = "Show Notch",
            ["tray.reset"] = "Reset position",
            ["tray.settings"] = "Notch settings",
            ["tray.exit"] = "Exit",

            // ─── File Shelf ───
            ["shelf.placeholder"] = "Drag files here for temporary storage",
            ["shelf.full"] = "Shelf full ({0}/{1}). Remove files before adding more.",
            ["shelf.unlockHint"] = "Drop to unlock upload limit",
            ["shelf.unlockMessage"] = "You're uploading {0} files. The safe limit is 30 files.\nIf you'd like to upload more, you can unlock the upload limit right here.",
            ["shelf.unlockButton"] = "Unlock limit",
            ["shelf.unlockDismiss"] = "Cancel",
            ["shelf.unlockSettingsHint"] = "You can also customize this in Settings.",
            ["shelf.removeFromShelf"] = "Remove from shelf",
            ["shelf.alreadyOnShelf"] = "These files are already on the shelf.",
            ["shelf.noFiles"] = "No files detected.",
            ["shelf.exceedsLimit"] = "Shelf limit is {0} files. Only {1} slot(s) left.",

            // ─── Setup Wizard ───
            ["setup.title"] = "V-Notch Setup",
            ["setup.step.language"] = "01  Language",
            ["setup.step.welcome"] = "02  Welcome",
            ["setup.step.about"] = "03  About",
            ["setup.step.location"] = "04  Location",
            ["setup.step.startup"] = "05  Startup",
            ["setup.step.install"] = "06  Install",
            ["setup.step.finish"] = "07  Finish",
            ["setup.welcome.headline"] = "Welcome to V-Notch ✨",
            ["setup.welcome.body"] = "Hey there! Thanks for choosing V-Notch.\n\nWe're about to set up a sleek little notch at the top of your screen — it handles your music, battery, calendar, file shelf, and more. Think of it as a tiny command center that stays out of your way until you need it.\n\nThis will only take a minute. Let's get started!",
            ["setup.btn.continue"] = "Continue",
            ["setup.btn.back"] = "Back",
            ["setup.btn.cancel"] = "Cancel",
            ["setup.btn.finish"] = "Finish",
            ["setup.btn.keepSetup"] = "Keep setup",
            ["setup.btn.cancelSetup"] = "Cancel setup",
            ["setup.language.headline"] = "Choose Language",
            ["setup.language.description"] = "Select your preferred language for V-Notch.",
            ["setup.intro.eyebrow"] = "About the project",
            ["setup.intro.headline"] = "Built by rainaku",
            ["setup.intro.lead"] = "V-Notch is an independent Windows project inspired by Dynamic Island and built in public.",
            ["setup.intro.projectTitle"] = "Independent project",
            ["setup.intro.projectBody"] = "Designed and maintained by rainaku for media, notifications, battery, calendar, and quick controls.",
            ["setup.intro.sourceTitle"] = "Open source",
            ["setup.intro.sourceBody"] = "Code, releases, and issues stay public on GitHub. V-Notch is Apache-2.0 licensed and free to use.",
            ["setup.directory.headline"] = "Choose Install Location",
            ["setup.directory.description"] = "Select the folder where V-Notch will be installed.",
            ["setup.directory.browse"] = "Browse",
            ["setup.startup.headline"] = "Startup Options",
            ["setup.startup.description"] = "Configure how V-Notch starts with your system.",
            ["setup.startup.checkbox"] = "Launch V-Notch when Windows starts",
            ["setup.finish.headline"] = "Installation Complete",
            ["setup.finish.description"] = "V-Notch has been successfully installed on your computer.\n\nClick Finish to close the installer and launch V-Notch.",
            ["setup.finish.launch"] = "Launch V-Notch now",
            ["setup.cancel.headline"] = "Cancel setup?",
            ["setup.cancel.description"] = "V-Notch will not be installed and setup will close.",
            ["setup.cancel.warningTitle"] = "You can go back and continue setup, or confirm to exit now.",
            ["setup.cancel.warningBody"] = "Choose Keep setup to return to the current step, or Cancel setup to close the installer.",

            // ─── Errors / Messages ───
            ["error.title"] = "V-Notch Error",
            ["error.unexpected"] = "An unexpected error occurred: {0}",
            ["error.alreadyRunning"] = "V-Notch is already running!",
            ["error.updateFailed"] = "Unable to download/install update right now. Please try again.",
            ["error.updateFailedTitle"] = "Update Failed",

            // ─── Misc ───
            ["greeting.morning"] = "Good morning",
            ["greeting.afternoon"] = "Good afternoon",
            ["greeting.evening"] = "Good evening",
        };
    }

    private static void InitializeVietnamese()
    {
        _strings["vi"] = new Dictionary<string, string>
        {
            // ─── Settings Window ───
            ["settings.title"] = "Cài đặt Notch",
            ["settings.subtitle"] = "Tùy chỉnh hành vi, giao diện và tích hợp hệ thống.",
            ["settings.appearance"] = "GIAO DIỆN",
            ["settings.behavior"] = "HÀNH VI",
            ["settings.updates"] = "CẬP NHẬT",
            ["settings.display"] = "MÀN HÌNH",
            ["settings.system"] = "HỆ THỐNG",
            ["settings.width"] = "Chiều rộng",
            ["settings.width.hint"] = "Độ rộng của notch ở chế độ thu gọn.",
            ["settings.height"] = "Chiều cao",
            ["settings.height.hint"] = "Độ dày của notch ở chế độ thu gọn.",
            ["settings.cornerRadius"] = "Bo góc",
            ["settings.cornerRadius.hint"] = "Sắc nét hơn cho tiện ích, mềm mại hơn cho cảm giác island.",
            ["settings.opacity"] = "Độ mờ",
            ["settings.opacity.hint"] = "Hòa notch vào desktop hoặc giữ nét rõ ràng.",
            ["settings.blurBrightness"] = "Độ sáng Blur",
            ["settings.blurBrightness.hint"] = "Tăng sáng blur media mở rộng mà không thay đổi độ mờ thân notch.",
            ["settings.hoverExpand"] = "Mở rộng khi di chuột",
            ["settings.hoverExpand.hint"] = "Tự động mở notch khi con trỏ lưu lại gần đó.",
            ["settings.expandDelay"] = "Độ trễ mở rộng",
            ["settings.expandDelay.hint"] = "Thời gian chờ trước khi hover mở notch.",
            ["settings.activeMonitor"] = "Màn hình hiển thị",
            ["settings.activeMonitor.hint"] = "Chọn màn hình neo notch.",
            ["settings.autoStart"] = "Khởi động cùng Windows",
            ["settings.autoStart.hint"] = "Tự động chạy khi bạn đăng nhập.",
            ["settings.musicNotify"] = "Hiện thông báo nhạc",
            ["settings.systemNotify"] = "Hiện thông báo hệ thống",
            ["settings.shelfUnlock"] = "Mở khóa giới hạn tải lên shelf",
            ["settings.checkingUpdates"] = "Đang kiểm tra cập nhật...",
            ["settings.upToDate"] = "Bạn đang dùng phiên bản mới nhất",
            ["settings.updateAvailable"] = "Có bản cập nhật: v{0}",
            ["settings.currentVersion"] = "Phiên bản hiện tại: {0}",
            ["settings.checkUpdate"] = "Kiểm tra",
            ["settings.download"] = "Tải xuống",
            ["settings.reset"] = "Khôi phục mặc định",

            // ─── Tray Menu ───
            ["tray.hide"] = "Ẩn Notch",
            ["tray.show"] = "Hiện Notch",
            ["tray.reset"] = "Đặt lại vị trí",
            ["tray.settings"] = "Cài đặt Notch",
            ["tray.exit"] = "Thoát",

            // ─── File Shelf ───
            ["shelf.placeholder"] = "Kéo thả file vào đây để lưu tạm",
            ["shelf.full"] = "Shelf đầy ({0}/{1}). Xóa file trước khi thêm mới.",
            ["shelf.unlockHint"] = "Thả để mở khóa giới hạn tải lên",
            ["shelf.unlockMessage"] = "Bạn đang tải lên {0} file. Giới hạn an toàn là 30 file.\nNếu muốn tải thêm, bạn có thể mở khóa giới hạn ngay tại đây.",
            ["shelf.unlockButton"] = "Mở khóa",
            ["shelf.unlockDismiss"] = "Hủy",
            ["shelf.unlockSettingsHint"] = "Bạn cũng có thể tùy chỉnh trong Cài đặt.",
            ["shelf.removeFromShelf"] = "Xóa khỏi shelf",
            ["shelf.alreadyOnShelf"] = "Các file này đã có trên shelf.",
            ["shelf.noFiles"] = "Không phát hiện file nào.",
            ["shelf.exceedsLimit"] = "Giới hạn shelf là {0} file. Chỉ còn {1} chỗ trống.",

            // ─── Setup Wizard ───
            ["setup.title"] = "Cài đặt V-Notch",
            ["setup.step.language"] = "01  Ngôn ngữ",
            ["setup.step.welcome"] = "02  Chào mừng",
            ["setup.step.about"] = "03  Giới thiệu",
            ["setup.step.location"] = "04  Vị trí",
            ["setup.step.startup"] = "05  Khởi động",
            ["setup.step.install"] = "06  Cài đặt",
            ["setup.step.finish"] = "07  Hoàn tất",
            ["setup.welcome.headline"] = "Chào mừng đến với V-Notch ✨",
            ["setup.welcome.body"] = "Xin chào! Cảm ơn bạn đã chọn V-Notch.\n\nChúng mình sắp thiết lập một thanh notch nhỏ gọn ở đầu màn hình — nó quản lý nhạc, pin, lịch, file shelf và nhiều thứ khác. Hãy nghĩ nó như một trung tâm điều khiển mini, luôn ở đó khi bạn cần.\n\nChỉ mất một phút thôi. Bắt đầu nào!",
            ["setup.btn.continue"] = "Tiếp tục",
            ["setup.btn.back"] = "Quay lại",
            ["setup.btn.cancel"] = "Hủy",
            ["setup.btn.finish"] = "Hoàn tất",
            ["setup.btn.keepSetup"] = "Tiếp tục cài đặt",
            ["setup.btn.cancelSetup"] = "Hủy cài đặt",
            ["setup.language.headline"] = "Chọn ngôn ngữ",
            ["setup.language.description"] = "Chọn ngôn ngữ bạn muốn sử dụng cho V-Notch.",
            ["setup.intro.eyebrow"] = "Về dự án",
            ["setup.intro.headline"] = "Được xây dựng bởi rainaku",
            ["setup.intro.lead"] = "V-Notch là dự án Windows độc lập lấy cảm hứng từ Dynamic Island và được phát triển công khai.",
            ["setup.intro.projectTitle"] = "Dự án độc lập",
            ["setup.intro.projectBody"] = "Được thiết kế và duy trì bởi rainaku cho media, thông báo, pin, lịch và điều khiển nhanh.",
            ["setup.intro.sourceTitle"] = "Mã nguồn mở",
            ["setup.intro.sourceBody"] = "Code, bản phát hành và issues được công khai trên GitHub. V-Notch được cấp phép Apache-2.0 và miễn phí sử dụng.",
            ["setup.directory.headline"] = "Chọn vị trí cài đặt",
            ["setup.directory.description"] = "Chọn thư mục để cài đặt V-Notch.",
            ["setup.directory.browse"] = "Duyệt",
            ["setup.startup.headline"] = "Tùy chọn khởi động",
            ["setup.startup.description"] = "Cấu hình cách V-Notch khởi động cùng hệ thống.",
            ["setup.startup.checkbox"] = "Chạy V-Notch khi Windows khởi động",
            ["setup.finish.headline"] = "Cài đặt hoàn tất",
            ["setup.finish.description"] = "V-Notch đã được cài đặt thành công trên máy tính của bạn.\n\nNhấn Hoàn tất để đóng trình cài đặt và khởi chạy V-Notch.",
            ["setup.finish.launch"] = "Khởi chạy V-Notch ngay",
            ["setup.cancel.headline"] = "Hủy cài đặt?",
            ["setup.cancel.description"] = "V-Notch sẽ không được cài đặt và trình cài đặt sẽ đóng.",
            ["setup.cancel.warningTitle"] = "Bạn có thể quay lại và tiếp tục, hoặc xác nhận thoát ngay.",
            ["setup.cancel.warningBody"] = "Chọn Tiếp tục cài đặt để quay lại bước hiện tại, hoặc Hủy cài đặt để đóng trình cài đặt.",

            // ─── Errors / Messages ───
            ["error.title"] = "Lỗi V-Notch",
            ["error.unexpected"] = "Đã xảy ra lỗi không mong muốn: {0}",
            ["error.alreadyRunning"] = "V-Notch đang chạy rồi!",
            ["error.updateFailed"] = "Không thể tải/cài đặt bản cập nhật. Vui lòng thử lại.",
            ["error.updateFailedTitle"] = "Cập nhật thất bại",

            // ─── Misc ───
            ["greeting.morning"] = "Chào buổi sáng",
            ["greeting.afternoon"] = "Chào buổi chiều",
            ["greeting.evening"] = "Chào buổi tối",
        };
    }
}
