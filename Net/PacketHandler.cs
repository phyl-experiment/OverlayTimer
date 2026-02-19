using System;
using System.Collections.Generic;

namespace OverlayTimer.Net
{
    public sealed class PacketHandler
    {
        private readonly ITimerTrigger _timerTrigger;
        private readonly SelfIdResolver _selfIdResolver;
        private readonly int _buffStartDataType;
        private readonly int _buffEndDataType;
        private readonly HashSet<uint> _buffKeySet;
        private readonly PacketTypeLogger? _logger;
        private readonly DpsTracker? _dpsTracker;
        private readonly BuffUptimeTracker? _buffUptimeTracker;
        private readonly int _dpsAttackType;
        private readonly int _dpsDamageType;
        private readonly TimeSpan _defaultActiveDuration;

        private readonly List<PendingDamage> _pendingDamages = new();
        private readonly Dictionary<ulong, TrackedBuffStart> _trackedBuffStartsByInstKey = new();
        private static readonly TimeSpan BuffTrackKeep = TimeSpan.FromMinutes(3);
        private ulong _lastSelfId;

        public PacketHandler(
            ITimerTrigger timerTrigger,
            SelfIdResolver selfIdResolver,
            int buffStartDataType,
            int buffEndDataType,
            uint[] buffKeys,
            PacketTypeLogger? logger = null,
            DpsTracker? dpsTracker = null,
            BuffUptimeTracker? buffUptimeTracker = null,
            int dpsAttackType = 20389,
            int dpsDamageType = 20897,
            int defaultActiveDurationSeconds = 20)
        {
            _timerTrigger = timerTrigger;
            _selfIdResolver = selfIdResolver;
            _buffStartDataType = buffStartDataType;
            _buffEndDataType = buffEndDataType;
            _buffKeySet = new HashSet<uint>(buffKeys ?? Array.Empty<uint>());
            _logger = logger;
            _dpsTracker = dpsTracker;
            _buffUptimeTracker = buffUptimeTracker;
            _dpsAttackType = dpsAttackType;
            _dpsDamageType = dpsDamageType;
            _defaultActiveDuration = TimeSpan.FromSeconds(Math.Max(1, defaultActiveDurationSeconds));
        }

        public void OnPacket(int dataType, ReadOnlySpan<byte> content)
        {
            _logger?.OnPacket(dataType, content);

            TryHandleDps(dataType, content);

            if (dataType == _buffStartDataType)
            {
                HandleBuffStart(content);
                return;
            }

            if (dataType == _buffEndDataType)
            {
                HandleBuffEnd(content);
                return;
            }

            var parsedId = _selfIdResolver.TryFeed(dataType, content);
            if (parsedId == 0)
                return;

            // Reset DPS and tracked buff instances only when the self ID actually changes.
            if (_lastSelfId != 0 && parsedId != _lastSelfId)
            {
                _dpsTracker?.Reset();
                _buffUptimeTracker?.Reset();
                _pendingDamages.Clear();
                _trackedBuffStartsByInstKey.Clear();
            }

            _lastSelfId = parsedId;
            LogHelper.Write($"{dataType}:{parsedId}");
        }

        private void HandleBuffStart(ReadOnlySpan<byte> content)
        {
            var parsed = PacketBuffStart.Parse(content);

            ulong selfId = _selfIdResolver.SelfId;
            if (selfId != 0 && parsed.UserId != selfId)
                return;

            var activeDuration = ResolveActiveDuration(parsed);

            // 모든 버프의 가동률을 추적 (buffKeys 필터 무관)
            _buffUptimeTracker?.OnBuffStart(parsed.BuffKey, parsed.InstKey, activeDuration);

            if (!_buffKeySet.Contains(parsed.BuffKey))
                return;

            TrackBuffStart(parsed, activeDuration);
            _timerTrigger.On(new TimerTriggerRequest(parsed.BuffKey, activeDuration));

            LogHelper.Write(
                $"[BuffStart] user={parsed.UserId} key={parsed.BuffKey} inst={parsed.InstKey} duration={activeDuration.TotalSeconds:0.#}s");
        }

        private void HandleBuffEnd(ReadOnlySpan<byte> content)
        {
            var parsed = PacketBuffEnd.Parse(content);

            ulong selfId = _selfIdResolver.SelfId;
            if (selfId != 0 && parsed.UserId != selfId)
                return;

            // 모든 버프의 가동률 종료 처리 (instKey 기반, buffKeys 필터 무관)
            _buffUptimeTracker?.OnBuffEnd(parsed.InstKey);

            CleanupTrackedBuffStarts();

            if (parsed.InstKey == 0)
                return;

            if (!_trackedBuffStartsByInstKey.TryGetValue(parsed.InstKey, out var tracked))
            {
                LogHelper.Write(
                    $"[BuffEnd] user={parsed.UserId} inst={parsed.InstKey} state={parsed.State} matched=0");
                return;
            }

            _trackedBuffStartsByInstKey.Remove(parsed.InstKey);

            var elapsed = DateTime.UtcNow - tracked.StartUtc;
            LogHelper.Write(
                $"[BuffEnd] user={parsed.UserId} key={tracked.BuffKey} inst={parsed.InstKey} state={parsed.State} matched=1 elapsed={elapsed.TotalSeconds:0.#}s startDuration={tracked.StartDuration.TotalSeconds:0.#}s");
        }

