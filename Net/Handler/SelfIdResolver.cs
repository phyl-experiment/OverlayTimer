using System;
using OverlayTimer.Net;

public sealed class SelfIdResolver
{
    /// <summary>
    /// ReadyToEnterWorldA hardcoded best-effort. payload schema가 다른 dataType(예: 60008)과
    /// 동일해서 probe로 자동 탐색 불가. 게임 업데이트 후 값이 밀려도 ReadyToEnterWorldB가
    /// 살아있으면 OR 게이팅이 동작한다. 자세한 분석은 memory/protocol-probe-plan.md 참고.
    /// </summary>
    private const int ReadyToEnterWorldTypeA = 110539;
    private const int ArmedPacketBudget = 8;

    public ulong SelfId => _selfId;
    public int EnterWorldDataType => _enterWorldType;

    private ulong _selfId;
    private readonly int _enterWorldType;
    private readonly int _readyToEnterWorldTypeB;
    private readonly OverlayTimer.DebugInfo? _debugInfo;
    private bool _enterWorldArmed;
    private int _remainingArmedPackets;

    public SelfIdResolver(int enterWorldType, int readyToEnterWorldTypeB, OverlayTimer.DebugInfo? debugInfo = null)
    {
        _enterWorldType = enterWorldType;
        _readyToEnterWorldTypeB = readyToEnterWorldTypeB;
        _debugInfo = debugInfo;
    }

    public ulong TryFeed(int dataType, ReadOnlySpan<byte> payload)
    {
        // ReadyA는 shape parser가 없는 hardcoded best-effort라 dataType만으로 arm.
        if (dataType == ReadyToEnterWorldTypeA)
        {
            ArmForNextEnterWorld();
            return 0;
        }

        // ReadyB는 shape signature로 검증해 dataType이 다른 패킷에 재할당된 케이스에서
        // false-arm 되지 않도록 한다.
        if (dataType == _readyToEnterWorldTypeB
            && PacketReadyToEnterWorldB.TryParse(payload, out _))
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
