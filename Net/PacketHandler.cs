using System;
using System.Collections.Generic;

namespace OverlayTimer.Net
{
    public sealed class PacketHandler
    {
        private readonly ITimerTrigger _timerTrigger;
        private readonly SelfIdResolver _selfIdResolver;
        private readonly int _buffStartDataType;
        private readonly uint[] _buffKeys;
        private readonly PacketTypeLogger? _logger;
        private readonly DpsTracker? _dpsTracker;
        private readonly int _dpsAttackType;
        private readonly int _dpsDamageType;
        private readonly TimeSpan _defaultActiveDuration;

        private readonly List<PendingDamage> _pendingDamages = new();
        private ulong _lastSelfId;

        public PacketHandler(
            ITimerTrigger timerTrigger,
            SelfIdResolver selfIdResolver,
            int buffStartDataType,
            uint[] buffKeys,
            PacketTypeLogger? logger = null,
            DpsTracker? dpsTracker = null,
            int dpsAttackType = 20389,
            int dpsDamageType = 20897,
            int defaultActiveDurationSeconds = 20)
        {
            _timerTrigger = timerTrigger;
            _selfIdResolver = selfIdResolver;
            _buffStartDataType = buffStartDataType;
            _buffKeys = buffKeys;
            _logger = logger;
            _dpsTracker = dpsTracker;
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

            var parsedId = _selfIdResolver.TryFeed(dataType, content);
            if (parsedId == 0)
                return;

            // Reset DPS only when the self ID actually changes (e.g. character switch).
            if (_dpsTracker != null && _lastSelfId != 0 && parsedId != _lastSelfId)
            {
                _dpsTracker.Reset();
                _pendingDamages.Clear();
            }

            _lastSelfId = parsedId;
            LogHelper.Write($"{dataType}:{parsedId}");
        }

        private void HandleBuffStart(ReadOnlySpan<byte> content)
        {
            var parsed = PacketBuffStart.Parse(content);
            if (Array.IndexOf(_buffKeys, parsed.BuffKey) < 0)
                return;

            LogHelper.Write($"[Light] id {parsed.UserId} current {_selfIdResolver.SelfId}");

            if (_selfIdResolver.SelfId != 0 && parsed.UserId != _selfIdResolver.SelfId)
            {
                LogHelper.Write($"{parsed.UserId} diff {_selfIdResolver.SelfId}");
                return;
            }

            var activeDuration = ResolveActiveDuration(parsed);
            _timerTrigger.On(new TimerTriggerRequest(parsed.BuffKey, activeDuration));

            LogHelper.Write(
                $"Timer On {parsed.UserId} key={parsed.BuffKey} active={activeDuration.TotalSeconds:0.#}s cooldown=fallback(manual)");
        }

        private TimeSpan ResolveActiveDuration(PacketBuffStart parsed)
        {
            if (TryResolveDuration(parsed.Value1, out var activeDuration))
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
