using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DungeonWalker;

public class MouseRecorder
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;

    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc fn, IntPtr hMod, uint tid);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? lpModuleName);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT { public int x, y; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    private IntPtr _hook = IntPtr.Zero;
    private LowLevelMouseProc? _proc;
    private readonly string _targetProcess;
    private readonly List<(int x, int y, long ms)> _clicks = [];
    private volatile bool _active;

    public int ClickCount => _clicks.Count;
    public event Action<int>? ClickRecorded;

    public MouseRecorder(string targetProcess) => _targetProcess = targetProcess;

    public void Start()
    {
        _clicks.Clear();
        _active = true;
        _proc = OnMouseEvent;
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
    }

    public List<StepConfig> Stop()
    {
        _active = false;
        long stopMs = Environment.TickCount64;
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        return BuildSteps(stopMs);
    }

    private List<StepConfig> BuildSteps(long stopMs)
    {
        var steps = new List<StepConfig>(_clicks.Count);
        for (int i = 0; i < _clicks.Count; i++)
        {
            var (x, y, ms) = _clicks[i];
            long next = i + 1 < _clicks.Count ? _clicks[i + 1].ms : stopMs;
            steps.Add(new StepConfig { Type = "click", X = x, Y = y, AfterDelayMs = (int)Math.Max(0, next - ms) });
        }
        return steps;
    }

    private IntPtr OnMouseEvent(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)WM_LBUTTONDOWN && _active && IsFocused())
        {
            var s = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            _clicks.Add((s.x, s.y, Environment.TickCount64));
            ClickRecorded?.Invoke(_clicks.Count);
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private bool IsFocused()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        try { return Process.GetProcessById((int)pid).ProcessName.Equals(_targetProcess, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
}
