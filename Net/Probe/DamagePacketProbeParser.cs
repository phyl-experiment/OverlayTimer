using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace OverlayTimer.Net
{
    /// <summary>
    /// Logs-side probing parser that attempts known damage packet shapes on candidate dataTypes.
    /// Parse failures are intentionally suppressed.
    /// </summary>
    public sealed class DamagePacketProbeParser
    {
        // DAMAGE-only 후보(관찰 로그 기준) + 기존 후보 확장
        private static readonly HashSet<int> CandidateTypes = new()
        {
            20101, 20302, 20389, 20411, 20503, 20568, 20897, 20947,
            100049, 100050, 100051, 100054, 100092, 100109, 100128,
            100173, 100174, 100192, 100193, 100195, 100197, 100201,
            100308, 100340, 100389, 100485, 100489, 100592, 100636,
            100716, 100722, 100723, 100727, 100894, 100999, 101006,
            101007, 101087,
        };

        // https://github.com/gamepoor-owl/m-inbody/blob/main/src/main/kotlin/com/gamboo/minbody/constants/DamageFlags.kt
        private static readonly (int Index, string Name, byte Mask)[] FlagDefs =
        {
            (0, "crit_flag", 0x01),
            (0, "unguarded_flag", 0x04),
            (0, "break_flag", 0x08),
            (0, "first_hit_flag", 0x40),
            (0, "default_attack_flag", 0x80),
            (1, "multi_attack_flag", 0x01),
            (1, "power_flag", 0x02),
            (1, "fast_flag", 0x04),
            (1, "dot_flag", 0x08),
            (1, "dot_flag2", 0x80),
            (2, "dot_flag3", 0x01),
            (3, "add_hit_flag", 0x08),
            (3, "bleed_flag", 0x10),
            (3, "dark_flag", 0x20),
            (3, "fire_flag", 0x40),
            (3, "holy_flag", 0x80),
            (4, "ice_flag", 0x01),
            (4, "electric_flag", 0x02),
            (4, "poison_flag", 0x04),
            (4, "mind_flag", 0x08),
            (4, "dot_flag4", 0x10),
        };

        public bool IsCandidate(int dataType) => CandidateTypes.Contains(dataType);

        public bool TryParseKnownDamageShape(int dataType, ReadOnlySpan<byte> payload, out string parsed)
        {
            parsed = string.Empty;

            if (!IsCandidate(dataType))
                return false;

            try
            {
                if (TryParseSelfDamage(payload, out var selfDamage))
                {
                    parsed =
                        $"as SELF_DAMAGE user={selfDamage.UserId} target={selfDamage.TargetId} damage={selfDamage.Damage} " +
                        $"flags=[{FormatFlags(selfDamage.Flags)}]";
                    return true;
                }

                if (TryParseAttack(payload, out var attack))
                {
                    parsed =
                        $"as ATTACK user={attack.UserId} target={attack.TargetId} key1={attack.Key1} key2={attack.Key2} " +
                        $"flags=[{FormatFlags(attack.Flags)}]";
                    return true;
                }

                if (TryParseHpChanged(payload, out var hpChanged))
                {
                    long delta = (long)hpChanged.PrevHp - hpChanged.CurrentHp;
                    parsed = $"as HP_CHANGED target={hpChanged.TargetId} prev={hpChanged.PrevHp} current={hpChanged.CurrentHp} delta={delta}";
                    return true;
                }

                if (TryParseCurrentHp(payload, out var currentHp))
                {
                    parsed = $"as CURRENT_HP target={currentHp.TargetId} current={currentHp.CurrentHp}";
                    return true;
                }
            }
            catch
            {
                // probing parse failures must be ignored
            }

            return false;
        }

        // m-inbody parseSelfDamage layout (strict length 39)
        private static bool TryParseSelfDamage(ReadOnlySpan<byte> payload, out SelfDamageParsed parsed)
        {
            parsed = default;
            if (payload.Length != 39)
                return false;

            uint userId = ReadU32(payload, 0);
            uint targetId = ReadU32(payload, 8);
            uint damage = ReadU32(payload, 16);

            if (userId == 0 || targetId == 0 || damage == 0 || damage > 2_095_071_572)
                return false;

            parsed = new SelfDamageParsed(userId, targetId, damage, payload.Slice(32, 7).ToArray());
            return true;
        }

        // m-inbody parseAttack layout (strict length 35)
        private static bool TryParseAttack(ReadOnlySpan<byte> payload, out AttackParsed parsed)
        {
            parsed = default;
            if (payload.Length != 35)
                return false;

            uint userId = ReadU32(payload, 0);
            uint targetId = ReadU32(payload, 8);
            uint key1 = ReadU32(payload, 16);
            uint key2 = ReadU32(payload, 20);

            if (userId == 0 || targetId == 0)
                return false;

            parsed = new AttackParsed(userId, targetId, key1, key2, payload.Slice(24, 7).ToArray());
            return true;
        }

        // m-inbody parseHpChanged layout (strict length 20)
        private static bool TryParseHpChanged(ReadOnlySpan<byte> payload, out HpChangedParsed parsed)
        {
            parsed = default;
            if (payload.Length != 20)
                return false;

            uint targetId = ReadU32(payload, 0);
            uint prevHp = ReadU32(payload, 8);
            uint currentHp = ReadU32(payload, 16);

            if (targetId == 0 || prevHp == 0)
                return false;

            parsed = new HpChangedParsed(targetId, prevHp, currentHp);
            return true;
        }

        // m-inbody parseCurrentHp layout (strict length 20)
        private static bool TryParseCurrentHp(ReadOnlySpan<byte> payload, out CurrentHpParsed parsed)
        {
            parsed = default;
            if (payload.Length != 20)
                return false;

            uint targetId = ReadU32(payload, 0);
            uint currentHp = ReadU32(payload, 12);

            if (targetId == 0 || currentHp == 0)
                return false;

            parsed = new CurrentHpParsed(targetId, currentHp);
            return true;
        }

        private static uint ReadU32(ReadOnlySpan<byte> payload, int offset)
            => BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset, 4));

        private static string FormatFlags(byte[] flags)
        {
            if (flags.Length == 0)
                return "-";

            var enabled = new List<string>(8);

            foreach (var def in FlagDefs)
            {
                if (def.Index < flags.Length && (flags[def.Index] & def.Mask) != 0)
                    enabled.Add(def.Name);
            }

            return enabled.Count == 0 ? "-" : string.Join(",", enabled);
        }

        private readonly struct AttackParsed
        {
            public uint UserId { get; }
            public uint TargetId { get; }
            public uint Key1 { get; }
            public uint Key2 { get; }
            public byte[] Flags { get; }

            public AttackParsed(uint userId, uint targetId, uint key1, uint key2, byte[] flags)
            {
                UserId = userId;
                TargetId = targetId;
                Key1 = key1;
                Key2 = key2;
                Flags = flags;
            }
        }

        private readonly struct SelfDamageParsed
        {
            public uint UserId { get; }
            public uint TargetId { get; }
            public uint Damage { get; }
            public byte[] Flags { get; }

            public SelfDamageParsed(uint userId, uint targetId, uint damage, byte[] flags)
            {
                UserId = userId;
                TargetId = targetId;
                Damage = damage;
                Flags = flags;
            }
        }

        private readonly struct HpChangedParsed
        {
            public uint TargetId { get; }
            public uint PrevHp { get; }
            public uint CurrentHp { get; }

            public HpChangedParsed(uint targetId, uint prevHp, uint currentHp)
            {
                TargetId = targetId;
                PrevHp = prevHp;
                CurrentHp = currentHp;
            }
        }

        private readonly struct CurrentHpParsed
        {
            public uint TargetId { get; }
            public uint CurrentHp { get; }

            public CurrentHpParsed(uint targetId, uint currentHp)
            {
                TargetId = targetId;
                CurrentHp = currentHp;
            }
        }
    }
}
