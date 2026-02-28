using System;
using System.Collections.Generic;
using System.Linq;

namespace OverlayTimer.Net
{
    public enum BenchmarkState { Idle, Armed, Running }

    /// <summary>
    /// 2분 DPS 측정 세션. 스레드 안전.
    /// Armed 상태에서 첫 데미지 수신 시 Running으로 전환되고,
    /// 120초 경과 시 OnCompleted 콜백으로 결과를 전달한 뒤 자동으로 Idle로 돌아간다.
    /// </summary>
    public sealed class DpsBenchmarkSession
    {
        private const double SessionSeconds = 120.0;

        private readonly object _lock = new();
        private BenchmarkState _state = BenchmarkState.Idle;
        private DateTime _startUtc;

        // 누적 데이터 (DpsTracker와 동일한 패턴)
        private long _totalDamage;
        private long _hitCount;
        private long _critCount;
        private long _addHitCount;
        private long _powerCount;
        private long _fastCount;
        private readonly Dictionary<ulong, long> _damageByTarget = new();
        private readonly Dictionary<uint, SkillStats> _bySkill = new();

        /// <summary>Armed → Running 전환 시 캡처 스레드에서 호출됨.</summary>
        public Action? OnSessionStarted { get; set; }

        /// <summary>120초 완료 시 캡처 스레드에서 호출됨. 완료 후 세션은 자동으로 Idle로 초기화됨.</summary>
        public Action<BenchmarkRawData>? OnCompleted { get; set; }

        public BenchmarkState State
        {
            get { lock (_lock) return _state; }
        }

        /// <summary>Idle 상태에서 Armed로 전환. 이미 Armed/Running 상태면 무시.</summary>
        public void Arm()
        {
            lock (_lock)
            {
                if (_state != BenchmarkState.Idle)
                    return;

                ResetData();
                _state = BenchmarkState.Armed;
            }
        }

        /// <summary>어느 상태에서든 Idle로 돌아간다.</summary>
        public void Cancel()
        {
            lock (_lock)
            {
                _state = BenchmarkState.Idle;
                ResetData();
            }
        }

        /// <summary>
        /// 매칭 완료된 데미지 이벤트를 세션에 전달.
        /// Armed이면 Running으로 전환 후 누적, Running이면 누적, 그 외엔 무시.
        /// </summary>
        public void OnDamage(ulong targetId, long damage, uint skillType,
            bool isCrit, bool isAddHit, bool isPower, bool isFast)
        {
            Action? startedCallback = null;
            Action<BenchmarkRawData>? completedCallback = null;
            BenchmarkRawData? completedData = null;

            lock (_lock)
            {
                if (_state == BenchmarkState.Armed)
                {
                    _state = BenchmarkState.Running;
                    _startUtc = DateTime.UtcNow;
                    startedCallback = OnSessionStarted;
                }
                else if (_state != BenchmarkState.Running)
                {
                    return;
                }

                Accumulate(targetId, damage, skillType, isCrit, isAddHit, isPower, isFast);

                double elapsed = (DateTime.UtcNow - _startUtc).TotalSeconds;
                if (elapsed >= SessionSeconds)
                {
                    completedData = BuildRawData(Math.Min(elapsed, SessionSeconds));
                    ResetData();
                    _state = BenchmarkState.Idle;
                    completedCallback = OnCompleted;
                }
            }

            // 콜백은 락 밖에서 호출
            startedCallback?.Invoke();
            if (completedData != null)
                completedCallback?.Invoke(completedData);
        }

        /// <summary>현재 상태와 경과 초를 반환.</summary>
        public (BenchmarkState State, double ElapsedSeconds) GetStatus()
        {
            lock (_lock)
            {
                if (_state == BenchmarkState.Running)
                    return (_state, (DateTime.UtcNow - _startUtc).TotalSeconds);

                return (_state, 0.0);
            }
        }

        /// <summary>버튼에 표시할 텍스트 반환.</summary>
        public string GetButtonText()
        {
            lock (_lock)
            {
                return _state switch
                {
                    BenchmarkState.Armed => "\uB300\uAE30\uC911\u2026",
                    BenchmarkState.Running => FormatElapsed((DateTime.UtcNow - _startUtc).TotalSeconds),
                    _ => "2\uBD84 DPS"
                };
            }
        }

        private static string FormatElapsed(double seconds)
        {
            int s = Math.Min((int)seconds, (int)SessionSeconds);
            return $"{s / 60}:{s % 60:D2}";
        }

