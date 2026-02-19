using System;
using System.Collections.Generic;
using System.Linq;

namespace OverlayTimer.Net
{
    public sealed class DpsTracker
    {
        // 초기 DPS 스파이크 완화용 휴리스틱 상한 (초).
        // 첫 타격 직후 경과 시간이 거의 0이 되는 문제를 막기 위해,
        // 분모를 min(측정 창 시작 이후 경과, 이 상수) 이상으로 보정한다.
        private const double HeuristicInitialSeconds = 1.0;

        private readonly object _lock = new();
        private DateTime _measureStartUtc; // 측정 창 시작 시각 (생성/Reset 시점)
        private DateTime _startUtc;        // 첫 데미지 수신 시각
        private DateTime _endUtc;
        private long _totalDamage;
        private readonly Dictionary<ulong, long> _damageByTarget = new();
        private readonly Dictionary<uint, SkillStats> _statsBySkill = new();
        private readonly Dictionary<ulong, Dictionary<uint, SkillStats>> _statsBySkillPerTarget = new();

        private long _hitCount;
        private long _critCount;
        private long _addHitCount;
        private long _powerCount;
        private long _fastCount;

        public DpsTracker()
        {
            _measureStartUtc = DateTime.UtcNow;
            _startUtc = default;
            _endUtc = default;
        }

        public void Reset()
        {
            lock (_lock)
            {
                _totalDamage = 0;
                _damageByTarget.Clear();
                _statsBySkill.Clear();
                _statsBySkillPerTarget.Clear();
                _hitCount = 0;
                _critCount = 0;
                _addHitCount = 0;
                _powerCount = 0;
                _fastCount = 0;
                _measureStartUtc = DateTime.UtcNow;
                _startUtc = default;
                _endUtc = default;
            }
        }

        public void AddDamage(ulong targetId, long damage, uint skillType, bool isCrit, bool isAddHit, bool isPower, bool isFast)
        {
            if (damage <= 0)
                return;

            lock (_lock)
            {
                if (_totalDamage > long.MaxValue - damage)
                    return;

                if (_startUtc == default)
                    _startUtc = DateTime.UtcNow;

                _totalDamage += damage;
                _damageByTarget[targetId] = (_damageByTarget.TryGetValue(targetId, out var current) ? current : 0L) + damage;

                if (!_statsBySkill.TryGetValue(skillType, out var skill))
                {
                    skill = new SkillStats();
                    _statsBySkill[skillType] = skill;
                }
                skill.Damage += damage;
                skill.HitCount++;
                if (isCrit) skill.CritCount++;
                if (isAddHit) skill.AddHitCount++;
                if (isPower) skill.PowerCount++;
                if (isFast) skill.FastCount++;

                if (!_statsBySkillPerTarget.TryGetValue(targetId, out var targetSkills))
                {
                    targetSkills = new Dictionary<uint, SkillStats>();
                    _statsBySkillPerTarget[targetId] = targetSkills;
                }
                if (!targetSkills.TryGetValue(skillType, out var targetSkill))
                {
                    targetSkill = new SkillStats();
                    targetSkills[skillType] = targetSkill;
                }
                targetSkill.Damage += damage;
                targetSkill.HitCount++;
                if (isCrit) targetSkill.CritCount++;
                if (isAddHit) targetSkill.AddHitCount++;
                if (isPower) targetSkill.PowerCount++;
                if (isFast) targetSkill.FastCount++;

                _hitCount++;
                if (isCrit) _critCount++;
                if (isAddHit) _addHitCount++;
                if (isPower) _powerCount++;
                if (isFast) _fastCount++;

                _endUtc = DateTime.UtcNow;
            }
        }

        public DpsSnapshot GetSnapshot()
        {
            lock (_lock)
            {
                // Keep DPS stable when combat is idle: use last damage timestamp as end.
                DateTime end = _endUtc;

                double elapsedSeconds = 0.0;
                if (_startUtc != default)
                {
                    double actualElapsed = Math.Max((end - _startUtc).TotalSeconds, 0.0);

                    // 휴리스틱 보정: 첫 타격 직후 elapsed ≈ 0 이 되어 DPS가 폭발하는 현상을 완화한다.
                    // 분모의 하한을 min(측정 창 시작 이후 경과 시간, HeuristicInitialSeconds) 으로 둔다.
                    // 실제 경과 시간이 이 값을 넘어서면 실제 값이 사용된다.
                    double timeSinceWindowStart = Math.Max((end - _measureStartUtc).TotalSeconds, 0.0);
                    double heuristicFloor = Math.Min(timeSinceWindowStart, HeuristicInitialSeconds);
                    elapsedSeconds = Math.Max(actualElapsed, heuristicFloor);
                }

                double totalDps = elapsedSeconds > 0.0 ? _totalDamage / elapsedSeconds : 0.0;

                var targetStats = _damageByTarget
                    .Select(kv => new DpsTargetSnapshot(
                        kv.Key,
                        kv.Value,
                        elapsedSeconds > 0.0 ? kv.Value / elapsedSeconds : 0.0))
                    .OrderByDescending(x => x.Damage)
                    .ToArray();

                var skillStats = _statsBySkill
                    .Select(kv => new DpsSkillSnapshot(
                        kv.Key,
                        kv.Value.Damage,
                        _totalDamage > 0 ? kv.Value.Damage * 100.0 / _totalDamage : 0.0,
                        kv.Value.HitCount,
                        kv.Value.HitCount > 0 ? kv.Value.CritCount * 100.0 / kv.Value.HitCount : 0.0,
                        // 추가타는 원래 타격에 딸려오는 별도 패킷이므로,
                        // 분모에서 추가타 횟수를 제외해야 "원래 타격 중 추가타 발생률"이 정확해진다.
                        (kv.Value.HitCount - kv.Value.AddHitCount) > 0 ? kv.Value.AddHitCount * 100.0 / (kv.Value.HitCount - kv.Value.AddHitCount) : 0.0,
                        kv.Value.HitCount > 0 ? kv.Value.PowerCount * 100.0 / kv.Value.HitCount : 0.0,
                        kv.Value.HitCount > 0 ? kv.Value.FastCount * 100.0 / kv.Value.HitCount : 0.0))
                    .OrderByDescending(x => x.DamageRatio)
                    .ToArray();

                return new DpsSnapshot(
                    _totalDamage,
                    elapsedSeconds,
                    totalDps,
                    targetStats,
                    skillStats,
                    _hitCount,
                    _critCount,
                    _addHitCount,
                    _powerCount,
                    _fastCount);
            }
        }

