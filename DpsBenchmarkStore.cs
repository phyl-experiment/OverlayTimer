using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using OverlayTimer.Net;

namespace OverlayTimer
{
    /// <summary>
    /// 2분 DPS 벤치마크 기록을 JSON 파일로 저장/로드한다.
    /// 저장 위치: 실행 파일 기준 dps_records/ 폴더.
    /// </summary>
    public sealed class DpsBenchmarkStore
    {
        private static readonly string Folder = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "dps_records");

        private static readonly JsonSerializerOptions WriteOptions = new()
        {
            WriteIndented = true
        };

        public void Save(DpsBenchmarkRecord record)
        {
            Directory.CreateDirectory(Folder);

            // 로컬 시각 기준 파일명
            var local = record.RecordedAt.ToLocalTime();
            var fileName = $"dps_{local:yyyyMMdd_HHmmss}.json";
            var path = Path.Combine(Folder, fileName);

            var json = JsonSerializer.Serialize(record, WriteOptions);
            File.WriteAllText(path, json);
        }

        public IReadOnlyList<DpsBenchmarkRecord> LoadAll()
        {
            if (!Directory.Exists(Folder))
                return Array.Empty<DpsBenchmarkRecord>();

            var records = new List<DpsBenchmarkRecord>();

            foreach (var file in Directory.GetFiles(Folder, "dps_*.json")
                         .OrderByDescending(f => f))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var record = JsonSerializer.Deserialize<DpsBenchmarkRecord>(json);
                    if (record != null)
                        records.Add(record);
                }
                catch
                {
                    // 손상된 파일은 건너뜀
                }
            }

            return records;
        }
    }
}
