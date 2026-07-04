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
    public int AfterDelayMs { get; set; }

    [JsonPropertyName("intervalMs")]
    public int IntervalMs { get; set; } = 1000;

    [JsonPropertyName("durationMs")]
    public int DurationMs { get; set; }
}