        public IReadOnlyList<DpsSkillSnapshot> GetSkillSnapshotForTargets(IReadOnlyCollection<ulong> targetIds)
        {
            lock (_lock)
            {
                if (targetIds.Count == 0)
                    return Array.Empty<DpsSkillSnapshot>();

                // 선택된 대상들의 스킬 통계를 합산한다.
                var merged = new Dictionary<uint, SkillStats>();
                long totalDamage = 0;

                foreach (var targetId in targetIds)
                {
                    if (_damageByTarget.TryGetValue(targetId, out var td))
                        totalDamage += td;

                    if (!_statsBySkillPerTarget.TryGetValue(targetId, out var skills))
                        continue;

                    foreach (var (skillType, stats) in skills)
                    {
                        if (!merged.TryGetValue(skillType, out var m))
                        {
                            m = new SkillStats();
                            merged[skillType] = m;
                        }
                        m.Damage += stats.Damage;
                        m.HitCount += stats.HitCount;
                        m.CritCount += stats.CritCount;
                        m.AddHitCount += stats.AddHitCount;
                        m.PowerCount += stats.PowerCount;
                        m.FastCount += stats.FastCount;
                    }
                }

                return merged
                    .Select(kv => new DpsSkillSnapshot(
                        kv.Key,
                        kv.Value.Damage,
                        totalDamage > 0 ? kv.Value.Damage * 100.0 / totalDamage : 0.0,
                        kv.Value.HitCount,
                        kv.Value.HitCount > 0 ? kv.Value.CritCount * 100.0 / kv.Value.HitCount : 0.0,
                        (kv.Value.HitCount - kv.Value.AddHitCount) > 0 ? kv.Value.AddHitCount * 100.0 / (kv.Value.HitCount - kv.Value.AddHitCount) : 0.0,
                        kv.Value.HitCount > 0 ? kv.Value.PowerCount * 100.0 / kv.Value.HitCount : 0.0,
                        kv.Value.HitCount > 0 ? kv.Value.FastCount * 100.0 / kv.Value.HitCount : 0.0))
                    .OrderByDescending(x => x.DamageRatio)
                    .ToArray();
            }
        }

        private sealed class SkillStats
        {
            public long Damage;
            public long HitCount;
            public long CritCount;
            public long AddHitCount;
            public long PowerCount;
            public long FastCount;
        }
    }

    public readonly struct DpsTargetSnapshot
    {
        public ulong TargetId { get; }
        public long Damage { get; }
        public double Dps { get; }

        public DpsTargetSnapshot(ulong targetId, long damage, double dps)
        {
            TargetId = targetId;
            Damage = damage;
            Dps = dps;
        }
    }

    public readonly struct DpsSkillSnapshot
    {
        public uint SkillType { get; }
        public long Damage { get; }
        public double DamageRatio { get; }
        public long HitCount { get; }
        public double CritRate { get; }
        public double AddHitRate { get; }
        public double PowerRate { get; }
        public double FastRate { get; }

        public DpsSkillSnapshot(
            uint skillType,
            long damage,
            double damageRatio,
            long hitCount,
            double critRate,
            double addHitRate,
            double powerRate,
            double fastRate)
        {
            SkillType = skillType;
            Damage = damage;
            DamageRatio = damageRatio;
            HitCount = hitCount;
            CritRate = critRate;
            AddHitRate = addHitRate;
            PowerRate = powerRate;
            FastRate = fastRate;
        }
    }

    public readonly struct DpsSnapshot
    {
        public long TotalDamage { get; }
        public double ElapsedSeconds { get; }
        public double TotalDps { get; }
        public IReadOnlyList<DpsTargetSnapshot> Targets { get; }
        public IReadOnlyList<DpsSkillSnapshot> Skills { get; }

        public long HitCount { get; }
        public long CritCount { get; }
        public long AddHitCount { get; }
        public long PowerCount { get; }
        public long FastCount { get; }

        public DpsSnapshot(
            long totalDamage,
            double elapsedSeconds,
            double totalDps,
            IReadOnlyList<DpsTargetSnapshot> targets,
            IReadOnlyList<DpsSkillSnapshot> skills,
            long hitCount,
            long critCount,
            long addHitCount,
            long powerCount,
            long fastCount)
        {
            TotalDamage = totalDamage;
            ElapsedSeconds = elapsedSeconds;
            TotalDps = totalDps;
            Targets = targets;
            Skills = skills;
            HitCount = hitCount;
            CritCount = critCount;
            AddHitCount = addHitCount;
            PowerCount = powerCount;
            FastCount = fastCount;
        }
    }
}
