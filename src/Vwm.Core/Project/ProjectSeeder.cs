using Vwm.Core.Script;

namespace Vwm.Core.Project;

/// <summary>
/// Builds the initial editable project from the same inputs the one-shot flow uses
/// (script text + proposed boundaries), so "analyze" now yields an editable project.
/// Each parsed step becomes a cue whose freeze region is that step's video slice.
/// </summary>
public static class ProjectSeeder
{
    public static WalkthroughProject FromScript(
        string videoPath,
        double videoDuration,
        string scriptText,
        IReadOnlyList<double> boundaries,
        string globalVoice,
        SubtitleStyleSettings globalSubtitle,
        bool keepOriginalAudio = false)
    {
        var steps = ScriptParser.Parse(scriptText);
        if (steps.Count == 0)
            throw new InvalidOperationException("The script is empty - nothing to narrate.");
        if (boundaries.Count != steps.Count - 1)
            throw new ArgumentException(
                $"Expected {steps.Count - 1} boundaries for {steps.Count} steps, got {boundaries.Count}.", nameof(boundaries));

        var cues = new List<Cue>(steps.Count);
        for (var i = 0; i < steps.Count; i++)
        {
            var start = i == 0 ? 0 : boundaries[i - 1];
            var end = i == steps.Count - 1 ? videoDuration : boundaries[i];
            cues.Add(new Cue { Text = steps[i].Text, SourceStart = start, SourceEnd = end });
        }

        return new WalkthroughProject
        {
            VideoPath = videoPath,
            VideoDuration = videoDuration,
            KeepOriginalAudio = keepOriginalAudio,
            GlobalVoice = globalVoice,
            GlobalSubtitle = globalSubtitle,
            Cues = cues,
        };
    }
}
