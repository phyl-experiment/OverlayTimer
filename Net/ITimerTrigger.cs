using System;

namespace OverlayTimer.Net
{
    public readonly record struct TimerTriggerRequest(
        uint BuffKey,
        TimeSpan ActiveDuration,
        TimeSpan? CooldownDuration = null);

    public interface ITimerTrigger
    {
        void On(TimerTriggerRequest request);
    }
}
