using System;
using System.Collections.Generic;

namespace OverlayTimer
{
    /// <summary>
    /// 스레드 안전한 진단 정보 컨테이너.
    /// 패킷 스레드에서 기록되고 UI 스레드에서 500ms마다 스냅샷을 읽는다.
    /// </summary>
    public sealed class DebugInfo
    {
        private readonly object _lock = new();
        private string _nicName = "(없음)";
        private ulong _selfId;
        private string _selfIdSource = "미확정";
        private readonly Queue<EnterWorldRecord> _enterWorldRecords = new();
        private readonly Queue<DamageRecord> _damageRecords = new();
        private const int MaxRecords = 10;

        public void SetNic(string name)
        {
            lock (_lock) _nicName = name;
        }

        public void SetSelfId(ulong id, string source)
        {
            lock (_lock) { _selfId = id; _selfIdSource = source; }
        }

        public void AddEnterWorldRecord(ulong id)
        {
            lock (_lock)
            {
                _enterWorldRecords.Enqueue(new EnterWorldRecord(DateTime.Now, id));
                if (_enterWorldRecords.Count > MaxRecords)
                    _enterWorldRecords.Dequeue();
            }
        }

        public void AddDamageRecord(ulong userId, ulong targetId, long damage)
        {
            lock (_lock)
            {
                _damageRecords.Enqueue(new DamageRecord(DateTime.Now, userId, targetId, damage));
                if (_damageRecords.Count > MaxRecords)
                    _damageRecords.Dequeue();
            }
        }

        public DebugSnapshot GetSnapshot()
        {
            lock (_lock)
            {
                return new DebugSnapshot(
                    _nicName,
                    _selfId,
                    _selfIdSource,
                    new List<EnterWorldRecord>(_enterWorldRecords),
                    new List<DamageRecord>(_damageRecords));
            }
        }
    }

    public sealed class DebugSnapshot
    {
        public string NicName { get; }
        public ulong SelfId { get; }
        public string SelfIdSource { get; }
        public IReadOnlyList<EnterWorldRecord> EnterWorldRecords { get; }
        public IReadOnlyList<DamageRecord> DamageRecords { get; }

        public DebugSnapshot(
            string nicName,
            ulong selfId,
            string selfIdSource,
            IReadOnlyList<EnterWorldRecord> enterWorldRecords,
            IReadOnlyList<DamageRecord> damageRecords)
        {
            NicName = nicName;
            SelfId = selfId;
            SelfIdSource = selfIdSource;
            EnterWorldRecords = enterWorldRecords;
            DamageRecords = damageRecords;
        }
    }

    public sealed class EnterWorldRecord
    {
        public DateTime Time { get; }
        public ulong PlayerId { get; }

        public EnterWorldRecord(DateTime time, ulong playerId)
        {
            Time = time;
            PlayerId = playerId;
        }
    }

    public sealed class DamageRecord
    {
        public DateTime Time { get; }
        public ulong UserId { get; }
        public ulong TargetId { get; }
        public long Damage { get; }

        public DamageRecord(DateTime time, ulong userId, ulong targetId, long damage)
        {
            Time = time;
            UserId = userId;
            TargetId = targetId;
            Damage = damage;
        }
    }
}
