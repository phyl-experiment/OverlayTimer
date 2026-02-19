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
            int dpsDamageType = 20897)
        {
            _timerTrigger = timerTrigger;
            _selfIdResolver = selfIdResolver;
            _buffStartDataType = buffStartDataType;
            _buffKeys = buffKeys;
            _logger = logger;
            _dpsTracker = dpsTracker;
            _dpsAttackType = dpsAttackType;
            _dpsDamageType = dpsDamageType;
        }

        public void OnPacket(int dataType, ReadOnlySpan<byte> content)
        {
            if (_logger != null) _logger.OnPacket(dataType, content);
            TryHandleDps(dataType, content);

            if (dataType == _buffStartDataType)
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

                LogHelper.Write($"Timer On {_selfIdResolver.SelfId}");
                _timerTrigger.On();
            }
            else
            {
                var parsedId = _selfIdResolver.TryFeed(dataType, content);
                if (parsedId != 0)
                {
                    // Reset DPS only when the self ID actually changes (e.g. character switch).
                    if (_dpsTracker != null && _lastSelfId != 0 && parsedId != _lastSelfId)
                    {
                        _dpsTracker.Reset();
                        _pendingDamages.Clear();
                    }

                    _lastSelfId = parsedId;
                    LogHelper.Write($"{dataType}:{parsedId}");
                }
            }
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
