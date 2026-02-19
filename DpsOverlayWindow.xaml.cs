using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using OverlayTimer.Net;

namespace OverlayTimer
{
    public partial class DpsOverlayWindow : Window
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private const int VK_LCONTROL = 0xA2;
        private const int VK_RCONTROL = 0xA3;

        private static IntPtr _kbHook = IntPtr.Zero;
        private static LowLevelKeyboardProc? _kbProc;
        private static volatile bool _ctrlDown;

        private readonly DpsTracker _tracker;
        private readonly IReadOnlyDictionary<uint, string> _skillNames;
        private readonly Net.BuffUptimeTracker _buffUptimeTracker;
        private readonly IReadOnlyDictionary<uint, string> _buffNames;
        private readonly DispatcherTimer _uiTimer;
        private bool _showTargets;
        private bool _showSkills;
        private bool _showBuffs;
        private bool _editMode;
        private readonly HashSet<ulong> _selectedTargetIds = new();
        private bool _suppressSelectionChanged;

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HTCAPTION = 0x2;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_LAYERED = 0x80000;
        private const int WS_EX_TRANSPARENT = 0x20;

        public DpsOverlayWindow(DpsTracker tracker, IReadOnlyDictionary<uint, string> skillNames,
            Net.BuffUptimeTracker buffUptimeTracker, IReadOnlyDictionary<uint, string> buffNames)
        {
            InitializeComponent();
            _tracker = tracker;
            _skillNames = skillNames;
            _buffUptimeTracker = buffUptimeTracker;
            _buffNames = buffNames;

            EnsureKeyboardHook();
            Closed += (_, _) => RemoveKeyboardHook();

            _uiTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _uiTimer.Tick += (_, _) => RefreshUi();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            MakeClickThrough();

            PreviewKeyDown += (_, _) => UpdateEditMode();
            PreviewKeyUp += (_, _) => UpdateEditMode();
            MouseLeftButtonDown += OnMouseLeftButtonDown;

            var inputTick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            inputTick.Tick += (_, _) => UpdateEditMode();
            inputTick.Start();

            _uiTimer.Start();
            RefreshUi();
        }

