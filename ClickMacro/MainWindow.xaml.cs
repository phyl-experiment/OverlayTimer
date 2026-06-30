using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace ClickMacro;

public partial class MainWindow : Window
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const uint VK_F12 = 0x7B;

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    private IntPtr _hookHandle = IntPtr.Zero;
    private LowLevelKeyboardProc? _hookProc;

    private MacroConfig? _config;
    private MacroRunner? _runner;

    public MainWindow()
    {
        InitializeComponent();
        LoadConfig();
        InstallHook();
    }

    private void LoadConfig()
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "macro_config.json");
        if (!File.Exists(configPath))
        {
            StatusText.Text = "macro_config.json 없음";
            ToggleButton.IsEnabled = false;
            return;
        }

        _config = MacroConfig.Load(configPath);

        var repeatStep = _config.Steps.FirstOrDefault(s => s.Type == "repeat");
        IntervalBox.Text = repeatStep != null
            ? (repeatStep.DurationMs / 1000.0).ToString("0.##")
            : "300";

        _runner = new MacroRunner(_config);
        _runner.StatusChanged += (state, msg) =>
            Dispatcher.Invoke(() => ApplyState(state, msg));
    }

    private void InstallHook()
    {
        _hookProc = KeyboardHookCallback;
        _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(null), 0);
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)WM_KEYDOWN)
        {
            var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if (kb.vkCode == VK_F12)
                Dispatcher.Invoke(ToggleMacro);
        }
        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private void ToggleMacro()
    {
        if (_runner?.State == MacroState.Idle)
            StartWithCurrentInterval();
        else
            _runner?.Stop();
    }

    private void ApplyState(MacroState state, string msg)
    {
        StatusText.Text = msg;
        IntervalBox.IsEnabled = state == MacroState.Idle;
        ToggleButton.Content = state == MacroState.Idle ? "시작  (F12)" : "중지  (F12)";
    }

    private void StartWithCurrentInterval()
    {
        if (_config == null || _runner == null) return;

        var repeatStep = _config.Steps.FirstOrDefault(s => s.Type == "repeat");
        if (repeatStep != null && double.TryParse(IntervalBox.Text, out double secs) && secs > 0)
            repeatStep.DurationMs = (int)(secs * 1000);

        _runner.Start();
    }

    private void ToggleButton_Click(object sender, RoutedEventArgs e) => ToggleMacro();

    protected override void OnClosed(EventArgs e)
    {
        _runner?.Stop();
        if (_hookHandle != IntPtr.Zero)
            UnhookWindowsHookEx(_hookHandle);
        base.OnClosed(e);
    }
}
