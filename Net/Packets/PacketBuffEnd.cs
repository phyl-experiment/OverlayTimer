using System;
using System.Buffers.Binary;

namespace OverlayTimer.Net
{
    // dataType 110056
    public readonly struct PacketBuffEnd : IPacketParser<PacketBuffEnd>
    {
        public ulong UserId { get; }  // offset 0x00
        public ulong InstKey { get; } // offset 0x08
        public uint State { get; }    // offset 0x10 (optional, 0 if absent)

        private PacketBuffEnd(ulong userId, ulong instKey, uint state)
        {
            UserId = userId;
            InstKey = instKey;
            State = state;
        }

        public static bool TryParse(ReadOnlySpan<byte> payload, out PacketBuffEnd packet)
        {
            packet = default;
            // userId(8) + instKey(8) = 16 minimum; state(4) is optional
            if (payload.Length < 16)
                return false;

            ulong userId  = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(0,  8));
            ulong instKey = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(8,  8));
            uint  state   = payload.Length >= 20
                ? BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(16, 4))
                : 0;

            packet = new PacketBuffEnd(userId, instKey, state);
            return true;
        }
    }
}