        private void TrackBuffStart(PacketBuffStart parsed, TimeSpan activeDuration)
        {
            if (parsed.InstKey == 0)
                return;

            CleanupTrackedBuffStarts();
            _trackedBuffStartsByInstKey[parsed.InstKey] = new TrackedBuffStart(
                parsed.UserId,
                parsed.BuffKey,
                activeDuration,
                DateTime.UtcNow);
        }

        private void CleanupTrackedBuffStarts()
        {
            if (_trackedBuffStartsByInstKey.Count == 0)
                return;

            DateTime cutoff = DateTime.UtcNow - BuffTrackKeep;
            var stale = new List<ulong>();

            foreach (var kv in _trackedBuffStartsByInstKey)
            {
                if (kv.Value.StartUtc < cutoff)
                    stale.Add(kv.Key);
            }

            for (int i = 0; i < stale.Count; i++)
                _trackedBuffStartsByInstKey.Remove(stale[i]);
        }

        private TimeSpan ResolveActiveDuration(PacketBuffStart parsed)
        {
            if (TryResolveDuration(parsed.DurationSeconds, out var activeDuration))
                return activeDuration;

            return _defaultActiveDuration;
        }

        private static bool TryResolveDuration(float valueSeconds, out TimeSpan duration)
        {
            duration = default;

            if (float.IsNaN(valueSeconds) || float.IsInfinity(valueSeconds))
                return false;

            if (valueSeconds < 1f || valueSeconds > 180f)
                return false;

            var rounded = Math.Round(valueSeconds, 1, MidpointRounding.AwayFromZero);
            duration = TimeSpan.FromSeconds(rounded);
            return true;
        }

        private void TryHandleDps(int dataType, ReadOnlySpan<byte> content)
        {
            if (_dpsTracker == null)
                return;

            CleanupPendingDamages();

            if (dataType == _dpsDamageType && PacketDamage20897.TryParse(content, out var damagePacket))
            {
                _pendingDamages.Add(new PendingDamage(damagePacket, DateTime.UtcNow));
                if (_pendingDamages.Count > 256)
                    _pendingDamages.RemoveRange(0, _pendingDamages.Count - 256);
                return;
            }

            if (dataType != _dpsAttackType || !PacketAttack20389.TryParse(content, out var attackPacket))
                return;

            int matchedIndex = -1;
            for (int i = 0; i < _pendingDamages.Count; i++)
            {
                var pending = _pendingDamages[i].Packet;
                if (pending.UserId != attackPacket.UserId)
                    continue;
                if (pending.TargetId != attackPacket.TargetId)
                    continue;
                if (!pending.Flags.AsSpan().SequenceEqual(attackPacket.Flags))
                    continue;

                matchedIndex = i;
                break;
            }

            if (matchedIndex < 0)
                return;

            var matched = _pendingDamages[matchedIndex].Packet;
            _pendingDamages.RemoveAt(matchedIndex);

            ulong selfId = _selfIdResolver.SelfId;
            if (selfId != 0 && matched.UserId != selfId)
                return;

            bool isCrit = IsFlagSet(attackPacket.Flags, 0, 0x01);
            bool isPower = IsFlagSet(attackPacket.Flags, 1, 0x02);
            bool isFast = IsFlagSet(attackPacket.Flags, 1, 0x04);
            bool isAddHit = IsFlagSet(attackPacket.Flags, 3, 0x08);
            uint skillType = DpsSkillClassifier.NormalizeSkillType(attackPacket.Key1, attackPacket.Flags);

            _dpsTracker.AddDamage(matched.TargetId, matched.Damage, skillType, isCrit, isAddHit, isPower, isFast);
        }

        private void CleanupPendingDamages()
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromSeconds(2);
            _pendingDamages.RemoveAll(x => x.ReceivedUtc < cutoff);
        }

        private readonly struct TrackedBuffStart
        {
            public ulong UserId { get; }
            public uint BuffKey { get; }
            public TimeSpan StartDuration { get; }
            public DateTime StartUtc { get; }

            public TrackedBuffStart(ulong userId, uint buffKey, TimeSpan startDuration, DateTime startUtc)
            {
                UserId = userId;
                BuffKey = buffKey;
                StartDuration = startDuration;
                StartUtc = startUtc;
            }
        }

        private readonly struct PendingDamage
        {
            public PacketDamage20897 Packet { get; }
            public DateTime ReceivedUtc { get; }

            public PendingDamage(PacketDamage20897 packet, DateTime receivedUtc)
            {
                Packet = packet;
                ReceivedUtc = receivedUtc;
            }
        }

        private static bool IsFlagSet(byte[] flags, int index, byte mask)
        {
            return index >= 0 && index < flags.Length && (flags[index] & mask) != 0;
        }
    }
}
