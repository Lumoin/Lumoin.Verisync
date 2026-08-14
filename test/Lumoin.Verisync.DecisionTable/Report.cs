using System.Globalization;

namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// The harness's output, formatted invariantly.
/// </summary>
/// <remarks>
/// Every number this harness prints goes through <see cref="CultureInfo.InvariantCulture"/>, because a table
/// of measurements whose decimal separator depends on the machine that produced it is not comparable with the
/// table beside it.
/// </remarks>
internal static class Report
{
    /// <summary>Writes an interpolated line, formatted invariantly.</summary>
    /// <param name="text">The line.</param>
    public static void Line(FormattableString text)
    {
        ArgumentNullException.ThrowIfNull(text);

        Console.Out.WriteLine(FormattableString.Invariant(text));
    }


    /// <summary>Writes a line that carries no numbers.</summary>
    /// <param name="text">The line.</param>
    public static void Text(string text) => Console.Out.WriteLine(text);


    /// <summary>Writes a blank line.</summary>
    public static void Blank() => Console.Out.WriteLine();
}
