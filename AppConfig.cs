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

        [JsonPropertyName("buffKeys")]
        public uint[] BuffKeys { get; set; } = [1590198662u, 2024838942u];

        [JsonPropertyName("timer")]
        public TimerConfig Timer { get; set; } = new();

        public static AppConfig Load()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "config.json");
            if (!File.Exists(path))
                return new AppConfig();

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
        }
    }

    public sealed class NetworkConfig
    {
        [JsonPropertyName("targetPort")]
        public int TargetPort { get; set; } = 16000;

        // null이면 자동 선택 (Ethernet 우선). 어댑터가 여러 개일 때 Description 부분 문자열로 지정.
        // 예: "Intel(R) Ethernet", "Realtek"
        [JsonPropertyName("deviceName")]
        public string? DeviceName { get; set; } = null;
    }

    public sealed class ProtocolConfig
    {
        // 공백/하이픈 구분자 허용. 예: "80 4E 00 00 00 00 00 00 00"
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

        [JsonPropertyName("enterWorld")]
        public int EnterWorld { get; set; } = 101059;
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
}
