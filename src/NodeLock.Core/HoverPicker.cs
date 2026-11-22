namespace NodeLock.Core;

public readonly record struct Candidate(long Id, double Distance, double Depth);
public static class HoverPicker
{
    public const double AcquireRadius = 28;
    public const double RetainRadius = 36;
    public static long? Pick(IEnumerable<Candidate> source, long? previous, double scale = 1)
    {
        if (!double.IsFinite(scale) || scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
        var candidates = source.Where(c => double.IsFinite(c.Distance) && c.Distance >= 0 && double.IsFinite(c.Depth) && c.Depth > 0).ToArray();
        var direct = candidates.Where(c => c.Distance <= 3 * scale).ToArray();
        var eligible = direct.Length > 0 ? direct : candidates.Where(c => c.Distance <= AcquireRadius * scale).ToArray();
        Candidate? old = candidates.Where(c => c.Id == previous && c.Distance <= RetainRadius * scale).Select(c => (Candidate?)c).FirstOrDefault();
        if (eligible.Length == 0) return old?.Id;
        double min = eligible.Min(c => c.Distance);
        var tied = eligible.Where(c => c.Distance <= min + scale).ToArray();
        var winner = tied.OrderBy(c => c.Depth).ThenBy(c => c.Id).First();
        if (direct.Length > 0 || old is null || tied.Any(c => c.Id == old.Value.Id)) return winner.Id;
        return winner.Distance <= old.Value.Distance - 3 * scale ? winner.Id : old.Value.Id;
    }
}
