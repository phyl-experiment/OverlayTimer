using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using OverlayTimer.Net;

namespace OverlayTimer;

public partial class App : System.Windows.Application
{
    private CancellationTokenSource? _cts;
    private SnifferService? _sniffer;
    private NotifyIcon? _trayIcon;
    private bool _isShuttingDown;
    private OverlayTimerWindow? _timerWindow;
    private DpsOverlayWindow?   _dpsWindow;
    private AppConfig?           _config;
    private System.Windows.Threading.DispatcherTimer? _saveDebounce;

    // 프로브 결과 확인 대기
    private const int ConfirmThreshold      = 3;   // 인식된 패킷 수
    private const int ConfirmTimeoutSeconds = 120;  // 타임아웃
    private bool _probePendingConfirmation;
    private System.Windows.Threading.DispatcherTimer? _confirmTimer;
    private DateTime _confirmDeadline;
    private ProbeConfigSnapshot? _probeRollbackSnapshot;

    // sniffer 재시작 시에도 보존하는 객체
    private ITimerTrigger?      _timerTrigger;
    private PacketTypeLogger?   _typeLogger;
    private DpsTracker?         _dpsTracker;
    private BuffUptimeTracker?  _buffUptimeTracker;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var config = AppConfig.Load();
        _config = config;
        var skillNames = SkillNameMap.Load();
        var buffNames  = BuffNameMap.Load();

        _saveDebounce = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _saveDebounce.Tick += (_, _) =>
        {
            _saveDebounce.Stop();
            _config?.Save();
        };

        _timerTrigger = NoopTimerTrigger.Instance;

        if (config.Overlays.Timer.Enabled)
        {
            var window = new OverlayTimerWindow
            {
                Left = config.Overlays.Timer.X,
                Top  = config.Overlays.Timer.Y
            };
            _timerWindow = window;
            AttachWindowCloseToAppShutdown(window);
            window.LocationChanged += (_, _) =>
            {
                config.Overlays.Timer.X = window.Left;
                config.Overlays.Timer.Y = window.Top;
                RestartSaveDebounce();
            };
            window.Show();

            _timerTrigger = new OverlayTriggerTimer(window, config.Timer);
            _typeLogger   = new PacketTypeLogger();
            OverlayTimerWindow.OnF9Press = _typeLogger.TogglePhase;
        }

        _dpsTracker        = new DpsTracker();
        _buffUptimeTracker = new BuffUptimeTracker();

        if (config.Overlays.Dps.Enabled)
        {
            _dpsWindow = new DpsOverlayWindow(
                _dpsTracker, skillNames,
                buffUptimeTracker: _buffUptimeTracker,
                buffNames: buffNames)
            {
                Left = config.Overlays.Dps.X,
                Top  = config.Overlays.Dps.Y
            };
            AttachWindowCloseToAppShutdown(_dpsWindow);
            _dpsWindow.LocationChanged += (_, _) =>
            {
                config.Overlays.Dps.X = _dpsWindow.Left;
                config.Overlays.Dps.Y = _dpsWindow.Top;
                RestartSaveDebounce();
            };
            _dpsWindow.Show();
        }

        _trayIcon = CreateTrayIcon();

