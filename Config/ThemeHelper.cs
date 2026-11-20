using Microsoft.Win32;

namespace LoafEar.Config;

public static class ThemeHelper
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>系统「应用」是否使用深色主题。</summary>
    public static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int flag && flag == 0;
        }
        catch
        {
            return false;
        }
    }
}
