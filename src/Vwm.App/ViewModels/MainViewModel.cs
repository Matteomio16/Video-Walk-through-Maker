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

public sealed record BlurStyleChoice(string DisplayName, BlurStyle Style)
{
    public override string ToString() => DisplayName;
}

public sealed record BlurStrengthChoice(string DisplayName, int Strength)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// One area to hide, as the editor works with it: a slice of the recording ("a length")
/// plus the rectangle covering the sensitive part of the frame. The rectangle is kept in
/// fractions of the frame, exactly as <see cref="BlurRegion"/> stores it, so what the user
/// drags on the preview still is what the renderer blurs at full size.
/// </summary>
public partial class BlurAreaItem : ObservableObject
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    /// <summary>Length of the recording, so the time sliders know their range.</summary>
    public double MaxSeconds { get; }

    public BlurAreaItem(int number, double startSeconds, double endSeconds, double maxSeconds)
    {
        _number = number;
        _startSeconds = startSeconds;
        _endSeconds = endSeconds;
        MaxSeconds = maxSeconds;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Header))]
    private int _number;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RangeLabel), nameof(StartLabel))]
    private double _startSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RangeLabel), nameof(EndLabel))]
    private double _endSeconds;

    [ObservableProperty] private double _x = 0.34;
    [ObservableProperty] private double _y = 0.40;
    [ObservableProperty] private double _width = 0.32;
    [ObservableProperty] private double _height = 0.16;

    [ObservableProperty] private BlurStyleChoice _style = MainViewModel.BlurStyleChoices[0];
    [ObservableProperty] private BlurStrengthChoice _strength = MainViewModel.BlurStrengthChoices[2];

    public string Header => $"Area {Number}";
    public string RangeLabel => $"{Stamp(StartSeconds)} → {Stamp(EndSeconds)}";
    public string StartLabel => Stamp(StartSeconds);
    public string EndLabel => Stamp(EndSeconds);

    // Dragging one end past the other would make an empty (invalid) slice, so each end
    // pushes the other along instead of crossing it.
    partial void OnStartSecondsChanged(double value)
    {
        if (value > EndSeconds - MinSpanSeconds)
            EndSeconds = Math.Min(MaxSeconds, value + MinSpanSeconds);
    }

    partial void OnEndSecondsChanged(double value)
    {
        if (value < StartSeconds + MinSpanSeconds)
            StartSeconds = Math.Max(0, value - MinSpanSeconds);
    }

    /// <summary>Shortest slice the sliders will produce. Below a frame or two the area
    /// would be there and gone again without ever hiding anything.</summary>
    private const double MinSpanSeconds = 0.2;

    public BlurRegion ToRegion() => new()
    {
        Id = Id,
        StartSeconds = StartSeconds,
        EndSeconds = EndSeconds,
        X = X,
        Y = Y,
        Width = Width,
        Height = Height,
        Style = Style.Style,
        Strength = Strength.Strength,
    };

    private static string Stamp(double seconds) =>
        TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"mm\:ss\.f");
}

/// <summary>One entry in the voice dropdown: a specific Piper voice, the Windows voice, or eSpeak.</summary>
public sealed record VoiceChoice(string Id, string DisplayName, Func<ITtsEngine> CreateEngine)
{
    public override string ToString() => DisplayName;
}

public sealed record SubtitleSizeChoice(string DisplayName, int Size)
{
    public override string ToString() => DisplayName;
}

public sealed record SubtitlePositionChoice(string DisplayName, SubtitlePosition Position)
{
    public override string ToString() => DisplayName;
}

public sealed record SubtitleBackgroundChoice(string DisplayName, SubtitleBackgroundStyle Style)
{
    public override string ToString() => DisplayName;
}

