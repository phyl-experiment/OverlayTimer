using System;
using System.Collections.Generic;

namespace OverlayTimer.Net
{
    public sealed class DpsBenchmarkRecord
    {
        public DateTimeOffset RecordedAt { get; set; }
        public double DurationSeconds { get; set; }
        public long TotalDamage { get; set; }
        public double TotalDps { get; set; }
        public long HitCount { get; set; }
        public long CritCount { get; set; }
        public long AddHitCount { get; set; }
        public long PowerCount { get; set; }
        public long FastCount { get; set; }

        public List<BenchmarkTargetEntry> Targets { get; set; } = new();
        public List<BenchmarkSkillEntry> Skills { get; set; } = new();
        public List<BenchmarkBuffEntry> Buffs { get; set; } = new();
    }

    public sealed class BenchmarkTargetEntry
    {
        public ulong TargetId { get; set; }
        public long Damage { get; set; }
        public double Dps { get; set; }
    }

    public sealed class BenchmarkSkillEntry
    {
        public uint SkillType { get; set; }
        public string SkillName { get; set; } = string.Empty;
        public long Damage { get; set; }
        public double DamageRatio { get; set; }
        public long HitCount { get; set; }
        public double CritRate { get; set; }
        public double AddHitRate { get; set; }
        public double PowerRate { get; set; }
        public double FastRate { get; set; }
    }

    public sealed class BenchmarkBuffEntry
    {
        public uint BuffKey { get; set; }
        public string BuffName { get; set; } = string.Empty;
        public double TotalSeconds { get; set; }
        public double UptimePct { get; set; }
    }
}
