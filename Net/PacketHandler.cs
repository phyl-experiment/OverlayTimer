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
        private readonly HashSet<uint> _awakeningBuffKeySet;
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

        // Damage-based self-ID fallback
        // Case 1: selfId == 0 → 유효 데미지 1회로 즉시 확정
        // Case 2: selfId != 0이지만 다른 userId가 연속 N회 → 덮어쓰기
        private ulong _consecutiveCandidateId;
        private int _consecutiveCandidateCount;
        private const int ConsecutiveOverrideThreshold = 3;

        // selfId 미확정 상태에서 수신한 각성 버프 패킷 임시 보관 (instKey → 정보)
        private readonly Dictionary<ulong, PendingAwakenBuff> _pendingAwakenBuffsByInstKey = new();

        /// <summary>알려진 패킷 타입과 일치한 누적 카운트. 새 프로토콜 확인에 사용.</summary>
        public int RecognizedPacketCount { get; private set; }

        public PacketHandler(
            ITimerTrigger timerTrigger,
            SelfIdResolver selfIdResolver,
            int buffStartDataType,
            int buffEndDataType,
            uint[] awakeningBuffKeys,
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
            _awakeningBuffKeySet = new HashSet<uint>(awakeningBuffKeys ?? Array.Empty<uint>());
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

            if (TryHandleDps(dataType, content))
                RecognizedPacketCount++;

            if (dataType == _buffStartDataType)
            {
                if (TryHandleBuffStart(content))
                    RecognizedPacketCount++;
                return;
            }

            if (dataType == _buffEndDataType)
            {
                if (TryHandleBuffEnd(content))
                    RecognizedPacketCount++;
                return;
            }

            ulong parsedId;
            try
            {
                parsedId = _selfIdResolver.TryFeed(dataType, content);
            }
            catch
            {
                return;
            }
            if (parsedId == 0)
                return;

            RecognizedPacketCount++;

            // Reset DPS and tracked buff instances only when the self ID actually changes.
            if (_lastSelfId != 0 && parsedId != _lastSelfId)
            {
                _dpsTracker?.Reset();
                _buffUptimeTracker?.Reset();
                _pendingDamages.Clear();
                _trackedBuffStartsByInstKey.Clear();
                _pendingAwakenBuffsByInstKey.Clear();
                _consecutiveCandidateId = 0;
                _consecutiveCandidateCount = 0;
            }

            _lastSelfId = parsedId;
            // EnterWorld로 selfId가 새로 확정됐을 때도 임시 각성 버프 처리
            ActivatePendingAwakenBuffs(parsedId);
            _consecutiveCandidateId = 0;
            _consecutiveCandidateCount = 0;
            LogHelper.Write($"{dataType}:{parsedId}");
        }

        private bool TryHandleBuffStart(ReadOnlySpan<byte> content)
        {
            PacketBuffStart parsed;
            try
            {
                parsed = PacketBuffStart.Parse(content);
            }
            catch
            {
                return false;
            }

            ulong selfId = _selfIdResolver.SelfId;
            // Buff is strict: until selfId is resolved, do not update buff state/timer.
            // Otherwise another user's buff can overwrite local timer state.
            // 단, 각성 버프는 selfId 확정 후 타이머를 소급 적용할 수 있도록 임시 보관.
            if (selfId == 0)
            {
                if (_awakeningBuffKeySet.Contains(parsed.BuffKey))
                {
                    CleanupPendingAwakenBuffs();
                    var dur = ResolveActiveDuration(parsed);
                    _pendingAwakenBuffsByInstKey[parsed.InstKey] = new PendingAwakenBuff(
                        parsed.UserId, parsed.BuffKey, dur, DateTime.UtcNow);
                }
                return true;
            }

            if (parsed.UserId != selfId)
                return true;

            var activeDuration = ResolveActiveDuration(parsed);

            // 모든 버프의 가동률을 추적 (buffKeys 필터 무관)
            _buffUptimeTracker?.OnBuffStart(parsed.BuffKey, parsed.InstKey, activeDuration);

            if (!_awakeningBuffKeySet.Contains(parsed.BuffKey))
                return true;

            TrackBuffStart(parsed, activeDuration);
            _timerTrigger.On(new TimerTriggerRequest(
                parsed.BuffKey,
                activeDuration,
                AllowSound: true));

            LogHelper.Write(
                $"[BuffStart] user={parsed.UserId} key={parsed.BuffKey} inst={parsed.InstKey} duration={activeDuration.TotalSeconds:0.#}s");
            return true;
        }

        private bool TryHandleBuffEnd(ReadOnlySpan<byte> content)
        {
            PacketBuffEnd parsed;
            try
            {
                parsed = PacketBuffEnd.Parse(content);
            }
            catch
            {
                return false;
            }

            ulong selfId = _selfIdResolver.SelfId;
            // Keep BuffEnd behavior consistent with BuffStart: ignore until selfId is known.
            if (selfId == 0)
                return true;

            if (parsed.UserId != selfId)
                return true;

            // 모든 버프의 가동률 종료 처리 (instKey 기반, buffKeys 필터 무관)
            _buffUptimeTracker?.OnBuffEnd(parsed.InstKey);

            CleanupTrackedBuffStarts();

            if (parsed.InstKey == 0)
                return true;

            if (!_trackedBuffStartsByInstKey.TryGetValue(parsed.InstKey, out var tracked))
            {
                LogHelper.Write(
                    $"[BuffEnd] user={parsed.UserId} inst={parsed.InstKey} state={parsed.State} matched=0");
                return true;
            }

            _trackedBuffStartsByInstKey.Remove(parsed.InstKey);

            var elapsed = DateTime.UtcNow - tracked.StartUtc;
            LogHelper.Write(
                $"[BuffEnd] user={parsed.UserId} key={tracked.BuffKey} inst={parsed.InstKey} state={parsed.State} matched=1 elapsed={elapsed.TotalSeconds:0.#}s startDuration={tracked.StartDuration.TotalSeconds:0.#}s");
            return true;
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

        private bool TryHandleDps(int dataType, ReadOnlySpan<byte> content)
        {
            if (_dpsTracker == null)
                return false;

            ulong selfId = _selfIdResolver.SelfId;
            bool hasSelfId = selfId != 0;

            // DPS is intentionally permissive before selfId resolution.
            // Current traffic is effectively self-only, so provisional accumulation is acceptable.
            // Once selfId is known, we immediately apply strict self filtering.

            CleanupPendingDamages();

            if (dataType == _dpsDamageType)
            {
                if (!PacketDamage20897.TryParse(content, out var damagePacket))
                    return false;

                if (!hasSelfId)
                {
                    // Case 1: selfId 미확정 → 유효 데미지 1회로 즉시 확정
                    ResolveSelfIdFromDamage(damagePacket.UserId);
                }
                else if (damagePacket.UserId != selfId)
                {
                    // Case 2: selfId 확정 상태에서 다른 userId → 연속 카운트, 임계치 초과 시 덮어쓰기
                    TrackConsecutiveCandidateDamage(damagePacket.UserId);
                    return true;
                }
                else
                {
                    // selfId와 일치 → 연속 후보 카운터 초기화
                    _consecutiveCandidateId = 0;
                    _consecutiveCandidateCount = 0;
                }

                _pendingDamages.Add(new PendingDamage(damagePacket, DateTime.UtcNow));
                if (_pendingDamages.Count > 256)
                    _pendingDamages.RemoveRange(0, _pendingDamages.Count - 256);
                return true;
            }

            if (dataType != _dpsAttackType || !PacketAttack20389.TryParse(content, out var attackPacket))
                return false;

            if (hasSelfId && attackPacket.UserId != selfId)
                return true;

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
                return true;

            var matched = _pendingDamages[matchedIndex].Packet;
            _pendingDamages.RemoveAt(matchedIndex);

            bool isCrit = IsFlagSet(attackPacket.Flags, 0, 0x01);
            bool isPower = IsFlagSet(attackPacket.Flags, 1, 0x02);
            bool isFast = IsFlagSet(attackPacket.Flags, 1, 0x04);
            bool isAddHit = IsFlagSet(attackPacket.Flags, 3, 0x08);
            uint skillType = DpsSkillClassifier.NormalizeSkillType(attackPacket.Key1, attackPacket.Flags);

            _dpsTracker.AddDamage(matched.TargetId, matched.Damage, skillType, isCrit, isAddHit, isPower, isFast);
            return true;
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

        /// <summary>
        /// selfId == 0 일 때 유효 데미지 패킷 1회로 즉시 self ID를 확정하고
        /// 임시 보관된 각성 버프 타이머를 소급 적용한다.
        /// </summary>
        private void ResolveSelfIdFromDamage(ulong userId)
        {
            _selfIdResolver.ForceSetId(userId);
            _lastSelfId = userId;
            _consecutiveCandidateId = 0;
            _consecutiveCandidateCount = 0;
            RecognizedPacketCount++;
            ActivatePendingAwakenBuffs(userId);
        }

        /// <summary>
        /// selfId가 확정된 상태에서 다른 userId의 데미지가 연속으로
        /// <see cref="ConsecutiveOverrideThreshold"/>회 이상 오면 selfId를 덮어쓴다.
        /// </summary>
        private void TrackConsecutiveCandidateDamage(ulong userId)
        {
            if (userId == _consecutiveCandidateId)
            {
                _consecutiveCandidateCount++;
            }
            else
            {
                _consecutiveCandidateId = userId;
                _consecutiveCandidateCount = 1;
            }

            if (_consecutiveCandidateCount < ConsecutiveOverrideThreshold)
                return;

            LogHelper.Write($"SelfId override via consecutive damage: {_selfIdResolver.SelfId} → {userId} (n={_consecutiveCandidateCount})");
            _selfIdResolver.ForceSetId(userId);
            _lastSelfId = userId;
            _consecutiveCandidateId = 0;
            _consecutiveCandidateCount = 0;
            RecognizedPacketCount++;
        }

        /// <summary>
        /// selfId가 확정됐을 때 그 ID 소유자의 임시 각성 버프를 잔여 시간으로 타이머 활성화.
        /// </summary>
        private void ActivatePendingAwakenBuffs(ulong selfId)
        {
            if (_pendingAwakenBuffsByInstKey.Count == 0)
                return;

            foreach (var kv in _pendingAwakenBuffsByInstKey)
            {
                var pending = kv.Value;
                if (pending.UserId != selfId)
                    continue;

                var elapsed = DateTime.UtcNow - pending.ReceivedUtc;
                var remaining = pending.Duration - elapsed;

                if (remaining <= TimeSpan.Zero)
                {
                    LogHelper.Write(
                        $"[PendingAwakenBuff] key={pending.BuffKey} already expired (elapsed={elapsed.TotalSeconds:0.#}s)");
                    continue;
                }

                LogHelper.Write(
                    $"[PendingAwakenBuff] key={pending.BuffKey} remaining={remaining.TotalSeconds:0.#}s");

                _timerTrigger.On(new TimerTriggerRequest(
                    pending.BuffKey,
                    remaining,
                    AllowSound: false));
            }

            _pendingAwakenBuffsByInstKey.Clear();
        }

        private void CleanupPendingAwakenBuffs()
        {
            if (_pendingAwakenBuffsByInstKey.Count == 0)
                return;

            DateTime cutoff = DateTime.UtcNow - BuffTrackKeep;
            var stale = new List<ulong>();

            foreach (var kv in _pendingAwakenBuffsByInstKey)
            {
                if (kv.Value.ReceivedUtc < cutoff)
                    stale.Add(kv.Key);
            }

            for (int i = 0; i < stale.Count; i++)
                _pendingAwakenBuffsByInstKey.Remove(stale[i]);
        }

        private readonly struct PendingAwakenBuff
        {
            public ulong UserId { get; }
            public uint BuffKey { get; }
            public TimeSpan Duration { get; }
            public DateTime ReceivedUtc { get; }

            public PendingAwakenBuff(ulong userId, uint buffKey, TimeSpan duration, DateTime receivedUtc)
            {
                UserId = userId;
                BuffKey = buffKey;
                Duration = duration;
                ReceivedUtc = receivedUtc;
            }
        }
    }
}
