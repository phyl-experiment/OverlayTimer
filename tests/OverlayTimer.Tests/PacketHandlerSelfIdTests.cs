using System;
using System.Buffers.Binary;
using OverlayTimer;
using OverlayTimer.Net;

namespace OverlayTimer.Tests;

public class PacketHandlerSelfIdTests
{
    private const int ReadyToEnterWorldTypeA = 110539;
    private const int ReadyToEnterWorldTypeB = 110540;
    private const int BuffStartType = 100055;
    private const int BuffEndType = 100056;
    private const int EnterWorldType = 101059;
    private const int DpsAttackType = 20389;
    private const int DpsDamageType = 20897;
    private const uint TestBuffKey = 1590198662u;

    [Fact]
    public void EnterWorld_IgnoredUntilReadyPacketSeen()
    {
        var resolver = new SelfIdResolver(EnterWorldType);

        Assert.Equal(0UL, resolver.TryFeed(EnterWorldType, MakeEnterWorldPayload(1111UL)));
        Assert.Equal(0UL, resolver.SelfId);

        resolver.TryFeed(ReadyToEnterWorldTypeA, Array.Empty<byte>());

        Assert.Equal(1111UL, resolver.TryFeed(EnterWorldType, MakeEnterWorldPayload(1111UL)));
        Assert.Equal(1111UL, resolver.SelfId);
    }

    [Theory]
    [InlineData(ReadyToEnterWorldTypeA)]
    [InlineData(ReadyToEnterWorldTypeB)]
    public void EnterWorld_ResolvedAfterEitherReadyPacket(int readyType)
    {
        var resolver = new SelfIdResolver(EnterWorldType);

        resolver.TryFeed(readyType, Array.Empty<byte>());

        Assert.Equal(2222UL, resolver.TryFeed(EnterWorldType, MakeEnterWorldPayload(2222UL)));
        Assert.Equal(2222UL, resolver.SelfId);
    }

    [Fact]
    public void EnterWorld_ConsumesOnlyFirstPacketAfterReady()
    {
        var resolver = new SelfIdResolver(EnterWorldType);

        resolver.TryFeed(ReadyToEnterWorldTypeA, Array.Empty<byte>());

        Assert.Equal(3333UL, resolver.TryFeed(EnterWorldType, MakeEnterWorldPayload(3333UL)));
        Assert.Equal(0UL, resolver.TryFeed(EnterWorldType, MakeEnterWorldPayload(4444UL)));
        Assert.Equal(3333UL, resolver.SelfId);
    }

    [Fact]
    public void ReadyPacket_ExpiresAfterShortWindow()
    {
        var resolver = new SelfIdResolver(EnterWorldType);

        resolver.TryFeed(ReadyToEnterWorldTypeB, Array.Empty<byte>());
        for (int i = 0; i < 8; i++)
            resolver.TryFeed(900000 + i, Array.Empty<byte>());

        Assert.Equal(0UL, resolver.TryFeed(EnterWorldType, MakeEnterWorldPayload(5555UL)));
        Assert.Equal(0UL, resolver.SelfId);
    }

    [Fact]
    public void EnterWorld_DebugInfoMarksConfirmedRecords()
    {
        var debugInfo = new DebugInfo();
        var resolver = new SelfIdResolver(EnterWorldType, debugInfo);

        Assert.Equal(0UL, resolver.TryFeed(EnterWorldType, MakeEnterWorldPayload(1111UL)));

        resolver.TryFeed(ReadyToEnterWorldTypeA, Array.Empty<byte>());
        Assert.Equal(2222UL, resolver.TryFeed(EnterWorldType, MakeEnterWorldPayload(2222UL)));

        var snapshot = debugInfo.GetSnapshot();
        Assert.Equal(2, snapshot.EnterWorldRecords.Count);
        Assert.False(snapshot.EnterWorldRecords[0].Confirmed);
        Assert.Equal(1111UL, snapshot.EnterWorldRecords[0].PlayerId);
        Assert.True(snapshot.EnterWorldRecords[1].Confirmed);
        Assert.Equal(2222UL, snapshot.EnterWorldRecords[1].PlayerId);
    }

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

        handler.OnPacket(ReadyToEnterWorldTypeA, Array.Empty<byte>());
        handler.OnPacket(EnterWorldType, MakeEnterWorldPayload(1111UL));
        Assert.Equal(1, trigger.Count);

        handler.OnPacket(BuffStartType, payload);
        Assert.Equal(2, trigger.Count);
    }

    [Fact]
    public void AwakenBuff_ResolvedViaDamage_ActivatesPendingTimer()
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
            dpsDamageType: DpsDamageType,
            allowInitialDamageFallback: true);

        byte[] flags = [0x01, 0x02, 0x03, 0x08, 0x00, 0x00, 0x00];

        handler.OnPacket(BuffStartType, MakeBuffStartPayload(userId: 2222UL, buffKey: TestBuffKey, instKey: 5555UL, durationSeconds: 30f));
        Assert.Equal(0, trigger.Count);
        Assert.Equal(0UL, resolver.SelfId);

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
            dpsDamageType: DpsDamageType,
            allowInitialDamageFallback: true);

        byte[] flags = [0x01, 0x02, 0x03, 0x08, 0x00, 0x00, 0x00];

        handler.OnPacket(DpsDamageType, MakeDpsDamagePayload(userId: 2222u, targetId: 3333u, damage: 50000u, flags: flags));
        handler.OnPacket(DpsAttackType, MakeDpsAttackPayload(userId: 2222u, targetId: 3333u, key1: 77u, key2: 88u, flags: flags));

        Assert.Equal(50000, dpsTracker.GetSnapshot().TotalDamage);

        handler.OnPacket(ReadyToEnterWorldTypeB, Array.Empty<byte>());
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
        var payload = new byte[18];
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(0, 8), selfId);
        payload[16] = 0x01;
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
