using System.Drawing;
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
    private OverlayTimerWindow? _timerWindow;
    private DpsOverlayWindow? _dpsWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var config = AppConfig.Load();
        var skillNames = SkillNameMap.Load();
        _cts = new CancellationTokenSource();

        OverlayTimerWindow? window = null;
        ITimerTrigger timerTrigger = NoopTimerTrigger.Instance;
        PacketTypeLogger? typeLogger = null;

        if (config.Overlays.Timer.Enabled)
        {
            window = new OverlayTimerWindow
            {
                Left = config.Overlays.Timer.X,
                Top = config.Overlays.Timer.Y
            };
            _timerWindow = window;
            window.Show();

            timerTrigger = new OverlayTriggerTimer(window, config.Timer);

            typeLogger = new PacketTypeLogger();
            OverlayTimerWindow.OnF9Press = typeLogger.TogglePhase;
        }

        var dpsTracker = new DpsTracker();
        if (config.Overlays.Dps.Enabled)
        {
            _dpsWindow = new DpsOverlayWindow(dpsTracker, skillNames)
            {
                Left = config.Overlays.Dps.X,
                Top = config.Overlays.Dps.Y
            };
            _dpsWindow.Show();
        }

        _trayIcon = CreateTrayIcon();

        var selfIdResolver = new SelfIdResolver(config.PacketTypes.EnterWorld);

        var packetHandler = new PacketHandler(
            timerTrigger,
            selfIdResolver,
            config.PacketTypes.BuffStart,
            config.BuffKeys,
            typeLogger,
            dpsTracker,
            config.PacketTypes.DpsAttack,
            config.PacketTypes.DpsDamage);

        _sniffer = new SnifferService(config.Network.TargetPort, config.Network.DeviceName, packetHandler, config.Protocol);
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

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _trayIcon?.Dispose();
            _cts?.Cancel();
            _sniffer?.Dispose();
        }
        catch { /* ignore */ }

        base.OnExit(e);
    }

    private NotifyIcon CreateTrayIcon()
    {
        var menu = new ContextMenuStrip();

        ToolStripMenuItem? timerItem = null;
        ToolStripMenuItem? dpsItem = null;

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

        // 메뉴가 열릴 때마다 현재 창 상태를 반영해 텍스트 갱신
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

        menu.Items.Add("종료").Click += (_, _) => Dispatcher.Invoke(Shutdown);

        var icon = new NotifyIcon
        {
            Icon = BuildIcon(),
            Text = "OverlayTimer",
            Visible = true,
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
        return Icon.FromHandle(bmp.GetHicon());
    }

    private void ShowStartupError(string message, string detail)
    {
        System.Windows.MessageBox.Show(
            $"{message}\n\n오류: {detail}",
            "OverlayTimer",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Warning);

        Shutdown();
    }
}

