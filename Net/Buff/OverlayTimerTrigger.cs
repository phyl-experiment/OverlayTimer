using System;
using System.IO;
using System.Media;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace OverlayTimer.Net
{
    public sealed class OverlayTriggerTimer : ITimerTrigger
    {
        private enum Phase { Idle, Active, Cooldown }

        private static readonly Brush _activeColor = MakeBrush(100, 220, 120);   // 초록
        private static readonly Brush _cooldownColor = MakeBrush(255, 100, 100); // 빨강
        private static readonly Brush _readyColor = MakeBrush(180, 180, 180);    // 회색

        private static Brush MakeBrush(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        private readonly Dispatcher _ui;
        private readonly OverlayTimerWindow _window;

        private readonly DispatcherTimer _tick;
        private DateTime _phaseStartUtc;
        private TimeSpan _phaseDuration;

        private Phase _phase = Phase.Idle;

        private readonly TimeSpan _defaultActiveDuration;
        private readonly TimeSpan _cooldownShort;
        private readonly TimeSpan _cooldownLong;
        private TimeSpan _manualCooldownDuration;

        private TimeSpan _currentActiveDuration;
        private TimeSpan _currentCooldownDuration;
        private bool _runtimeCooldownForCurrentCycle;
        private readonly SoundPlayer? _triggerSoundPlayer;

        /// <summary>쿨타임이 토글될 때 호출. 인자: isShort (true=32s, false=70s)</summary>
        public Action<bool>? OnCooldownToggled;

        public OverlayTriggerTimer(OverlayTimerWindow window, TimerConfig timer, SoundConfig sound)
        {
            _window = window;
            _ui = window.Dispatcher;
            _window.PreviewMouseRightButtonDown += OnWindowPreviewRightDown;

            _defaultActiveDuration = TimeSpan.FromSeconds(timer.ActiveDurationSeconds);
            _cooldownShort = TimeSpan.FromSeconds(timer.CooldownShortSeconds);
            _cooldownLong = TimeSpan.FromSeconds(timer.CooldownLongSeconds);
            _manualCooldownDuration = timer.UseShortCooldown ? _cooldownShort : _cooldownLong;

            _currentActiveDuration = _defaultActiveDuration;
            _currentCooldownDuration = _manualCooldownDuration;
            _triggerSoundPlayer = CreateSoundPlayer(sound);

            _tick = new DispatcherTimer(DispatcherPriority.Render, _ui)
            {
                Interval = TimeSpan.FromMilliseconds(33)
            };
            _tick.Tick += (_, __) => UpdateUi();

            SetReadyUi();
        }

        private void OnWindowPreviewRightDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == 0)
                return;

            ToggleCooldownDuration();
            e.Handled = true;
        }

        public bool On(TimerTriggerRequest request)
        {
            if (_ui.CheckAccess())
                return TryStartFromRequest(request);

            return _ui.Invoke(() => TryStartFromRequest(request));
        }

        private bool TryStartFromRequest(TimerTriggerRequest request)
        {
            if (_phase == Phase.Active)
                return false;

            var activeDuration = NormalizeDuration(request.ActiveDuration, _defaultActiveDuration);

            // 버프 지속시간이 기본값과 다를 때 delta만큼 쿨타임을 보정한다.
            // 소급 적용(retroactive)처럼 adjustCooldown이 false인 경우는 manual 값 그대로 사용.
            TimeSpan cooldownDuration;
            if (request.AdjustCooldownForActiveDuration)
            {
                var delta = activeDuration - _defaultActiveDuration;
                cooldownDuration = _manualCooldownDuration - delta;
                if (cooldownDuration < TimeSpan.FromSeconds(1))
                    cooldownDuration = TimeSpan.FromSeconds(1);
                _runtimeCooldownForCurrentCycle = (delta != TimeSpan.Zero);
            }
            else
            {
                cooldownDuration = _manualCooldownDuration;
                _runtimeCooldownForCurrentCycle = false;
            }

            _currentActiveDuration = activeDuration;
            _currentCooldownDuration = cooldownDuration;

            StartPhase(Phase.Active, _currentActiveDuration);
            if (request.AllowSound)
                PlayTriggerSound();

            return true;
        }

        private void StartPhase(Phase phase, TimeSpan duration)
        {
            _phase = phase;
            _phaseStartUtc = DateTime.UtcNow;
            _phaseDuration = duration;

            _window.Show();
            UpdatePhaseAppearance();

            if (!_tick.IsEnabled)
                _tick.Start();

            UpdateUi();
        }

        private void UpdatePhaseAppearance()
        {
            var (label, detail, color) = _phase switch
            {
                Phase.Active   => ("ACTIVE",   $"CD {(int)_currentCooldownDuration.TotalSeconds}s", _activeColor),
                Phase.Cooldown => ("COOLDOWN",  $"{(int)_currentCooldownDuration.TotalSeconds}s",    _cooldownColor),
                _              => ("READY",     $"CD {(int)_manualCooldownDuration.TotalSeconds}s",  _readyColor),
            };
            _window.SetPhaseLabel(label);
            _window.SetDetail(detail);
            _window.SetPhaseColor(color);
        }

        private void UpdateUi()
        {
            if (_phase == Phase.Idle)
            {
                _tick.Stop();
                SetReadyUi();
                return;
            }

            var elapsed = DateTime.UtcNow - _phaseStartUtc;
            var remaining = _phaseDuration - elapsed;
            if (remaining < TimeSpan.Zero)
                remaining = TimeSpan.Zero;

            double progress01 = 1.0 - (elapsed.TotalSeconds / _phaseDuration.TotalSeconds);
            if (progress01 < 0)
                progress01 = 0;
            if (progress01 > 1)
                progress01 = 1;

            _window.SetTime($"{remaining.TotalSeconds:0.0}s");
            _window.SetProgress(progress01);

            if (remaining <= TimeSpan.Zero)
            {
                if (_phase == Phase.Active)
                {
                    StartPhase(Phase.Cooldown, _currentCooldownDuration);
                    return;
                }

                if (_phase == Phase.Cooldown)
                {
                    _phase = Phase.Idle;
                    _tick.Stop();
                    SetReadyUi();
                }
            }
        }

        public void ToggleCooldownDuration()
        {
            _ui.BeginInvoke(() =>
            {
                _manualCooldownDuration = (_manualCooldownDuration == _cooldownShort)
                    ? _cooldownLong
                    : _cooldownShort;

                OnCooldownToggled?.Invoke(_manualCooldownDuration == _cooldownShort);

                if (_phase == Phase.Cooldown)
                {
                    _runtimeCooldownForCurrentCycle = false;
                    _currentCooldownDuration = _manualCooldownDuration;

                    var elapsed = DateTime.UtcNow - _phaseStartUtc;
                    _phaseDuration = _currentCooldownDuration;

                    if (elapsed >= _phaseDuration)
                        _phaseStartUtc = DateTime.UtcNow - _phaseDuration;

                    UpdatePhaseAppearance();
                    UpdateUi();
                    return;
                }

                if (_phase == Phase.Active && !_runtimeCooldownForCurrentCycle)
                {
                    _currentCooldownDuration = _manualCooldownDuration;
                }

                UpdatePhaseAppearance();
            });
        }

        private static TimeSpan NormalizeDuration(TimeSpan candidate, TimeSpan fallback)
        {
            if (candidate <= TimeSpan.Zero || candidate > TimeSpan.FromMinutes(10))
                return fallback;

            return candidate;
        }

        private void SetReadyUi()
        {
            UpdatePhaseAppearance();
            _window.SetTime("0.0s");
            _window.SetProgress(0);
        }

        private static SoundPlayer? CreateSoundPlayer(SoundConfig sound)
        {
            if (!sound.Enabled)
                return null;

            string relativePath = string.IsNullOrWhiteSpace(sound.TriggerFile)
                ? "assets/sounds/timer-trigger.wav"
                : sound.TriggerFile;

            string fullPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, relativePath));
            if (!File.Exists(fullPath))
            {
                LogHelper.Write($"[Sound] Trigger file not found: {fullPath}");
                return null;
            }

            try
            {
                var player = new SoundPlayer(fullPath);
                player.Load();
                return player;
            }
            catch (Exception ex)
            {
                LogHelper.Write($"[Sound] Trigger file load failed: {ex.Message}");
                return null;
            }
        }

        private void PlayTriggerSound()
        {
            if (_triggerSoundPlayer == null)
                return;

            try
            {
                _triggerSoundPlayer.Play();
            }
            catch (Exception ex)
            {
                LogHelper.Write($"[Sound] Trigger playback failed: {ex.Message}");
            }
        }
    }
}
