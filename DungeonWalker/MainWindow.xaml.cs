using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;

namespace DungeonWalker;

public partial class MainWindow : Window
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const uint VK_F12 = 0x7B;
    private const uint VK_F11 = 0x7A;

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
    private MacroRunner? _runner;
    private MouseRecorder? _recorder;
    private bool _isRecording;

    public MainWindow()
    {
        InitializeComponent();
        LoadConfig();
        InstallHook();
    }

    private void LoadConfig()
    {
        var baseDir = AppContext.BaseDirectory;
        var configPath = Path.Combine(baseDir, "macro_config.json");

        if (!File.Exists(configPath))
        {
            StatusText.Text = "macro_config.json 없음";
            ToggleButton.IsEnabled = false;
            return;
        }

        var config = MacroConfig.Load(configPath);
        _runner = new MacroRunner(config.TargetProcess);
        _runner.StatusChanged += (state, msg) =>
            Dispatcher.Invoke(() => ApplyState(state, msg));
        _recorder = new MouseRecorder(config.TargetProcess);
        _recorder.ClickRecorded += count =>
            Dispatcher.Invoke(() => StatusText.Text = $"녹화 중... {count}회");

        LoadScenarios(baseDir);
    }

    private void LoadScenarios(string baseDir)
    {
        var dir = Path.Combine(baseDir, "scenarios");
        if (!Directory.Exists(dir))
        {
            StatusText.Text = "scenarios 폴더 없음";
            ToggleButton.IsEnabled = false;
            return;
        }

        var scenarios = Directory.GetFiles(dir, "*.json")
            .Select(ScenarioConfig.Load)
            .OrderBy(s => s.Name)
            .ToList();

        if (scenarios.Count == 0)
        {
            StatusText.Text = "시나리오 없음";
            ToggleButton.IsEnabled = false;
            return;
        }

        ScenarioList.ItemsSource = scenarios;
        ScenarioList.SelectedIndex = 0;
    }

    private void ScenarioList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ScenarioList.SelectedItem is not ScenarioConfig scenario) return;
        RepeatStepsPanel.ItemsSource = scenario.RepeatSteps;
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
            else if (kb.vkCode == VK_F11)
                Dispatcher.Invoke(ToggleRecord);
        }
        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private void ToggleMacro()
    {
        if (_runner?.State == MacroState.Idle)
            StartSelected();
        else
            _runner?.Stop();
    }

    private void ApplyState(MacroState state, string msg)
    {
        var isIdle = state == MacroState.Idle;
        StatusText.Text = msg;
        ScenarioList.IsEnabled = isIdle;
        RepeatStepsPanel.IsEnabled = isIdle;
        ToggleButton.Content = isIdle ? "시작  (F12)" : "중지  (F12)";
        RecordButton.IsEnabled = isIdle;
    }

    private void RecordButton_Click(object sender, RoutedEventArgs e) => ToggleRecord();

    private void ToggleRecord()
    {
        if (!_isRecording)
            StartRecording();
        else
            StopRecording();
    }

    private void StartRecording()
    {
        _isRecording = true;
        RecordButton.Content = "녹화 중지  (F11)";
        ScenarioList.IsEnabled = false;
        RepeatStepsPanel.IsEnabled = false;
        ToggleButton.IsEnabled = false;
        StatusText.Text = "녹화 중... 0회  (MabinogiMobile 클릭만 기록됨)";
        _recorder?.Start();
    }

    private void StopRecording()
    {
        var steps = _recorder?.Stop() ?? [];
        _isRecording = false;
        RecordButton.Content = "녹화 시작  (F11)";
        ScenarioList.IsEnabled = true;
        RepeatStepsPanel.IsEnabled = true;
        ToggleButton.IsEnabled = true;

        if (steps.Count == 0)
        {
            StatusText.Text = "녹화 완료 (기록 없음)";
            return;
        }

        SaveRecording(steps);
    }

    private void SaveRecording(List<StepConfig> steps)
    {
        var scenariosDir = Path.Combine(AppContext.BaseDirectory, "scenarios");
        if (!Directory.Exists(scenariosDir))
            Directory.CreateDirectory(scenariosDir);

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "시나리오 저장",
            Filter = "JSON 파일 (*.json)|*.json",
            InitialDirectory = scenariosDir,
            FileName = "새 시나리오"
        };

        if (dialog.ShowDialog() != true)
        {
            StatusText.Text = $"저장 취소 ({steps.Count}회 기록됨)";
            return;
        }

        var name = Path.GetFileNameWithoutExtension(dialog.FileName);
        ScenarioConfig.Save(dialog.FileName, name, steps);
        LoadScenarios(AppContext.BaseDirectory);

        for (int i = 0; i < ScenarioList.Items.Count; i++)
        {
            if (ScenarioList.Items[i] is ScenarioConfig s && s.Name == name)
            {
                ScenarioList.SelectedIndex = i;
                break;
            }
        }

        StatusText.Text = $"저장 완료: {name} ({steps.Count}회 클릭)";
    }

    private void StartSelected()
    {
        if (_runner == null || ScenarioList.SelectedItem is not ScenarioConfig scenario) return;
        _runner.Start(scenario);
    }

    private void ToggleButton_Click(object sender, RoutedEventArgs e) => ToggleMacro();

    protected override void OnClosed(EventArgs e)
    {
        _runner?.Stop();
        if (_isRecording) _recorder?.Stop();
        if (_hookHandle != IntPtr.Zero)
            UnhookWindowsHookEx(_hookHandle);
        base.OnClosed(e);
    }
}
