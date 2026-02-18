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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var config = AppConfig.Load();
        _cts = new CancellationTokenSource();

        var window = new OverlayTimerWindow();
        window.Show();

        _trayIcon = CreateTrayIcon();

        var selfIdResolver = new SelfIdResolver(config.PacketTypes.EnterWorld);
        ITimerTrigger timerTrigger = new OverlayTriggerTimer(window, config.Timer);

        var typeLogger = new PacketTypeLogger();
        OverlayTimerWindow.OnF9Press = typeLogger.TogglePhase;

        var packetHandler = new PacketHandler(timerTrigger, selfIdResolver, config.PacketTypes.BuffStart, config.BuffKeys, typeLogger);

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
        menu.Items.Add("醫낅즺").Click += (_, _) => Shutdown();

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

