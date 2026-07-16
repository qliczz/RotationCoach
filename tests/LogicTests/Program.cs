using RotationCoach;

static void Near(double expected, double actual, string field)
{
    if (Math.Abs(expected - actual) > 0.001)
        throw new InvalidOperationException($"{field}: expected {expected}, actual {actual}");
}

var log = new Dictionary<uint, List<ActionCast>>
{
    [0] = new()
    {
        new() { TimeMs = 0, IsGcd = true },
        new() { TimeMs = 2500, IsGcd = true },
        new() { TimeMs = 5200, IsGcd = true },
        new() { TimeMs = 10200, IsGcd = true },
    },
};

var report = RotationAnalysis.Analyze(log, 0, 12, null!);
Near(2.5, report.GcdCadenceSec, "GCD baseline");
Near(2.5, report.IdleSec, "idle counts only excess time");
if (report.Issues.Count(x => x.Kind == "clip") != 1)
    throw new InvalidOperationException("expected one delayed-GCD clip issue");
if (report.Issues.Count(x => x.Kind == "idle") != 1)
    throw new InvalidOperationException("expected one downtime issue");

Console.WriteLine("Rotation analysis logic tests passed.");
