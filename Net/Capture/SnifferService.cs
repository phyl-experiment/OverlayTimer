using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using PacketDotNet;
using SharpPcap;

namespace OverlayTimer.Net
{
    public sealed class SnifferService : IDisposable
    {
        private const int ReadTimeoutMs = 1000;
        private static readonly TimeSpan AutoRescoreNoPacketThreshold = TimeSpan.FromSeconds(12);
        private static readonly TimeSpan AutoRescoreProbeDuration = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan AutoRescoreCooldown = TimeSpan.FromSeconds(25);
        private const int AutoRescoreMaxAttempts = 4;

        private readonly int _targetPort;
        private readonly PacketHandler _packetHandler;
        private readonly ProtocolConfig _protocolConfig;
        private readonly PacketTypesConfig _typesConfig;
        private readonly LoggingConfig _loggingConfig;

        private readonly string? _captureFilter;
        private readonly string? _deviceName;
        private readonly bool _autoReselect;
        private readonly OverlayTimer.DebugInfo? _debugInfo;

        private readonly object _captureLock = new();
        private PacketStreamParser _parser;
        private CaptureWorker _worker;
        private ICaptureDevice? _device;
        private bool _running;
        private System.Threading.Timer? _statsTimer;

        private IReadOnlyList<DeviceSelector.DeviceCandidate> _candidates =
            Array.Empty<DeviceSelector.DeviceCandidate>();
        private DeviceSelector.DeviceCandidate? _activeCandidate;

        private long _arrivalCount;
        private long _tcpPayloadPacketCount;
        private long _tcpPayloadBytes;
        private long _startedUtcTicks;
        private long _lastPacketUtcTicks;
        private int _firstPacketLogged;

        private int _autoRescoreAttempts;
        private long _nextAutoRescoreUtcTicks;
        private int _autoRescoreInProgress;

        public Action<ProbeResult>? OnProbeSuccess { get; set; }

        public int RecognizedPacketCount => _packetHandler.RecognizedPacketCount;

        public SnifferService(
            int targetPort,
            string? captureFilter,
            string? deviceName,
            bool autoReselect,
            PacketHandler packetHandler,
            ProtocolConfig protocolConfig,
            PacketTypesConfig typesConfig,
            LoggingConfig loggingConfig,
            OverlayTimer.DebugInfo? debugInfo = null)
        {
            _targetPort = targetPort;
            _captureFilter = captureFilter;
            _deviceName = deviceName;
            _autoReselect = autoReselect;
            _packetHandler = packetHandler;
            _protocolConfig = protocolConfig;
            _typesConfig = typesConfig;
            _loggingConfig = loggingConfig ?? new LoggingConfig();
            _debugInfo = debugInfo;

            _parser = new PacketStreamParser(
                packetHandler,
                protocolConfig.StartMarkerBytes,
                protocolConfig.EndMarkerBytes);
            _worker = new CaptureWorker(_parser, _protocolConfig, _typesConfig)
            {
                OnProbeSuccess = r => OnProbeSuccess?.Invoke(r),
                GetRecognizedPackets = () => _packetHandler.RecognizedPacketCount
            };
        }

