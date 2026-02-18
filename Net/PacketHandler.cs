using System;

namespace OverlayTimer.Net
{
    public sealed class PacketHandler
    {
        private readonly ITimerTrigger _timerTrigger;
        private readonly SelfIdResolver _selfIdResolver;
        private readonly int _buffStartDataType;
        private readonly uint[] _buffKeys;

        public PacketHandler(ITimerTrigger timerTrigger, SelfIdResolver selfIdResolver, int buffStartDataType, uint[] buffKeys)
        {
            _timerTrigger = timerTrigger;
            _selfIdResolver = selfIdResolver;
            _buffStartDataType = buffStartDataType;
            _buffKeys = buffKeys;
        }

        public void OnPacket(int dataType, ReadOnlySpan<byte> content)
        {
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
                    LogHelper.Write($"{dataType}:{parsedId}");
            }
        }
    }
}
