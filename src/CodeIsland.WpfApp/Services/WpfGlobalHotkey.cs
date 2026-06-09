using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CodeIsland.WpfApp.Services;

public sealed class WpfGlobalHotkey : IDisposable
{
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;
    private const int WM_HOTKEY = 0x0312;
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _callbacks = new();
    private readonly List<RegisteredHotkey> _registeredHotkeys = new();
    private int _nextId = 1;

    public WpfGlobalHotkey()
    {
        var parameters = new HwndSourceParameters("CodeIsland WPF Hotkey")
        {
            Width = 0,
            Height = 0,
            WindowStyle = unchecked((int)0x80000000)
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    public bool RegisterConfigured(string togglePanelHotkey, string approveHotkey, string denyHotkey, Action onTogglePanel, Action onApprove, Action onDeny, out string message)
    {
        var parsed = new (string Label, string Text, Action Callback, uint Modifiers, uint Vk)[3];
        var errors = new List<string>();

        TryParseConfigured("切换面板", togglePanelHotkey, onTogglePanel, parsed, 0, errors);
        TryParseConfigured("批准", approveHotkey, onApprove, parsed, 1, errors);
        TryParseConfigured("拒绝", denyHotkey, onDeny, parsed, 2, errors);
        AddDuplicateHotkeyErrors(parsed, errors);

        if (errors.Count > 0)
        {
            message = string.Join("；", errors);
            return false;
        }

        var previousCallbacks = new Dictionary<int, Action>(_callbacks);
        var previousHotkeys = _registeredHotkeys.ToArray();
        UnregisterAll();

        foreach (var item in parsed)
        {
            var id = _nextId++;
            if (!RegisterHotKey(_source.Handle, id, item.Modifiers | MOD_NOREPEAT, item.Vk))
            {
                errors.Add($"{item.Label}快捷键注册失败，可能已被占用：{item.Text}");
                continue;
            }
            _callbacks[id] = item.Callback;
            _registeredHotkeys.Add(new RegisteredHotkey(id, item.Modifiers | MOD_NOREPEAT, item.Vk));
        }

        if (errors.Count == 0)
        {
            message = "快捷键已生效";
            return true;
        }

        UnregisterAll();
        _callbacks.Clear();
        foreach (var item in previousHotkeys)
        {
            if (RegisterHotKey(_source.Handle, item.Id, item.Modifiers, item.VirtualKey)
                && previousCallbacks.TryGetValue(item.Id, out var callback))
            {
                _callbacks[item.Id] = callback;
                _registeredHotkeys.Add(item);
            }
        }

        message = string.Join("；", errors) + "；已保留原有快捷键";
        return false;
    }

    public static bool ValidateConfigured(string togglePanelHotkey, string approveHotkey, string denyHotkey, out string message)
    {
        var parsed = new (string Label, string Text, Action Callback, uint Modifiers, uint Vk)[3];
        var errors = new List<string>();
        TryParseConfigured("切换面板", togglePanelHotkey, static () => { }, parsed, 0, errors);
        TryParseConfigured("批准", approveHotkey, static () => { }, parsed, 1, errors);
        TryParseConfigured("拒绝", denyHotkey, static () => { }, parsed, 2, errors);
        AddDuplicateHotkeyErrors(parsed, errors);
        message = errors.Count == 0 ? "" : string.Join("；", errors);
        return errors.Count == 0;
    }

    private static void TryParseConfigured(string label, string text, Action callback, (string Label, string Text, Action Callback, uint Modifiers, uint Vk)[] parsed, int index, List<string> errors)
    {
        if (!TryParse(text, out var modifiers, out var vk, out var error))
        {
            errors.Add($"{label}快捷键无效：{error}");
            return;
        }

        parsed[index] = (label, text, callback, modifiers, vk);
    }

    private static void AddDuplicateHotkeyErrors((string Label, string Text, Action Callback, uint Modifiers, uint Vk)[] parsed, List<string> errors)
    {
        for (var i = 0; i < parsed.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(parsed[i].Text))
                continue;

            for (var j = i + 1; j < parsed.Length; j++)
            {
                if (string.IsNullOrWhiteSpace(parsed[j].Text))
                    continue;

                if (parsed[i].Modifiers == parsed[j].Modifiers && parsed[i].Vk == parsed[j].Vk)
                    errors.Add($"{parsed[i].Label}和{parsed[j].Label}快捷键不能相同：{parsed[i].Text}");
            }
        }
    }

    private static bool TryParse(string text, out uint modifiers, out uint vk, out string error)
    {
        modifiers = 0;
        vk = 0;
        error = "";
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            error = "不能为空";
            return false;
        }
        foreach (var part in parts[..^1])
        {
            if (string.Equals(part, "Ctrl", StringComparison.OrdinalIgnoreCase) || string.Equals(part, "Control", StringComparison.OrdinalIgnoreCase)) modifiers |= MOD_CONTROL;
            else if (string.Equals(part, "Alt", StringComparison.OrdinalIgnoreCase)) modifiers |= MOD_ALT;
            else if (string.Equals(part, "Shift", StringComparison.OrdinalIgnoreCase)) modifiers |= MOD_SHIFT;
            else if (string.Equals(part, "Win", StringComparison.OrdinalIgnoreCase)) modifiers |= MOD_WIN;
            else { error = $"未知修饰键 {part}"; return false; }
        }
        var key = parts[^1].ToUpperInvariant();
        if (key.Length == 1 && key[0] is >= 'A' and <= 'Z') vk = key[0];
        else if (key.Length == 1 && key[0] is >= '0' and <= '9') vk = key[0];
        else if (key.StartsWith('F') && int.TryParse(key[1..], out var f) && f is >= 1 and <= 24) vk = (uint)(0x70 + f - 1);
        else { error = $"未知按键 {key}"; return false; }
        if (modifiers == 0)
        {
            error = "必须包含 Ctrl、Alt、Shift 或 Win 修饰键";
            return false;
        }
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _callbacks.TryGetValue(wParam.ToInt32(), out var callback))
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(callback);
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void UnregisterAll()
    {
        foreach (var item in _registeredHotkeys)
            UnregisterHotKey(_source.Handle, item.Id);
        _registeredHotkeys.Clear();
        _callbacks.Clear();
    }

    private readonly record struct RegisteredHotkey(int Id, uint Modifiers, uint VirtualKey);

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
