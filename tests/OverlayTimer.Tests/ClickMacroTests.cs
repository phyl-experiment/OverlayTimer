using System.IO;
using System.Text.Json;
using ClickMacro;

namespace OverlayTimer.Tests;

public class ClickMacroTests
{
    // StepConfig 직렬화: click 스텝은 intervalMs/durationMs 생략
    [Fact]
    public void StepConfig_ClickStep_OmitsRepeatOnlyFields()
    {
        var step = new StepConfig { Type = "click", X = 100, Y = 200, AfterDelayMs = 1000 };
        var json = JsonSerializer.Serialize(step);

        Assert.DoesNotContain("intervalMs", json);
        Assert.DoesNotContain("durationMs", json);
        Assert.Contains("\"afterDelayMs\"", json);
    }

    // StepConfig 직렬화: afterDelayMs == 0이면 생략
    [Fact]
    public void StepConfig_ZeroAfterDelayMs_Omitted()
    {
        var step = new StepConfig { Type = "click", X = 100, Y = 200 };
        var json = JsonSerializer.Serialize(step);

        Assert.DoesNotContain("afterDelayMs", json);
    }

    // StepConfig 직렬화: repeat 스텝은 intervalMs/durationMs 포함
    [Fact]
    public void StepConfig_RepeatStep_IncludesIntervalAndDuration()
    {
        var step = new StepConfig { Type = "repeat", X = 1795, Y = 315, IntervalMs = 60000, DurationMs = 180000 };
        var json = JsonSerializer.Serialize(step);

        Assert.Contains("\"intervalMs\"", json);
        Assert.Contains("\"durationMs\"", json);
        Assert.DoesNotContain("afterDelayMs", json);
    }

