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
        var packetHandler = new PacketHandler(timerTrigger, selfIdResolver, config.PacketTypes.BuffStart, config.BuffKeys);

        _sniffer = new SnifferService(config.Network.TargetPort, config.Network.DeviceName, packetHandler, config.Protocol);
        _sniffer.Start(_cts.Token);
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
        menu.Items.Add("종료").Click += (_, _) => Shutdown();

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
}
