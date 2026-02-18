using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;


namespace OverlayTimer
{
    public partial class OverlayTimerWindow : Window
    {
        // === Global keyboard hook (Ctrl 감지용, 포커스 무관) ===
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private const int VK_LCONTROL = 0xA2;
        private const int VK_RCONTROL = 0xA3;

        private static IntPtr _kbHook = IntPtr.Zero;
        private static LowLevelKeyboardProc? _kbProc; // delegate GC 방지
        private static volatile bool _ctrlDown;

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

        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HTCAPTION = 0x2;

        private bool _editMode;

        public OverlayTimerWindow()
        {
            InitializeComponent();

            EnsureKeyboardHook();
            Closed += (_, _) => RemoveKeyboardHook();
        }

        // === 오버레이 세팅 ===
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            MakeClickThrough();

            PreviewKeyDown += (_, _) => UpdateEditMode();
            PreviewKeyUp += (_, _) => UpdateEditMode();
            MouseLeftButtonDown += Overlay_MouseLeftButtonDown;

            var inputTick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            inputTick.Tick += (_, _) => UpdateEditMode();
            inputTick.Start();
        }

        private void UpdateEditMode()
        {
            bool wantEdit = _ctrlDown; // 전역 훅에서 세팅됨

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

        private void Overlay_MouseLeftButtonDown(object? sender, MouseButtonEventArgs e)
        {
            if (!_editMode) return;   // Ctrl 안 누르면 무시

            var hwnd = new WindowInteropHelper(this).Handle;

            ReleaseCapture();
            SendMessage(hwnd, WM_NCLBUTTONDOWN, HTCAPTION, 0);

            e.Handled = true;
        }

        private void MakeClickThrough()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_LAYERED | WS_EX_TRANSPARENT);
        }

        public void SetMode(string text) => ModeText.Text = text;
        public void SetTime(string text) => TimeText.Text = text;

        public void SetProgress(double progress01)
        {
            progress01 = Math.Clamp(progress01, 0.0, 1.0);

            const double size = 220.0;
            const double stroke = 14.0;

            double radius = (size - stroke) / 2.0;
            double cx = size / 2.0;
            double cy = size / 2.0;

            double startAngle = -90.0;
            double sweepAngle = 360.0 * progress01;

            if (sweepAngle <= 0.001)
            {
                ProgressArc.Data = null;
                return;
            }

            if (sweepAngle >= 359.999)
                sweepAngle = 359.999;

            Point StartPoint(double angleDeg)
            {
                double a = angleDeg * Math.PI / 180.0;
                return new Point(cx + radius * Math.Cos(a), cy + radius * Math.Sin(a));
            }

            var p0 = StartPoint(startAngle);
            var p1 = StartPoint(startAngle + sweepAngle);

            bool isLargeArc = sweepAngle > 180.0;

            var fig = new PathFigure { StartPoint = p0, IsClosed = false, IsFilled = false };
            fig.Segments.Add(new ArcSegment
            {
                Point = p1,
                Size = new Size(radius, radius),
                IsLargeArc = isLargeArc,
                SweepDirection = SweepDirection.Clockwise
            });

            var geo = new PathGeometry();
            geo.Figures.Add(fig);

            ProgressArc.Data = geo;
        }

        private static void EnsureKeyboardHook()
        {
            if (_kbHook != IntPtr.Zero) return;

            _kbProc = KbHookCallback;
            using var curProcess = Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule!;
            IntPtr hMod = GetModuleHandle(curModule.ModuleName);

            _kbHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc, hMod, 0);
            if (_kbHook == IntPtr.Zero)
                throw new Exception("Hook failed");
        }

        private static void RemoveKeyboardHook()
        {
            if (_kbHook == IntPtr.Zero) return;
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
                bool isDownMsg = (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN);
                bool isUpMsg = (msg == WM_KEYUP || msg == WM_SYSKEYUP);

                if (isDownMsg || isUpMsg)
                {
                    int vkCode = Marshal.ReadInt32(lParam); // KBDLLHOOKSTRUCT.vkCode
                    if (vkCode == VK_LCONTROL || vkCode == VK_RCONTROL)
                        _ctrlDown = isDownMsg;
                }
            }

            return CallNextHookEx(_kbHook, nCode, wParam, lParam);
        }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_LAYERED = 0x80000;
        private const int WS_EX_TRANSPARENT = 0x20;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    }
}
