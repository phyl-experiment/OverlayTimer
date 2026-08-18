using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DungeonWalker;

public enum MacroState { Idle, WaitingForFocus, Running }

public class MacroRunner
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);

    private readonly string _targetProcess;
    private readonly Func<bool>? _isFocusedOverride;
    private readonly Action<int, int>? _clickOverride;
    private CancellationTokenSource? _cts;

    public MacroState State { get; private set; } = MacroState.Idle;
    public event Action<MacroState, string>? StatusChanged;

    public MacroRunner(string targetProcess, Func<bool>? isFocused = null, Action<int, int>? click = null)
    {
        _targetProcess = targetProcess;
        _isFocusedOverride = isFocused;
        _clickOverride = click;
    }

    public void Start(ScenarioConfig scenario)
    {
        if (State != MacroState.Idle) return;
        _cts = new CancellationTokenSource();
        _ = RunLoop(scenario.Steps, _cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    private bool IsGameFocused()
    {
        if (_isFocusedOverride != null) return _isFocusedOverride();
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        try
        {
            var proc = Process.GetProcessById((int)pid);
            return string.Equals(proc.ProcessName, _targetProcess, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // 루프 재시작 시점에 게임 창을 포그라운드로 올려 포커스 대기 없이 연속 실행되도록 함
    private void TryBringGameToForeground()
    {
        if (_isFocusedOverride != null) return;
        try
        {
            var procs = Process.GetProcessesByName(_targetProcess);
            if (procs.Length > 0 && procs[0].MainWindowHandle != IntPtr.Zero)
                SetForegroundWindow(procs[0].MainWindowHandle);
        }
        catch { }
    }

    private void DoClick(int x, int y)
    {
        if (_clickOverride != null) { _clickOverride(x, y); return; }
        MouseInput.Click(x, y);
    }

    private async Task WaitForFocus(CancellationToken ct)
    {
        if (IsGameFocused()) return;
        Notify(MacroState.WaitingForFocus, "게임 포커스 대기 중...");
        while (!IsGameFocused() && !ct.IsCancellationRequested)
            await Task.Delay(500, ct);
    }

    private async Task RunLoop(List<StepConfig> steps, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TryBringGameToForeground();
                await WaitForFocus(ct);
                if (ct.IsCancellationRequested) break;

                bool aborted = false;
                for (int i = 0; i < steps.Count && !ct.IsCancellationRequested && !aborted; i++)
                    aborted = await ExecuteStep(steps[i], i + 1, steps.Count, ct);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            Notify(MacroState.Idle, "중지됨");
        }
    }

    private async Task<bool> ExecuteStep(StepConfig step, int num, int total, CancellationToken ct)
    {
        await WaitForFocus(ct);
        if (ct.IsCancellationRequested) return true;

        if (step.Type == "repeat")
        {
            int intervalMs = step.IntervalMs > 0 ? step.IntervalMs : 1000;
            int count = step.DurationMs / intervalMs;
            int totalSecs = intervalMs / 1000;

            for (int rep = 0; rep < count && !ct.IsCancellationRequested; rep++)
            {
                await WaitForFocus(ct);
                if (ct.IsCancellationRequested) return true;
                Notify(MacroState.Running, $"Step {num}  ·  {rep + 1} / {count}회");
                DoClick(step.X, step.Y);

                if (rep < count - 1)
                {
                    if (totalSecs >= 1)
                    {
                        for (int s = 1; s <= totalSecs && !ct.IsCancellationRequested; s++)
                        {
                            await Task.Delay(1000, ct);
                            Notify(MacroState.Running, $"Step {num}  ·  {rep + 1} / {count}회  ({s}/{totalSecs}초)");
                        }
                    }
                    else
                    {
                        await Task.Delay(intervalMs, ct);
                    }
                }
            }
        }
        else
        {
            Notify(MacroState.Running, $"Step {num} / {total}");
            DoClick(step.X, step.Y);
            if (step.AfterDelayMs > 0)
                await Task.Delay(step.AfterDelayMs, ct);
        }

        return false;
    }

    private void Notify(MacroState state, string msg)
    {
        State = state;
        StatusChanged?.Invoke(state, msg);
    }
}