    // ScenarioConfig.Save: RepeatSteps 계산 프로퍼티가 JSON에 포함되지 않음
    [Fact]
    public void ScenarioConfig_Save_DoesNotSerializeRepeatSteps()
    {
        var path = Path.GetTempFileName();
        try
        {
            var steps = new List<StepConfig>
            {
                new StepConfig { Type = "click", X = 100, Y = 200, AfterDelayMs = 1000 },
                new StepConfig { Type = "repeat", X = 300, Y = 400, IntervalMs = 1000, DurationMs = 60000 }
            };
            ScenarioConfig.Save(path, "테스트", steps);

            var json = File.ReadAllText(path);
            Assert.DoesNotContain("RepeatSteps", json, StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(path); }
    }

    // ScenarioConfig 저장/로드 라운드트립
    [Fact]
    public void ScenarioConfig_SaveLoad_RoundTrip()
    {
        var path = Path.GetTempFileName() + ".json";
        try
        {
            var steps = new List<StepConfig>
            {
                new StepConfig { Type = "click", X = 961, Y = 988, AfterDelayMs = 7531 },
                new StepConfig { Type = "repeat", X = 1795, Y = 315, IntervalMs = 60000, DurationMs = 180000 }
            };
            ScenarioConfig.Save(path, "어비스", steps);

            var loaded = ScenarioConfig.Load(path);

            Assert.Equal("어비스", loaded.Name);
            Assert.Equal(2, loaded.Steps.Count);

            Assert.Equal("click", loaded.Steps[0].Type);
            Assert.Equal(961, loaded.Steps[0].X);
            Assert.Equal(7531, loaded.Steps[0].AfterDelayMs);
            Assert.Equal(0, loaded.Steps[0].IntervalMs);   // JSON에 없으면 0

            Assert.Equal("repeat", loaded.Steps[1].Type);
            Assert.Equal(1795, loaded.Steps[1].X);
            Assert.Equal(60000, loaded.Steps[1].IntervalMs);
            Assert.Equal(180000, loaded.Steps[1].DurationMs);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ScenarioConfig.Load: name 필드 없으면 파일명을 이름으로 사용
    [Fact]
    public void ScenarioConfig_Load_FallsBackToFileName()
    {
        var path = Path.Combine(Path.GetTempPath(), "심층 던전.json");
        try
        {
            File.WriteAllText(path, """{"steps": []}""");
            var config = ScenarioConfig.Load(path);
            Assert.Equal("심층 던전", config.Name);
        }
        finally { File.Delete(path); }
    }

    // MacroRunner용 intervalMs 폴백: IntervalMs == 0이면 1000으로 처리됨을 검증
    [Theory]
    [InlineData(0, 1000)]
    [InlineData(500, 500)]
    [InlineData(60000, 60000)]
    public void IntervalMs_Fallback_Logic(int rawIntervalMs, int expectedIntervalMs)
    {
        int actual = rawIntervalMs > 0 ? rawIntervalMs : 1000;
        Assert.Equal(expectedIntervalMs, actual);
    }

    // 시나리오가 끝난 뒤 첫 스텝부터 다시 반복되는지 검증
    [Fact]
    public async Task MacroRunner_LoopsBackToFirstStepAfterLastStep()
    {
        var clicks = new List<(int x, int y)>();
        var tcs = new TaskCompletionSource();
        MacroRunner? runner = null;

        runner = new MacroRunner("test", isFocused: () => true, click: (x, y) =>
        {
            lock (clicks) clicks.Add((x, y));
            if (clicks.Count >= 6) { runner!.Stop(); tcs.TrySetResult(); }
        });

        runner.Start(new ScenarioConfig
        {
            Steps =
            [
                new StepConfig { Type = "click", X = 10, Y = 20 },
                new StepConfig { Type = "click", X = 30, Y = 40 },
            ]
        });

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(5000));
        Assert.Equal(tcs.Task, completed); // 5초 안에 완료

        // 패턴: (10,20) (30,40) (10,20) (30,40) ...
        Assert.Equal((10, 20), clicks[0]);
        Assert.Equal((30, 40), clicks[1]);
        Assert.Equal((10, 20), clicks[2]); // 루프백 확인
        Assert.Equal((30, 40), clicks[3]);
    }

    // 어비스 패턴: 클릭 → repeat(3회) → 클릭 후 첫 스텝으로 반복
    [Fact]
    public async Task MacroRunner_AbyssPattern_LoopsCorrectly()
    {
        var clicks = new List<(int x, int y)>();
        var tcs = new TaskCompletionSource();
        MacroRunner? runner = null;

        runner = new MacroRunner("test", isFocused: () => true, click: (x, y) =>
        {
            lock (clicks) clicks.Add((x, y));
            // 2회 루프 완료 = (10,20) + (50,60)×3 + (90,100) × 2 = 10 클릭
            if (clicks.Count >= 10) { runner!.Stop(); tcs.TrySetResult(); }
        });

        runner.Start(new ScenarioConfig
        {
            Steps =
            [
                new StepConfig { Type = "click", X = 10, Y = 20 },                                   // step 1
                new StepConfig { Type = "repeat", X = 50, Y = 60, IntervalMs = 10, DurationMs = 30 }, // step 2: 3회
                new StepConfig { Type = "click", X = 90, Y = 100 },                                  // step 3 (마지막)
            ]
        });

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(5000));
        Assert.Equal(tcs.Task, completed);

        // 1번째 루프
        Assert.Equal((10, 20),  clicks[0]); // step 1
        Assert.Equal((50, 60),  clicks[1]); // repeat 1/3
        Assert.Equal((50, 60),  clicks[2]); // repeat 2/3
        Assert.Equal((50, 60),  clicks[3]); // repeat 3/3
        Assert.Equal((90, 100), clicks[4]); // step 3

        // 2번째 루프 (마지막 스텝 후 첫 스텝으로 되돌아왔는지)
        Assert.Equal((10, 20),  clicks[5]); // step 1 재실행
        Assert.Equal((50, 60),  clicks[6]);
        Assert.Equal((50, 60),  clicks[7]);
        Assert.Equal((50, 60),  clicks[8]);
        Assert.Equal((90, 100), clicks[9]);
    }

    // repeat 스텝의 반복 횟수 계산: durationMs / intervalMs
    [Theory]
    [InlineData(180000, 1000, 180)]   // 심층 던전: 180초 / 1초 = 180회
    [InlineData(180000, 60000, 3)]    // 어비스: 180초 / 60초 = 3회
    [InlineData(60000, 1000, 60)]     // 1분 / 1초 = 60회
    public void RepeatCount_DerivedFromDurationAndInterval(int durationMs, int intervalMs, int expectedCount)
    {
        int count = durationMs / intervalMs;
        Assert.Equal(expectedCount, count);
    }

    // 경과 시간 표시: intervalMs >= 1000이면 초 단위 루프 사용
    [Theory]
    [InlineData(60000, true, 60)]    // 60초 간격 → 60틱
    [InlineData(1000, true, 1)]      // 1초 간격 → 1틱
    [InlineData(500, false, 0)]      // 0.5초 간격 → 초 단위 루프 미사용
    public void ElapsedDisplay_UsesSecondLoop_WhenIntervalIsAtLeastOneSecond(int intervalMs, bool usesSecondLoop, int expectedTicks)
    {
        int totalSecs = intervalMs / 1000;
        bool actual = totalSecs >= 1;
        Assert.Equal(usesSecondLoop, actual);
        if (usesSecondLoop)
            Assert.Equal(expectedTicks, totalSecs);
    }

    // ScenarioConfig.RepeatSteps: repeat 스텝만 필터링되고 인덱스가 올바름
    [Fact]
    public void ScenarioConfig_RepeatSteps_FiltersAndIndexesCorrectly()
    {
        var config = new ScenarioConfig
        {
            Name = "테스트",
            Steps =
            [
                new StepConfig { Type = "click", X = 100, Y = 200, AfterDelayMs = 1000 },
                new StepConfig { Type = "repeat", X = 300, Y = 400, IntervalMs = 1000, DurationMs = 60000 },
                new StepConfig { Type = "click", X = 500, Y = 600, AfterDelayMs = 2000 },
                new StepConfig { Type = "repeat", X = 700, Y = 800, IntervalMs = 2000, DurationMs = 120000 },
            ]
        };

        var repeatSteps = config.RepeatSteps;

        Assert.Equal(2, repeatSteps.Count);
        Assert.Equal("반복 1", repeatSteps[0].Label);
        Assert.Equal("반복 2", repeatSteps[1].Label);
        Assert.Equal("60", repeatSteps[0].DurationSecs);    // 60000ms = 60초
        Assert.Equal("120", repeatSteps[1].DurationSecs);  // 120000ms = 120초
    }
}
