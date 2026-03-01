using System;
using OverlayTimer.Net;

public sealed class SelfIdResolver
{
    public ulong SelfId => _selfId;

    private ulong _selfId;
    private readonly int _enterWorldType;
    private readonly OverlayTimer.DebugInfo? _debugInfo;

    public SelfIdResolver(int enterWorldType, OverlayTimer.DebugInfo? debugInfo = null)
    {
        _enterWorldType = enterWorldType;
        _debugInfo = debugInfo;
    }

    public ulong TryFeed(int dataType, ReadOnlySpan<byte> payload)
    {
        if (dataType != _enterWorldType)
            return 0;

        if (!PacketEnterWorld.TryParse(payload, out var pkt))
            return 0;

        LogHelper.Write($"SelfId set {pkt.SelfId}");
        _selfId = pkt.SelfId;
        _debugInfo?.AddEnterWorldRecord(pkt.SelfId);
        _debugInfo?.SetSelfId(pkt.SelfId, "EnterWorld");
        return pkt.SelfId;
    }

    /// <summary>EnterWorld 없이 데미지 패킷 등에서 self ID를 추론한 경우 강제 설정.</summary>
    public void ForceSetId(ulong id)
    {
        if (id == 0) return;
        LogHelper.Write($"SelfId set (damage fallback) {id}");
        _selfId = id;
    }
}
