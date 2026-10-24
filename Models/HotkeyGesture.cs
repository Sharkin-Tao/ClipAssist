using System.Windows.Input;

namespace ClipAssist.Models;

/// <summary>
/// 一个全局快捷键：修饰键 + 一个普通键。
/// 只负责语法解析、规范化显示和“是否适合做全局快捷键”的校验；
/// 真正的系统注册由 HotkeyService 完成。
/// </summary>
public readonly record struct HotkeyGesture(ModifierKeys Modifiers, Key Key)
{
    private const ModifierKeys AllModifiers =
        ModifierKeys.Alt | ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Windows;

    public static HotkeyGesture Default { get; } =
        new(ModifierKeys.Control | ModifierKeys.Shift, Key.V);

    public string DisplayText => Format(Modifiers, Key);

    public bool IsValid => TryCreate(Modifiers, Key, out _, out _);

    public static bool TryCreate(
        ModifierKeys modifiers,
        Key key,
        out HotkeyGesture gesture,
        out string? error)
    {
        gesture = default;
        error = null;

        modifiers &= AllModifiers;

        if (modifiers == ModifierKeys.None)
        {
            error = "快捷键至少需要一个修饰键（Ctrl、Alt、Shift 或 Win）。";
            return false;
        }

        if (!IsSupportedKey(key))
        {
            error = "这个按键不能用作全局快捷键，请换一个普通键。";
            return false;
        }

        if (modifiers == ModifierKeys.Shift)
        {
            error = "不能只使用 Shift 作为全局快捷键，Shift+A 会干扰正常输入。";
            return false;
        }

        if (IsReservedByWindows(modifiers, key))
        {
            error = $"快捷键 {Format(modifiers, key)} 是 Windows 保留组合，不能注册。";
            return false;
        }

        if (IsLetterOrDigit(key) && CountModifiers(modifiers) < 2)
        {
            error = "字母或数字键至少需要两个修饰键，避免全局劫持 Ctrl+C、Ctrl+V 等常用操作。";
            return false;
        }

        gesture = new HotkeyGesture(modifiers, key);
        return true;
    }

    public static bool TryParse(string? text, out HotkeyGesture gesture, out string? error)
    {
        gesture = default;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "快捷键不能为空。";
            return false;
        }

        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            error = "快捷键格式应为“修饰键+按键”，例如 Ctrl+Shift+V。";
            return false;
        }

        var modifiers = ModifierKeys.None;

        for (var index = 0; index < parts.Length - 1; index++)
        {
            if (!TryParseModifier(parts[index], out var modifier))
            {
                error = $"不认识的修饰键：{parts[index]}。";
                return false;
            }

            modifiers |= modifier;
        }

        if (!TryParseKey(parts[^1], out var key))
        {
            error = $"不支持的按键：{parts[^1]}。";
            return false;
        }

        return TryCreate(modifiers, key, out gesture, out error);
    }

    /// <summary>录入过程中只按了修饰键时，用这个显示实时预览。</summary>
    public static string FormatModifiers(ModifierKeys modifiers)
    {
        modifiers &= AllModifiers;
        var parts = new List<string>();

        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            parts.Add("Win");
        }

        return parts.Count == 0 ? "…" : string.Join("+", parts) + "+…";
    }

    public static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or
        Key.LWin or Key.RWin;

    private static bool IsSupportedKey(Key key)
    {
        var value = (int)key;

        if (value >= (int)Key.A && value <= (int)Key.Z)
        {
            return true;
        }

        if (value >= (int)Key.D0 && value <= (int)Key.D9)
        {
            return true;
        }

        if (value >= (int)Key.NumPad0 && value <= (int)Key.NumPad9)
        {
            return true;
        }

        if (value >= (int)Key.F1 && value <= (int)Key.F24)
        {
            return true;
        }

        return key is Key.Up or Key.Down or Key.Left or Key.Right
            or Key.Home or Key.End or Key.PageUp or Key.PageDown
            or Key.Insert or Key.Delete
            or Key.Space or Key.Enter or Key.Tab or Key.Escape or Key.Back;
    }

    private static bool IsLetterOrDigit(Key key)
    {
        var value = (int)key;

        return value >= (int)Key.A && value <= (int)Key.Z
            || value >= (int)Key.D0 && value <= (int)Key.D9
            || value >= (int)Key.NumPad0 && value <= (int)Key.NumPad9;
    }

    private static int CountModifiers(ModifierKeys modifiers)
    {
        var count = 0;

        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            count++;
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            count++;
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            count++;
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            count++;
        }

        return count;
    }

    private static bool IsReservedByWindows(ModifierKeys modifiers, Key key)
    {
        // 这些组合由系统/壳层占用，注册了也容易失效或抢占系统行为。
        if (modifiers.HasFlag(ModifierKeys.Alt) && key == Key.Tab)
        {
            return true;
        }

        if (modifiers == ModifierKeys.Alt && key == Key.F4)
        {
            return true;
        }

        if (modifiers == ModifierKeys.Control && key == Key.Escape)
        {
            return true;
        }

        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.Escape)
        {
            return true;
        }

        if (modifiers == (ModifierKeys.Control | ModifierKeys.Alt) && key == Key.Delete)
        {
            return true;
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            // 常见 Win 系统快捷键，注册会抢占系统行为。
            if (key is Key.L or Key.D or Key.E or Key.R or Key.S or Key.X
                or Key.A or Key.I or Key.V or Key.Tab or Key.Space)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseModifier(string token, out ModifierKeys modifier)
    {
        switch (token.Trim().ToUpperInvariant())
        {
            case "CTRL":
            case "CONTROL":
                modifier = ModifierKeys.Control;
                return true;
            case "ALT":
                modifier = ModifierKeys.Alt;
                return true;
            case "SHIFT":
                modifier = ModifierKeys.Shift;
                return true;
            case "WIN":
            case "WINDOWS":
                modifier = ModifierKeys.Windows;
                return true;
            default:
                modifier = ModifierKeys.None;
                return false;
        }
    }

    private static bool TryParseKey(string token, out Key key)
    {
        var normalized = token.Trim();

        if (normalized.Length == 1)
        {
            var character = char.ToUpperInvariant(normalized[0]);

            if (character is >= 'A' and <= 'Z')
            {
                key = (Key)((int)Key.A + (character - 'A'));
                return true;
            }

            if (character is >= '0' and <= '9')
            {
                key = (Key)((int)Key.D0 + (character - '0'));
                return true;
            }
        }

        switch (normalized.ToUpperInvariant())
        {
            case "ESC":
            case "ESCAPE":
                key = Key.Escape;
                return true;
            case "RETURN":
            case "ENTER":
                key = Key.Enter;
                return true;
            case "BACKSPACE":
            case "BACK":
                key = Key.Back;
                return true;
            case "SPACE":
                key = Key.Space;
                return true;
            case "TAB":
                key = Key.Tab;
                return true;
            case "UP":
                key = Key.Up;
                return true;
            case "DOWN":
                key = Key.Down;
                return true;
            case "LEFT":
                key = Key.Left;
                return true;
            case "RIGHT":
                key = Key.Right;
                return true;
            case "HOME":
                key = Key.Home;
                return true;
            case "END":
                key = Key.End;
                return true;
            case "PAGEUP":
            case "PGUP":
                key = Key.PageUp;
                return true;
            case "PAGEDOWN":
            case "PGDN":
                key = Key.PageDown;
                return true;
            case "INSERT":
            case "INS":
                key = Key.Insert;
                return true;
            case "DELETE":
            case "DEL":
                key = Key.Delete;
                return true;
        }

        if (normalized.StartsWith('F') &&
            int.TryParse(normalized[1..], out var functionNumber) &&
            functionNumber is >= 1 and <= 24)
        {
            key = (Key)((int)Key.F1 + (functionNumber - 1));
            return true;
        }

        if (normalized.StartsWith("NUMPAD", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(normalized[6..], out var numPadNumber) &&
            numPadNumber is >= 0 and <= 9)
        {
            key = (Key)((int)Key.NumPad0 + numPadNumber);
            return true;
        }

        key = Key.None;
        return false;
    }

    private static string Format(ModifierKeys modifiers, Key key)
    {
        modifiers &= AllModifiers;
        var parts = new List<string>();

        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            parts.Add("Win");
        }

        parts.Add(FormatKey(key));
        return string.Join("+", parts);
    }

    private static string FormatKey(Key key)
    {
        var value = (int)key;

        if (value >= (int)Key.D0 && value <= (int)Key.D9)
        {
            return (value - (int)Key.D0).ToString();
        }

        if (value >= (int)Key.NumPad0 && value <= (int)Key.NumPad9)
        {
            return "NumPad" + (value - (int)Key.NumPad0);
        }

        return key switch
        {
            Key.Escape => "Esc",
            Key.Back => "Backspace",
            _ => key.ToString(),
        };
    }
}