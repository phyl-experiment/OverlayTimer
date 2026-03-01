using System;
using System.Buffers.Binary;

namespace OverlayTimer.Net
{
    // dataType 100055
    public readonly struct PacketBuffEnd : IPacketParser<PacketBuffEnd>
    {
        public ulong UserId { get; }  // offset 0x00
        public ulong InstKey { get; } // offset 0x08
        public uint State { get; }    // offset 0x10

        private PacketBuffEnd(ulong userId, ulong instKey, uint state)
        {
            UserId = userId;
            InstKey = instKey;
            State = state;
        }

        public static bool TryParse(ReadOnlySpan<byte> payload, out PacketBuffEnd packet)
        {
            packet = default;
            // userId(8) + instKey(8) + state(4) = 20
            if (payload.Length < 20)
                return false;

            ulong userId  = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(0,  8));
            ulong instKey = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(8,  8));
            uint  state   = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(16, 4));

            packet = new PacketBuffEnd(userId, instKey, state);
            return true;
        }
    }
}
