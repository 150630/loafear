using System.Text.Json.Serialization;
using System.Windows.Input;
using LoafEar.Interop;

namespace LoafEar.Config;

/// <summary>
/// 一个全局热键的定义。存进 JSON，用户可以在主界面里重新录制。
/// </summary>
public sealed class HotkeySpec
{
    /// <summary>修饰键组合，取值同 Win32 的 MOD_*。</summary>
    public uint Modifiers { get; set; } = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT;

    /// <summary>
    /// 主键的虚拟键码。
    /// 名字不叫 Key 是为了避开 System.Windows.Input.Key 枚举的同名冲突。
    /// </summary>
    [JsonPropertyName("Key")]
    public uint VirtualKey { get; set; }

    public HotkeySpec Clone() => new() { Modifiers = Modifiers, VirtualKey = VirtualKey };

    [JsonIgnore]
    public bool IsValid => VirtualKey != 0 && Modifiers != 0;

    /// <summary>是否和另一个热键完全一样，用来拦住重复绑定。</summary>
    public bool SameAs(HotkeySpec? other) =>
        other is not null && other.VirtualKey == VirtualKey && other.Modifiers == Modifiers;

    [JsonIgnore]
    public string Display => IsValid ? $"{ModifierText()}{KeyText()}" : "未设置";

    private string ModifierText()
    {
        var text = string.Empty;
        if ((Modifiers & NativeMethods.MOD_CONTROL) != 0)
            text += "Ctrl+";
        if ((Modifiers & NativeMethods.MOD_ALT) != 0)
            text += "Alt+";
        if ((Modifiers & NativeMethods.MOD_SHIFT) != 0)
            text += "Shift+";
        if ((Modifiers & NativeMethods.MOD_WIN) != 0)
            text += "Win+";
        return text;
    }

    private string KeyText() => VirtualKeyName(VirtualKey);

    private static string VirtualKeyName(uint vk) =>
        vk switch
        {
            0x20 => "Space",
            0xBC => ",",
            0xBE => ".",
            0xBA => ";",
            0xBD => "-",
            0xBB => "=",
            0xDB => "[",
            0xDD => "]",
            0xDC => "\\",
            0xBF => "/",
            0xC0 => "`",
            0x25 => "←",
            0x26 => "↑",
            0x27 => "→",
            0x28 => "↓",
            0x0D => "Enter",
            0x09 => "Tab",
            0x08 => "Backspace",
            >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",
            >= 0x30 and <= 0x39 => ((char)vk).ToString(),
            >= 0x41 and <= 0x5A => ((char)vk).ToString(),
            _ => $"VK{vk:X2}",
        };

    /// <summary>把 WPF 的按键事件翻译成虚拟键码 + 修饰键。返回 null 表示这个键不能用作热键。</summary>
    public static HotkeySpec? FromKeyEvent(KeyEventArgs e)
    {
        var mods = 0u;

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            mods |= NativeMethods.MOD_CONTROL;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            mods |= NativeMethods.MOD_ALT;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            mods |= NativeMethods.MOD_SHIFT;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows))
            mods |= NativeMethods.MOD_WIN;

        // SystemKey 是按住 Alt 时 WPF 报告真实按键的位置
        var key = e.Key == System.Windows.Input.Key.System
            ? e.SystemKey
            : e.Key;

        // 只按了修饰键本身，还没到主键
        if (key is System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl
            or System.Windows.Input.Key.LeftAlt or System.Windows.Input.Key.RightAlt
            or System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift
            or System.Windows.Input.Key.LWin or System.Windows.Input.Key.RWin)
        {
            return null;
        }

        var vk = KeyInterop.VirtualKeyFromKey(key);

        // 0 或超出单字节范围（如 IME 组合键）不能用
        if (vk <= 0 || vk > 0xFE)
            return null;

        return new HotkeySpec { Modifiers = mods, VirtualKey = (uint)vk };
    }
}
