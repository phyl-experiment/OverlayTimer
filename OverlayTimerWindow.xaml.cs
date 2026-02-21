using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WpfCursors = System.Windows.Input.Cursors;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace OverlayTimer
{
    public partial class OverlayTimerWindow : Window
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private const int VK_LCONTROL = 0xA2;
        private const int VK_RCONTROL = 0xA3;
        private const int VK_F9 = 0x78;

        private const int HTLEFT = 10;
        private const int HTRIGHT = 11;
        private const int HTTOP = 12;
        private const int HTTOPLEFT = 13;
        private const int HTTOPRIGHT = 14;
        private const int HTBOTTOM = 15;
        private const int HTBOTTOMLEFT = 16;
        private const int HTBOTTOMRIGHT = 17;

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_LAYERED = 0x80000;
        private const int WS_EX_TRANSPARENT = 0x20;
        private const double BaseRingSize = 220.0;
        private const double BaseRingStroke = 14.0;
        private const double BaseModeFont = 20.0;
        private const double BaseTimeFont = 48.0;

        internal static Action? OnF9Press;

        private static IntPtr _kbHook = IntPtr.Zero;
        private static LowLevelKeyboardProc? _kbProc;
        private static volatile bool _ctrlDown;

        private bool _editMode;
        private bool _sizeInitialized;
        private double? _pendingWidth;
        private double? _pendingHeight;
        private bool _isResizeDragging;
        private int _resizeHit;
        private Point _resizeStartScreen;
        private double _resizeStartLeft;
        private double _resizeStartTop;
        private double _resizeStartWidth;
        private double _resizeStartHeight;
        private double _lastProgress01;

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        public OverlayTimerWindow()
        {
            InitializeComponent();

            EnsureKeyboardHook();
            Closed += (_, _) => RemoveKeyboardHook();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            InitializeSizingMode();
            MakeClickThrough();

            PreviewKeyDown += (_, _) => UpdateEditMode();
            PreviewKeyUp += (_, _) => UpdateEditMode();
            MouseLeftButtonDown += Overlay_MouseLeftButtonDown;
            MouseMove += Overlay_MouseMove;
            MouseLeftButtonUp += Overlay_MouseLeftButtonUp;
            LostMouseCapture += (_, _) => EndResizeDrag();
            SizeChanged += (_, _) => UpdateDynamicLayout();

            var inputTick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            inputTick.Tick += (_, _) => UpdateEditMode();
            inputTick.Start();

            UpdateDynamicLayout();
        }

        public void SetInitialSize(double? width, double? height)
        {
            if (width.HasValue && width.Value > 0)
                _pendingWidth = width.Value;
            if (height.HasValue && height.Value > 0)
                _pendingHeight = height.Value;

            if (_sizeInitialized)
                ApplyConfiguredSize();
        }

        private void InitializeSizingMode()
        {
            if (_sizeInitialized)
                return;

            _sizeInitialized = true;
            SizeToContent = SizeToContent.Manual;
            ApplyConfiguredSize();
        }

        private void ApplyConfiguredSize()
        {
            double width = _pendingWidth ?? ActualWidth;
            double height = _pendingHeight ?? ActualHeight;

            if (width > 0)
                Width = ClampWidth(width);
            if (height > 0)
                Height = ClampHeight(height);

            UpdateDynamicLayout();
        }

        private void UpdateEditMode()
        {
            bool wantEdit = IsCtrlPressed();

            if (_editMode == wantEdit)
                return;

            _editMode = wantEdit;
            if (!_editMode)
                EndResizeDrag();
            Root.IsHitTestVisible = _editMode;

            var hwnd = new WindowInteropHelper(this).Handle;
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);

            if (_editMode)
                SetWindowLong(hwnd, GWL_EXSTYLE, (ex | WS_EX_LAYERED) & ~WS_EX_TRANSPARENT);
            else
                SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_LAYERED | WS_EX_TRANSPARENT);

            Cursor = _editMode ? WpfCursors.SizeAll : WpfCursors.Arrow;
        }

        private void Overlay_MouseLeftButtonDown(object? sender, MouseButtonEventArgs e)
        {
            if (!_editMode)
                return;

            InitializeSizingMode();
            int hit = GetSizingHit(e.GetPosition(this));

            if (hit == 0)
            {
                try
                {
                    DragMove();
                }
                catch
                {
                    // Ignore drag exceptions in overlay mode.
                }
            }
            else
            {
                BeginResizeDrag(hit, e);
            }

            e.Handled = true;
        }

        private void Overlay_MouseMove(object? sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_isResizeDragging)
            {
                UpdateResizeDrag(e);
                return;
            }

            if (!_editMode)
                return;

            int hit = GetSizingHit(e.GetPosition(this));
            Cursor = CursorForHit(hit);
        }

        private void Overlay_MouseLeftButtonUp(object? sender, MouseButtonEventArgs e)
        {
            EndResizeDrag();
        }

        private void BeginResizeDrag(int hit, MouseButtonEventArgs e)
        {
            _isResizeDragging = true;
            _resizeHit = hit;
            _resizeStartScreen = PointToScreen(e.GetPosition(this));
            _resizeStartLeft = Left;
            _resizeStartTop = Top;
            _resizeStartWidth = ActualWidth;
            _resizeStartHeight = ActualHeight;

            Cursor = CursorForHit(hit);
            Mouse.Capture(this);
        }

        private void UpdateResizeDrag(System.Windows.Input.MouseEventArgs e)
        {
            if (!_isResizeDragging)
                return;

            var current = PointToScreen(e.GetPosition(this));
            double dx = current.X - _resizeStartScreen.X;
            double dy = current.Y - _resizeStartScreen.Y;

            double newLeft = _resizeStartLeft;
            double newTop = _resizeStartTop;
            double newWidth = _resizeStartWidth;
            double newHeight = _resizeStartHeight;

            if (_resizeHit == HTLEFT || _resizeHit == HTTOPLEFT || _resizeHit == HTBOTTOMLEFT)
            {
                newWidth = ClampWidth(_resizeStartWidth - dx);
                newLeft = _resizeStartLeft + (_resizeStartWidth - newWidth);
            }
            else if (_resizeHit == HTRIGHT || _resizeHit == HTTOPRIGHT || _resizeHit == HTBOTTOMRIGHT)
            {
                newWidth = ClampWidth(_resizeStartWidth + dx);
            }

            if (_resizeHit == HTTOP || _resizeHit == HTTOPLEFT || _resizeHit == HTTOPRIGHT)
            {
                newHeight = ClampHeight(_resizeStartHeight - dy);
                newTop = _resizeStartTop + (_resizeStartHeight - newHeight);
            }
            else if (_resizeHit == HTBOTTOM || _resizeHit == HTBOTTOMLEFT || _resizeHit == HTBOTTOMRIGHT)
            {
                newHeight = ClampHeight(_resizeStartHeight + dy);
            }

            Left = newLeft;
            Top = newTop;
            Width = newWidth;
            Height = newHeight;
        }

        private void EndResizeDrag()
        {
            if (!_isResizeDragging)
                return;

            _isResizeDragging = false;
            _resizeHit = 0;
            if (Mouse.Captured == this)
                Mouse.Capture(null);
            Cursor = _editMode ? WpfCursors.SizeAll : WpfCursors.Arrow;
        }

        private double ClampWidth(double value)
        {
            double max = MaxWidth;
            if (!double.IsFinite(max) || max <= 0)
                max = double.PositiveInfinity;
            return Math.Min(max, Math.Max(MinWidth, value));
        }

        private double ClampHeight(double value)
        {
            double max = MaxHeight;
            if (!double.IsFinite(max) || max <= 0)
                max = double.PositiveInfinity;
            return Math.Min(max, Math.Max(MinHeight, value));
        }

        private int GetSizingHit(Point p)
        {
            const double grip = 18.0;

            bool left = p.X <= grip;
            bool right = p.X >= ActualWidth - grip;
            bool top = p.Y <= grip;
            bool bottom = p.Y >= ActualHeight - grip;

            if (left && top) return HTTOPLEFT;
            if (right && top) return HTTOPRIGHT;
            if (left && bottom) return HTBOTTOMLEFT;
            if (right && bottom) return HTBOTTOMRIGHT;
            if (left) return HTLEFT;
            if (right) return HTRIGHT;
            if (top) return HTTOP;
            if (bottom) return HTBOTTOM;

            return 0;
        }

        private static System.Windows.Input.Cursor CursorForHit(int hit)
        {
            return hit switch
            {
                HTLEFT => WpfCursors.SizeWE,
                HTRIGHT => WpfCursors.SizeWE,
                HTTOP => WpfCursors.SizeNS,
                HTBOTTOM => WpfCursors.SizeNS,
                HTTOPLEFT => WpfCursors.SizeNWSE,
                HTBOTTOMRIGHT => WpfCursors.SizeNWSE,
                HTTOPRIGHT => WpfCursors.SizeNESW,
                HTBOTTOMLEFT => WpfCursors.SizeNESW,
                _ => WpfCursors.SizeAll
            };
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
            _lastProgress01 = Math.Clamp(progress01, 0.0, 1.0);
            DrawProgressArc(_lastProgress01);
        }

        private void UpdateDynamicLayout()
        {
            if (!IsLoaded)
                return;

            const double borderPadding = 36.0; // Border padding(18) * 2
            double modeReserve = ModeText.ActualHeight + ModeText.Margin.Top + ModeText.Margin.Bottom;
            if (modeReserve <= 0)
                modeReserve = BaseModeFont + 10.0;

            double availableWidth = Math.Max(1.0, ActualWidth - borderPadding);
            double availableHeight = Math.Max(1.0, ActualHeight - borderPadding - modeReserve);
            double ringSize = Math.Max(1.0, Math.Min(availableWidth, availableHeight));

            RingCanvas.Width = ringSize;
            RingCanvas.Height = ringSize;

            double scale = ringSize / BaseRingSize;
            ModeText.FontSize = Math.Clamp(BaseModeFont * scale, 12.0, 40.0);
            TimeText.FontSize = Math.Clamp(BaseTimeFont * scale, 18.0, 96.0);

            double stroke = Math.Clamp(BaseRingStroke * scale, 5.0, 32.0);
            BackgroundRing.StrokeThickness = stroke;
            ProgressArc.StrokeThickness = stroke;

            DrawProgressArc(_lastProgress01);
        }

        private void DrawProgressArc(double progress01)
        {
            double size = RingCanvas.ActualWidth > 0 ? RingCanvas.ActualWidth : RingCanvas.Width;
            if (!double.IsFinite(size) || size <= 0)
                size = BaseRingSize;

            double stroke = ProgressArc.StrokeThickness;
            if (!double.IsFinite(stroke) || stroke <= 0)
                stroke = BaseRingStroke;

            if (size <= stroke + 2.0)
            {
                ProgressArc.Data = null;
                return;
            }

            double radius = (size - stroke) / 2.0;
            double cx = size / 2.0;
            double cy = size / 2.0;

            double startAngle = -90.0;
            double sweepAngle = 360.0 * Math.Clamp(progress01, 0.0, 1.0);

            if (sweepAngle <= 0.001)
            {
                ProgressArc.Data = null;
                return;
            }

            if (sweepAngle >= 359.999)
                sweepAngle = 359.999;

            Point ArcPoint(double angleDeg)
            {
                double a = angleDeg * Math.PI / 180.0;
                return new Point(cx + radius * Math.Cos(a), cy + radius * Math.Sin(a));
            }

            var p0 = ArcPoint(startAngle);
            var p1 = ArcPoint(startAngle + sweepAngle);

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
            if (_kbHook != IntPtr.Zero)
                return;

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
                    if (isDownMsg && vkCode == VK_F9)
                        OnF9Press?.Invoke();
                }
            }

            return CallNextHookEx(_kbHook, nCode, wParam, lParam);
        }

        private static bool IsCtrlPressed()
        {
            return (GetAsyncKeyState(VK_LCONTROL) & 0x8000) != 0 ||
                   (GetAsyncKeyState(VK_RCONTROL) & 0x8000) != 0;
        }
    }
}
