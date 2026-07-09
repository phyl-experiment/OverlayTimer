using System;

namespace OverlayTimer.Net
{
    // ReadyToEnterWorldB (현재 dataType 110540, 버전에 따라 변경 가능)
    //
    // 35-byte 고정 페이로드. SelfIdResolver 의 EnterWorld 게이팅용으로만 사용됨.
    // 페이로드 자체에서 의미있는 필드를 꺼내 쓰지 않으며, shape 검증으로 dataType
    // 정체성만 확인한다.
    //
    // shape signature (2026-04-25 dump 6+회 모두 일치, 다른 dataType 0건 매칭):
    //   payload.Length == 35
    //   payload[28..35]              = 모두 0x00 (trailing 7 zero)
    //   payload[4,6,8,10,12,14,16,18] = 모두 0x00 (UTF-16 BE high-byte)
    //   payload[5,7,9,11,13,15,17,19] = 모두 printable ASCII (0x20..0x7E)
    public readonly struct PacketReadyToEnterWorldB : IPacketParser<PacketReadyToEnterWorldB>
    {
        public const int FixedPayloadLength = 35;

        public static bool TryParse(ReadOnlySpan<byte> payload, out PacketReadyToEnterWorldB packet)
        {
            packet = default;
            if (payload.Length != FixedPayloadLength)
                return false;

            for (int i = 28; i < FixedPayloadLength; i++)
                if (payload[i] != 0x00)
                    return false;

            for (int i = 4; i < 20; i += 2)
            {
                if (payload[i] != 0x00)
                    return false;

                byte lo = payload[i + 1];
                if (lo < 0x20 || lo > 0x7E)
                    return false;
            }

            packet = new PacketReadyToEnterWorldB();
            return true;
        }
    }
}
