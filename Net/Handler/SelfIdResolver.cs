using System;
using OverlayTimer.Net;

public sealed class SelfIdResolver
{
    private const int ReadyToEnterWorldTypeA = 110539;
    private const int ReadyToEnterWorldTypeB = 110540;
    private const int ArmedPacketBudget = 8;

    public ulong SelfId => _selfId;
    public int EnterWorldDataType => _enterWorldType;

    private ulong _selfId;
    private readonly int _enterWorldType;
    private readonly OverlayTimer.DebugInfo? _debugInfo;
    private bool _enterWorldArmed;
    private int _remainingArmedPackets;

    public SelfIdResolver(int enterWorldType, OverlayTimer.DebugInfo? debugInfo = null)
    {
        _enterWorldType = enterWorldType;
        _debugInfo = debugInfo;
    }

    public ulong TryFeed(int dataType, ReadOnlySpan<byte> payload)
    {
        if (dataType == ReadyToEnterWorldTypeA || dataType == ReadyToEnterWorldTypeB)
        {
            ArmForNextEnterWorld();
            return 0;
        }

        if (dataType == _enterWorldType)
        {
            if (!PacketEnterWorld.TryParse(payload, out var pkt))
            {
                if (_enterWorldArmed)
                    DisarmReadyEnterWorld();
                return 0;
            }

            _debugInfo?.AddEnterWorldRecord(pkt.SelfId, confirmed: _enterWorldArmed);
            if (!_enterWorldArmed)
                return 0;

            ulong resolved = TryResolveEnterWorld(pkt);
            DisarmReadyEnterWorld();
            return resolved;
        }

        if (_enterWorldArmed)
        {
            _remainingArmedPackets--;
            if (_remainingArmedPackets <= 0)
                DisarmReadyEnterWorld();
        }

        return 0;
    }

    /// <summary>EnterWorld 없이 데미지 패킷 등에서 self ID를 추론한 경우 강제 설정.</summary>
    public void ForceSetId(ulong id)
    {
        if (id == 0) return;

        DisarmReadyEnterWorld();
        LogHelper.Write($"SelfId set (damage fallback) {id}");
        _selfId = id;
    }

    private ulong TryResolveEnterWorld(PacketEnterWorld pkt)
    {
        LogHelper.Write($"SelfId set {pkt.SelfId}");
        _selfId = pkt.SelfId;
        _debugInfo?.SetSelfId(pkt.SelfId, "EnterWorld");
        return pkt.SelfId;
    }

    private void ArmForNextEnterWorld()
    {
        _enterWorldArmed = true;
        _remainingArmedPackets = ArmedPacketBudget;
    }

    private void DisarmReadyEnterWorld()
    {
        _enterWorldArmed = false;
        _remainingArmedPackets = 0;
    }
}
