using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vwm.Core;
using Vwm.Core.Render;
using Vwm.Core.Script;
using Vwm.Core.Timeline;
using Vwm.Core.Tools;
using Vwm.Core.Tts;
using Vwm.Core.Video;

namespace Vwm.App.ViewModels;

public partial class StepItem(int index, string text) : ObservableObject
{
    public int Index { get; } = index;
    public string Header => $"Step {Index + 1}";
    [ObservableProperty] private string _text = text;
    [ObservableProperty] private bool _isPlaying;

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

/// <summary>One entry in the voice dropdown: a specific Piper voice, the Windows voice, or eSpeak.</summary>
public sealed record VoiceChoice(string Id, string DisplayName, Func<ITtsEngine> CreateEngine)
{
    public override string ToString() => DisplayName;
}

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

    public ObservableCollection<VoiceChoice> Voices { get; } = [];
    [ObservableProperty] private VoiceChoice? _selectedVoice;

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
        foreach (var voice in AvailableVoices())
            Voices.Add(voice);
        SelectedVoice = Voices.FirstOrDefault();
    }

    private static IEnumerable<VoiceChoice> AvailableVoices()
    {
        if (ToolLocator.Find("piper") is not null)
        {
            foreach (var v in PiperVoiceCatalog.Enumerate())
                yield return new VoiceChoice(v.Id, v.DisplayName, () => new PiperTtsEngine(v));
        }
        if (OperatingSystem.IsWindows())
            yield return new VoiceChoice("windows", "Windows built-in voice", CreateWindowsEngine);
        if (ToolLocator.Find("espeak-ng") is not null)
            yield return new VoiceChoice("espeak", "eSpeak (test voice)", () => new EspeakTtsEngine());
    }

    private ITtsEngine CreateEngine() =>
        (SelectedVoice ?? throw new InvalidOperationException(
            "No voice is available — reinstall the app so the bundled voices are present."))
        .CreateEngine();

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
            var boundaries = SceneDetector.ProposeBoundaries(
                cuts, _videoDuration, steps.Count, NarrationEstimator.EstimateWeights(steps));

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
    private void BackToInput()
    {
        StopPlayback();
        PageIndex = 0;
    }

    private Process? _playback;
    private StepItem? _playingStep;

    /// <summary>Voice-only preview, played inline (hidden ffplay process) — no window opens.</summary>
    [RelayCommand]
    private async Task PreviewVoiceAsync(StepItem step)
    {
        if (step.IsPlaying)
        {
            StopPlayback();
            return;
        }
        StopPlayback();
        ErrorMessage = "";
        IsBusy = true;
        try
        {
            var wav = await PreviewBuilder.BuildVoicePreviewAsync(
                step.ToScriptStep(), CreateEngine(), Path.Combine(_workDir, "preview"));

            var ffplay = ToolLocator.Find("ffplay")
                ?? throw new ToolNotFoundException("ffplay", "bundle ffplay.exe in the 'tools' folder next to the app");
            var psi = new ProcessStartInfo(ffplay)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in new[] { "-nodisp", "-autoexit", "-loglevel", "error", wav })
                psi.ArgumentList.Add(a);

            _playback = Process.Start(psi);
            if (_playback is not null)
            {
                _playingStep = step;
                step.IsPlaying = true;
                _ = MonitorPlaybackAsync(_playback, step);
            }
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

    private async Task MonitorPlaybackAsync(Process playback, StepItem step)
    {
        try
        {
            await playback.WaitForExitAsync();
        }
        catch
        {
            // killed by StopPlayback — nothing to do
        }
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            step.IsPlaying = false;
            if (_playback == playback)
            {
                _playback = null;
                _playingStep = null;
            }
        });
    }

    private void StopPlayback()
    {
        if (_playback is { HasExited: false } p)
        {
            try { p.Kill(); } catch { /* already gone */ }
        }
        _playback = null;
        if (_playingStep is not null)
        {
            _playingStep.IsPlaying = false;
            _playingStep = null;
        }
    }

    /// <summary>Video+voice preview of one step; opens in the OS default player.</summary>
    [RelayCommand]
    private async Task PreviewClipAsync(StepItem step)
    {
        StopPlayback();
        ErrorMessage = "";
        IsBusy = true;
        try
        {
            var sourceStart = step.Index == 0 ? 0 : Boundaries[step.Index - 1].TimeSeconds;
            var sourceEnd = step.Index == Boundaries.Count ? _videoDuration : Boundaries[step.Index].TimeSeconds;
            if (sourceEnd <= sourceStart)
            {
                ErrorMessage = "This step's boundaries overlap — adjust the sliders first.";
                return;
            }

            var previewDir = Path.Combine(_workDir, "preview");
            var wav = await PreviewBuilder.BuildVoicePreviewAsync(step.ToScriptStep(), CreateEngine(), previewDir);
            var clip = await PreviewBuilder.BuildClipPreviewAsync(VideoPath, sourceStart, sourceEnd, wav, previewDir);
            Process.Start(new ProcessStartInfo(clip) { UseShellExecute = true });
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
        StopPlayback();
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
