using System;

namespace OverlayTimer.Net
{
    /// <summary>
    /// 패킷 파서 표준 규격 인터페이스.
    /// 구현체는 <c>static bool TryParse(ReadOnlySpan&lt;byte&gt; payload, out TPacket packet)</c>를
    /// 제공해야 하며, 파싱 실패 시 false를 반환하고 예외를 던지지 않는다.
    /// </summary>
    public interface IPacketParser<TPacket>
    {
        static abstract bool TryParse(ReadOnlySpan<byte> payload, out TPacket packet);
    }
}
