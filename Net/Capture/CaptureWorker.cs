using OverlayTimer;
using OverlayTimer.Net;
using System;
using System.Collections.Generic;

public sealed class CaptureWorker
{
    private readonly PacketStreamParser _streamParser;
    private readonly ProtocolConfig     _protocolConfig;
    private readonly PacketTypesConfig  _typesConfig;

    private readonly List<byte> _c2sBuffer = new();
    private readonly List<byte> _s2cBuffer = new();

    // ------------------------------------------------------------------
    // Probe buffer: S2C 원본 바이트를 최대 ProbeBufferMax 만큼 보존
    // (파서가 소비한 뒤에도 프로브용으로 사용)
    // ------------------------------------------------------------------
    private const int  ProbeBufferMax        = 128 * 1024; // 128KB 보존
    private const int  ProbeThreshold        =  64 * 1024; // 64KB 수신 후 첫 프로브
    private const long ProbeThresholdCap     =   1 * 1024 * 1024; // 1MB 상한 — 이후 같은 간격 반복
    private const int  MinFramesForTypeProbe = 10;          // condition②: 마커 정상이나 타입 미인식

    private const int  ConfirmedMarkerReprobeMissThreshold = 2;

    private readonly List<byte> _probeBuffer  = new();
    private long _totalS2cBytes       = 0;
    private int  _probeAttemptCount   = 0;
    private long _nextProbeThreshold  = ProbeThreshold;
    private int _confirmedMarkerMissCount;

    /// <summary>
    /// 새 프로토콜 값이 발견되면 호출된다. UI 스레드에서 구독자가 AppConfig 를 갱신하고 재시작한다.
    /// </summary>
    public Action<ProbeResult>? OnProbeSuccess { get; set; }

    /// <summary>
    /// 인식된 패킷 수 (확인용).
    /// SnifferService 가 PacketHandler.RecognizedPacketCount 를 연결한다.
    /// </summary>
    public Func<int>? GetRecognizedPackets { get; set; }

    /// <summary>
    /// 인식된 dataType 집합을 반환한다.
    /// SnifferService 가 PacketHandler.RecognizedDataTypes 를 연결한다.
    /// </summary>
    public Func<IReadOnlySet<int>>? GetRecognizedDataTypes { get; set; }

    /// <summary>디버그 오버레이에 probe 상태를 표시하기 위한 참조.</summary>
    public OverlayTimer.DebugInfo? DebugInfo { get; set; }

    public CaptureWorker(
        PacketStreamParser streamParser,
        ProtocolConfig     protocolConfig,
        PacketTypesConfig  typesConfig)
    {
        _streamParser   = streamParser;
        _protocolConfig = protocolConfig;
        _typesConfig    = typesConfig;
    }

    public enum Direction
    {
        ClientToServer,
        ServerToClient
    }

    public void OnTcpPayload(ReadOnlySpan<byte> payload, Direction direction)
    {
        if (payload.IsEmpty)
            return;

        var buffer = direction == Direction.ClientToServer ? _c2sBuffer : _s2cBuffer;
        var raw    = payload.ToArray();

        buffer.AddRange(raw);

        if (direction == Direction.ServerToClient)
        {
            _totalS2cBytes += raw.Length;
            AppendToProbeBuffer(raw);
            TryTriggerProbe();
        }

        // 가능한 만큼 즉시 파싱
        while (buffer.Count > 0)
        {
            var data     = buffer.ToArray();
            var consumed = _streamParser.ParsePackets(data);

            if (consumed <= 0)
                break;

            if (consumed >= buffer.Count) buffer.Clear();
            else buffer.RemoveRange(0, consumed);
        }

        // 안전장치
        const int MaxBufferBytes = 4 * 1024 * 1024;
        if (buffer.Count > MaxBufferBytes)
        {
            Console.WriteLine($"[WARN] Buffer too large ({buffer.Count} bytes). Clearing.");
            buffer.Clear();
        }
    }

    // ------------------------------------------------------------------
    // Probe buffer management
    // ------------------------------------------------------------------

    private void AppendToProbeBuffer(byte[] raw)
    {
        _probeBuffer.AddRange(raw);
        if (_probeBuffer.Count > ProbeBufferMax)
            _probeBuffer.RemoveRange(0, _probeBuffer.Count - ProbeBufferMax);
    }

    // ------------------------------------------------------------------
    // Failure detection & probe trigger
    // ------------------------------------------------------------------

    /// <summary>
    /// unconfirmed 타입 중 아직 인식되지 않은 것이 있는지 확인.
    /// </summary>
    public bool HasUnrecognizedUnconfirmedTypes()
    {
        return GetUnrecognizedUnconfirmedNames().Count > 0;
    }