        private void OnMouseLeftButtonDown(object? sender, MouseButtonEventArgs e)
        {
            if (!_editMode)
                return;

            var hwnd = new WindowInteropHelper(this).Handle;
            ReleaseCapture();
            SendMessage(hwnd, WM_NCLBUTTONDOWN, HTCAPTION, 0);
            e.Handled = true;
        }

        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            _tracker.Reset();
            _buffUptimeTracker.Reset();
            _selectedTargetIds.Clear();
            RefreshUi();
        }

        private void TargetToggleButton_Click(object sender, RoutedEventArgs e)
        {
            _showTargets = !_showTargets;
            TargetPanel.Visibility = _showTargets ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SkillToggleButton_Click(object sender, RoutedEventArgs e)
        {
            _showSkills = !_showSkills;
            SkillPanel.Visibility = _showSkills ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BuffToggleButton_Click(object sender, RoutedEventArgs e)
        {
            _showBuffs = !_showBuffs;
            BuffPanel.Visibility = _showBuffs ? Visibility.Visible : Visibility.Collapsed;
        }

        private void TargetItems_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelectionChanged)
                return;

            foreach (TargetRow row in e.RemovedItems)
                _selectedTargetIds.Remove(row.TargetId);
            foreach (TargetRow row in e.AddedItems)
                _selectedTargetIds.Add(row.TargetId);

            RefreshUi();
        }

        private void RefreshUi()
        {
            var snapshot = _tracker.GetSnapshot();

            DpsText.Text = FormatMan(snapshot.TotalDps);
            MetaText.Text = $"Total {FormatMan(snapshot.TotalDamage)} / {snapshot.ElapsedSeconds:0.0}s";

            var targetRows = new List<TargetRow>(snapshot.Targets.Count);
            foreach (var target in snapshot.Targets)
            {
                targetRows.Add(new TargetRow
                {
                    TargetId = target.TargetId,
                    TargetLabel = $"Target {target.TargetId}",
                    DpsLabel = FormatMan(target.Dps)
                });
            }
            // SelectionChanged가 ItemsSource/SelectedItems 변경에 반응해 RefreshUi를 재진입하는 것을 방지
            _suppressSelectionChanged = true;
            TargetItems.ItemsSource = targetRows;
            // 사라진 대상은 선택에서 제거한 뒤 남은 행만 복원
            _selectedTargetIds.IntersectWith(targetRows.Select(r => r.TargetId));
            foreach (var row in targetRows)
                if (_selectedTargetIds.Contains(row.TargetId))
                    TargetItems.SelectedItems.Add(row);
            _suppressSelectionChanged = false;

            // 스킬 데이터 소스 결정
            IReadOnlyList<Net.DpsSkillSnapshot> skillSource = _selectedTargetIds.Count > 0
                ? _tracker.GetSkillSnapshotForTargets(_selectedTargetIds)
                : snapshot.Skills;

            // 스킬 패널 헤더 갱신
            SkillPanelTitle.Text = _selectedTargetIds.Count switch
            {
                0 => "\uC2A4\uD0AC\uBCC4 \uD1B5\uACC4",
                1 => $"\uC2A4\uD0AC\uBCC4 \uD1B5\uACC4 (Target {_selectedTargetIds.First()} \uD55C\uC815)",
                _ => $"\uC2A4\uD0AC\uBCC4 \uD1B5\uACC4 ({_selectedTargetIds.Count}\uAC1C \uB300\uC0C1 \uD55C\uC815)"
            };

            var skillRows = new List<SkillRow>(skillSource.Count);
            foreach (var skill in skillSource)
            {
                skillRows.Add(new SkillRow
                {
                    SkillLabel = ResolveSkillLabel(skill.SkillType),
                    RatioLabel = $"{skill.DamageRatio:0.0}%  ({skill.HitCount}\uD0C0)",
                    FlagLabel = $"\uD06C\uB9AC:{skill.CritRate:0.0}%  \uCD94\uAC00:{skill.AddHitRate:0.0}%  \uAC15\uD0C0:{skill.PowerRate:0.0}%  \uC5F0\uD0C0:{skill.FastRate:0.0}%"
                });
            }
            SkillItems.ItemsSource = skillRows;

            // 버프 패널 갱신
            // % 분모: DPS elapsed (전투 시작~마지막 타격)를 우선 사용, 없으면 버프 내부 elapsed
            var buffSnapshot = _buffUptimeTracker.GetSnapshot();
            double buffRef = snapshot.ElapsedSeconds > 0 ? snapshot.ElapsedSeconds : buffSnapshot.ElapsedSeconds;

            var buffRows = new List<BuffRow>(buffSnapshot.Rows.Count);
            foreach (var buff in buffSnapshot.Rows)
            {
                double pct = buffRef > 0 ? Math.Min(buff.TotalSeconds / buffRef * 100.0, 100.0) : 0.0;
                string buffLabel = buff.IsActive
                    ? $"● {ResolveBuffLabel(buff.BuffKey)}"
                    : ResolveBuffLabel(buff.BuffKey);
                string uptimeLabel = buff.IsActive
                    ? $"{buff.RemainingSeconds:0.0}s \ub0a8\uc74c  (\uc5f0: {buff.TotalSeconds:0.0}s  {pct:0.0}%)"
                    : $"\uc5f0: {buff.TotalSeconds:0.0}s  {pct:0.0}%";

                buffRows.Add(new BuffRow
                {
                    BuffLabel = buffLabel,
                    UptimeLabel = uptimeLabel
                });
            }
            BuffItems.ItemsSource = buffRows;
            BuffElapsedText.Text = $"\uAE30\uC900: {buffRef:0.0}s";
        }

        private string ResolveBuffLabel(uint buffKey)
        {
            if (_buffNames.TryGetValue(buffKey, out var name) && !string.IsNullOrWhiteSpace(name))
                return name;

            return $"Buff {buffKey}";
        }

        private string ResolveSkillLabel(uint skillType)
        {
            if (_skillNames.TryGetValue(skillType, out var mapped) && !string.IsNullOrWhiteSpace(mapped))
                return mapped;

            if (DpsSkillClassifier.TryGetSpecialSkillName(skillType, out var specialName))
                return specialName;

            return $"Skill {skillType}";
        }

        private static string FormatMan(double value)
        {
            return $"{value / 10000.0:0.0}\uB9CC";
        }

        private static string FormatMan(long value)
        {
            return $"{value / 10000.0:0.0}\uB9CC";
        }

        private void UpdateEditMode()
        {
            bool wantEdit = IsCtrlPressed();
            if (_editMode == wantEdit)
                return;

            _editMode = wantEdit;
            Root.IsHitTestVisible = _editMode;

            var hwnd = new WindowInteropHelper(this).Handle;
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);

            if (_editMode)
                SetWindowLong(hwnd, GWL_EXSTYLE, (ex | WS_EX_LAYERED) & ~WS_EX_TRANSPARENT);
            else
                SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_LAYERED | WS_EX_TRANSPARENT);
        }

        private void MakeClickThrough()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_LAYERED | WS_EX_TRANSPARENT);
        }

        private static void EnsureKeyboardHook()
        {
            if (_kbHook != IntPtr.Zero)
                return;

            _kbProc = KbHookCallback;
            using var curProcess = Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule!;
            IntPtr hMod = GetModuleHandle(curModule.ModuleName);

            _kbHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc, hMod, 0);
            if (_kbHook == IntPtr.Zero)
                throw new Exception("DPS keyboard hook failed");
        }

        private static void RemoveKeyboardHook()
        {
            if (_kbHook == IntPtr.Zero)
                return;

            UnhookWindowsHookEx(_kbHook);
            _kbHook = IntPtr.Zero;
            _kbProc = null;
            _ctrlDown = false;
        }

        private static IntPtr KbHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                bool isDownMsg = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                bool isUpMsg = msg == WM_KEYUP || msg == WM_SYSKEYUP;

                if (isDownMsg || isUpMsg)
                {
                    int vkCode = Marshal.ReadInt32(lParam);
                    if (vkCode == VK_LCONTROL || vkCode == VK_RCONTROL)
                        _ctrlDown = isDownMsg;
                }
            }

            return CallNextHookEx(_kbHook, nCode, wParam, lParam);
        }

        private static bool IsCtrlPressed()
        {
            return (GetAsyncKeyState(VK_LCONTROL) & 0x8000) != 0 ||
                   (GetAsyncKeyState(VK_RCONTROL) & 0x8000) != 0;
        }

        private sealed class TargetRow
        {
            public ulong TargetId { get; set; }
            public string TargetLabel { get; set; } = string.Empty;
            public string DpsLabel { get; set; } = string.Empty;
        }

        private sealed class SkillRow
        {
            public string SkillLabel { get; set; } = string.Empty;
            public string RatioLabel { get; set; } = string.Empty;
            public string FlagLabel { get; set; } = string.Empty;
        }

        private sealed class BuffRow
        {
            public string BuffLabel { get; set; } = string.Empty;
            public string UptimeLabel { get; set; } = string.Empty;
        }
    }
}
