using System;
using System.Media;
using System.Windows.Threading;

namespace OverlayTimer.Net
{
    public sealed class OverlayTriggerTimer : ITimerTrigger
    {
        private enum Phase { Idle, Active, Cooldown }

        private readonly Dispatcher _ui;
        private readonly OverlayTimerWindow _window;

        private readonly DispatcherTimer _tick;
        private DateTime _phaseStartUtc;
        private TimeSpan _phaseDuration;

        private Phase _phase = Phase.Idle;

        private readonly TimeSpan _activeDuration;
        private readonly TimeSpan _cooldown32;
        private readonly TimeSpan _cooldown70;
        private TimeSpan _cooldownDuration;

        public OverlayTriggerTimer(OverlayTimerWindow window, TimerConfig timer)
        {
            _window = window;
            _ui = window.Dispatcher;
            _window.PreviewMouseRightButtonDown += OnWindowPreviewRightDown;

            _activeDuration = TimeSpan.FromSeconds(timer.ActiveDurationSeconds);
            _cooldown32 = TimeSpan.FromSeconds(timer.CooldownShortSeconds);
            _cooldown70 = TimeSpan.FromSeconds(timer.CooldownLongSeconds);
            _cooldownDuration = _cooldown70;

            _tick = new DispatcherTimer(DispatcherPriority.Render, _ui)
            {
                Interval = TimeSpan.FromMilliseconds(33) // 30fps 정도
            };
            _tick.Tick += (_, __) => UpdateUi();
        }

        private void OnWindowPreviewRightDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Ctrl+우클릭만 토글 (드래그용 Ctrl+좌클릭과 안 겹침)
            if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == 0)
                return;

            ToggleCooldownDuration();
            e.Handled = true;
        }

        // 패킷에서 "버프 ON"으로 판정됐을 때 호출
        public void On()
        {
            _ui.BeginInvoke(() =>
            {
                // 이미 Active면 중복 ON 무시 (깜빡임 방지 핵심)
                if (_phase == Phase.Active) return;

                SystemSounds.Asterisk.Play();
                StartPhase(Phase.Active, _activeDuration, $"Active ({(int)_activeDuration.TotalSeconds}s)");
            });
        }

        private void StartPhase(Phase phase, TimeSpan duration, string modeText)
        {
            _phase = phase;
            _phaseStartUtc = DateTime.UtcNow;
            _phaseDuration = duration;

            _window.Show();
            _window.SetMode(modeText);

            if (!_tick.IsEnabled)
                _tick.Start();

            UpdateUi();
        }

        private void UpdateUi()
        {
            if (_phase == Phase.Idle)
            {
                _tick.Stop();
                _window.Hide();
                return;
            }

            var elapsed = DateTime.UtcNow - _phaseStartUtc;
            var remaining = _phaseDuration - elapsed;
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;

            double progress01 = 1.0 - (elapsed.TotalSeconds / _phaseDuration.TotalSeconds);
            if (progress01 < 0) progress01 = 0;
            if (progress01 > 1) progress01 = 1;

            _window.SetTime($"{remaining.TotalSeconds:0.0}s");
            _window.SetProgress(progress01);

            // Phase 완료 처리
            if (remaining <= TimeSpan.Zero)
            {
                if (_phase == Phase.Active)
                {
                    StartPhase(Phase.Cooldown, _cooldownDuration, $"Cooldown ({(int)_cooldownDuration.TotalSeconds}s)");
                    return;
                }

                if (_phase == Phase.Cooldown)
                {
                    _phase = Phase.Idle;
                    _tick.Stop();
                    _window.Hide();
                }
            }
        }

        public void ToggleCooldownDuration()
        {
            _ui.BeginInvoke(() =>
            {
                _cooldownDuration = (_cooldownDuration == _cooldown32) ? _cooldown70 : _cooldown32;

                if (_phase == Phase.Cooldown)
                {
                    var elapsed = DateTime.UtcNow - _phaseStartUtc;
                    _phaseDuration = _cooldownDuration;

                    if (elapsed >= _phaseDuration)
                        _phaseStartUtc = DateTime.UtcNow - _phaseDuration;
                }

                _window.SetMode($"Cooldown ({(int)_cooldownDuration.TotalSeconds}s)");
                UpdateUi();
            });
        }
    }
}