        StartSniffer(config);
    }

    // ------------------------------------------------------------------
    // Sniffer lifecycle (재시작 가능)
    // ------------------------------------------------------------------

    private void StartSniffer(AppConfig config)
    {
        try { _sniffer?.Dispose(); } catch { /* ignore */ }
        try { _cts?.Cancel(); }      catch { /* ignore */ }
        try { _cts?.Dispose(); }     catch { /* ignore */ }
        _sniffer = null;
        _cts = new CancellationTokenSource();

        var selfIdResolver = new SelfIdResolver(config.PacketTypes.EnterWorld);
        var packetHandler = new PacketHandler(
            _timerTrigger!,
            selfIdResolver,
            config.PacketTypes.BuffStart,
            config.PacketTypes.BuffEnd,
            config.BuffKeys,
            _typeLogger,
            _dpsTracker,
            _buffUptimeTracker,
            config.PacketTypes.DpsAttack,
            config.PacketTypes.DpsDamage,
            config.Timer.ActiveDurationSeconds);

        _sniffer = new SnifferService(
            config.Network.TargetPort,
            config.Network.DeviceName,
            packetHandler,
            config.Protocol,
            config.PacketTypes);

        _sniffer.OnProbeSuccess = result =>
            Dispatcher.Invoke(() => HandleProbeSuccess(result));

        try
        {
            _sniffer.Start(_cts.Token);
        }
        catch (UnauthorizedAccessException ex)
        {
            ShowStartupError(
                "패킷 캡처 권한이 없습니다.\n\n" +
                "관리자 권한으로 실행한 뒤 다시 시도해 주세요.",
                ex.Message);
        }
        catch (DllNotFoundException ex)
        {
            ShowStartupError(
                "Npcap 라이브러리를 찾을 수 없습니다.\n\n" +
                "Npcap 설치 상태를 확인한 뒤 다시 실행해 주세요.",
                ex.Message);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("No capture devices found", StringComparison.OrdinalIgnoreCase))
        {
            ShowStartupError(
                "캡처 가능한 네트워크 장치를 찾지 못했습니다.\n\n" +
                "Npcap 설치 여부와 네트워크 어댑터 상태를 확인해 주세요.",
                ex.Message);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("No capture device matching", StringComparison.OrdinalIgnoreCase))
        {
            ShowStartupError(
                "설정된 네트워크 장치를 찾지 못했습니다.\n\n" +
                "config.json의 network.deviceName 값을 확인해 주세요.",
                ex.Message);
        }
        catch (Exception ex)
        {
            ShowStartupError(
                "패킷 캡처 시작에 실패했습니다.\n\n" +
                "관리자 권한 실행 및 Npcap 설치 상태를 확인해 주세요.",
                ex.Message);
        }
    }

    // ------------------------------------------------------------------
    // Probe success handler (UI 스레드에서 호출됨)
    // ------------------------------------------------------------------

    private void HandleProbeSuccess(ProbeResult r)
    {
        if (_isShuttingDown || _config == null) return;
        if (_probePendingConfirmation)
        {
            LogHelper.Write("[Probe] Ignored a new probe result while confirmation is pending.");
            return;
        }

        _probeRollbackSnapshot = CaptureProbeConfigSnapshot(_config);
        ApplyProbeResult(r, _config);

        // config.json 즉시 저장하지 않음 — N개 패킷 인식 후 확정 저장
        _probePendingConfirmation = true;
        _confirmDeadline = DateTime.UtcNow.AddSeconds(ConfirmTimeoutSeconds);

        LogHelper.Write("[Probe] Config updated in memory. Waiting for confirmation before saving.");

        StartSniffer(_config);

        // 확인 타이머 시작
        if (_confirmTimer == null)
        {
            _confirmTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            _confirmTimer.Tick += OnConfirmTick;
        }
        _confirmTimer.Start();
    }

    private void OnConfirmTick(object? sender, EventArgs e)
    {
        if (_isShuttingDown || _config == null || !_probePendingConfirmation)
        {
            _confirmTimer?.Stop();
            return;
        }

        if (DateTime.UtcNow > _confirmDeadline)
        {
            _confirmTimer!.Stop();
            _probePendingConfirmation = false;

            if (_probeRollbackSnapshot != null)
            {
                RestoreProbeConfigSnapshot(_config, _probeRollbackSnapshot);
                _probeRollbackSnapshot = null;
                LogHelper.Write("[Probe] Confirmation timed out. Restored previous config and restarting sniffer.");
                StartSniffer(_config);
            }
            else
            {
                LogHelper.Write("[Probe] Confirmation timed out. Rollback snapshot missing; config not saved.");
            }

            return;
        }

        int recognized = _sniffer?.RecognizedPacketCount ?? 0;
        if (recognized >= ConfirmThreshold)
        {
            _confirmTimer!.Stop();
            _probePendingConfirmation = false;
            _probeRollbackSnapshot = null;
            _config.Save();
            LogHelper.Write($"[Probe] Confirmed ({recognized} packets recognized). Config saved.");
        }
    }

    private static void ApplyProbeResult(ProbeResult r, AppConfig config)
    {
        if (r.NewStartMarker != null)
            config.Protocol.StartMarker = ToHexString(r.NewStartMarker);
        if (r.NewEndMarker != null)
            config.Protocol.EndMarker = ToHexString(r.NewEndMarker);
        if (r.NewBuffStart.HasValue)  config.PacketTypes.BuffStart  = r.NewBuffStart.Value;
        if (r.NewBuffEnd.HasValue)    config.PacketTypes.BuffEnd    = r.NewBuffEnd.Value;
        if (r.NewEnterWorld.HasValue) config.PacketTypes.EnterWorld = r.NewEnterWorld.Value;
        if (r.NewDpsAttack.HasValue)  config.PacketTypes.DpsAttack  = r.NewDpsAttack.Value;
        if (r.NewDpsDamage.HasValue)  config.PacketTypes.DpsDamage  = r.NewDpsDamage.Value;
    }

    private static string ToHexString(byte[] bytes) =>
        BitConverter.ToString(bytes).Replace("-", " ");

    private static ProbeConfigSnapshot CaptureProbeConfigSnapshot(AppConfig config)
    {
        return new ProbeConfigSnapshot(
            config.Protocol.StartMarker,
            config.Protocol.EndMarker,
            config.PacketTypes.BuffStart,
            config.PacketTypes.BuffEnd,
            config.PacketTypes.EnterWorld,
            config.PacketTypes.DpsAttack,
            config.PacketTypes.DpsDamage);
    }

    private static void RestoreProbeConfigSnapshot(AppConfig config, ProbeConfigSnapshot snapshot)
    {
        config.Protocol.StartMarker = snapshot.StartMarker;
        config.Protocol.EndMarker = snapshot.EndMarker;
        config.PacketTypes.BuffStart = snapshot.BuffStart;
        config.PacketTypes.BuffEnd = snapshot.BuffEnd;
        config.PacketTypes.EnterWorld = snapshot.EnterWorld;
        config.PacketTypes.DpsAttack = snapshot.DpsAttack;
        config.PacketTypes.DpsDamage = snapshot.DpsDamage;
    }

    private sealed record ProbeConfigSnapshot(
        string StartMarker,
        string EndMarker,
        int BuffStart,
        int BuffEnd,
        int EnterWorld,
        int DpsAttack,
        int DpsDamage);

    // ------------------------------------------------------------------
    // App lifecycle
    // ------------------------------------------------------------------

    protected override void OnExit(ExitEventArgs e)
    {
        CleanupForExit();
        base.OnExit(e);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        CleanupForExit();
        base.OnSessionEnding(e);
    }

    private void RestartSaveDebounce()
    {
        // 프로브 결과 확인 대기 중에는 창 이동으로 인한 저장 억제
        if (_saveDebounce == null || _probePendingConfirmation) return;
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    private void AttachWindowCloseToAppShutdown(Window window)
    {
        window.Closing += (_, _) => BeginShutdown();
    }

    private void BeginShutdown()
    {
        if (_isShuttingDown) return;
        _isShuttingDown = true;
        Shutdown();
    }

    private void CleanupForExit()
    {
        _isShuttingDown = true;
        _probePendingConfirmation = false;
        _probeRollbackSnapshot = null;

        if (_confirmTimer != null)
        {
            try { _confirmTimer.Stop(); }
            catch { /* ignore */ }
            try { _confirmTimer.Tick -= OnConfirmTick; }
            catch { /* ignore */ }
            _confirmTimer = null;
        }

        if (_trayIcon != null)
        {
            try { _trayIcon.Visible = false; }           catch { /* ignore */ }
            try
            {
                _trayIcon.ContextMenuStrip?.Dispose();
                _trayIcon.ContextMenuStrip = null;
            }
            catch { /* ignore */ }
            try
            {
                var icon = _trayIcon.Icon;
                _trayIcon.Icon = null;
                icon?.Dispose();
            }
            catch { /* ignore */ }
            try { _trayIcon.Dispose(); } catch { /* ignore */ }
            _trayIcon = null;
        }

        try { _cts?.Cancel(); }      catch { /* ignore */ }
        try { _sniffer?.Dispose(); } catch { /* ignore */ }
        _sniffer = null;
        try { _cts?.Dispose(); }     catch { /* ignore */ }
        _cts = null;
    }

    // ------------------------------------------------------------------
    // Tray icon
    // ------------------------------------------------------------------

    private NotifyIcon CreateTrayIcon()
    {
        var menu = new ContextMenuStrip();

        ToolStripMenuItem? timerItem = null;
        ToolStripMenuItem? dpsItem   = null;

        if (_timerWindow != null)
        {
            timerItem = new ToolStripMenuItem();
            timerItem.Click += (_, _) => Dispatcher.Invoke(() =>
            {
                if (_timerWindow.WindowState != WindowState.Minimized)
                    _timerWindow.WindowState = WindowState.Minimized;
                else
                    _timerWindow.WindowState = WindowState.Normal;
            });
            menu.Items.Add(timerItem);
        }

        if (_dpsWindow != null)
        {
            dpsItem = new ToolStripMenuItem();
            dpsItem.Click += (_, _) => Dispatcher.Invoke(() =>
            {
                if (_dpsWindow.WindowState != WindowState.Minimized)
                    _dpsWindow.WindowState = WindowState.Minimized;
                else
                    _dpsWindow.WindowState = WindowState.Normal;
            });
            menu.Items.Add(dpsItem);
        }

        menu.Opening += (_, _) => Dispatcher.Invoke(() =>
        {
            if (timerItem != null)
                timerItem.Text = _timerWindow!.WindowState != WindowState.Minimized
                    ? "타이머 최소화" : "타이머 복원";
            if (dpsItem != null)
                dpsItem.Text = _dpsWindow!.WindowState != WindowState.Minimized
                    ? "DPS 최소화" : "DPS 복원";
        });

        if (menu.Items.Count > 0)
            menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("종료").Click += (_, _) => Dispatcher.Invoke(BeginShutdown);

        var icon = new NotifyIcon
        {
            Icon             = BuildIcon(),
            Text             = "OverlayTimer",
            Visible          = true,
            ContextMenuStrip = menu
        };
        return icon;
    }

    private static Icon BuildIcon()
    {
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            using var pen = new Pen(Color.White, 2f);
            g.DrawEllipse(pen, 1, 1, 13, 13);
            g.DrawLine(pen, 8, 8, 8, 3);
            g.DrawLine(pen, 8, 8, 12, 8);
        }

        var hIcon = bmp.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(hIcon);
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private void ShowStartupError(string message, string detail)
    {
        System.Windows.MessageBox.Show(
            $"{message}\n\n오류: {detail}",
            "OverlayTimer",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Warning);

        BeginShutdown();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
