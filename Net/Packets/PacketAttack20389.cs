using System;
using System.Buffers.Binary;

namespace OverlayTimer.Net
{
    public readonly struct PacketAttack20389 : IPacketParser<PacketAttack20389>
    {
        public ulong UserId { get; }
        public ulong TargetId { get; }
        public uint Key1 { get; }
        public uint Key2 { get; }
        public byte[] Flags { get; }

        public PacketAttack20389(ulong userId, ulong targetId, uint key1, uint key2, byte[] flags)
        {
            UserId = userId;
            TargetId = targetId;
            Key1 = key1;
            Key2 = key2;
            Flags = flags;
        }

        public static bool TryParse(ReadOnlySpan<byte> payload, out PacketAttack20389 parsed)
        {
            parsed = default;

            if (payload.Length != 35)
                return false;

            ulong userId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(0, 4));
            ulong targetId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(8, 4));
            uint key1 = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(16, 4));
            uint key2 = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(20, 4));
            byte[] flags = payload.Slice(24, 7).ToArray();

            if (userId == 0 || targetId == 0)
                return false;

            parsed = new PacketAttack20389(userId, targetId, key1, key2, flags);
            return true;
        }
    }
}
