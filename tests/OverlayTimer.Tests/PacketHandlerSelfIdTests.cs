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

        handler.OnPacket(BuffStartType, payload);
        Assert.Equal(0, trigger.Count);

        handler.OnPacket(EnterWorldType, MakeEnterWorldPayload(1111UL));
        handler.OnPacket(BuffStartType, payload);

        Assert.Equal(1, trigger.Count);
    }

    [Fact]
    public void Dps_IgnoredUntilSelfIdResolved()
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

        Assert.Equal(0, dpsTracker.GetSnapshot().TotalDamage);

        handler.OnPacket(EnterWorldType, MakeEnterWorldPayload(2222UL));
        handler.OnPacket(DpsDamageType, MakeDpsDamagePayload(userId: 2222u, targetId: 3333u, damage: 50000u, flags: flags));
        handler.OnPacket(DpsAttackType, MakeDpsAttackPayload(userId: 2222u, targetId: 3333u, key1: 77u, key2: 88u, flags: flags));

        Assert.Equal(50000, dpsTracker.GetSnapshot().TotalDamage);
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
