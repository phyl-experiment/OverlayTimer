using System;

namespace OverlayTimer.Net
{
    public static class DpsSkillClassifier
    {
        // key1 == 0 buckets (reserved pseudo IDs unlikely to collide with real skill IDs)
        public const uint BasicAttackSkill = 0xFFFF0001;
        public const uint DotSkill = 0xFFFF0002;
        public const uint AdditionalHitSkill = 0xFFFF0003;
        public const uint DotFireSkill = 0xFFFF0101;
        public const uint DotIceSkill = 0xFFFF0102;
        public const uint DotElectricSkill = 0xFFFF0103;
        public const uint DotPoisonSkill = 0xFFFF0104;
        public const uint DotHolySkill = 0xFFFF0105;
        public const uint DotDarkSkill = 0xFFFF0106;
        public const uint DotBleedSkill = 0xFFFF0107;
        public const uint DotMindSkill = 0xFFFF0108;
        public const uint DotMixedSkill = 0xFFFF01FF;

        public static uint NormalizeSkillType(uint key1, ReadOnlySpan<byte> flags)
        {
            if (key1 != 0)
                return key1;

            if (IsDot(flags))
                return ResolveDotType(flags);

            if (IsFlagSet(flags, 3, 0x08)) // add_hit_flag
                return AdditionalHitSkill;

            // default bucket for key1==0: treat as basic attack first.
            return BasicAttackSkill;
        }

        public static bool TryGetSpecialSkillName(uint skillType, out string name)
        {
            switch (skillType)
            {
                case BasicAttackSkill:
                    name = "평타";
                    return true;
                case DotSkill:
                    name = "지속피해";
                    return true;
                case DotFireSkill:
                    name = "지속피해(화염)";
                    return true;
                case DotIceSkill:
                    name = "지속피해(빙결)";
                    return true;
                case DotElectricSkill:
                    name = "지속피해(전기)";
                    return true;
                case DotPoisonSkill:
                    name = "지속피해(중독)";
                    return true;
                case DotHolySkill:
                    name = "지속피해(신성)";
                    return true;
                case DotDarkSkill:
                    name = "지속피해(암흑)";
                    return true;
                case DotBleedSkill:
                    name = "지속피해(출혈)";
                    return true;
                case DotMindSkill:
                    name = "지속피해(정신)";
                    return true;
                case DotMixedSkill:
                    name = "지속피해(복합)";
                    return true;
                case AdditionalHitSkill:
                    name = "추가 타격";
                    return true;
                default:
                    name = string.Empty;
                    return false;
            }
        }

        private static bool IsDot(ReadOnlySpan<byte> flags)
        {
            // dot_flag / dot_flag2 / dot_flag3 / dot_flag4
            return IsFlagSet(flags, 1, 0x08) ||
                   IsFlagSet(flags, 1, 0x80) ||
                   IsFlagSet(flags, 2, 0x01) ||
                   IsFlagSet(flags, 4, 0x10);
        }

        private static uint ResolveDotType(ReadOnlySpan<byte> flags)
        {
            uint resolved = DotSkill;
            int typeCount = 0;

            AddDotTypeIf(flags, 3, 0x40, DotFireSkill, ref resolved, ref typeCount);   // fire_flag
            AddDotTypeIf(flags, 4, 0x01, DotIceSkill, ref resolved, ref typeCount);    // ice_flag
            AddDotTypeIf(flags, 4, 0x02, DotElectricSkill, ref resolved, ref typeCount); // electric_flag
            AddDotTypeIf(flags, 4, 0x04, DotPoisonSkill, ref resolved, ref typeCount); // poison_flag
            AddDotTypeIf(flags, 3, 0x80, DotHolySkill, ref resolved, ref typeCount);   // holy_flag
            AddDotTypeIf(flags, 3, 0x20, DotDarkSkill, ref resolved, ref typeCount);   // dark_flag
            AddDotTypeIf(flags, 3, 0x10, DotBleedSkill, ref resolved, ref typeCount);  // bleed_flag
            AddDotTypeIf(flags, 4, 0x08, DotMindSkill, ref resolved, ref typeCount);   // mind_flag

            if (typeCount > 1)
                return DotMixedSkill;

            return resolved;
        }

        private static void AddDotTypeIf(
            ReadOnlySpan<byte> flags,
            int index,
            byte mask,
            uint dotType,
            ref uint resolved,
            ref int typeCount)
        {
            if (!IsFlagSet(flags, index, mask))
                return;

            resolved = dotType;
            typeCount++;
        }

        private static bool IsFlagSet(ReadOnlySpan<byte> flags, int index, byte mask)
        {
            return index >= 0 && index < flags.Length && (flags[index] & mask) != 0;
        }
    }
}
