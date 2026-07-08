using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vwm.Core;
using Vwm.Core.Script;
using Vwm.Core.Tools;
using Vwm.Core.Tts;
using Vwm.Core.Video;

namespace Vwm.App.ViewModels;

public partial class StepItem(int index, string text) : ObservableObject
{
    public int Index { get; } = index;
    public string Header => $"Step {Index + 1}";
    [ObservableProperty] private string _text = text;

    public ScriptStep ToScriptStep() => new(Index, Text, ScriptParser.SplitSentences(Text));
}

public partial class BoundaryItem(int afterStep, double time, double max) : ObservableObject
{
    public string Label => $"End of step {afterStep + 1}";
    public double Max { get; } = max;
    [ObservableProperty] private double _timeSeconds = time;
    public string TimeLabel => TimeSpan.FromSeconds(TimeSeconds).ToString(@"mm\:ss\.f");
    partial void OnTimeSecondsChanged(double value) => OnPropertyChanged(nameof(TimeLabel));
}

public sealed record ThumbItem(Avalonia.Media.Imaging.Bitmap Image, string TimeLabel);

public partial class MainViewModel : ObservableObject
{
    // --- navigation -----------------------------------------------------------
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInputPage), nameof(IsReviewPage), nameof(IsGeneratePage))]
    private int _pageIndex;

    public bool IsInputPage => PageIndex == 0;
    public bool IsReviewPage => PageIndex == 1;
    public bool IsGeneratePage => PageIndex == 2;

    // --- input page -----------------------------------------------------------
    [ObservableProperty] private string _videoPath = "";
    [ObservableProperty] private string _scriptText = "";
    [ObservableProperty] private string _outputPath = "";
    [ObservableProperty] private bool _keepOriginalAudio;
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private bool _isBusy;

    public ObservableCollection<string> Engines { get; } = [];
    [ObservableProperty] private string _selectedEngine = "";

    // --- review page ----------------------------------------------------------
    public ObservableCollection<StepItem> Steps { get; } = [];
    public ObservableCollection<BoundaryItem> Boundaries { get; } = [];
    public ObservableCollection<ThumbItem> Thumbnails { get; } = [];
    private double _videoDuration;

    // --- generate page --------------------------------------------------------
    public ObservableCollection<string> Log { get; } = [];
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private string _finalOutputPath = "";

    private readonly string _workDir =
        Path.Combine(Path.GetTempPath(), "vwm-app", Path.GetRandomFileName());

    public MainViewModel()
    {
        foreach (var engine in AvailableEngines())
            Engines.Add(engine);
        SelectedEngine = Engines.FirstOrDefault() ?? "";
    }

    private static IEnumerable<string> AvailableEngines()
    {
        if (ToolLocator.Find("piper") is not null)
            yield return "Piper (bundled neural voice)";
        if (OperatingSystem.IsWindows())
            yield return "Windows built-in voice";
        if (ToolLocator.Find("espeak-ng") is not null)
            yield return "eSpeak (test)";
    }

    private ITtsEngine CreateEngine() => SelectedEngine switch
    {
        var s when s.StartsWith("Piper") => new PiperTtsEngine(),
        var s when s.StartsWith("Windows") => CreateWindowsEngine(),
        _ => new EspeakTtsEngine(),
    };

    private static ITtsEngine CreateWindowsEngine()
    {
#if WINDOWS10_0_19041_0_OR_GREATER
        return new Vwm.Tts.Windows.WindowsTtsEngine();
#else
        throw new InvalidOperationException("The Windows voice engine is only available in the Windows build.");
#endif
    }

    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        ErrorMessage = "";
        if (!File.Exists(VideoPath))
        {
            ErrorMessage = "Select a video file first.";
            return;
        }
        if (string.IsNullOrWhiteSpace(ScriptText))
        {
            ErrorMessage = "Paste or load the script first.";
            return;
        }
        if (string.IsNullOrWhiteSpace(OutputPath))
        {
            OutputPath = Path.Combine(
                Path.GetDirectoryName(VideoPath)!,
                Path.GetFileNameWithoutExtension(VideoPath) + "_narrated.mp4");
        }

        IsBusy = true;
        try
        {
            var steps = ScriptParser.Parse(ScriptText);
            if (steps.Count == 0)
            {
                ErrorMessage = "The script appears to be empty.";
                return;
            }

            _videoDuration = await SceneDetector.GetDurationAsync(VideoPath);
            var cuts = await SceneDetector.DetectAsync(VideoPath);
            var boundaries = SceneDetector.ProposeBoundaries(cuts, _videoDuration, steps.Count);

            Directory.CreateDirectory(_workDir);
            var thumbs = await ThumbnailExtractor.ExtractAsync(
                VideoPath, _videoDuration, count: 12, Path.Combine(_workDir, "thumbs"));

            Steps.Clear();
            foreach (var s in steps)
                Steps.Add(new StepItem(s.Index, s.Text));

            Boundaries.Clear();
            for (var i = 0; i < boundaries.Count; i++)
                Boundaries.Add(new BoundaryItem(i, boundaries[i], _videoDuration));

            Thumbnails.Clear();
            foreach (var (time, path) in thumbs)
            {
                Thumbnails.Add(new ThumbItem(
                    new Avalonia.Media.Imaging.Bitmap(path),
                    TimeSpan.FromSeconds(time).ToString(@"mm\:ss")));
            }

            PageIndex = 1;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void BackToInput() => PageIndex = 0;

    [RelayCommand]
    private async Task PreviewStepAsync(StepItem step)
    {
        ErrorMessage = "";
        IsBusy = true;
        try
        {
            var sentences = ScriptParser.SplitSentences(step.Text);
            if (sentences.Count == 0)
                return;
            var wav = Path.Combine(_workDir, $"preview_{step.Index}.wav");
            await CreateEngine().SynthesizeAsync(sentences[0], wav);
            Process.Start(new ProcessStartInfo(wav) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task GenerateAsync()
    {
        ErrorMessage = "";
        var boundaries = Boundaries.Select(b => b.TimeSeconds).ToList();
        if (boundaries.Zip(boundaries.Skip(1)).Any(p => p.Second <= p.First) ||
            boundaries.Any(b => b <= 0 || b >= _videoDuration))
        {
            ErrorMessage = "Step boundaries must be in increasing order, inside the video.";
            return;
        }

        PageIndex = 2;
        IsBusy = true;
        IsDone = false;
        Log.Clear();
        try
        {
            var result = await WalkthroughPipeline.RunAsync(
                new PipelineOptions
                {
                    VideoPath = VideoPath,
                    ScriptText = ScriptText,
                    Steps = Steps.Select(s => s.ToScriptStep()).ToList(),
                    OutputPath = OutputPath,
                    TtsEngine = CreateEngine(),
                    Boundaries = boundaries,
                    KeepOriginalAudio = KeepOriginalAudio,
                    WorkDir = Path.Combine(_workDir, "render"),
                },
                progress: new Progress<string>(Log.Add));
            FinalOutputPath = result.OutputPath;
            IsDone = true;
        }
        catch (Exception ex)
        {
            Log.Add($"Failed: {ex.Message}");
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenOutputFolder()
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(FinalOutputPath));
        if (dir is not null)
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
    }

    [RelayCommand]
    private void StartOver()
    {
        PageIndex = 0;
        IsDone = false;
        Log.Clear();
    }
}
