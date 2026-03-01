using System;
using System.Buffers.Binary;

namespace OverlayTimer.Net
{
    // dataType 100054
    public readonly struct PacketBuffStart : IPacketParser<PacketBuffStart>
    {
        public ulong UserId { get; }          // offset 0x00
        public ulong InstKey { get; }         // offset 0x08
        public uint BuffKey { get; }          // offset 0x10
        public float DurationSeconds { get; } // offset 0x14
        public int FlagA { get; }             // offset 0x18
        public int FlagB { get; }             // offset 0x1C

        private PacketBuffStart(ulong userId, ulong instKey, uint buffKey, float durationSeconds, int flagA, int flagB)
        {
            UserId = userId;
            InstKey = instKey;
            BuffKey = buffKey;
            DurationSeconds = durationSeconds;
            FlagA = flagA;
            FlagB = flagB;
        }

        public static bool TryParse(ReadOnlySpan<byte> payload, out PacketBuffStart packet)
        {
            packet = default;
            // userId(8) + instKey(8) + buffKey(4) + float(4) + flagA(4) + flagB(4) = 32
            if (payload.Length < 32)
                return false;

            ulong userId        = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(0,  8));
            ulong instKey       = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(8,  8));
            uint  buffKey       = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(16, 4));
            float durationSecs  = BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(20, 4));
            int   flagA         = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(24, 4));
            int   flagB         = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(28, 4));

            packet = new PacketBuffStart(userId, instKey, buffKey, durationSecs, flagA, flagB);
            return true;
        }
    }
}
