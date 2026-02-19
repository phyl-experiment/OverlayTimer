namespace OverlayTimer.Net
{
    public sealed class NoopTimerTrigger : ITimerTrigger
    {
        public static readonly NoopTimerTrigger Instance = new();

        private NoopTimerTrigger()
        {
        }

        public void On()
        {
            // intentionally no-op
        }
    }
}
