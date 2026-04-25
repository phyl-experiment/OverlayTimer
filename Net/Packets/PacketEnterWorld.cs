using System;
using System.Buffers.Binary;

namespace OverlayTimer.Net
{
    // dataType 110022 (버전에 따라 다를 수 있음)
    // 현재 포맷: len=18, encodeType=0
    // 0x00(8): selfId    — 내 캐릭터 ID (인스턴스마다 변경)
    // 0x08(8): padding   — 0
    // 0x10(2): flag      — 항상 0x0100 관찰
    public readonly struct PacketEnterWorld : IPacketParser<PacketEnterWorld>
    {
        public ulong SelfId { get; } // offset 0x00

        private PacketEnterWorld(ulong selfId)
        {
            SelfId = selfId;
        }

        public static bool TryParse(ReadOnlySpan<byte> payload, out PacketEnterWorld packet)
        {
            packet = default;
            if (payload.Length < 16)
                return false;

            ulong selfId = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(0, 8));

            if (selfId == 0)
                return false;

            packet = new PacketEnterWorld(selfId);
            return true;
        }
    }
}
