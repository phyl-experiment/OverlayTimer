using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using OverlayTimer;
using OverlayTimer.Net;

namespace OverlayTimer.Tests;

/// <summary>
/// ProtocolProbe 의 마커·패킷 타입 탐색 로직을 검증하는 단위 테스트.
/// 실제 네트워크·WPF 없이 합성 바이트 스트림으로 동작한다.
/// </summary>
public class ProtocolProbeTests
{
    // ------------------------------------------------------------------
    // 테스트용 상수 (현재 프로덕션 값)
    // ------------------------------------------------------------------

    private static readonly byte[] BaseStart = ParseHex("80 4E 00 00 00 00 00 00 00");
    private static readonly byte[] BaseEnd   = ParseHex("12 4F 00 00 00 00 00 00 00");

    private const int CurBuffStart  = 100054;
    private const int CurBuffEnd    = 100055;
    private const int CurEnterWorld = 101059;
    private const int CurDpsAttack  = 20389;
    private const int CurDpsDamage  = 20897;

    // ------------------------------------------------------------------
    // StreamBuilder: 합성 프레임 스트림 생성 헬퍼
    // ------------------------------------------------------------------

    private sealed class StreamBuilder
    {
        private readonly List<byte> _bytes = new();

        public StreamBuilder AddNoise(int count = 32)
        {
            for (int i = 0; i < count; i++)
                _bytes.Add((byte)(i % 251));
            return this;
        }

