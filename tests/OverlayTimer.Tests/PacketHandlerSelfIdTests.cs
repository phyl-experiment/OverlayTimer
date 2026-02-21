using System;
using System.Buffers.Binary;
using OverlayTimer.Net;

namespace OverlayTimer.Tests;

public class PacketHandlerSelfIdTests
{
    private const int BuffStartType = 100054;
    private const int BuffEndType = 100055;
    private const int EnterWorldType = 101059;
    private const int DpsAttackType = 20389;
    private const int DpsDamageType = 20897;
    private const uint TestBuffKey = 1590198662u;

    [Fact]
    public void BuffStart_IgnoredUntilSelfIdResolved()
    {
        // 각성 버프 패킷은 selfId 미확정 시 즉시 발화하지 않고 임시 보관됨.
        // selfId 확정(EnterWorld) 시 잔여 시간으로 소급 발화(1회),
        // 이후 같은 버프 패킷 재수신 시 다시 발화(1회) → 총 2회.
        var trigger = new CountingTrigger();
        var resolver = new SelfIdResolver(EnterWorldType);
        var handler = new PacketHandler(
            trigger,
            resolver,
            BuffStartType,
            BuffEndType,
            [TestBuffKey],
            dpsTracker: null,
            buffUptimeTracker: null,
            dpsAttackType: DpsAttackType,
            dpsDamageType: DpsDamageType);

        var payload = MakeBuffStartPayload(userId: 1111UL, buffKey: TestBuffKey, instKey: 9001UL);

        // selfId 미확정 → 즉시 발화 없음
        handler.OnPacket(BuffStartType, payload);
        Assert.Equal(0, trigger.Count);

        // EnterWorld 도착 → 임시 보관된 각성 버프 소급 발화
        handler.OnPacket(EnterWorldType, MakeEnterWorldPayload(1111UL));
        Assert.Equal(1, trigger.Count);

        // selfId 확정 후 버프 패킷 재수신 → 정상 발화
        handler.OnPacket(BuffStartType, payload);
        Assert.Equal(2, trigger.Count);
    }

    [Fact]
    public void AwakenBuff_ResolvedViaDamage_ActivatesPendingTimer()
    {
        // EnterWorld 없이 데미지 패킷 N회(임계치)로 selfId 확정 시,
        // 이전에 수신한 각성 버프 타이머가 잔여 시간으로 활성화되는지 검증.
        var trigger = new CountingTrigger();
        var resolver = new SelfIdResolver(EnterWorldType);
        var dpsTracker = new DpsTracker();
        var handler = new PacketHandler(
            trigger,
            resolver,
            BuffStartType,
            BuffEndType,
            [TestBuffKey],
            dpsTracker: dpsTracker,
            buffUptimeTracker: null,
            dpsAttackType: DpsAttackType,
            dpsDamageType: DpsDamageType);

        byte[] flags = [0x01, 0x02, 0x03, 0x08, 0x00, 0x00, 0x00];

        // selfId 미확정 상태에서 각성 버프 수신 → 임시 보관
        handler.OnPacket(BuffStartType, MakeBuffStartPayload(userId: 2222UL, buffKey: TestBuffKey, instKey: 5555UL, durationSeconds: 30f));
        Assert.Equal(0, trigger.Count);
        Assert.Equal(0UL, resolver.SelfId);

        // 유효 데미지 1회 → selfId 즉시 확정 + 각성 타이머 소급 발화
        handler.OnPacket(DpsDamageType, MakeDpsDamagePayload(userId: 2222u, targetId: 3333u, damage: 50000u, flags: flags));
        Assert.Equal(2222UL, resolver.SelfId);
        Assert.Equal(1, trigger.Count);
    }

    [Fact]
    public void Dps_AllowedBeforeSelfIdResolved()
    {
        var trigger = new CountingTrigger();
        var resolver = new SelfIdResolver(EnterWorldType);
        var dpsTracker = new DpsTracker();
        var handler = new PacketHandler(
            trigger,
            resolver,
            BuffStartType,
            BuffEndType,
            [TestBuffKey],
            dpsTracker: dpsTracker,
            buffUptimeTracker: null,
            dpsAttackType: DpsAttackType,
            dpsDamageType: DpsDamageType);

        byte[] flags = [0x01, 0x02, 0x03, 0x08, 0x00, 0x00, 0x00];

        handler.OnPacket(DpsDamageType, MakeDpsDamagePayload(userId: 2222u, targetId: 3333u, damage: 50000u, flags: flags));
        handler.OnPacket(DpsAttackType, MakeDpsAttackPayload(userId: 2222u, targetId: 3333u, key1: 77u, key2: 88u, flags: flags));

        Assert.Equal(50000, dpsTracker.GetSnapshot().TotalDamage);

        handler.OnPacket(EnterWorldType, MakeEnterWorldPayload(2222UL));
        handler.OnPacket(DpsDamageType, MakeDpsDamagePayload(userId: 2222u, targetId: 3333u, damage: 50000u, flags: flags));
        handler.OnPacket(DpsAttackType, MakeDpsAttackPayload(userId: 2222u, targetId: 3333u, key1: 77u, key2: 88u, flags: flags));

        Assert.Equal(100000, dpsTracker.GetSnapshot().TotalDamage);
    }

    private sealed class CountingTrigger : ITimerTrigger
    {
        public int Count { get; private set; }

        public bool On(TimerTriggerRequest request)
        {
            Count++;
            return true;
        }
    }

    private static byte[] MakeEnterWorldPayload(ulong selfId)
    {
        var payload = new byte[24];
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(16, 8), selfId);
        return payload;
    }

    private static byte[] MakeBuffStartPayload(ulong userId, uint buffKey, ulong instKey, float durationSeconds = 20.0f)
    {
        var payload = new byte[32];
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(0, 8), userId);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(8, 8), instKey);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16, 4), buffKey);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(20, 4), BitConverter.SingleToInt32Bits(durationSeconds));
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(24, 4), 0);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(28, 4), 0);
        return payload;
    }

    private static byte[] MakeDpsDamagePayload(uint userId, uint targetId, uint damage, byte[] flags)
    {
        var payload = new byte[39];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), userId);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8, 4), targetId);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16, 4), damage);
        flags.AsSpan(0, Math.Min(flags.Length, 7)).CopyTo(payload.AsSpan(32, 7));
        return payload;
    }

    private static byte[] MakeDpsAttackPayload(uint userId, uint targetId, uint key1, uint key2, byte[] flags)
    {
        var payload = new byte[35];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), userId);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8, 4), targetId);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16, 4), key1);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(20, 4), key2);
        flags.AsSpan(0, Math.Min(flags.Length, 7)).CopyTo(payload.AsSpan(24, 7));
        return payload;
    }
}
