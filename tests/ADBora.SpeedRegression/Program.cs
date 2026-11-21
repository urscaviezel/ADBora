using System.Globalization;
using AdbTool.Core;
using AdbTool.Tabs;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Media;
using SkiaSharp;

internal static class Program
{
    private const string EnglishDown = "↓ Median 800 Mbit/s · avg 800 · min 800 · 0 dip(s) below 50 % · 0 stall(s)";
    private const string EnglishUp = "↑ Median 80 Mbit/s · avg 80 · min 80 · 0 dip(s) below 50 % · 0 stall(s)";
    private const string GermanDown = "↓ Median 800 Mbit/s · Ø 800 · Min 800 · 0 Einbruch/Einbrüche unter 50 % · 0 Aussetzer";
    private const string GermanUp = "↑ Median 80 Mbit/s · Ø 80 · Min 80 · 0 Einbruch/Einbrüche unter 50 % · 0 Aussetzer";

    [STAThread]
    private static int Main()
    {
        // Safety: the tests create pages with real settings objects – they must never write the user's settings.json
        if (!AppSettings.IsPortable)
        {
            Console.Error.WriteLine("portable.flag missing next to the test program – aborting so the real ADBora settings stay untouched.");
            return 2;
        }
        AppSettings.SavingDisabled = true; // second safety net
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        AppBuilder.Configure<Application>().UseSkia().WithInterFont()
            .With(new FontManagerOptions { DefaultFamilyName = "avares://Avalonia.Fonts.Inter/Assets#Inter" })
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();

        (string Name, Action Run)[] tests =
        [
            ("Separate download and upload", BothDirections),
            ("Cancelled before upload starts", DownloadOnly),
            ("Cancelled shortly after upload starts", ShortUpload),
            ("Sparse upload must not contaminate download", SparseUpload),
            ("Sparse download with usable upload", UploadOnly),
            ("Too few samples in both directions", TooFewSamples),
            ("Dips and stalls are counted per direction", DipsAndStalls),
            ("ADB retries retain the single summary", AdbRetries),
            ("UI, history, PNG and CSV in German and English", () => ReportsAndLanguage(partial: false)),
            ("Cancelled test in UI, history, PNG and CSV", () => ReportsAndLanguage(partial: true)),
        ];

        int failed = 0;
        foreach (var test in tests)
        {
            Loc.SetLanguage("en");
            try
            {
                test.Run();
                Console.WriteLine($"PASS {test.Name}");
            }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine($"FAIL {test.Name}: {ex}");
            }
        }
        Console.WriteLine($"{tests.Length - failed}/{tests.Length} passed");
        return failed == 0 ? 0 : 1;
    }

    private static void BothDirections()
    {
        var chart = new SpeedChart();
        Feed(chart, chart.AddPhase(() => "Download", 1), 0, 800, 800, 800);
        Feed(chart, chart.AddPhase(() => "Upload", 2), 5, 80, 80, 80);
        var result = Analyze(chart);
        Equal(800d, result.Down?.Median, "download median");
        Equal(80d, result.Up?.Median, "upload median");
        Equal($"{EnglishDown}   |   {EnglishUp}", result.Summary(), "two-direction summary");
    }

    private static void DownloadOnly()
    {
        var chart = new SpeedChart();
        Feed(chart, chart.AddPhase(() => "Download", 1), 0, 800, 800);
        AssertDownloadOnly(chart);
    }

    private static void ShortUpload()
    {
        var chart = new SpeedChart();
        Feed(chart, chart.AddPhase(() => "Download", 1), 0, 800, 800);
        int up = chart.AddPhase(() => "Upload", 2);
        // Four points, but never two after ramp-up.
        for (int i = 0; i < 4; i++) chart.Add(new RateSample(5 + i * .25, 80, up));
        AssertDownloadOnly(chart);
    }

    private static void SparseUpload()
    {
        var chart = new SpeedChart();
        Feed(chart, chart.AddPhase(() => "Download", 1), 0, 800, 800);
        FeedSparse(chart, chart.AddPhase(() => "Upload", 2), 5, 80);
        AssertDownloadOnly(chart);
    }

    private static void AssertDownloadOnly(SpeedChart chart)
    {
        var result = Analyze(chart);
        Equal(800d, result.Down?.Median, "download median");
        Equal(800d, result.Average, "average excludes incomplete upload");
        Equal<SpeedAnalysis?>(null, result.Up, "upload analysis");
        Equal(EnglishDown, result.Summary(), "only labelled download");
    }

    private static void UploadOnly()
    {
        var chart = new SpeedChart();
        FeedSparse(chart, chart.AddPhase(() => "Download", 1), 0, 800);
        Feed(chart, chart.AddPhase(() => "Upload", 2), 5, 80, 80);
        var result = Analyze(chart);
        Equal<SpeedAnalysis?>(null, result.Down, "download analysis");
        Equal(80d, result.Average, "average excludes incomplete download");
        Equal(EnglishUp, result.Summary(), "only labelled upload");
    }

    private static void TooFewSamples()
    {
        var chart = new SpeedChart();
        Equal<SpeedAnalysis?>(null, chart.Analyze(), "empty test");
        int down = chart.AddPhase(() => "Download", 1);
        FeedSparse(chart, down, 0, 800);
        Equal<SpeedAnalysis?>(null, chart.Analyze(), "three samples in one direction");
        FeedSparse(chart, chart.AddPhase(() => "Upload", 2), 5, 80);
        Equal<SpeedAnalysis?>(null, chart.Analyze(), "three samples in each direction");
    }

    private static void DipsAndStalls()
    {
        var chart = new SpeedChart();
        Feed(chart, chart.AddPhase(() => "Download", 1), 0, 800, 200, 200, 800, 800, 0, 0, 800, 800, 800);
        Feed(chart, chart.AddPhase(() => "Upload", 2), 5, 80, 20, 80, 80, 20, 80, 80, 0, 0);
        var result = Analyze(chart);
        Equal(new SpeedAnalysis(800, 520, 0, 1, 1, .75, .75), result.Down, "download statistics");
        Equal(new SpeedAnalysis(80, 440d / 9, 0, 2, 1, .5, .5), result.Up, "upload statistics");
    }

    private static void AdbRetries()
    {
        var chart = new SpeedChart();
        Feed(chart, chart.AddPhase("ADB attempt 1"), 0, 200, 200);
        Feed(chart, chart.AddPhase("ADB attempt 2"), 5, 400, 400);
        var result = Analyze(chart);
        Equal(new SpeedAnalysis(300, 300, 200, 0, 0, 0, 0), result, "ADB statistics");
        Equal("Median 300 Mbit/s · avg 300 · min 200 · 0 dip(s) below 50 % · 0 stall(s)", result.Summary(), "ADB summary");
    }

    private static void ReportsAndLanguage(bool partial)
    {
        Loc.SetLanguage("de");
        var page = new SpeedTestPage(new AppState(new AppSettings()));
        var chart = page.GetLogicalDescendants().OfType<SpeedChart>().Single();
        Feed(chart, page.AddDirectPhase(download: true), 0, 800, 800);
        int upload = page.AddDirectPhase(download: false);
        if (partial) FeedSparse(chart, upload, 5, 80);
        else Feed(chart, upload, 5, 80, 80);

        string de = partial ? GermanDown : $"{GermanDown}   |   {GermanUp}";
        string en = partial ? EnglishDown : $"{EnglishDown}   |   {EnglishUp}";
        page.PublishFinalAnalysis();
        var labels = page.GetLogicalDescendants().OfType<TextBlock>().ToArray();
        var history = page.GetLogicalDescendants().OfType<TextBox>().Single(box => box.Text?.Contains(de) == true);
        var summary = labels.Single(label => label.Text == de);
        var uploadLabel = labels.Single(label => label.Text == "↑ PC → Headset:");
        Equal(true, labels.Any(label => label.Text == "↓ Headset → PC:"), "download result label");

        var folder = Directory.CreateTempSubdirectory("adbora-speed-");
        try
        {
            using var german = CheckReport(page, chart, folder.FullName, "de", de, "↓ Headset → PC", "↑ PC → Headset");
            Loc.SetLanguage("en");
            Equal(en, summary.Text, "same summary control after switching");
            Equal("↑ PC → headset:", uploadLabel.Text, "same upload value label after switching");
            Equal(true, history.Text?.Contains(de), "final summary recorded in the history");
            using var english = CheckReport(page, chart, folder.FullName, "en", en, "↓ headset → PC", "↑ PC → headset");
            Equal(false, SameRows(german, english, 136, 158), "PNG legend changes language");
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    private static SKBitmap CheckReport(SpeedTestPage page, SpeedChart chart, string folder, string name, string summary,
                                        string down, string up)
    {
        var (png, csv) = page.WriteReport(folder, name);
        string[] lines = File.ReadAllLines(csv);
        Equal("# " + summary, lines.Single(line => line.StartsWith("# ↓")), "CSV summary");
        Equal(true, lines.Contains($"1.25;{down};800.0;;;;;;"), "CSV download sample and direction");
        Equal(true, lines.Contains($"6.25;{up};80.0;;;;;;"), "CSV upload sample and direction");

        // Compare the PNG summary row with independently specified text rendered at the same location.
        var (reference, _) = chart.Export(folder, name + "-reference", ["ADBora", "", "", summary]);
        using var expected = SKBitmap.Decode(reference);
        var actual = SKBitmap.Decode(png);
        Equal(1800, actual.Width, "PNG width");
        Equal(900, actual.Height, "PNG height");
        Equal(true, SameRows(expected, actual, 102, 126), "PNG summary pixels");
        return actual;
    }

    private static bool SameRows(SKBitmap first, SKBitmap second, int top, int bottom)
    {
        for (int y = top; y < bottom; y++)
            for (int x = 0; x < first.Width; x++)
                if (first.GetPixel(x, y) != second.GetPixel(x, y)) return false;
        return true;
    }

    private static void Feed(SpeedChart chart, int phase, double start, params double[] steady)
    {
        for (int i = 0; i < 4; i++) chart.Add(new RateSample(start + i * .25, 9999, phase));
        for (int i = 0; i < steady.Length; i++) chart.Add(new RateSample(start + 1 + i * .25, steady[i], phase));
    }

    private static void FeedSparse(SpeedChart chart, int phase, double start, double rate)
    {
        chart.Add(new RateSample(start, 9999, phase));
        chart.Add(new RateSample(start + 1, rate, phase));
        chart.Add(new RateSample(start + 1.25, rate, phase));
    }

    private static SpeedAnalysis Analyze(SpeedChart chart) =>
        chart.Analyze() ?? throw new InvalidOperationException("Expected a usable analysis.");

    private static void Equal<T>(T expected, T actual, string description)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{description}: expected '{expected}', got '{actual}'.");
    }
}