        public StreamBuilder AddFrame(byte[] startMarker, byte[] endMarker,
            params (int dataType, byte[] payload)[] packets)
        {
            _bytes.AddRange(startMarker);
            foreach (var (dt, payload) in packets)
            {
                // 9-byte header
                var header = new byte[9];
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0, 4), dt);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), payload.Length);
                header[8] = 0; // encodeType
                _bytes.AddRange(header);
                _bytes.AddRange(payload);
            }
            _bytes.AddRange(endMarker);
            return this;
        }

        public byte[] Build() => _bytes.ToArray();
    }

    // ------------------------------------------------------------------
    // Payload builders: 각 패킷 타입의 최소 유효 payload
    // ------------------------------------------------------------------

    private static byte[] MakeBuffStartPayload(
        ulong userId = 12345678UL,
        float durationSeconds = 20.0f)
    {
        var p = new byte[32];
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(0x00, 8), userId);
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(0x08, 8), 999UL);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(0x10, 4), 1590198662u);
        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(0x14, 4),
            BitConverter.SingleToInt32Bits(durationSeconds));
        return p;
    }

    private static byte[] MakeBuffEndPayload(ulong userId = 12345678UL, ulong instKey = 999UL)
    {
        var p = new byte[20];
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(0, 8), userId);
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(8, 8), instKey);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(16, 4), 1u);
        return p;
    }

    private static byte[] MakeEnterWorldPayload(ulong selfId = 99887766UL)
    {
        var p = new byte[24];
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(16, 8), selfId);
        return p;
    }

    private static byte[] MakeDpsAttackPayload(uint userId = 111u, uint targetId = 222u)
    {
        var p = new byte[35];
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(0, 4), userId);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8, 4), targetId);
        return p;
    }

    private static byte[] MakeDpsDamagePayload(uint userId = 111u, uint targetId = 222u, uint damage = 50000u)
    {
        var p = new byte[39];
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(0, 4), userId);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8, 4), targetId);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(16, 4), damage);
        return p;
    }

    private static byte[] ShiftMarker(byte[] original, int delta)
    {
        var result = (byte[])original.Clone();
        ushort val = BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan(0, 2));
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(0, 2), (ushort)(val + delta));
        return result;
    }

    private static ProtocolConfig MakeProtocolConfig(byte[] start, byte[] end) =>
        new()
        {
            StartMarker = BitConverter.ToString(start).Replace("-", " "),
            EndMarker   = BitConverter.ToString(end).Replace("-", " ")
        };

    private static PacketTypesConfig MakeTypesConfig(
        int buffStart  = CurBuffStart,
        int buffEnd    = CurBuffEnd,
        int enterWorld = CurEnterWorld,
        int dpsAttack  = CurDpsAttack,
        int dpsDamage  = CurDpsDamage) =>
        new()
        {
            BuffStart  = buffStart,
            BuffEnd    = buffEnd,
            EnterWorld = enterWorld,
            DpsAttack  = dpsAttack,
            DpsDamage  = dpsDamage
        };

    private static byte[] ParseHex(string hex)
    {
        hex = hex.Replace(" ", "");
        var result = new byte[hex.Length / 2];
        for (int i = 0; i < result.Length; i++)
            result[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return result;
    }

    // ------------------------------------------------------------------
    // GenerateMarkerCandidates 테스트
    // ------------------------------------------------------------------

    [Fact]
    public void GenerateMarkerCandidates_EmptyInput_YieldsNothing()
    {
        var result = new List<byte[]>(ProtocolProbe.GenerateMarkerCandidates(Array.Empty<byte>(), 10));
        Assert.Empty(result);
    }

    [Fact]
    public void GenerateMarkerCandidates_ZeroRadius_YieldsOnlyCurrentValue()
    {
        var result = new List<byte[]>(ProtocolProbe.GenerateMarkerCandidates(BaseStart, 0));
        Assert.Single(result);
        Assert.Equal(BaseStart, result[0]);
    }

    [Fact]
    public void GenerateMarkerCandidates_Radius3_YieldsFourEntries()
    {
        var result = new List<byte[]>(ProtocolProbe.GenerateMarkerCandidates(BaseStart, 3));
        Assert.Equal(4, result.Count); // delta 0,1,2,3
    }

    [Fact]
    public void GenerateMarkerCandidates_IncrementsByOne_CorrectBytes()
    {
        var list = new List<byte[]>(ProtocolProbe.GenerateMarkerCandidates(BaseStart, 2));
        ushort base16 = BinaryPrimitives.ReadUInt16LittleEndian(BaseStart.AsSpan(0, 2));
        for (int i = 0; i < list.Count; i++)
        {
            ushort val = BinaryPrimitives.ReadUInt16LittleEndian(list[i].AsSpan(0, 2));
            Assert.Equal((ushort)(base16 + i), val);
            // 나머지 바이트는 그대로
            for (int j = 2; j < list[i].Length; j++)
                Assert.Equal(BaseStart[j], list[i][j]);
        }
    }

    // ------------------------------------------------------------------
    // ScoreMarkerPair 테스트
    // ------------------------------------------------------------------

    [Fact]
    public void ScoreMarkerPair_EmptyData_ReturnsZero()
    {
        int score = ProtocolProbe.ScoreMarkerPair(Array.Empty<byte>(), BaseStart, BaseEnd);
        Assert.Equal(0, score);
    }

    [Fact]
    public void ScoreMarkerPair_CorrectMarkers_CountsCompleteFrames()
    {
        var data = new StreamBuilder()
            .AddFrame(BaseStart, BaseEnd,
                (CurBuffStart, MakeBuffStartPayload()),
                (CurEnterWorld, MakeEnterWorldPayload()))
            .AddFrame(BaseStart, BaseEnd,
                (CurBuffStart, MakeBuffStartPayload()))
            .Build();

        int score = ProtocolProbe.ScoreMarkerPair(data, BaseStart, BaseEnd);
        Assert.Equal(2, score);
    }

    [Fact]
    public void ScoreMarkerPair_WrongStartMarker_ReturnsZero()
    {
        var data = new StreamBuilder()
            .AddFrame(BaseStart, BaseEnd, (CurBuffStart, MakeBuffStartPayload()))
            .Build();

        var wrongStart = ShiftMarker(BaseStart, 99);
        int score = ProtocolProbe.ScoreMarkerPair(data, wrongStart, BaseEnd);
        Assert.Equal(0, score);
    }

    [Fact]
    public void ScoreMarkerPair_WrongEndMarker_ReturnsZero()
    {
        var data = new StreamBuilder()
            .AddFrame(BaseStart, BaseEnd, (CurBuffStart, MakeBuffStartPayload()))
            .Build();

        var wrongEnd = ShiftMarker(BaseEnd, 99);
        int score = ProtocolProbe.ScoreMarkerPair(data, BaseStart, wrongEnd);
        Assert.Equal(0, score);
    }

    // ------------------------------------------------------------------
    // TryDiscover — 마커 미변경 (변경 없음 → null)
    // ------------------------------------------------------------------

    [Fact]
    public void TryDiscover_NoChange_ReturnsNull()
    {
        var data = new StreamBuilder()
            .AddFrame(BaseStart, BaseEnd,
                (CurBuffStart, MakeBuffStartPayload()),
                (CurEnterWorld, MakeEnterWorldPayload()))
            .AddFrame(BaseStart, BaseEnd,
                (CurBuffStart, MakeBuffStartPayload()))
            .AddFrame(BaseStart, BaseEnd,
                (CurDpsAttack, MakeDpsAttackPayload()),
                (CurDpsDamage, MakeDpsDamagePayload()))
            .Build();

        var result = ProtocolProbe.TryDiscover(data,
            MakeProtocolConfig(BaseStart, BaseEnd),
            MakeTypesConfig());

        Assert.Null(result);
    }

    // ------------------------------------------------------------------
    // TryDiscover — 마커만 변경
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(50)]
    [InlineData(100)]
    public void TryDiscover_MarkerShiftedByDelta_DiscoverNewMarker(int delta)
    {
        var newStart = ShiftMarker(BaseStart, delta);
        var newEnd   = ShiftMarker(BaseEnd,   delta);

        var data = new StreamBuilder()
            .AddNoise()
            .AddFrame(newStart, newEnd, (CurBuffStart, MakeBuffStartPayload()))
            .AddFrame(newStart, newEnd, (CurBuffStart, MakeBuffStartPayload()))
            .AddFrame(newStart, newEnd, (CurEnterWorld, MakeEnterWorldPayload()))
            .AddNoise()
            .Build();

        var result = ProtocolProbe.TryDiscover(data,
            MakeProtocolConfig(BaseStart, BaseEnd),
            MakeTypesConfig(),
            markerRadius: 128,
            typeRadius: 10);

        Assert.NotNull(result);
        Assert.NotNull(result.NewStartMarker);
        Assert.NotNull(result.NewEndMarker);
        Assert.Equal(newStart, result.NewStartMarker);
        Assert.Equal(newEnd,   result.NewEndMarker);
        Assert.True(result.FramesFound >= 2);
    }

    // ------------------------------------------------------------------
    // TryDiscover — radius 부족 → null 반환
    // ------------------------------------------------------------------

    [Fact]
    public void TryDiscover_ShiftExceedsRadius_ReturnsNull()
    {
        int delta    = 200;
        var newStart = ShiftMarker(BaseStart, delta);
        var newEnd   = ShiftMarker(BaseEnd,   delta);

        var data = new StreamBuilder()
            .AddFrame(newStart, newEnd, (CurBuffStart, MakeBuffStartPayload()))
            .AddFrame(newStart, newEnd, (CurBuffStart, MakeBuffStartPayload()))
            .Build();

        var result = ProtocolProbe.TryDiscover(data,
            MakeProtocolConfig(BaseStart, BaseEnd),
            MakeTypesConfig(),
            markerRadius: 100,  // 200 > 100 → 탐색 실패
            typeRadius: 10);

        Assert.Null(result);
    }

    // ------------------------------------------------------------------
    // TryDiscover — 패킷 타입만 변경
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(22)]   // enterWorld 실사례
    [InlineData(3)]    // buffStart 실사례
    [InlineData(156)]  // dpsDamage 실사례
    public void TryDiscover_PacketTypeShifted_DiscoverNewType(int delta)
    {
        int newBuffStart  = CurBuffStart  + delta;
        int newBuffEnd    = CurBuffEnd    + delta;
        int newEnterWorld = CurEnterWorld + delta;
        int newDpsAttack  = CurDpsAttack  + delta;
        int newDpsDamage  = CurDpsDamage  + delta;

        var data = new StreamBuilder()
            .AddFrame(BaseStart, BaseEnd,
                (newBuffStart,  MakeBuffStartPayload()),
                (newBuffEnd,    MakeBuffEndPayload()),
                (newEnterWorld, MakeEnterWorldPayload()),
                (newDpsAttack,  MakeDpsAttackPayload()),
                (newDpsDamage,  MakeDpsDamagePayload()))
            .AddFrame(BaseStart, BaseEnd,
                (newBuffStart,  MakeBuffStartPayload()),
                (newDpsAttack,  MakeDpsAttackPayload()),  // dpsAttack minHits=2
                (newDpsDamage,  MakeDpsDamagePayload()))
            .Build();

        var result = ProtocolProbe.TryDiscover(data,
            MakeProtocolConfig(BaseStart, BaseEnd),
            MakeTypesConfig(),
            markerRadius: 10,
            typeRadius: 300);

        Assert.NotNull(result);
        Assert.Equal(newBuffStart,  result.NewBuffStart);
        Assert.Equal(newBuffEnd,    result.NewBuffEnd);
        Assert.Equal(newEnterWorld, result.NewEnterWorld);
        Assert.Equal(newDpsAttack,  result.NewDpsAttack);
        Assert.Equal(newDpsDamage,  result.NewDpsDamage);
    }

    // ------------------------------------------------------------------
    // TryDiscover — 마커 + 타입 동시 변경
    // ------------------------------------------------------------------

    [Fact]
    public void TryDiscover_MarkerAndTypesBothChanged_DiscoversBoth()
    {
        int markerDelta = 7;
        int typeDelta   = 44;

        var newStart = ShiftMarker(BaseStart, markerDelta);
        var newEnd   = ShiftMarker(BaseEnd,   markerDelta);
        int newBuffStart = CurBuffStart + typeDelta;

        var data = new StreamBuilder()
            .AddNoise()
            .AddFrame(newStart, newEnd,
                (newBuffStart, MakeBuffStartPayload()),
                (CurEnterWorld + typeDelta, MakeEnterWorldPayload()))
            .AddFrame(newStart, newEnd,
                (newBuffStart, MakeBuffStartPayload()))
            .AddNoise()
            .Build();

        var result = ProtocolProbe.TryDiscover(data,
            MakeProtocolConfig(BaseStart, BaseEnd),
            MakeTypesConfig(),
            markerRadius: 128,
            typeRadius: 300);

        Assert.NotNull(result);
        Assert.Equal(newStart,    result.NewStartMarker);
        Assert.Equal(newEnd,      result.NewEndMarker);
        Assert.Equal(newBuffStart, result.NewBuffStart);
    }

    // ------------------------------------------------------------------
    // TryDiscover — 무작위 노이즈 데이터 → false positive 없음
    // ------------------------------------------------------------------

    [Fact]
    public void TryDiscover_RandomNoise_ReturnsNull()
    {
        // 패턴 없는 노이즈 64KB
        var rng  = new Random(42);
        var data = new byte[64 * 1024];
        rng.NextBytes(data);

        var result = ProtocolProbe.TryDiscover(data,
            MakeProtocolConfig(BaseStart, BaseEnd),
            MakeTypesConfig());

        // 노이즈에서 우연히 유효한 프레임 2개가 나올 확률은 극히 낮아야 함
        // (false positive 가 발생하면 FramesFound ≥ 2 조건을 느슨하게 검토할 것)
        Assert.Null(result);
    }

    // ------------------------------------------------------------------
    // TryDiscover — 빈 데이터
    // ------------------------------------------------------------------

    [Fact]
    public void TryDiscover_EmptyData_ReturnsNull()
    {
        var result = ProtocolProbe.TryDiscover(
            Array.Empty<byte>(),
            MakeProtocolConfig(BaseStart, BaseEnd),
            MakeTypesConfig());

        Assert.Null(result);
    }

    // ------------------------------------------------------------------
    // TryDiscover — 프레임 경계 잘림 (partial frame) — 크래시 없음
    // ------------------------------------------------------------------

    [Fact]
    public void TryDiscover_PartialFrame_NoCrash()
    {
        var full = new StreamBuilder()
            .AddFrame(BaseStart, BaseEnd,
                (CurBuffStart, MakeBuffStartPayload()),
                (CurEnterWorld, MakeEnterWorldPayload()))
            .Build();

        // 마지막 20바이트를 잘라낸 불완전한 데이터
        var partial = full[..Math.Max(0, full.Length - 20)];

        var ex = Record.Exception(() =>
            ProtocolProbe.TryDiscover(partial,
                MakeProtocolConfig(BaseStart, BaseEnd),
                MakeTypesConfig()));

        Assert.Null(ex);
    }

    // ------------------------------------------------------------------
    // TryFindBuffStart / TryFindBuffEnd 단독 검증
    // ------------------------------------------------------------------

    [Fact]
    public void TryFindBuffStart_CorrectDelta_ReturnsNewType()
    {
        int newType = CurBuffStart + 8;
        // minHits=2 이므로 동일 타입의 유효 페이로드가 2개 필요
        var packets = new List<(int, byte[])>
        {
            (newType, MakeBuffStartPayload(durationSeconds: 20f)),
            (newType, MakeBuffStartPayload(durationSeconds: 30f))
        };

        int? found = ProtocolProbe.TryFindBuffStart(packets, CurBuffStart, 100);
        Assert.Equal(newType, found);
    }

    [Fact]
    public void TryFindBuffStart_SingleMatchBelowMinHits_ReturnsNull()
    {
        int newType = CurBuffStart + 8;
        // minHits=2 인데 1개만 있으므로 null 반환해야 함
        var packets = new List<(int, byte[])>
        {
            (newType, MakeBuffStartPayload(durationSeconds: 20f))
        };

        int? found = ProtocolProbe.TryFindBuffStart(packets, CurBuffStart, 100);
        Assert.Null(found);
    }

    [Fact]
    public void TryFindBuffStart_InvalidDuration_NotReturned()
    {
        int newType = CurBuffStart + 1;
        var payload = MakeBuffStartPayload(durationSeconds: 999f); // 범위 초과
        var packets = new List<(int, byte[])> { (newType, payload) };

        int? found = ProtocolProbe.TryFindBuffStart(packets, CurBuffStart, 100);
        Assert.Null(found);
    }

    [Fact]
    public void TryFindBuffEnd_UsesBuffStartPlusOneHeuristic()
    {
        int newBuffStart = CurBuffStart + 5;
        int newBuffEnd   = newBuffStart + 1; // 패턴: buffEnd = buffStart+1

        var packets = new List<(int, byte[])>
        {
            (newBuffEnd, MakeBuffEndPayload())
        };

        int? found = ProtocolProbe.TryFindBuffEnd(packets, CurBuffEnd, newBuffStart, 200);
        Assert.Equal(newBuffEnd, found);
    }

    // ------------------------------------------------------------------
    // TryFindDpsDamage — 손상 값 경계 검증
    // ------------------------------------------------------------------

    [Fact]
    public void TryFindDpsDamage_DamageAtMax_AcceptedAsValid()
    {
        int newType = CurDpsDamage + 10;
        var packets = new List<(int, byte[])>
        {
            (newType, MakeDpsDamagePayload(damage: 2_095_071_572u)),
            (newType, MakeDpsDamagePayload(damage: 2_095_071_572u))
        };

        int? found = ProtocolProbe.TryFindDpsDamage(packets, CurDpsDamage, 100);
        Assert.Equal(newType, found);
    }

    [Fact]
    public void TryFindDpsDamage_SingleMatchBelowMinHits_ReturnsNull()
    {
        int newType = CurDpsDamage + 10;
        var packets = new List<(int, byte[])>
        {
            (newType, MakeDpsDamagePayload(damage: 50000u))
        };

        int? found = ProtocolProbe.TryFindDpsDamage(packets, CurDpsDamage, 100);
        Assert.Null(found);
    }

    [Fact]
    public void TryFindDpsDamage_DamageExceedsMax_Rejected()
    {
        int newType = CurDpsDamage + 10;
        var packets = new List<(int, byte[])>
        {
            (newType, MakeDpsDamagePayload(damage: 2_095_071_573u)) // 초과
        };

        int? found = ProtocolProbe.TryFindDpsDamage(packets, CurDpsDamage, 100);
        Assert.Null(found);
    }

    // ------------------------------------------------------------------
    // TryFindDpsAttack — 정확히 35바이트만 허용
    // ------------------------------------------------------------------

    [Fact]
    public void TryFindDpsAttack_WrongPayloadSize_Rejected()
    {
        int newType = CurDpsAttack + 1;
        var packets = new List<(int, byte[])>
        {
            (newType, new byte[34]) // 35가 아닌 34
        };

        int? found = ProtocolProbe.TryFindDpsAttack(packets, CurDpsAttack, 100);
        Assert.Null(found);
    }

    [Fact]
    public void TryFindDpsAttack_SingleMatchBelowMinHits_ReturnsNull()
    {
        int newType = CurDpsAttack + 1;
        var packets = new List<(int, byte[])>
        {
            (newType, MakeDpsAttackPayload())
        };

        int? found = ProtocolProbe.TryFindDpsAttack(packets, CurDpsAttack, 100);
        Assert.Null(found);
    }

    [Fact]
    public void TryFindDpsAttack_TwoMatches_ReturnsNewType()
    {
        int newType = CurDpsAttack + 1;
        var packets = new List<(int, byte[])>
        {
            (newType, MakeDpsAttackPayload()),
            (newType, MakeDpsAttackPayload(userId: 333u, targetId: 444u))
        };

        int? found = ProtocolProbe.TryFindDpsAttack(packets, CurDpsAttack, 100);
        Assert.Equal(newType, found);
    }

    // ------------------------------------------------------------------
    // buffStart+1 패턴: buffEnd 가 항상 buffStart+1 임을 확인
    // ------------------------------------------------------------------

    [Fact]
    public void TryDiscover_BuffEndIsAlwaysBuffStartPlusOne()
    {
        int delta    = 10;
        int newStart = CurBuffStart + delta;
        int newEnd   = newStart + 1; // buffEnd = buffStart+1

        var data = new StreamBuilder()
            .AddFrame(BaseStart, BaseEnd,
                (newStart, MakeBuffStartPayload()),
                (newEnd,   MakeBuffEndPayload()))
            .AddFrame(BaseStart, BaseEnd,
                (newStart, MakeBuffStartPayload()),
                (newEnd,   MakeBuffEndPayload()))
            .Build();

        var result = ProtocolProbe.TryDiscover(data,
            MakeProtocolConfig(BaseStart, BaseEnd),
            MakeTypesConfig(),
            markerRadius: 5,
            typeRadius: 50);

        Assert.NotNull(result);
        Assert.Equal(newStart, result.NewBuffStart);
        Assert.Equal(newEnd,   result.NewBuffEnd);
    }

    // ------------------------------------------------------------------
    // ExtractPackets 기본 동작 확인
    // ------------------------------------------------------------------

    [Fact]
    public void ExtractPackets_CorrectMarkers_ReturnsAllPackets()
    {
        var data = new StreamBuilder()
            .AddFrame(BaseStart, BaseEnd,
                (CurBuffStart,  MakeBuffStartPayload()),
                (CurEnterWorld, MakeEnterWorldPayload()))
            .AddFrame(BaseStart, BaseEnd,
                (CurDpsAttack,  MakeDpsAttackPayload()))
            .Build();

        var packets = ProtocolProbe.ExtractPackets(data, BaseStart, BaseEnd);

        Assert.Equal(3, packets.Count);
        Assert.Equal(CurBuffStart,  packets[0].dataType);
        Assert.Equal(CurEnterWorld, packets[1].dataType);
        Assert.Equal(CurDpsAttack,  packets[2].dataType);
    }

    [Fact]
    public void ExtractPackets_WrongMarker_ReturnsEmpty()
    {
        var data = new StreamBuilder()
            .AddFrame(BaseStart, BaseEnd, (CurBuffStart, MakeBuffStartPayload()))
            .Build();

        var wrong = ShiftMarker(BaseStart, 50);
        var packets = ProtocolProbe.ExtractPackets(data, wrong, BaseEnd);
        Assert.Empty(packets);
    }
}
