using System.IO;

namespace OverlayTimer.Tests;

public class AppConfigSaveTests
{
    private static readonly object ConfigFileLock = new();

    [Fact]
    public void Save_NormalizesNonFiniteOverlayValues()
    {
        lock (ConfigFileLock)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "config.json");
            string? backup = File.Exists(path) ? File.ReadAllText(path) : null;

            try
            {
                var config = new AppConfig();
                config.Overlays.Timer.X = double.NaN;
                config.Overlays.Timer.Y = double.PositiveInfinity;
                config.Overlays.Timer.Width = double.NaN;
                config.Overlays.Timer.Height = double.NegativeInfinity;
                config.Overlays.Dps.Width = -10;
                config.Overlays.Dps.Height = 200;

                var ex = Record.Exception(config.Save);

                Assert.Null(ex);

                var saved = AppConfig.Load();
                Assert.Equal(0, saved.Overlays.Timer.X);
                Assert.Equal(0, saved.Overlays.Timer.Y);
                Assert.Null(saved.Overlays.Timer.Width);
                Assert.Null(saved.Overlays.Timer.Height);
                Assert.Null(saved.Overlays.Dps.Width);
                Assert.Equal(200, saved.Overlays.Dps.Height);
            }
            finally
            {
                if (backup is null)
                    File.Delete(path);
                else
                    File.WriteAllText(path, backup);
            }
        }
    }
}
