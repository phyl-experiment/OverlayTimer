using System;
using System.Buffers.Binary;

namespace OverlayTimer.Net
{
    public readonly struct PacketDamage20897
    {
        public ulong UserId { get; }
        public ulong TargetId { get; }
        public long Damage { get; }
        public byte[] Flags { get; }

        public PacketDamage20897(ulong userId, ulong targetId, long damage, byte[] flags)
        {
            UserId = userId;
            TargetId = targetId;
            Damage = damage;
            Flags = flags;
        }

        public static bool TryParse(ReadOnlySpan<byte> payload, out PacketDamage20897 parsed)
        {
            parsed = default;

            // m-inbody self-damage shape + observed extension tail
            if (payload.Length < 39)
                return false;

            ulong userId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(0, 4));
            ulong targetId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(8, 4));
            long damage = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(16, 4));
            byte[] flags = payload.Slice(32, 7).ToArray();

            if (userId == 0 || targetId == 0 || damage <= 0)
                return false;

            parsed = new PacketDamage20897(userId, targetId, damage, flags);
            return true;
        }
    }
}
