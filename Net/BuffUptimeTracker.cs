using System;
using System.Collections.Generic;
using System.Linq;

namespace OverlayTimer.Net
{
    public sealed class BuffUptimeTracker
    {
        private readonly object _lock = new();
        private DateTime _measureStartUtc;
        private readonly Dictionary<ulong, ActiveEntry> _activeByInstKey = new();
        private readonly Dictionary<uint, double> _completedByKey = new();
        private static readonly TimeSpan StaleLimit = TimeSpan.FromMinutes(5);

        public BuffUptimeTracker()
        {
            _measureStartUtc = DateTime.UtcNow;
        }

        public void OnBuffStart(uint buffKey, ulong instKey, TimeSpan expectedDuration)
        {
            if (instKey == 0) return;

            lock (_lock)
            {
                CleanupStaleEntries();
                _activeByInstKey[instKey] = new ActiveEntry(buffKey, DateTime.UtcNow, expectedDuration);
            }
        }

        /// <summary>
        /// 버프 종료 패킷 수신 시 호출. instKey 기준으로 내부 시작 시각을 찾아 경과 시간을 누적한다.
        /// </summary>
        public void OnBuffEnd(ulong instKey)
        {
            if (instKey == 0) return;

            lock (_lock)
            {
                if (!_activeByInstKey.TryGetValue(instKey, out var entry))
                    return;

                _activeByInstKey.Remove(instKey);
                var seconds = Math.Min(
                    (DateTime.UtcNow - entry.StartUtc).TotalSeconds,
                    entry.ExpectedDuration.TotalSeconds);

                if (seconds <= 0) return;
                _completedByKey[entry.BuffKey] = (_completedByKey.TryGetValue(entry.BuffKey, out var cur) ? cur : 0.0) + seconds;
            }
        }

        public void Reset()
        {
            lock (_lock)
            {
                _activeByInstKey.Clear();
                _completedByKey.Clear();
                _measureStartUtc = DateTime.UtcNow;
            }
        }

        public BuffUptimeSnapshot GetSnapshot()
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                double elapsed = Math.Max((now - _measureStartUtc).TotalSeconds, 0.0);

                // 완료된 시간에 현재 활성 중인 버프의 경과 시간을 합산
                var combined = new Dictionary<uint, double>(_completedByKey);
                var maxRemainingByKey = new Dictionary<uint, double>();

                foreach (var (_, entry) in _activeByInstKey)
                {
                    var soFar = Math.Min(
                        (now - entry.StartUtc).TotalSeconds,
                        entry.ExpectedDuration.TotalSeconds);

                    if (soFar > 0)
                        combined[entry.BuffKey] = (combined.TryGetValue(entry.BuffKey, out var cur) ? cur : 0.0) + soFar;

                    var remaining = entry.ExpectedDuration.TotalSeconds - (now - entry.StartUtc).TotalSeconds;
                    if (remaining > 0)
                    {
                        // 같은 buffKey의 여러 인스턴스 중 가장 긴 남은 시간을 표시
                        if (!maxRemainingByKey.TryGetValue(entry.BuffKey, out var prev) || remaining > prev)
                            maxRemainingByKey[entry.BuffKey] = remaining;
                    }
                }

                var rows = combined
                    .Select(kv => new BuffUptimeRow(
                        kv.Key,
                        kv.Value,
                        maxRemainingByKey.TryGetValue(kv.Key, out var rem) ? rem : -1.0))
                    .OrderByDescending(r => r.IsActive)
                    .ThenByDescending(r => r.TotalSeconds)
                    .ToArray();

                return new BuffUptimeSnapshot(elapsed, rows);
            }
        }

        private void CleanupStaleEntries()
        {
            var cutoff = DateTime.UtcNow - StaleLimit;
            var stale = new List<ulong>();
            foreach (var kv in _activeByInstKey)
                if (kv.Value.StartUtc < cutoff) stale.Add(kv.Key);
            foreach (var key in stale)
                _activeByInstKey.Remove(key);
        }

        private readonly struct ActiveEntry
        {
            public uint BuffKey { get; }
            public DateTime StartUtc { get; }
            public TimeSpan ExpectedDuration { get; }

            public ActiveEntry(uint buffKey, DateTime startUtc, TimeSpan expectedDuration)
            {
                BuffKey = buffKey;
                StartUtc = startUtc;
                ExpectedDuration = expectedDuration;
            }
        }
    }

    public readonly struct BuffUptimeRow
    {
        public uint BuffKey { get; }
        public double TotalSeconds { get; }
        /// <summary>활성 중이면 남은 초, 활성이 아니면 음수.</summary>
        public double RemainingSeconds { get; }

        public bool IsActive => RemainingSeconds >= 0;

        public BuffUptimeRow(uint buffKey, double totalSeconds, double remainingSeconds)
        {
            BuffKey = buffKey;
            TotalSeconds = totalSeconds;
            RemainingSeconds = remainingSeconds;
        }
    }

    public readonly struct BuffUptimeSnapshot
    {
        public double ElapsedSeconds { get; }
        public IReadOnlyList<BuffUptimeRow> Rows { get; }

        public BuffUptimeSnapshot(double elapsedSeconds, IReadOnlyList<BuffUptimeRow> rows)
        {
            ElapsedSeconds = elapsedSeconds;
            Rows = rows;
        }
    }
}
