using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClickMacro;

public class MacroConfig
{
    [JsonPropertyName("targetProcess")]
    public string TargetProcess { get; set; } = "MabinogiMobile";

    public static MacroConfig Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<MacroConfig>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new MacroConfig();
    }
}

public class StepConfig
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "click";

    [JsonPropertyName("x")]
    public int X { get; set; }

    [JsonPropertyName("y")]
    public int Y { get; set; }

    [JsonPropertyName("afterDelayMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int AfterDelayMs { get; set; }

    [JsonPropertyName("intervalMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int IntervalMs { get; set; }

    [JsonPropertyName("durationMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int DurationMs { get; set; }
}
