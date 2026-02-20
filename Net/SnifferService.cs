using System;
using System.Threading;
using PacketDotNet;
using SharpPcap;

namespace OverlayTimer.Net
{
    public sealed class SnifferService : IDisposable
    {
        private readonly int               _targetPort;
        private readonly PacketHandler     _packetHandler;
        private readonly PacketStreamParser _parser;
        private readonly ProtocolConfig    _protocolConfig;
        private readonly PacketTypesConfig _typesConfig;

        private CaptureWorker? _worker;
        private ICaptureDevice? _device;
        private bool _running;

        private readonly string? _deviceName;

        /// <summary>
        /// ProtocolProbe がプロトコル更新を発見したとき呼ばれる。
        /// App.xaml.cs でサブスクライブし、設定保存→再起動を行う。
        /// </summary>
        public Action<ProbeResult>? OnProbeSuccess { get; set; }

        /// <summary>현재 sniffer 세션에서 인식된 누적 패킷 수. 프로브 결과 확인에 사용.</summary>
        public int RecognizedPacketCount => _packetHandler.RecognizedPacketCount;

        public SnifferService(
            int targetPort,
            string? deviceName,
            PacketHandler packetHandler,
            ProtocolConfig protocolConfig,
            PacketTypesConfig typesConfig)
        {
            _targetPort     = targetPort;
            _deviceName     = deviceName;
            _packetHandler  = packetHandler;
            _protocolConfig = protocolConfig;
            _typesConfig    = typesConfig;
            _parser = new PacketStreamParser(
                packetHandler,
                protocolConfig.StartMarkerBytes,
                protocolConfig.EndMarkerBytes);
        }

        public void Start(CancellationToken ct)
        {
            if (_running) return;
            _running = true;

            _worker = new CaptureWorker(_parser, _protocolConfig, _typesConfig)
            {
                OnProbeSuccess       = r => OnProbeSuccess?.Invoke(r),
                GetRecognizedPackets = () => _packetHandler.RecognizedPacketCount
            };

            _device = DeviceSelector.OpenBestEthernetOrFallback(_deviceName, readTimeoutMs: 1000);
            _device.Filter = $"tcp src port {_targetPort}";
            _device.OnPacketArrival += OnPacketArrival;
            _device.StartCapture();

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
                var raw    = e.GetPacket();
                var packet = Packet.ParsePacket(raw.LinkLayerType, raw.Data);
                var tcp    = packet.Extract<TcpPacket>();
                if (tcp == null) return;

                var dir = (tcp.DestinationPort == _targetPort)
                    ? CaptureWorker.Direction.ClientToServer
                    : CaptureWorker.Direction.ServerToClient;

                _worker?.OnTcpPayload(tcp.PayloadData, dir);
            }
            catch
            {
                // 캡처 중 예외는 무시
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
