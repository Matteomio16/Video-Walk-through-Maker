using Vwm.Core.Script;

namespace Vwm.Core.Timeline;

/// <summary>
/// Estimates each step's relative narration length from its text, before any audio
/// exists. Only the proportions matter — they serve as a prior for placing step
/// boundaries in videos whose steps are very unevenly sized.
/// </summary>
public static class NarrationEstimator
{
    /// <summary>Extra weight per sentence, in "characters": accounts for lead-in/gap/tail pauses.</summary>
    private const int PauseCharsPerSentence = 18;

    public static IReadOnlyList<double> EstimateWeights(IReadOnlyList<ScriptStep> steps) =>
        steps.Select(s => (double)Math.Max(1, s.Text.Length + s.Sentences.Count * PauseCharsPerSentence))
             .ToList();
}
