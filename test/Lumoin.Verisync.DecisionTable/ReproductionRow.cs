namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// One published figure set beside the figure this harness reproduced for it.
/// </summary>
/// <param name="Row">The published row the figure belongs to.</param>
/// <param name="Metric">Which column of that row it is.</param>
/// <param name="Published">The published value.</param>
/// <param name="Reproduced">The value this harness measured.</param>
/// <param name="Tolerance">
/// Half the last published digit. A published rate carries three decimals and a published mean two, so a
/// value inside this band is one that rounds to the figure the note prints - which is exactly what
/// "reproduces the published row" means and the strictest reading the publication precision admits.
/// </param>
internal sealed record ReproductionRow(string Row, string Metric, double Published, double Reproduced, double Tolerance)
{
    /// <summary>
    /// A tolerance that is exactly half the last published digit is a boundary a decimal midpoint lands on,
    /// and no such midpoint is representable in binary: 0.2425 against 0.242 differs by 0.0005 plus a few
    /// bits. Without the slack the comparison would reject the one value that rounds to the published figure.
    /// </summary>
    private const double Slack = 1e-9;


    /// <summary>How far the reproduced figure lies from the published one.</summary>
    public double Difference => Reproduced - Published;

    /// <summary>Whether the reproduced figure lands inside the published figure's own precision.</summary>
    public bool IsReproduced => Math.Abs(Difference) <= Tolerance + Slack;
}
