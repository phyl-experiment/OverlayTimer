using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClickMacro;

public class ScenarioConfig
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("steps")]
    public List<StepConfig> Steps { get; set; } = [];

    private List<RepeatStepViewModel>? _repeatSteps;
    [JsonIgnore]
    public List<RepeatStepViewModel> RepeatSteps =>
        _repeatSteps ??= Steps
            .Where(s => s.Type == "repeat")
            .Select((s, i) => new RepeatStepViewModel(s, i + 1))
            .ToList();

    public static ScenarioConfig Load(string path)
    {
        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<ScenarioConfig>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new ScenarioConfig();
        if (string.IsNullOrWhiteSpace(config.Name))
            config.Name = Path.GetFileNameWithoutExtension(path);
        return config;
    }

    public static void Save(string path, string name, List<StepConfig> steps)
    {
        var config = new ScenarioConfig { Name = name, Steps = steps };
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }
}

public class RepeatStepViewModel
{
    private readonly StepConfig _step;
    public string Label { get; }

    public string DurationSecs
    {
        get => (_step.DurationMs / 1000.0).ToString("0.##");
        set
        {
            if (double.TryParse(value, out double secs) && secs > 0)
                _step.DurationMs = (int)(secs * 1000);
        }
    }

    public RepeatStepViewModel(StepConfig step, int index)
    {
        _step = step;
        Label = $"반복 {index}";
    }
}
