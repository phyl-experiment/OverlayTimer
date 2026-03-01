using System;

namespace OverlayTimer.Net
{
    public readonly record struct TimerTriggerRequest(
        uint BuffKey,
        TimeSpan ActiveDuration,
        TimeSpan? CooldownDuration = null,
        bool AllowSound = true);

    public interface ITimerTrigger
    {
        bool On(TimerTriggerRequest request);
    }
}