        public void Start(CancellationToken ct)
        {
            if (_running) return;
            _running = true;
            ResetCaptureCounters();

            LogHelper.Write(
                $"[Sniffer] Start: targetPort={_targetPort} " +
                $"captureFilter=\"{GetEffectiveFilter()}\" " +
                $"deviceName=\"{_deviceName ?? "(auto)"}\" autoReselect={_autoReselect} " +
                $"startMarker=\"{_protocolConfig.StartMarker}\" endMarker=\"{_protocolConfig.EndMarker}\" " +
                $"types(bStart={_typesConfig.BuffStart}, bEnd={_typesConfig.BuffEnd}, enter={_typesConfig.EnterWorld}, atk={_typesConfig.DpsAttack}, dmg={_typesConfig.DpsDamage})");

            _candidates = DeviceSelector.GetCandidates();
            DeviceSelector.LogCandidates(_candidates);

            var selected = DeviceSelector.SelectCandidate(_candidates, _deviceName);
            OpenCaptureOnCandidate(selected, reason: string.IsNullOrWhiteSpace(_deviceName)
                ? $"initial auto staticScore={selected.StaticScore}"
                : $"configured deviceName={_deviceName}");

            StartStatsTimer();

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
                Interlocked.Increment(ref _arrivalCount);
                Interlocked.Exchange(ref _lastPacketUtcTicks, DateTime.UtcNow.Ticks);

                var raw = e.GetPacket();
                var packet = Packet.ParsePacket(raw.LinkLayerType, raw.Data);
                var tcp = packet.Extract<TcpPacket>();
                if (tcp == null) return;
                if (tcp.PayloadData == null || tcp.PayloadData.Length == 0) return;

                Interlocked.Increment(ref _tcpPayloadPacketCount);
                Interlocked.Add(ref _tcpPayloadBytes, tcp.PayloadData.Length);

                if (Interlocked.Exchange(ref _firstPacketLogged, 1) == 0)
                    LogHelper.Write("[Sniffer] First TCP payload packet captured.");

                var dir = (tcp.DestinationPort == _targetPort)
                    ? CaptureWorker.Direction.ClientToServer
                    : CaptureWorker.Direction.ServerToClient;

                _worker.OnTcpPayload(tcp.PayloadData, dir);
            }
            catch (Exception ex)
            {
                LogHelper.Write($"[Sniffer] OnPacketArrival error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (!_running) return;
            _running = false;
            StopStatsTimer();
            LogCaptureStats();
            CloseCurrentDevice();
        }

        private void StartStatsTimer()
        {
            if (LogHelper.Disabled)
                return;

            int intervalSeconds = _loggingConfig.CaptureStatsIntervalSeconds;
            if (intervalSeconds <= 0)
                return;

            var interval = TimeSpan.FromSeconds(intervalSeconds);
            _statsTimer = new System.Threading.Timer(_ => LogCaptureStats(), null, interval, interval);
        }

        private void StopStatsTimer()
        {
            if (_statsTimer == null)
                return;

            try { _statsTimer.Dispose(); }
            catch { /* ignore */ }
            _statsTimer = null;
        }

        private void LogCaptureStats()
        {
            if (LogHelper.Disabled || !_running)
                return;

            long arrivalCount = Interlocked.Read(ref _arrivalCount);
            long tcpPackets = Interlocked.Read(ref _tcpPayloadPacketCount);
            long tcpBytes = Interlocked.Read(ref _tcpPayloadBytes);
            long startedTicks = Interlocked.Read(ref _startedUtcTicks);
            long lastTicks = Interlocked.Read(ref _lastPacketUtcTicks);

            if (startedTicks <= 0 || lastTicks <= 0)
                return;

            var startedUtc = new DateTime(startedTicks, DateTimeKind.Utc);
            var lastUtc = new DateTime(lastTicks, DateTimeKind.Utc);
            var uptime = DateTime.UtcNow - startedUtc;
            var idle = DateTime.UtcNow - lastUtc;

            LogHelper.Write(
                $"[CaptureStats] device=\"{_activeCandidate?.Device.Name ?? "(none)"}\" uptime={uptime.TotalSeconds:0}s " +
                $"arrivals={arrivalCount} tcpPayloadPackets={tcpPackets} tcpPayloadBytes={tcpBytes} " +
                $"frames={_parser.FramesFound} recognized={_packetHandler.RecognizedPacketCount} idle={idle.TotalSeconds:0}s");

            if (arrivalCount == 0 && uptime.TotalSeconds >= 10)
            {
                LogHelper.Write(
                    "[CaptureStats] No packets captured. Check selected device, admin privilege, and Npcap compatibility mode.");
            }

            MaybeTriggerAutoRescore(uptime, arrivalCount);
        }

        private void MaybeTriggerAutoRescore(TimeSpan uptime, long arrivals)
        {
            if (!_running)
                return;

            if (!_autoReselect || !string.IsNullOrWhiteSpace(_deviceName))
                return;

            if (arrivals > 0 || uptime < AutoRescoreNoPacketThreshold)
                return;

            if (_autoRescoreAttempts >= AutoRescoreMaxAttempts)
                return;

            long nowTicks = DateTime.UtcNow.Ticks;
            long nextTicks = Interlocked.Read(ref _nextAutoRescoreUtcTicks);
            if (nextTicks > nowTicks)
                return;

            if (Interlocked.CompareExchange(ref _autoRescoreInProgress, 1, 0) != 0)
                return;

            _autoRescoreAttempts++;
            Interlocked.Exchange(
                ref _nextAutoRescoreUtcTicks,
                DateTime.UtcNow.Add(AutoRescoreCooldown).Ticks);

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    RunAutoRescore();
                }
                catch (Exception ex)
                {
                    LogHelper.Write($"[AutoRescore] Failed: {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref _autoRescoreInProgress, 0);
                }
            });
        }

