using System;
using System.Buffers.Binary;

namespace OverlayTimer.Net
{
    // dataType 101059 (버전에 따라 다를 수 있음, 예: 101037)
    // len=24, encodeType=0
    // 0x00(8): unknown0  — 세션/계정 관련 추정, 정확한 의미 미상
    // 0x08(8): unknown8  — 의미 미상
    // 0x10(8): selfId    — 내 캐릭터 ID
    public readonly struct PacketEnterWorld : IPacketParser<PacketEnterWorld>
    {
        public ulong Unknown0 { get; } // offset 0x00
        public ulong Unknown8 { get; } // offset 0x08
        public ulong SelfId   { get; } // offset 0x10

        private PacketEnterWorld(ulong unknown0, ulong unknown8, ulong selfId)
        {
            Unknown0 = unknown0;
            Unknown8 = unknown8;
            SelfId   = selfId;
        }

        public static bool TryParse(ReadOnlySpan<byte> payload, out PacketEnterWorld packet)
        {
            packet = default;
            if (payload.Length < 24)
                return false;

            ulong unknown0 = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(0,  8));
            ulong unknown8 = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(8,  8));
            ulong selfId   = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(16, 8));

            if (selfId == 0)
                return false;

            packet = new PacketEnterWorld(unknown0, unknown8, selfId);
            return true;
        }
    }
}
