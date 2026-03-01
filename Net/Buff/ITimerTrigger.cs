using System;

namespace OverlayTimer.Net
{
    public readonly record struct TimerTriggerRequest(
        uint BuffKey,
        TimeSpan ActiveDuration,
        bool AdjustCooldownForActiveDuration = false,
        bool AllowSound = true);

    public interface ITimerTrigger
    {
        bool On(TimerTriggerRequest request);
    }
}