    /// <summary>
    /// unconfirmed이면서 미인식인 타입 이름 목록을 반환한다.
    /// </summary>
    public List<string> GetUnrecognizedUnconfirmedNames()
    {
        var result = new List<string>();
        var recognized = GetRecognizedDataTypes?.Invoke();
        if (recognized == null) return result;

        if (!_typesConfig.BuffStart.Confirmed          && !recognized.Contains(_typesConfig.BuffStart.Value))          result.Add("buffStart");
        if (!_typesConfig.BuffEnd.Confirmed            && !recognized.Contains(_typesConfig.BuffEnd.Value))            result.Add("buffEnd");
        if (!_typesConfig.EnterWorld.Confirmed         && !recognized.Contains(_typesConfig.EnterWorld.Value))         result.Add("enterWorld");
        if (!_typesConfig.ReadyToEnterWorldB.Confirmed && !recognized.Contains(_typesConfig.ReadyToEnterWorldB.Value)) result.Add("readyToEnterWorldB");
        if (!_typesConfig.DpsAttack.Confirmed          && !recognized.Contains(_typesConfig.DpsAttack.Value))          result.Add("dpsAttack");
        if (!_typesConfig.DpsDamage.Confirmed          && !recognized.Contains(_typesConfig.DpsDamage.Value))          result.Add("dpsDamage");

        return result;
    }

    private void TryTriggerProbe()
    {
        if (_totalS2cBytes < _nextProbeThreshold) return;

        int framesFound = _streamParser.FramesFound;
        int recognized  = GetRecognizedPackets?.Invoke() ?? 0;

        // condition①: 마커 자체를 인식 못함 → 풀 프로브
        bool fullProbe = framesFound == 0;
        bool includeConfirmedMarkers = false;
        bool typeProbe = false;
        List<string>? unrecognizedNames = null;

        if (fullProbe)
        {
            includeConfirmedMarkers = ShouldForceConfirmedMarkerProbe();
        }
        else
        {
            _confirmedMarkerMissCount = 0;
            unrecognizedNames = GetUnrecognizedUnconfirmedNames();
            typeProbe = framesFound >= MinFramesForTypeProbe && unrecognizedNames.Count > 0;
        }

        if (!fullProbe && !typeProbe)
        {
            DebugInfo?.SetProbeStatus("");
            return;
        }

        // 디버그 표시: probe 대상
        var targets = fullProbe
            ? (includeConfirmedMarkers ? "markers+types (forced)" : "markers+types")
            : string.Join(", ", unrecognizedNames!);
        DebugInfo?.SetProbeStatus($"Probing: {targets}");

        _probeAttemptCount++;
        // 임계치 증가 (상한 도달 시 같은 간격 반복 → 무한 재시도 가능)
        if (_nextProbeThreshold < ProbeThresholdCap)
            _nextProbeThreshold *= 2;
        else
            _nextProbeThreshold += ProbeThresholdCap;

        var snapshot = _probeBuffer.ToArray();
        string mode  = fullProbe
            ? (includeConfirmedMarkers ? "full-forced" : "full")
            : "type-only";
        LogHelper.Write(
            $"[Probe] Attempt {_probeAttemptCount} ({mode}): " +
            $"totalS2c={_totalS2cBytes} probeLen={snapshot.Length} " +
            $"frames={framesFound} recognized={recognized}");

        var result = fullProbe
            ? ProtocolProbe.TryDiscover(
                snapshot,
                _protocolConfig,
                _typesConfig,
                includeConfirmedMarkers: includeConfirmedMarkers)
            : ProtocolProbe.TryDiscover(
                snapshot,
                _protocolConfig,
                _typesConfig,
                markerRadius: 0);

        if (result == null)
        {
            LogHelper.Write($"[Probe] Attempt {_probeAttemptCount}: No updated protocol found.");
            return;
        }

        LogHelper.Write(
            $"[Probe] Found! frames={result.FramesFound}" +
            $" startMarker={FormatMarker(result.NewStartMarker)}" +
            $" endMarker={FormatMarker(result.NewEndMarker)}" +
            $" buffStart={result.NewBuffStart}" +
            $" buffEnd={result.NewBuffEnd}" +
            $" enterWorld={result.NewEnterWorld}" +
            $" readyToEnterWorldB={result.NewReadyToEnterWorldB}" +
            $" dpsAttack={result.NewDpsAttack}" +
            $" dpsDamage={result.NewDpsDamage}");

        OnProbeSuccess?.Invoke(result);
    }

    private bool ShouldForceConfirmedMarkerProbe()
    {
        if (!_protocolConfig.StartMarker.Confirmed || !_protocolConfig.EndMarker.Confirmed)
        {
            _confirmedMarkerMissCount = 0;
            return false;
        }

        _confirmedMarkerMissCount++;
        return _confirmedMarkerMissCount >= ConfirmedMarkerReprobeMissThreshold;
    }

    private static string FormatMarker(byte[]? marker)
    {
        if (marker == null) return "(unchanged)";
        return BitConverter.ToString(marker).Replace("-", " ");
    }
}