        private void Accumulate(ulong targetId, long damage, uint skillType,
            bool isCrit, bool isAddHit, bool isPower, bool isFast)
        {
            if (damage <= 0) return;

            _totalDamage += damage;
            _hitCount++;
            if (isCrit) _critCount++;
            if (isAddHit) _addHitCount++;
            if (isPower) _powerCount++;
            if (isFast) _fastCount++;

            _damageByTarget[targetId] = (_damageByTarget.TryGetValue(targetId, out var cur) ? cur : 0L) + damage;

            if (!_bySkill.TryGetValue(skillType, out var sk))
            {
                sk = new SkillStats();
                _bySkill[skillType] = sk;
            }
            sk.Damage += damage;
            sk.HitCount++;
            if (isCrit) sk.CritCount++;
            if (isAddHit) sk.AddHitCount++;
            if (isPower) sk.PowerCount++;
            if (isFast) sk.FastCount++;
        }

        private BenchmarkRawData BuildRawData(double durationSeconds)
        {
            var targets = _damageByTarget
                .Select(kv => new BenchmarkRawTarget(kv.Key, kv.Value,
                    durationSeconds > 0 ? kv.Value / durationSeconds : 0.0))
                .OrderByDescending(t => t.Damage)
                .ToArray();

            var skills = _bySkill
                .Select(kv => new BenchmarkRawSkill(
                    kv.Key,
                    kv.Value.Damage,
                    _totalDamage > 0 ? kv.Value.Damage * 100.0 / _totalDamage : 0.0,
                    kv.Value.HitCount,
                    kv.Value.HitCount > 0 ? kv.Value.CritCount * 100.0 / kv.Value.HitCount : 0.0,
                    (kv.Value.HitCount - kv.Value.AddHitCount) > 0
                        ? kv.Value.AddHitCount * 100.0 / (kv.Value.HitCount - kv.Value.AddHitCount)
                        : 0.0,
                    kv.Value.HitCount > 0 ? kv.Value.PowerCount * 100.0 / kv.Value.HitCount : 0.0,
                    kv.Value.HitCount > 0 ? kv.Value.FastCount * 100.0 / kv.Value.HitCount : 0.0))
                .OrderByDescending(s => s.DamageRatio)
                .ToArray();

            return new BenchmarkRawData(
                durationSeconds,
                _totalDamage,
                durationSeconds > 0 ? _totalDamage / durationSeconds : 0.0,
                _hitCount, _critCount, _addHitCount, _powerCount, _fastCount,
                targets, skills);
        }

        private void ResetData()
        {
            _totalDamage = 0;
            _hitCount = 0;
            _critCount = 0;
            _addHitCount = 0;
            _powerCount = 0;
            _fastCount = 0;
            _damageByTarget.Clear();
            _bySkill.Clear();
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

    // ---------------------------------------------------------------------------
    // 완료 시 전달되는 원시 데이터 (이름 해석 전)
    // ---------------------------------------------------------------------------

    public sealed class BenchmarkRawData
    {
        public double DurationSeconds { get; }
        public long TotalDamage { get; }
        public double TotalDps { get; }
        public long HitCount { get; }
        public long CritCount { get; }
        public long AddHitCount { get; }
        public long PowerCount { get; }
        public long FastCount { get; }
        public BenchmarkRawTarget[] Targets { get; }
        public BenchmarkRawSkill[] Skills { get; }

        public BenchmarkRawData(
            double durationSeconds, long totalDamage, double totalDps,
            long hitCount, long critCount, long addHitCount, long powerCount, long fastCount,
            BenchmarkRawTarget[] targets, BenchmarkRawSkill[] skills)
        {
            DurationSeconds = durationSeconds;
            TotalDamage = totalDamage;
            TotalDps = totalDps;
            HitCount = hitCount;
            CritCount = critCount;
            AddHitCount = addHitCount;
            PowerCount = powerCount;
            FastCount = fastCount;
            Targets = targets;
            Skills = skills;
        }
    }

    public sealed class BenchmarkRawTarget
    {
        public ulong TargetId { get; }
        public long Damage { get; }
        public double Dps { get; }

        public BenchmarkRawTarget(ulong targetId, long damage, double dps)
        {
            TargetId = targetId;
            Damage = damage;
            Dps = dps;
        }
    }

    public sealed class BenchmarkRawSkill
    {
        public uint SkillType { get; }
        public long Damage { get; }
        public double DamageRatio { get; }
        public long HitCount { get; }
        public double CritRate { get; }
        public double AddHitRate { get; }
        public double PowerRate { get; }
        public double FastRate { get; }

        public BenchmarkRawSkill(uint skillType, long damage, double damageRatio,
            long hitCount, double critRate, double addHitRate, double powerRate, double fastRate)
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
}
