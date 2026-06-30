using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClickMacro;

public enum MacroState { Idle, WaitingForFocus, Running }

public class MacroRunner
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private readonly MacroConfig _config;
    private CancellationTokenSource? _cts;

    public MacroState State { get; private set; } = MacroState.Idle;
    public event Action<MacroState, string>? StatusChanged;

    public MacroRunner(MacroConfig config) => _config = config;

    public void Start()
    {
        if (State != MacroState.Idle) return;
        _cts = new CancellationTokenSource();
        _ = RunLoop(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    public void Toggle()
    {
        if (State == MacroState.Idle) Start();
        else Stop();
    }

    private bool IsGameFocused()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        try
        {
            var proc = Process.GetProcessById((int)pid);
            return string.Equals(proc.ProcessName, _config.TargetProcess, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private async Task WaitForFocus(CancellationToken ct)
    {
        if (IsGameFocused()) return;
        Notify(MacroState.WaitingForFocus, "게임 포커스 대기 중...");
        while (!IsGameFocused() && !ct.IsCancellationRequested)
            await Task.Delay(500, ct);
    }

    private async Task RunLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await WaitForFocus(ct);
                if (ct.IsCancellationRequested) break;

                bool aborted = false;
                for (int i = 0; i < _config.Steps.Count && !ct.IsCancellationRequested && !aborted; i++)
                    aborted = await ExecuteStep(_config.Steps[i], i + 1, ct);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            Notify(MacroState.Idle, "중지됨");
        }
    }

    private async Task<bool> ExecuteStep(StepConfig step, int num, CancellationToken ct)
    {
        await WaitForFocus(ct);
        if (ct.IsCancellationRequested) return true;

        if (step.Type == "repeat")
        {
            int total = step.DurationMs / Math.Max(1, step.IntervalMs);
            for (int rep = 0; rep < total && !ct.IsCancellationRequested; rep++)
            {
                await WaitForFocus(ct);
                if (ct.IsCancellationRequested) return true;
                Notify(MacroState.Running, $"Step {num}  ·  {rep + 1} / {total}회");
                MouseInput.Click(step.X, step.Y);
                await Task.Delay(step.IntervalMs, ct);
            }
        }
        else
        {
            Notify(MacroState.Running, $"Step {num} / {_config.Steps.Count}");
            MouseInput.Click(step.X, step.Y);
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