        private void RunAutoRescore()
        {
            if (!_running)
                return;

            string filter = GetEffectiveFilter();
            LogHelper.Write(
                $"[AutoRescore] Attempt {_autoRescoreAttempts}/{AutoRescoreMaxAttempts}: " +
                $"probing {_candidates.Count} candidates with filter=\"{filter}\" duration={AutoRescoreProbeDuration.TotalSeconds:0.#}s");

            CloseCurrentDevice();
            ResetParserAndWorker();

            DeviceSelector.DeviceProbeScore? best = null;
            foreach (var candidate in _candidates.OrderByDescending(c => c.StaticScore).ThenBy(c => c.Index))
            {
                DeviceSelector.DeviceProbeScore probe;
                try
                {
                    probe = DeviceSelector.ProbeCandidateTraffic(
                        candidate,
                        filter,
                        AutoRescoreProbeDuration);
                }
                catch (Exception ex)
                {
                    LogHelper.Write(
                        $"[AutoRescore] Probe failed: name=\"{candidate.Device.Name}\" error={ex.GetType().Name}:{ex.Message}");
                    continue;
                }

                LogHelper.Write(
                    $"[AutoRescore] Probe: name=\"{candidate.Device.Name}\" staticScore={candidate.StaticScore} " +
                    $"arrivals={probe.ArrivalPackets} tcpPayloadPackets={probe.TcpPayloadPackets} " +
                    $"tcpPayloadBytes={probe.TcpPayloadBytes} dynamicScore={probe.DynamicScore} " +
                    $"reason=\"{probe.DynamicReason}\"");

                if (best == null ||
                    probe.DynamicScore > best.DynamicScore ||
                    (probe.DynamicScore == best.DynamicScore &&
                     probe.Candidate.StaticScore > best.Candidate.StaticScore))
                {
                    best = probe;
                }
            }

            if (best == null)
            {
                if (!_running)
                    return;

                LogHelper.Write("[AutoRescore] No probe result. Reopening previous best static candidate.");
                var fallback = DeviceSelector.SelectCandidate(_candidates, null);
                OpenCaptureOnCandidate(fallback, reason: "autoRescore fallback(static)");
                return;
            }

            if (!_running)
                return;

            OpenCaptureOnCandidate(
                best.Candidate,
                reason: $"autoRescore dynamicScore={best.DynamicScore}");
            LogHelper.Write(
                $"[AutoRescore] Switched to name=\"{best.Candidate.Device.Name}\" desc=\"{best.Candidate.Device.Description ?? "(null)"}\"");
        }

        private void OpenCaptureOnCandidate(DeviceSelector.DeviceCandidate candidate, string reason)
        {
            if (!_running)
                return;

            lock (_captureLock)
            {
                if (!_running)
                    return;

                CloseCurrentDevice();

                string filter = GetEffectiveFilter();
                var opened = DeviceSelector.OpenCandidate(candidate, ReadTimeoutMs);
                opened.Filter = filter;
                opened.OnPacketArrival += OnPacketArrival;
                opened.StartCapture();

                _device = opened;
                _activeCandidate = candidate;
                ResetCaptureCounters();

                string nicLabel = string.IsNullOrWhiteSpace(opened.Description)
                    ? opened.Name
                    : $"{opened.Description} ({opened.Name})";
                _debugInfo?.SetNic(nicLabel);

                LogHelper.Write(
                    $"[Sniffer] Capture started: device=\"{opened.Name}\" desc=\"{opened.Description ?? "(null)"}\" " +
                    $"filter=\"{filter}\" reason=\"{reason}\" staticScore={candidate.StaticScore}");
            }
        }

        private void CloseCurrentDevice()
        {
            lock (_captureLock)
            {
                if (_device == null)
                    return;

                try { _device.OnPacketArrival -= OnPacketArrival; } catch { /* ignore */ }
                try { _device.StopCapture(); } catch { /* ignore */ }
                try { _device.Close(); } catch { /* ignore */ }
                _device = null;
            }
        }

        private void ResetParserAndWorker()
        {
            _parser = new PacketStreamParser(
                _packetHandler,
                _protocolConfig.StartMarkerBytes,
                _protocolConfig.EndMarkerBytes);

            _worker = new CaptureWorker(_parser, _protocolConfig, _typesConfig)
            {
                OnProbeSuccess = r => OnProbeSuccess?.Invoke(r),
                GetRecognizedPackets = () => _packetHandler.RecognizedPacketCount
            };
        }

        private void ResetCaptureCounters()
        {
            _arrivalCount = 0;
            _tcpPayloadPacketCount = 0;
            _tcpPayloadBytes = 0;
            _firstPacketLogged = 0;
            _startedUtcTicks = DateTime.UtcNow.Ticks;
            _lastPacketUtcTicks = _startedUtcTicks;
        }

        private string GetEffectiveFilter()
        {
            return string.IsNullOrWhiteSpace(_captureFilter)
                ? $"tcp src port {_targetPort}"
                : _captureFilter!;
        }
    }
}
