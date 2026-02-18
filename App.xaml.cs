using System.Threading;
using System.Windows;
using OverlayTimer.Net;

namespace OverlayTimer;

public partial class App : Application
{
    private CancellationTokenSource? _cts;
    private SnifferService? _sniffer;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var config = AppConfig.Load();
        _cts = new CancellationTokenSource();

        var window = new OverlayTimerWindow();
        window.Show();

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
            _cts?.Cancel();
            _sniffer?.Dispose();
        }
        catch { /* ignore */ }

        base.OnExit(e);
    }
}
