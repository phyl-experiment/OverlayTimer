using OverlayTimer.Net;

namespace OverlayTimer.Tests;

public class PacketReadyToEnterWorldBTests
{
    // 2026-04-25 PacketAnalyzer dump 의 6+ 인스턴스 모두 byte-for-byte 동일.
    private static readonly byte[] DumpSamplePayload = ParseHex(
        "001000000046004B003700370041005000500050009830A82500000000000000000000");

    [Fact]
    public void TryParse_DumpSample_Succeeds()
    {
        Assert.True(PacketReadyToEnterWorldB.TryParse(DumpSamplePayload, out _));
    }

    [Theory]
    [InlineData(34)]
    [InlineData(36)]
    [InlineData(0)]
    [InlineData(100)]
    public void TryParse_WrongLength_Fails(int length)
    {
        var payload = new byte[length];
        Assert.False(PacketReadyToEnterWorldB.TryParse(payload, out _));
    }

    [Fact]
    public void TryParse_NonZeroTrailingByte_Fails()
    {
        var payload = (byte[])DumpSamplePayload.Clone();
        payload[34] = 0x01;
        Assert.False(PacketReadyToEnterWorldB.TryParse(payload, out _));
    }

    [Fact]
    public void TryParse_NonZeroEvenOffset_Fails()
    {
        // offset 6은 UTF-16 BE high-byte 위치 (always 0x00).
        var payload = (byte[])DumpSamplePayload.Clone();
        payload[6] = 0x01;
        Assert.False(PacketReadyToEnterWorldB.TryParse(payload, out _));
    }

    [Fact]
    public void TryParse_NonPrintableLowByte_Fails()
    {
        // offset 5는 UTF-16 BE 의 첫 글자 ASCII (printable required).
        var payload = (byte[])DumpSamplePayload.Clone();
        payload[5] = 0x01; // control char
        Assert.False(PacketReadyToEnterWorldB.TryParse(payload, out _));

        payload = (byte[])DumpSamplePayload.Clone();
        payload[5] = 0x7F; // DEL
        Assert.False(PacketReadyToEnterWorldB.TryParse(payload, out _));
    }

    [Fact]
    public void TryParse_RandomBytes_Fails()
    {
        var rng = new Random(1234);
        for (int trial = 0; trial < 100; trial++)
        {
            var payload = new byte[35];
            rng.NextBytes(payload);
            Assert.False(PacketReadyToEnterWorldB.TryParse(payload, out _));
        }
    }

    [Fact]
    public void TryParse_AllZero35Bytes_Fails()
    {
        // 35-byte all-zero: trailing zeros 만족하나 ASCII printable 검증에서 실패해야 한다.
        var payload = new byte[35];
        Assert.False(PacketReadyToEnterWorldB.TryParse(payload, out _));
    }

    private static byte[] ParseHex(string hex)
    {
        hex = hex.Replace(" ", "");
        var result = new byte[hex.Length / 2];
        for (int i = 0; i < result.Length; i++)
            result[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return result;
    }
}
