using System.Globalization;
using System.Text.Json;
using Vwm.Core;
using Vwm.Core.Script;
using Vwm.Core.Timeline;
using Vwm.Core.Tools;
using Vwm.Core.Tts;
using Vwm.Core.Video;

var (command, opts) = Args.Parse(args);
try
{
    return command switch
    {
        "make" => await MakeAsync(opts),
        "detect" => await DetectAsync(opts),
        "voices" => ListVoices(),
        _ => Usage(),
    };
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return Usage();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

static int Usage()
{
    Console.Error.WriteLine("""
        Video Walk-through Maker

        usage:
          vwm make --video <in.mp4> --script <script.txt> --out <out.mp4>
                   [--engine espeak|piper|windows] [--voice <piper voice id or windows voice name>]
                   [--piper-model <voice.onnx>] [--boundaries <boundaries.json>]
                   [--threshold 0.005] [--keep-original-audio] [--work-dir <dir>]
                   [--sub-font Arial] [--sub-size 16] [--sub-position bottom|middle|top]
                   [--sub-background box|shadow]

          vwm voices
                   List the bundled Piper voice ids.

          vwm detect --video <in.mp4> --steps <N> [--script <script.txt>] [--threshold 0.005]
                   Prints proposed step boundaries (seconds) as JSON. With --script,
                   step lengths from the script guide the placement.
        """);
    return 2;
}

static Vwm.Core.Render.SubtitlePosition ParseSubtitlePosition(string s) => s.ToLowerInvariant() switch
{
    "bottom" => Vwm.Core.Render.SubtitlePosition.Bottom,
    "middle" => Vwm.Core.Render.SubtitlePosition.Middle,
    "top" => Vwm.Core.Render.SubtitlePosition.Top,
    var p => throw new ArgumentException($"Unknown --sub-position '{p}'. Use bottom, middle or top."),
};

static Vwm.Core.Render.SubtitleBackgroundStyle ParseSubtitleBackground(string s) => s.ToLowerInvariant() switch
{
    "box" => Vwm.Core.Render.SubtitleBackgroundStyle.Box,
    "shadow" => Vwm.Core.Render.SubtitleBackgroundStyle.Shadow,
    var p => throw new ArgumentException($"Unknown --sub-background '{p}'. Use box or shadow."),
};

static ITtsEngine CreateEngine(Args opts) => opts.Get("engine", "piper") switch
{
    "piper" => CreatePiperEngine(opts),
    "espeak" => new EspeakTtsEngine(),
#if WINDOWS10_0_19041_0_OR_GREATER
    "windows" => new Vwm.Tts.Windows.WindowsTtsEngine(opts.GetOrNull("voice")),
#endif
    var e => throw new ArgumentException($"Unknown engine '{e}'. Use piper, espeak or windows."),
};

static PiperTtsEngine CreatePiperEngine(Args opts)
{
    if (opts.GetOrNull("piper-model") is string modelPath)
        return new PiperTtsEngine(modelPath: modelPath);
    if (opts.GetOrNull("voice") is string id)
    {
        var voice = PiperVoiceCatalog.FindById(id)
            ?? throw new ArgumentException($"Unknown voice '{id}'. Run 'vwm voices' to list the bundled voices.");
        return new PiperTtsEngine(voice);
    }
    return new PiperTtsEngine();
}

static int ListVoices()
{
    var voices = PiperVoiceCatalog.Enumerate();
    if (voices.Count == 0)
    {
        Console.Error.WriteLine("No Piper voices found (looked in 'tools/voices' next to the app).");
        return 1;
    }
    foreach (var v in voices)
        Console.WriteLine($"{v.Id,-30} {v.DisplayName}");
    return 0;
}

static async Task<int> MakeAsync(Args opts)
{
    var video = opts.Require("video");
    var script = await File.ReadAllTextAsync(opts.Require("script"));
    var output = opts.Require("out");

    IReadOnlyList<double>? boundaries = null;
    if (opts.GetOrNull("boundaries") is string bFile)
        boundaries = JsonSerializer.Deserialize<double[]>(await File.ReadAllTextAsync(bFile));

    var result = await WalkthroughPipeline.RunAsync(
        new PipelineOptions
        {
            VideoPath = video,
            ScriptText = script,
            OutputPath = output,
            TtsEngine = CreateEngine(opts),
            Boundaries = boundaries,
            SceneThreshold = opts.GetDouble("threshold", 0.005),
            KeepOriginalAudio = opts.Has("keep-original-audio"),
            SubtitleFont = opts.Get("sub-font", "Arial"),
            SubtitleFontSize = (int)opts.GetDouble("sub-size", 16),
            SubtitlePosition = ParseSubtitlePosition(opts.Get("sub-position", "bottom")),
            SubtitleBackground = ParseSubtitleBackground(opts.Get("sub-background", "box")),
            WorkDir = opts.GetOrNull("work-dir"),
        },
        progress: new Progress<string>(s => Console.WriteLine($"[vwm] {s}")));

    if (opts.GetOrNull("dump-plan") is string planPath)
    {
        var dump = new
        {
            total = result.Plan.TotalDuration,
            segments = result.Plan.Segments.Select(s => new
            {
                s.StepIndex, s.SourceStart, s.SourceEnd, s.HoldSeconds, s.OutputStart, s.OutputDuration,
            }),
            cues = result.Plan.Cues.Select(c => new { c.Text, c.Start, c.Duration }),
        };
        await File.WriteAllTextAsync(planPath,
            JsonSerializer.Serialize(dump, new JsonSerializerOptions { WriteIndented = true }));
    }

    Console.WriteLine($"[vwm] steps: {result.Steps.Count}, boundaries: " +
        string.Join(", ", result.Boundaries.Select(b => b.ToString("F2", CultureInfo.InvariantCulture))));
    Console.WriteLine($"[vwm] wrote {result.OutputPath} and {result.SrtPath}");
    return 0;
}

static async Task<int> DetectAsync(Args opts)
{
    var video = LocalPath.RequireInputFile(opts.Require("video"), "Input video");
    IReadOnlyList<double>? weights = null;
    int steps;
    if (opts.GetOrNull("script") is string scriptFile)
    {
        var parsed = ScriptParser.Parse(await File.ReadAllTextAsync(scriptFile));
        weights = NarrationEstimator.EstimateWeights(parsed);
        steps = parsed.Count;
    }
    else
    {
        steps = (int)opts.GetDouble("steps", 0);
        if (steps < 1)
            throw new ArgumentException("--steps must be a positive integer (or pass --script).");
    }

    var duration = await SceneDetector.GetDurationAsync(video);
    var cuts = await SceneDetector.DetectAsync(video, opts.GetDouble("threshold", 0.005));
    var boundaries = SceneDetector.ProposeBoundaries(cuts, duration, steps, weights);
    Console.WriteLine(JsonSerializer.Serialize(boundaries));
    return 0;
}

internal sealed class Args
{
    private readonly Dictionary<string, string?> _values = [];

    public static (string Command, Args Options) Parse(string[] args)
    {
        var parsed = new Args();
        var command = args.Length > 0 && !args[0].StartsWith('-') ? args[0] : "";
        for (var i = command == "" ? 0 : 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--"))
                throw new ArgumentException($"Unexpected argument '{args[i]}'.");
            var key = args[i][2..];
            var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--");
            parsed._values[key] = hasValue ? args[++i] : null;
        }
        return (command, parsed);
    }

    public bool Has(string key) => _values.ContainsKey(key);
    public string? GetOrNull(string key) => _values.GetValueOrDefault(key);
    public string Get(string key, string fallback) => GetOrNull(key) ?? fallback;
    public string Require(string key) => GetOrNull(key)
        ?? throw new ArgumentException($"Missing required option --{key}.");
    public double GetDouble(string key, double fallback) => GetOrNull(key) is string v
        ? double.Parse(v, CultureInfo.InvariantCulture)
        : fallback;
}
