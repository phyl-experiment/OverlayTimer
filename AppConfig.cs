using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Reflection;

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

        [JsonPropertyName("selfId")]
        public SelfIdConfig SelfId { get; set; } = new();

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
            NormalizeForSave();
            var path = Path.Combine(AppContext.BaseDirectory, "config.json");
            var json = JsonSerializer.Serialize(this, _writeOptions);
            File.WriteAllText(path, json);
        }

        private void NormalizeForSave()
        {
            NormalizeOverlay(Overlays.Timer);
            NormalizeOverlay(Overlays.Dps);
        }

        private static void NormalizeOverlay(OverlayConfig overlay)
        {
            overlay.X = SanitizePosition(overlay.X);
            overlay.Y = SanitizePosition(overlay.Y);
            overlay.Width = SanitizeSize(overlay.Width);
            overlay.Height = SanitizeSize(overlay.Height);
        }

        private static double SanitizePosition(double value)
        {
            return double.IsFinite(value) ? value : 0;
        }

        private static double? SanitizeSize(double? value)
        {
            if (!value.HasValue)
                return null;

            double v = value.Value;
            return double.IsFinite(v) && v > 0 ? v : null;
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
        [JsonPropertyName("startMarker")]
        public Confirmable<string> StartMarker { get; set; } = new("82 4E 00 00 00 00 00 00 00", confirmed: true);

        [JsonPropertyName("endMarker")]
        public Confirmable<string> EndMarker { get; set; } = new("18 4F 00 00 00 00 00 00 00", confirmed: true);

        [JsonIgnore]
        public byte[] StartMarkerBytes => ParseHex(StartMarker.Value);

        [JsonIgnore]
        public byte[] EndMarkerBytes => ParseHex(EndMarker.Value);

        internal static byte[] ParseHex(string hex)
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
        public Confirmable<int> BuffStart { get; set; } = new(100055, confirmed: true);

        [JsonPropertyName("buffEnd")]
        public Confirmable<int> BuffEnd { get; set; } = new(100056, confirmed: true);

        [JsonPropertyName("enterWorld")]
        public Confirmable<int> EnterWorld { get; set; } = new(101072, confirmed: true);

        [JsonPropertyName("dpsAttack")]
        public Confirmable<int> DpsAttack { get; set; } = new(20389, confirmed: false);

        [JsonPropertyName("dpsDamage")]
        public Confirmable<int> DpsDamage { get; set; } = new(20897, confirmed: false);
    }

    public sealed class TimerConfig
    {
        [JsonPropertyName("activeDurationSeconds")]
        public int ActiveDurationSeconds { get; set; } = 20;

        [JsonPropertyName("cooldownShortSeconds")]
        public int CooldownShortSeconds { get; set; } = 32;

        [JsonPropertyName("cooldownLongSeconds")]
        public int CooldownLongSeconds { get; set; } = 70;

        /// <summary>마지막으로 선택된 쿨타임. true = 32s, false = 70s.</summary>
        [JsonPropertyName("useShortCooldown")]
        public bool UseShortCooldown { get; set; } = false;

        [JsonPropertyName("colors")]
        public TimerColorsConfig Colors { get; set; } = new();

        /// <summary>라벨 텍스트(Active/Cooldown 등) 추가 배율. 1.0 = 기본 크기.</summary>
        [JsonPropertyName("labelFontScale")]
        public double LabelFontScale { get; set; } = 1.0;

        /// <summary>메인(초) 텍스트 추가 배율. 1.0 = 기본 크기.</summary>
        [JsonPropertyName("timeFontScale")]
        public double TimeFontScale { get; set; } = 1.0;

        /// <summary>디테일 텍스트 추가 배율. 1.0 = 기본 크기.</summary>
        [JsonPropertyName("detailFontScale")]
        public double DetailFontScale { get; set; } = 1.0;

        /// <summary>라벨 텍스트 FontWeight. "Normal", "SemiBold", "Bold" 등. 기본 Bold.</summary>
        [JsonPropertyName("labelFontWeight")]
        public string LabelFontWeight { get; set; } = "Bold";

        /// <summary>메인(초) 텍스트 FontWeight. 기본 Bold.</summary>
        [JsonPropertyName("timeFontWeight")]
        public string TimeFontWeight { get; set; } = "Bold";

        /// <summary>디테일 텍스트 FontWeight. 기본 Bold.</summary>
        [JsonPropertyName("detailFontWeight")]
        public string DetailFontWeight { get; set; } = "Bold";

        /// <summary>원형 링 테두리의 아웃라인 스타일.</summary>
        [JsonPropertyName("ringStyle")]
        public RingStyleConfig RingStyle { get; set; } = new();
    }

    public sealed class RingStyleConfig
    {
        /// <summary>링 외곽 아웃라인 두께 (픽셀). 0이면 아웃라인 없음.</summary>
        [JsonPropertyName("outlineThickness")]
        public double OutlineThickness { get; set; } = 2.0;

        [JsonPropertyName("outlineR")]
        public byte OutlineR { get; set; } = 0;

        [JsonPropertyName("outlineG")]
        public byte OutlineG { get; set; } = 0;

        [JsonPropertyName("outlineB")]
        public byte OutlineB { get; set; } = 0;

        /// <summary>아웃라인 불투명도 (0.0~1.0).</summary>
        [JsonPropertyName("outlineOpacity")]
        public double OutlineOpacity { get; set; } = 1.0;
    }

    public sealed class TimerColorsConfig
    {
        [JsonPropertyName("ready")]
        public TimerColorEntry Ready { get; set; } = new(255, 255, 255);

        [JsonPropertyName("active")]
        public TimerColorEntry Active { get; set; } = new(100, 255, 120);

        [JsonPropertyName("cooldown")]
        public TimerColorEntry Cooldown { get; set; } = new(255, 100, 100);
    }

    /// <summary>
    /// 텍스트 외곽선·그림자·불투명도 스타일 설정.
    /// TimerColorEntry의 메인(초) 텍스트, 라벨 텍스트, 디테일 텍스트에 각각 사용.
    /// </summary>
    public sealed class TextStyleConfig
    {
        [JsonPropertyName("outlineThickness")]
        public double OutlineThickness { get; set; }

        [JsonPropertyName("outlineR")]
        public byte OutlineR { get; set; }

        [JsonPropertyName("outlineG")]
        public byte OutlineG { get; set; }

        [JsonPropertyName("outlineB")]
        public byte OutlineB { get; set; }

        [JsonPropertyName("opacity")]
        public double Opacity { get; set; } = 1.0;

        [JsonPropertyName("shadowBlur")]
        public double ShadowBlur { get; set; }

        [JsonPropertyName("shadowDepth")]
        public double ShadowDepth { get; set; } = 2.0;

        [JsonPropertyName("shadowOpacity")]
        public double ShadowOpacity { get; set; } = 0.8;

        [JsonPropertyName("shadowR")]
        public byte ShadowR { get; set; }

        [JsonPropertyName("shadowG")]
        public byte ShadowG { get; set; }

        [JsonPropertyName("shadowB")]
        public byte ShadowB { get; set; }

        public TextStyleConfig() { }

        public TextStyleConfig(double outlineThickness, double shadowBlur, double shadowDepth)
        {
            OutlineThickness = outlineThickness;
            ShadowBlur = shadowBlur;
            ShadowDepth = shadowDepth;
        }
    }

    public sealed class TimerColorEntry
    {
        [JsonPropertyName("r")]
        public byte R { get; set; }

        [JsonPropertyName("g")]
        public byte G { get; set; }

        [JsonPropertyName("b")]
        public byte B { get; set; }

        /// <summary>메인(초) 텍스트 외곽선 두께 (픽셀). 0이면 외곽선 없음.</summary>
        [JsonPropertyName("outlineThickness")]
        public double OutlineThickness { get; set; } = 0.0;

        /// <summary>외곽선 색상 R (outlineThickness > 0일 때 사용). 기본값 0 = 검정.</summary>
        [JsonPropertyName("outlineR")]
        public byte OutlineR { get; set; } = 0;

        /// <summary>외곽선 색상 G.</summary>
        [JsonPropertyName("outlineG")]
        public byte OutlineG { get; set; } = 0;

        /// <summary>외곽선 색상 B.</summary>
        [JsonPropertyName("outlineB")]
        public byte OutlineB { get; set; } = 0;

        /// <summary>메인(초) 텍스트 불투명도 (0.0 = 완전 투명, 1.0 = 완전 불투명). 기본값 1.0.</summary>
        [JsonPropertyName("opacity")]
        public double Opacity { get; set; } = 1.0;

        /// <summary>그림자 흐림 반경 (픽셀). 0이면 그림자 없음.</summary>
        [JsonPropertyName("shadowBlur")]
        public double ShadowBlur { get; set; } = 0.0;

        /// <summary>그림자 거리 (픽셀). shadowBlur > 0일 때 사용.</summary>
        [JsonPropertyName("shadowDepth")]
        public double ShadowDepth { get; set; } = 2.0;

        /// <summary>그림자 불투명도 (0.0~1.0).</summary>
        [JsonPropertyName("shadowOpacity")]
        public double ShadowOpacity { get; set; } = 0.8;

        /// <summary>그림자 색상 R. 기본값 0 = 검정.</summary>
        [JsonPropertyName("shadowR")]
        public byte ShadowR { get; set; } = 0;

        /// <summary>그림자 색상 G.</summary>
        [JsonPropertyName("shadowG")]
        public byte ShadowG { get; set; } = 0;

        /// <summary>그림자 색상 B.</summary>
        [JsonPropertyName("shadowB")]
        public byte ShadowB { get; set; } = 0;

        /// <summary>상단 라벨 텍스트(Active/Cooldown 등) 스타일. null이면 기본값 사용.</summary>
        [JsonPropertyName("labelStyle")]
        public TextStyleConfig LabelStyle { get; set; } = new();

        /// <summary>하단 디테일 텍스트 스타일. null이면 기본값 사용.</summary>
        [JsonPropertyName("detailStyle")]
        public TextStyleConfig DetailStyle { get; set; } = new();

        public TimerColorEntry() { }
        public TimerColorEntry(byte r, byte g, byte b) { R = r; G = g; B = b; }
    }

    public sealed class SoundConfig
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        [JsonPropertyName("triggerFile")]
        public string TriggerFile { get; set; } = "assets/sounds/timer-trigger.wav";
    }

    public sealed class SelfIdConfig
    {
        /// <summary>
        /// true: selfId 미확정 시 첫 유효 데미지 패킷의 userId로 즉시 확정.
        /// false(기본): EnterWorld 패킷으로만 확정. 다른 플레이어 패킷이 섞일 때 사용.
        /// </summary>
        [JsonPropertyName("initialDamageFallback")]
        public bool InitialDamageFallback { get; set; } = false;

        /// <summary>
        /// true(기본): selfId 확정 후 다른 userId가 연속 N회 오면 덮어씀.
        /// false: 연속 데미지 덮어쓰기 비활성.
        /// </summary>
        [JsonPropertyName("consecutiveDamageOverride")]
        public bool ConsecutiveDamageOverride { get; set; } = false;
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

        [JsonPropertyName("width")]
        public double? Width { get; set; }

        [JsonPropertyName("height")]
        public double? Height { get; set; }
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

    /// <summary>
    /// 값과 confirmed 플래그를 함께 저장하는 래퍼.
    /// confirmed=true 인 값은 프로브 대상에서 제외된다.
    /// JSON 하위 호환: bare 값(string/int)은 confirmed=false 로 역직렬화된다.
    /// </summary>
    [JsonConverter(typeof(ConfirmableConverterFactory))]
    public sealed class Confirmable<T>
    {
        [JsonPropertyName("confirmed")]
        public bool Confirmed { get; set; }

        [JsonPropertyName("value")]
        public T Value { get; set; }

        public Confirmable() { Value = default!; }
        public Confirmable(T value, bool confirmed = false) { Value = value; Confirmed = confirmed; }
    }

    public sealed class ConfirmableConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert)
        {
            return typeToConvert.IsGenericType
                && typeToConvert.GetGenericTypeDefinition() == typeof(Confirmable<>);
        }

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        {
            var innerType = typeToConvert.GetGenericArguments()[0];
            var converterType = typeof(ConfirmableConverter<>).MakeGenericType(innerType);
            return (JsonConverter)Activator.CreateInstance(converterType)!;
        }
    }

    public sealed class ConfirmableConverter<T> : JsonConverter<Confirmable<T>>
    {
        public override Confirmable<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            // bare value (old format): string or number token
            if (reader.TokenType == JsonTokenType.String
                || reader.TokenType == JsonTokenType.Number)
            {
                var value = JsonSerializer.Deserialize<T>(ref reader, options)!;
                return new Confirmable<T>(value, confirmed: false);
            }

            // new format: { "confirmed": bool, "value": T }
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                bool confirmed = false;
                T value = default!;

                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndObject)
                        break;

                    if (reader.TokenType == JsonTokenType.PropertyName)
                    {
                        var prop = reader.GetString();
                        reader.Read();
                        if (string.Equals(prop, "confirmed", StringComparison.OrdinalIgnoreCase))
                            confirmed = reader.GetBoolean();
                        else if (string.Equals(prop, "value", StringComparison.OrdinalIgnoreCase))
                            value = JsonSerializer.Deserialize<T>(ref reader, options)!;
                        else
                            reader.Skip();
                    }
                }

                return new Confirmable<T>(value, confirmed);
            }

            throw new JsonException($"Unexpected token {reader.TokenType} for Confirmable<{typeof(T).Name}>");
        }

        public override void Write(Utf8JsonWriter writer, Confirmable<T> value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteBoolean("confirmed", value.Confirmed);
            writer.WritePropertyName("value");
            JsonSerializer.Serialize(writer, value.Value, options);
            writer.WriteEndObject();
        }
    }
}
