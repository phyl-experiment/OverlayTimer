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
    private const int  MaxProbeAttempts      = 3;
    private const int  MinFramesForTypeProbe = 50;          // condition②: 마커 정상이나 타입 미인식

    private readonly List<byte> _probeBuffer  = new();
    private long _totalS2cBytes       = 0;
    private int  _probeAttemptCount   = 0;
    private long _nextProbeThreshold  = ProbeThreshold;

    /// <summary>
    /// 새 프로토콜 값이 발견되면 호출된다. UI 스레드에서 구독자가 AppConfig 를 갱신하고 재시작한다.
    /// </summary>
    public Action<ProbeResult>? OnProbeSuccess { get; set; }

    /// <summary>
    /// condition②(마커 정상·타입 미인식) 판별에 사용.
    /// SnifferService 가 PacketHandler.RecognizedPacketCount 를 연결한다.
    /// </summary>
    public Func<int>? GetRecognizedPackets { get; set; }

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

    private void TryTriggerProbe()
    {
        if (_probeAttemptCount >= MaxProbeAttempts) return;
        if (_totalS2cBytes < _nextProbeThreshold)   return;

        int framesFound = _streamParser.FramesFound;
        int recognized  = GetRecognizedPackets?.Invoke() ?? 0;

        // condition①: 마커 자체를 인식 못함 → 풀 프로브
        bool fullProbe = framesFound == 0;
        // condition②: 마커는 인식하나 패킷 타입을 전혀 인식 못함 → 타입만 프로브
        bool typeProbe = !fullProbe && framesFound >= MinFramesForTypeProbe && recognized == 0;

        if (!fullProbe && !typeProbe) return;

        _probeAttemptCount++;
        _nextProbeThreshold *= 2;

        var snapshot = _probeBuffer.ToArray();
        string mode  = fullProbe ? "full" : "type-only";
        LogHelper.Write(
            $"[Probe] Attempt {_probeAttemptCount}/{MaxProbeAttempts} ({mode}): " +
            $"totalS2c={_totalS2cBytes} probeLen={snapshot.Length} " +
            $"frames={framesFound} recognized={recognized}");

        var result = fullProbe
            ? ProtocolProbe.TryDiscover(snapshot, _protocolConfig, _typesConfig)
            : ProtocolProbe.TryDiscover(snapshot, _protocolConfig, _typesConfig, markerRadius: 0);

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
            $" dpsAttack={result.NewDpsAttack}" +
            $" dpsDamage={result.NewDpsDamage}");

        OnProbeSuccess?.Invoke(result);
    }

    private static string FormatMarker(byte[]? marker)
    {
        if (marker == null) return "(unchanged)";
        return BitConverter.ToString(marker).Replace("-", " ");
    }
}
