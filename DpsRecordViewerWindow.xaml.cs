using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using OverlayTimer.Net;

namespace OverlayTimer
{
    public partial class DpsRecordViewerWindow : Window
    {
        private readonly DpsBenchmarkStore _store;
        private readonly IReadOnlyDictionary<uint, string> _skillNames;
        private readonly IReadOnlyDictionary<uint, string> _buffNames;

        public DpsRecordViewerWindow(
            DpsBenchmarkStore store,
            IReadOnlyDictionary<uint, string> skillNames,
            IReadOnlyDictionary<uint, string> buffNames)
        {
            InitializeComponent();
            _store = store;
            _skillNames = skillNames;
            _buffNames = buffNames;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            LoadRecords();
        }

        public void Reload() => LoadRecords();

        private void LoadRecords()
        {
            var records = _store.LoadAll();

            if (records.Count == 0)
            {
                RecordList.Visibility = Visibility.Collapsed;
                NoRecordsText.Visibility = Visibility.Visible;
                return;
            }

            RecordList.Visibility = Visibility.Visible;
            NoRecordsText.Visibility = Visibility.Collapsed;

            RecordList.ItemsSource = records.Select(r => new RecordListRow(r)).ToList();
        }

        private void RecordList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (RecordList.SelectedItem is not RecordListRow row)
            {
                DetailPanel.Visibility = Visibility.Collapsed;
                // "기록을 선택하세요"
                DetailHeader.Text = "\uAE30\uB85D\uC744 \uC120\uD0DD\uD558\uC138\uC694";
                return;
            }

            ShowDetail(row.Record);
        }

        private void ShowDetail(DpsBenchmarkRecord record)
        {
            var local = record.RecordedAt.ToLocalTime();
            // "{date}  측정 완료"
            DetailHeader.Text = $"{local:yyyy-MM-dd HH:mm:ss}  \uCE21\uC815 \uC644\uB8CC";
            DetailPanel.Visibility = Visibility.Visible;

            SummaryDps.Text = FormatMan(record.TotalDps);
            SummaryDamage.Text = FormatMan(record.TotalDamage);
            SummaryDuration.Text = $"{record.DurationSeconds:0.0}s";

            double critPct = record.HitCount > 0 ? record.CritCount * 100.0 / record.HitCount : 0.0;
            // "타격: N  크리: N (N%)  추가타: N  강타: N  연타: N"
            SummaryFlags.Text =
                $"\uD0C0\uACA9: {record.HitCount}  " +
                $"\uD06C\uB9AC: {record.CritCount} ({critPct:0.0}%)  " +
                $"\uCD94\uAC00\uD0C0: {record.AddHitCount}  " +
                $"\uAC15\uD0C0: {record.PowerCount}  " +
                $"\uC5F0\uD0C0: {record.FastCount}";

            // 대상별 데미지
            DetailTargets.ItemsSource = record.Targets.Select(t => new TargetDetailRow
            {
                TargetLabel = $"Target {t.TargetId}",
                DpsLabel = FormatMan(t.Dps),
                DamageLabel = FormatMan(t.Damage)
            }).ToList();

            // 스킬별 통계
            // "크리:N%  추가:N%  강타:N%  연타:N%"
            DetailSkills.ItemsSource = record.Skills.Select(s => new SkillDetailRow
            {
                SkillLabel = string.IsNullOrWhiteSpace(s.SkillName)
                    ? ResolveSkillLabel(s.SkillType)
                    : s.SkillName,
                RatioLabel = $"{s.DamageRatio:0.0}%  ({s.HitCount}\uD0C0)",
                FlagLabel =
                    $"\uD06C\uB9AC:{s.CritRate:0.0}%  " +
                    $"\uCD94\uAC00:{s.AddHitRate:0.0}%  " +
                    $"\uAC15\uD0C0:{s.PowerRate:0.0}%  " +
                    $"\uC5F0\uD0C0:{s.FastRate:0.0}%"
            }).ToList();

            // 버프 가동률
            // "Ns  (N%)"
            DetailBuffs.ItemsSource = record.Buffs.Select(b => new BuffDetailRow
            {
                BuffLabel = string.IsNullOrWhiteSpace(b.BuffName)
                    ? ResolveBuffLabel(b.BuffKey)
                    : b.BuffName,
                UptimeLabel = $"{b.TotalSeconds:0.0}s  ({b.UptimePct:0.0}%)"
            }).ToList();
        }

        private string ResolveSkillLabel(uint skillType)
        {
            if (_skillNames.TryGetValue(skillType, out var name) && !string.IsNullOrWhiteSpace(name))
                return name;

            if (DpsSkillClassifier.TryGetSpecialSkillName(skillType, out var special))
                return special;

            return $"Skill {skillType}";
        }

        private string ResolveBuffLabel(uint buffKey)
        {
            if (_buffNames.TryGetValue(buffKey, out var name) && !string.IsNullOrWhiteSpace(name))
                return name;

            return $"Buff {buffKey}";
        }

        private static string FormatMan(double value)
        {
            const double Eok = 100_000_000.0;
            const double Jo = 1_000_000_000_000.0;

            if (!double.IsFinite(value))
                return "0.0\uB9CC";

            double abs = Math.Abs(value);
            if (abs >= Jo) return $"{value / Jo:0.00}\uC870";
            if (abs >= Eok) return $"{value / Eok:0.00}\uC5B5";
            return $"{value / 10000.0:0.0}\uB9CC";
        }

        private static string FormatMan(long value) => FormatMan((double)value);

        // ---------------------------------------------------------------------------
        // Row types for ItemsControl bindings
        // ---------------------------------------------------------------------------

        private sealed class RecordListRow
        {
            public DpsBenchmarkRecord Record { get; }
            public string DateLabel { get; }
            public string DpsLabel { get; }
            public string DamageLabel { get; }

            public RecordListRow(DpsBenchmarkRecord record)
            {
                Record = record;
                var local = record.RecordedAt.ToLocalTime();
                DateLabel = local.ToString("yyyy-MM-dd  HH:mm:ss");
                DpsLabel = $"DPS  {FormatMan(record.TotalDps)}";
                // "총 데미지  N"
                DamageLabel = $"\uCD1D \uB370\uBBF8\uC9C0  {FormatMan(record.TotalDamage)}";
            }

            private static string FormatMan(double value)
            {
                const double Eok = 100_000_000.0;
                const double Jo = 1_000_000_000_000.0;
                if (!double.IsFinite(value)) return "0.0\uB9CC";
                double abs = Math.Abs(value);
                if (abs >= Jo) return $"{value / Jo:0.00}\uC870";
                if (abs >= Eok) return $"{value / Eok:0.00}\uC5B5";
                return $"{value / 10000.0:0.0}\uB9CC";
            }
        }

        private sealed class TargetDetailRow
        {
            public string TargetLabel { get; set; } = string.Empty;
            public string DpsLabel { get; set; } = string.Empty;
            public string DamageLabel { get; set; } = string.Empty;
        }

        private sealed class SkillDetailRow
        {
            public string SkillLabel { get; set; } = string.Empty;
            public string RatioLabel { get; set; } = string.Empty;
            public string FlagLabel { get; set; } = string.Empty;
        }

        private sealed class BuffDetailRow
        {
            public string BuffLabel { get; set; } = string.Empty;
            public string UptimeLabel { get; set; } = string.Empty;
        }
    }
}
