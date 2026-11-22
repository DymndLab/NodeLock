namespace NodeLock.Core;

public readonly record struct SourceCandidate(int Index, double Alignment);

public static class SourcePicker
{
    // Reject sides more than 60 degrees away from mating. Fine rotation stays
    // on the same connector until another side is clearly better.
    public const double MinimumAlignment = 0.5;
    public const double SwitchMargin = 0.15;
    public static int? Pick(IEnumerable<SourceCandidate> candidates, int previous)
    {
        var eligible = candidates.Where(c => double.IsFinite(c.Alignment) && c.Alignment >= MinimumAlignment)
            .OrderByDescending(c => c.Alignment).ThenBy(c => c.Index).ToArray();
        if (eligible.Length == 0) return null;
        var retained = eligible.FirstOrDefault(c => c.Index == previous);
        if (eligible.Any(c => c.Index == previous) && eligible[0].Alignment - retained.Alignment < SwitchMargin)
            return previous;
        return eligible[0].Index;
    }
}
