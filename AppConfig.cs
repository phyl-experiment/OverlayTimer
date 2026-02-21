using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OverlayTimer
{
    public sealed class AppConfig
    {
        [JsonPropertyName("network")]
        public NetworkConfig Network { get; set; } = new();

        [JsonPropertyName("protocol")]
        public ProtocolConfig Protocol { get; set; } = new();

        [JsonPropertyName("packetTypes")]
        public PacketTypesConfig PacketTypes { get; set; } = new();

        [JsonPropertyName("awakeningBuffKeys")]
        public uint[] AwakeningBuffKeys { get; set; } = [1590198662u, 2024838942u, 1184371696u];

        // legacy alias. If awakeningBuffKeys is empty, this value is used as a fallback.
        [JsonPropertyName("buffKeys")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public uint[]? LegacyBuffKeys { get; set; }

        [JsonIgnore]
        public uint[] TimerBuffKeys =>
            AwakeningBuffKeys is { Length: > 0 }
                ? AwakeningBuffKeys
                : (LegacyBuffKeys ?? Array.Empty<uint>());

        [JsonPropertyName("timer")]
        public TimerConfig Timer { get; set; } = new();

        [JsonPropertyName("overlays")]
        public OverlaysConfig Overlays { get; set; } = new();

        [JsonPropertyName("sound")]
        public SoundConfig Sound { get; set; } = new();

        [JsonPropertyName("logging")]
        public LoggingConfig Logging { get; set; } = new();

        private static readonly JsonSerializerOptions _writeOptions = new() { WriteIndented = true };

        public static AppConfig Load()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "config.json");
            if (!File.Exists(path))
                return new AppConfig();

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
        }

        public void Save()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "config.json");
            var json = JsonSerializer.Serialize(this, _writeOptions);
            File.WriteAllText(path, json);
        }
    }

    public sealed class NetworkConfig
    {
        [JsonPropertyName("targetPort")]
        public int TargetPort { get; set; } = 16000;

        [JsonPropertyName("captureFilter")]
        public string? CaptureFilter { get; set; } = null;

        [JsonPropertyName("autoReselect")]
        public bool AutoReselect { get; set; } = true;

        // null?????癒?짗 ?醫뤾문 (Ethernet ?怨쀪퐨). ????怨? ????揶쏆뮇????Description ?봔???얜챷???以?筌왖??
        // ?? "Intel(R) Ethernet", "Realtek"
        [JsonPropertyName("deviceName")]
        public string? DeviceName { get; set; } = null;
    }

    public sealed class ProtocolConfig
    {
        // ?⑤벉媛???륁뵠???닌됲뀋????됱뒠. ?? "80 4E 00 00 00 00 00 00 00"
        [JsonPropertyName("startMarker")]
        public string StartMarker { get; set; } = "80 4E 00 00 00 00 00 00 00";

        [JsonPropertyName("endMarker")]
        public string EndMarker { get; set; } = "12 4F 00 00 00 00 00 00 00";

        [JsonIgnore]
        public byte[] StartMarkerBytes => ParseHex(StartMarker);

        [JsonIgnore]
        public byte[] EndMarkerBytes => ParseHex(EndMarker);

        private static byte[] ParseHex(string hex)
        {
            hex = hex.Replace(" ", "").Replace("-", "");
            var result = new byte[hex.Length / 2];
            for (int i = 0; i < result.Length; i++)
                result[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return result;
        }
    }

    public sealed class PacketTypesConfig
    {
        [JsonPropertyName("buffStart")]
        public int BuffStart { get; set; } = 100054;

        [JsonPropertyName("buffEnd")]
        public int BuffEnd { get; set; } = 100055;

        [JsonPropertyName("enterWorld")]
        public int EnterWorld { get; set; } = 101059;

        [JsonPropertyName("dpsAttack")]
        public int DpsAttack { get; set; } = 20389;

        [JsonPropertyName("dpsDamage")]
        public int DpsDamage { get; set; } = 20897;
    }

    public sealed class TimerConfig
    {
        [JsonPropertyName("activeDurationSeconds")]
        public int ActiveDurationSeconds { get; set; } = 20;

        [JsonPropertyName("cooldownShortSeconds")]
        public int CooldownShortSeconds { get; set; } = 32;

        [JsonPropertyName("cooldownLongSeconds")]
        public int CooldownLongSeconds { get; set; } = 70;
    }

    public sealed class SoundConfig
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        [JsonPropertyName("triggerFile")]
        public string TriggerFile { get; set; } = "assets/sounds/timer-trigger.wav";
    }

    public sealed class LoggingConfig
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = false;

        [JsonPropertyName("packetHeaders")]
        public bool PacketHeaders { get; set; } = false;

        [JsonPropertyName("captureStatsIntervalSeconds")]
        public int CaptureStatsIntervalSeconds { get; set; } = 10;
    }

    public sealed class OverlaysConfig
    {
        [JsonPropertyName("timer")]
        public OverlayConfig Timer { get; set; } = new()
        {
            Enabled = true,
            X = 500,
            Y = 60
        };

        [JsonPropertyName("dps")]
        public OverlayConfig Dps { get; set; } = new()
        {
            Enabled = true,
            X = 760,
            Y = 60
        };
    }

    public sealed class OverlayConfig
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        [JsonPropertyName("x")]
        public double X { get; set; } = 0;

        [JsonPropertyName("y")]
        public double Y { get; set; } = 0;
    }

    public static class BuffNameMap
    {
        public static IReadOnlyDictionary<uint, string> Load()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "buff_names.json");
            if (!File.Exists(path))
                return new Dictionary<uint, string>();

            try
            {
                var json = File.ReadAllText(path);
                var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (raw == null)
                    return new Dictionary<uint, string>();

                var result = new Dictionary<uint, string>(raw.Count);
                foreach (var kv in raw)
                {
                    if (uint.TryParse(kv.Key, out var id))
                        result[id] = kv.Value;
                }
                return result;
            }
            catch
            {
                return new Dictionary<uint, string>();
            }
        }
    }

    public static class SkillNameMap
    {
        public static IReadOnlyDictionary<uint, string> Load()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "skill_names.json");
            if (!File.Exists(path))
                return new Dictionary<uint, string>();

            try
            {
                var json = File.ReadAllText(path);
                var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (raw == null)
                    return new Dictionary<uint, string>();

                var result = new Dictionary<uint, string>(raw.Count);
                foreach (var kv in raw)
                {
                    if (uint.TryParse(kv.Key, out var id))
                        result[id] = kv.Value;
                }
                return result;
            }
            catch
            {
                return new Dictionary<uint, string>();
            }
        }
    }
}
