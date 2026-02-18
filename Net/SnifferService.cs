using System;
using System.Threading;
using PacketDotNet;
using SharpPcap;

namespace OverlayTimer.Net
{
    public sealed class SnifferService : IDisposable
    {
        private readonly int _targetPort;
        private readonly PacketHandler _packetHandler;
        private readonly PacketStreamParser _parser;
        private CaptureWorker? _worker;

        private ICaptureDevice? _device;
        private bool _running;

        public SnifferService(int targetPort, PacketHandler packetHandler, ProtocolConfig protocol)
        {
            _targetPort = targetPort;
            _packetHandler = packetHandler;
            _parser = new PacketStreamParser(packetHandler, protocol.StartMarkerBytes, protocol.EndMarkerBytes);
        }

        public void Start(CancellationToken ct)
        {
            if (_running) return;
            _running = true;
            _worker = new CaptureWorker(_parser);

            _device = DeviceSelector.OpenBestEthernetOrFallback(readTimeoutMs: 1000);

            // 클라이언트가 "받는" 쪽을 주로 보고 싶다면 src port
            _device.Filter = $"tcp src port {_targetPort}";

            _device.OnPacketArrival += OnPacketArrival;
            _device.StartCapture();

            // cancellation 연동
            ct.Register(() =>
            {
                try { Dispose(); }
                catch { /* ignore */ }
            });
        }

        private void OnPacketArrival(object sender, PacketCapture e)
        {
            try
            {
                var raw = e.GetPacket();
                var packet = Packet.ParsePacket(raw.LinkLayerType, raw.Data);
                var tcp = packet.Extract<TcpPacket>();
                if (tcp == null) return;

                var dir = (tcp.DestinationPort == _targetPort)
                    ? CaptureWorker.Direction.ClientToServer
                    : CaptureWorker.Direction.ServerToClient;

                _worker?.OnTcpPayload(tcp.PayloadData, dir);
            }
            catch
            {
                // 캡처 중 예외는 죽지 않게 무시(필요하면 로깅)
            }
        }

        public void Dispose()
        {
            if (!_running) return;
            _running = false;

            if (_device != null)
            {
                try
                {
                    _device.OnPacketArrival -= OnPacketArrival;
                    _device.StopCapture();
                }
                catch { /* ignore */ }

                try { _device.Close(); }
                catch { /* ignore */ }

                _device = null;
            }
        }
    }

}