public partial class MainViewModel : ObservableObject
{
    // --- navigation -----------------------------------------------------------
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInputPage), nameof(IsReviewPage), nameof(IsGeneratePage),
        nameof(IsReviewReached), nameof(IsGenerateReached))]
    private int _pageIndex;

    public bool IsInputPage => PageIndex == 0;
    public bool IsReviewPage => PageIndex == 1;
    public bool IsGeneratePage => PageIndex == 2;

    // stepper highlighting: a step stays lit once the wizard has reached it
    public bool IsReviewReached => PageIndex >= 1;
    public bool IsGenerateReached => PageIndex >= 2;

    // --- input page -----------------------------------------------------------
    [ObservableProperty] private string _videoPath = "";
    [ObservableProperty] private string _scriptText = "";
    [ObservableProperty] private string _outputPath = "";
    [ObservableProperty] private bool _keepOriginalAudio;
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private bool _isBusy;

    public ObservableCollection<VoiceChoice> Voices { get; } = [];
    [ObservableProperty] private VoiceChoice? _selectedVoice;

    // Subtitle styling: a short curated set of choices, not a full font/size picker.
    public IReadOnlyList<string> SubtitleFonts { get; } =
        ["Arial", "Verdana", "Tahoma", "Georgia", "Segoe UI"];
    [ObservableProperty] private string _selectedSubtitleFont = "Arial";

    public IReadOnlyList<SubtitleSizeChoice> SubtitleSizes { get; } =
        [new("Small", 14), new("Medium", 16), new("Large", 20), new("Extra large", 24)];
    [ObservableProperty] private SubtitleSizeChoice? _selectedSubtitleSize;

    public IReadOnlyList<SubtitlePositionChoice> SubtitlePositions { get; } =
    [
        new("Bottom", SubtitlePosition.Bottom),
        new("Middle", SubtitlePosition.Middle),
        new("Top", SubtitlePosition.Top),
    ];
    [ObservableProperty] private SubtitlePositionChoice? _selectedSubtitlePosition;

    public IReadOnlyList<SubtitleBackgroundChoice> SubtitleBackgrounds { get; } =
    [
        new("Box", SubtitleBackgroundStyle.Box),
        new("Drop shadow", SubtitleBackgroundStyle.Shadow),
    ];
    [ObservableProperty] private SubtitleBackgroundChoice? _selectedSubtitleBackground;

    // --- review page ----------------------------------------------------------
    public ObservableCollection<StepItem> Steps { get; } = [];
    public ObservableCollection<BoundaryItem> Boundaries { get; } = [];
    public ObservableCollection<ThumbItem> Thumbnails { get; } = [];
    private double _videoDuration;

    // --- blur / redaction -----------------------------------------------------
    public static IReadOnlyList<BlurStyleChoice> BlurStyleChoices { get; } =
    [
        new("Blur", BlurStyle.Blur),
        new("Solid grey block", BlurStyle.Solid),
    ];

    public static IReadOnlyList<BlurStrengthChoice> BlurStrengthChoices { get; } =
    [
        new("Light", 4),
        new("Medium", 7),
        new("Strong", BlurRegion.DefaultStrength),
    ];

    public IReadOnlyList<BlurStyleChoice> BlurStyles => BlurStyleChoices;
    public IReadOnlyList<BlurStrengthChoice> BlurStrengths => BlurStrengthChoices;

    /// <summary>The feature is opt-in: unchecked, not one frame of the recording is touched.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BlurSummary))]
    private bool _blurEnabled;

    public ObservableCollection<BlurAreaItem> BlurAreas { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedBlurArea))]
    private BlurAreaItem? _selectedBlurArea;

    public bool HasSelectedBlurArea => SelectedBlurArea is not null;

    [ObservableProperty] private bool _isBlurEditorOpen;
    [ObservableProperty] private Avalonia.Media.Imaging.Bitmap? _blurFrame;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BlurPreviewLabel))]
    private double _blurPreviewSeconds;
    [ObservableProperty] private double _blurPreviewMin;
    [ObservableProperty] private double _blurPreviewMax = 1;

    /// <summary>True while the canvas shows the frame actually run through the blur filters
    /// rather than the placement box, so the user can confirm what will be rendered.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPlacementBox), nameof(BlurPreviewButtonText))]
    private bool _isShowingBlurredPreview;

    public bool ShowPlacementBox => !IsShowingBlurredPreview;
    public string BlurPreviewLabel =>
        TimeSpan.FromSeconds(Math.Max(0, BlurPreviewSeconds)).ToString(@"mm\:ss\.f");
    public string BlurPreviewButtonText =>
        IsShowingBlurredPreview ? "Back to editing" : "Show the real blur";

    public string BlurSummary => !BlurEnabled
        ? "Off — the recording is rendered exactly as captured."
        : BlurAreas.Count switch
        {
            0 => "No areas yet — add one to hide something.",
            1 => "1 area will be hidden.",
            var n => $"{n} areas will be hidden.",
        };

    public string BlurButtonText => BlurAreas.Count == 0
        ? "Blur areas…"
        : $"Blur areas ({BlurAreas.Count})…";

    // --- generate page --------------------------------------------------------
    public ObservableCollection<string> Log { get; } = [];
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private string _finalOutputPath = "";

    private readonly string _workDir =
        Path.Combine(Path.GetTempPath(), "vwm-app", Path.GetRandomFileName());
    private CancellationTokenSource? _generateCts;

    public MainViewModel()
    {
        foreach (var voice in AvailableVoices())
            Voices.Add(voice);
        SelectedVoice = Voices.FirstOrDefault();
        SelectedSubtitleSize = SubtitleSizes[1];       // Medium
        SelectedSubtitlePosition = SubtitlePositions[0]; // Bottom
        SelectedSubtitleBackground = SubtitleBackgrounds[0]; // Box
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

            // Areas are anchored to this recording's timeline, so a fresh analysis (possibly
            // of a different file) starts from a clean slate rather than stale ranges.
            SelectedBlurArea = null;
            BlurAreas.Clear();
            BlurEnabled = false;
            BlurFrame = null;
            NotifyBlurAreasChanged();

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
        IsClipPreviewOpen = false;
        IsBlurEditorOpen = false;
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

    /// <summary>True while the in-app clip player panel is showing.</summary>
    [ObservableProperty] private bool _isClipPreviewOpen;

    /// <summary>
    /// Raised when a step's video+voice preview clip is ready to play. The window
    /// plays it in the embedded player (or falls back to the OS player if the
    /// native playback libraries are unavailable).
    /// </summary>
    public event Action<string, string>? ClipPreviewRequested;

    /// <summary>Video+voice preview of one step, played inside the app.</summary>
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
            var clip = await PreviewBuilder.BuildClipPreviewAsync(
                VideoPath, sourceStart, sourceEnd, wav, previewDir, CurrentBlurRegions());
            ClipPreviewRequested?.Invoke(clip, $"Preview — {step.Header}");
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

    /// <summary>Last-resort clip preview when embedded playback isn't available.</summary>
    public static void OpenInExternalPlayer(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    // --- blur editor ----------------------------------------------------------

    /// <summary>Ticking the box opens the editor straight away: an enabled blur with no
    /// areas hides nothing, so the next thing to do is always to place one.</summary>
    partial void OnBlurEnabledChanged(bool value)
    {
        if (value)
            OpenBlurEditorCommand.Execute(null);
        else
            IsBlurEditorOpen = false;
    }

    [RelayCommand]
    private async Task OpenBlurEditorAsync()
    {
        StopPlayback();
        IsClipPreviewOpen = false;
        ErrorMessage = "";
        BlurEnabled = true;
        IsBlurEditorOpen = true;
        IsShowingBlurredPreview = false;
        if (BlurAreas.Count == 0)
            AddBlurArea();
        else
            SelectedBlurArea ??= BlurAreas[0];
        await ReloadBlurFrameAsync();
    }

    [RelayCommand]
    private void CloseBlurEditor()
    {
        IsBlurEditorOpen = false;
        IsShowingBlurredPreview = false;
    }

    /// <summary>Adds an area covering the whole recording, which the user then trims to the
    /// stretch that actually shows the sensitive content.</summary>
    [RelayCommand]
    private void AddBlurArea()
    {
        var area = new BlurAreaItem(BlurAreas.Count + 1, 0, Math.Max(_videoDuration, 1), Math.Max(_videoDuration, 1));
        BlurAreas.Add(area);
        SelectedBlurArea = area;
        NotifyBlurAreasChanged();
    }

    [RelayCommand]
    private void RemoveBlurArea(BlurAreaItem area)
    {
        var index = BlurAreas.IndexOf(area);
        if (index < 0)
            return;
        BlurAreas.RemoveAt(index);
        for (var i = 0; i < BlurAreas.Count; i++)
            BlurAreas[i].Number = i + 1;
        SelectedBlurArea = BlurAreas.Count == 0
            ? null
            : BlurAreas[Math.Min(index, BlurAreas.Count - 1)];
        NotifyBlurAreasChanged();
    }

    [RelayCommand]
    private async Task ToggleBlurredPreviewAsync()
    {
        IsShowingBlurredPreview = !IsShowingBlurredPreview;
        await ReloadBlurFrameAsync();
    }

    private void NotifyBlurAreasChanged()
    {
        OnPropertyChanged(nameof(BlurSummary));
        OnPropertyChanged(nameof(BlurButtonText));
    }

    partial void OnSelectedBlurAreaChanged(BlurAreaItem? oldValue, BlurAreaItem? newValue)
    {
        if (oldValue is not null)
            oldValue.PropertyChanged -= OnSelectedAreaPropertyChanged;
        if (newValue is not null)
            newValue.PropertyChanged += OnSelectedAreaPropertyChanged;

        // The scrub slider walks the selected area's own slice, so the frame under the box
        // is always one the blur will actually apply to.
        BlurPreviewMin = newValue?.StartSeconds ?? 0;
        BlurPreviewMax = Math.Max(newValue?.EndSeconds ?? 1, BlurPreviewMin + 0.1);
        BlurPreviewSeconds = BlurPreviewMin;
        _ = ReloadBlurFrameAsync();
    }

    private void OnSelectedAreaPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not BlurAreaItem area)
            return;
        if (e.PropertyName is nameof(BlurAreaItem.StartSeconds) or nameof(BlurAreaItem.EndSeconds))
        {
            BlurPreviewMin = area.StartSeconds;
            BlurPreviewMax = Math.Max(area.EndSeconds, area.StartSeconds + 0.1);
            BlurPreviewSeconds = Math.Clamp(BlurPreviewSeconds, BlurPreviewMin, BlurPreviewMax);
        }
        else if (IsShowingBlurredPreview)
        {
            // Geometry or strength changed while the real blur is on screen — re-render it.
            _ = ReloadBlurFrameAsync();
        }
    }

    partial void OnBlurPreviewSecondsChanged(double value) => _ = ReloadBlurFrameAsync();

    private CancellationTokenSource? _blurFrameCts;

    /// <summary>
    /// Loads the still under the placement box — plain, or actually blurred when the user
    /// asked to see the real thing. Dragging a slider fires this many times a second, so each
    /// call cancels the one before it and waits out a short settle before touching ffmpeg.
    /// </summary>
    private async Task ReloadBlurFrameAsync()
    {
        if (!IsBlurEditorOpen || !File.Exists(VideoPath))
            return;

        _blurFrameCts?.Cancel();
        _blurFrameCts?.Dispose();
        var cts = new CancellationTokenSource();
        _blurFrameCts = cts;
        // Hold the token, not the source: a later scrub cancels and disposes this source
        // while these awaits are still in flight, and reading .Token from a disposed source
        // would throw. A token struct stays readable after its source is gone.
        var token = cts.Token;
        try
        {
            await Task.Delay(120, token);
            var dir = Path.Combine(_workDir, "blur");
            var path = IsShowingBlurredPreview
                ? await PreviewBuilder.BuildBlurredFrameAsync(
                    VideoPath, BlurPreviewSeconds, CurrentBlurRegions(), dir, ct: token)
                : await ThumbnailExtractor.ExtractFrameAsync(
                    VideoPath, BlurPreviewSeconds, dir, ct: token);
            token.ThrowIfCancellationRequested();
            BlurFrame = new Avalonia.Media.Imaging.Bitmap(path);
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer scrub position — nothing to show for this one
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    /// <summary>The areas as the engine sees them, or nothing at all when blur is switched off.</summary>
    private IReadOnlyList<BlurRegion> CurrentBlurRegions() =>
        BlurEnabled ? BlurAreas.Select(a => a.ToRegion()).ToList() : [];

    [RelayCommand]
    private async Task GenerateAsync()
    {
        StopPlayback();
        IsClipPreviewOpen = false;
        IsBlurEditorOpen = false;
        ErrorMessage = "";
        var boundaries = Boundaries.Select(b => b.TimeSeconds).ToList();
        if (boundaries.Zip(boundaries.Skip(1)).Any(p => p.Second <= p.First) ||
            boundaries.Any(b => b <= 0 || b >= _videoDuration))
        {
            ErrorMessage = "Step boundaries must be in increasing order, inside the video.";
            return;
        }

        var blurRegions = CurrentBlurRegions();
        try
        {
            BlurRegion.ValidateAll(blurRegions, _videoDuration);
        }
        catch (ArgumentException ex)
        {
            ErrorMessage = ex.Message;
            return;
        }

        PageIndex = 2;
        IsBusy = true;
        IsDone = false;
        Log.Clear();
        _generateCts = new CancellationTokenSource();
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
                    SubtitleFont = SelectedSubtitleFont,
                    SubtitleFontSize = SelectedSubtitleSize?.Size ?? 16,
                    SubtitlePosition = SelectedSubtitlePosition?.Position ?? SubtitlePosition.Bottom,
                    SubtitleBackground = SelectedSubtitleBackground?.Style ?? SubtitleBackgroundStyle.Box,
                    BlurRegions = blurRegions,
                    WorkDir = Path.Combine(_workDir, "render"),
                },
                progress: new Progress<string>(Log.Add),
                ct: _generateCts.Token);
            FinalOutputPath = result.OutputPath;
            IsDone = true;
        }
        catch (OperationCanceledException)
        {
            Log.Add("Cancelled.");
        }
        catch (Exception ex)
        {
            Log.Add($"Failed: {ex.Message}");
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
            _generateCts?.Dispose();
            _generateCts = null;
        }
    }

    [RelayCommand]
    private void CancelGenerate() => _generateCts?.Cancel();

    /// <summary>Deletes the session's temp workspace (thumbnails, previews, render intermediates).
    /// Called when the window closes so corporate recordings are not left under %TEMP%.</summary>
    public void Cleanup()
    {
        try { if (Directory.Exists(_workDir)) Directory.Delete(_workDir, recursive: true); }
        catch { /* best effort */ }
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
