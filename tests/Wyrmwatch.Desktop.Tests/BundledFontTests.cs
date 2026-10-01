using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Wyrmwatch.Desktop;

namespace Wyrmwatch.Desktop.Tests;

public class BundledFontTests
{
    [AvaloniaFact]
    public void MainWindowAndBoldTypographyRenderWithoutAnyInstalledFonts()
    {
        var root = Path.Combine(Path.GetTempPath(), "wyrmwatch-font-" + Guid.NewGuid().ToString("N"));
        Program.HeadlessTest = true; Program.Demo = false; Program.DataDirectory = root;
        var window = new MainWindow();
        try
        {
            Assert.Empty(FontManager.Current.SystemFonts);
            Assert.Equal(new FontFamily("fonts:Inter#Inter"), FontManager.Current.DefaultFontFamily);
            window.Show();
            foreach (var weight in new[] { FontWeight.Normal, FontWeight.SemiBold, FontWeight.Bold })
            {
                Assert.True(FontManager.Current.TryGetGlyphTypeface(new Typeface(window.FontFamily, weight: weight), out var face));
                Assert.Contains("Inter", face.FamilyName);
            }
            foreach (var page in Enum.GetValues<WorkspacePage>())
            {
                window.ShowPage(page); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
            }
        }
        finally { window.Close(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
