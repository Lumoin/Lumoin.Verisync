using System.Text.RegularExpressions;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// The documented exception contract against the exceptions the source actually raises.
/// </summary>
/// <remarks>
/// <para>
/// THE COMPILER CANNOT CHECK THIS AND HAS NEVER CLAIMED TO. Turning on documentation generation proves every
/// <c>cref</c> RESOLVES; nothing proves the documented exception is the one thrown. A tag naming a base type
/// above a member that raises a library type is well formed and false, and it builds at zero warnings for as
/// long as nobody reads it. That is not hypothetical: the typed refusal families shipped with every affected
/// member still documenting <c>ArgumentException</c> or <c>InvalidOperationException</c>, and the consumer
/// that met them found them by a row failing rather than by reading the contract.
/// </para>
/// <para>
/// The check is over source rather than reflection because the fact it needs — which exception a member
/// constructs — exists only in the source. Reflection sees a member's signature and its documentation and
/// cannot see a <c>throw</c>. The rule is therefore per file rather than per member: a file that raises one
/// of these types names it in an exception tag somewhere in that same file. That is coarse, and it is enough,
/// because the defect it exists to catch is a whole family shipping undocumented.
/// </para>
/// </remarks>
[TestClass]
internal sealed class ExceptionDocumentationTests
{
    public TestContext TestContext { get; set; } = null!;

    /// <summary>The exception types this library defines and therefore owes documentation for.</summary>
    private static string[] Owned { get; } = ["StateRestoreException", "ConsensusRefusedException", "MessageDeserializationException"];


    [TestMethod]
    public void EveryFileRaisingALibraryExceptionDocumentsThatException()
    {
        List<string> undocumented = [];

        foreach (string file in SourceFiles())
        {
            string source = File.ReadAllText(file);

            foreach (string owned in Owned)
            {
                if (!Regex.IsMatch(source, @"throw new " + owned + @"\b"))
                {
                    continue;
                }

                if (!source.Contains("cref=\"" + owned + "\"", StringComparison.Ordinal))
                {
                    undocumented.Add($"{Path.GetFileName(file)} raises {owned} and no exception tag names it.");
                }
            }
        }

        Assert.IsEmpty(undocumented, string.Join(" ", undocumented));
    }


    /// <summary>
    /// A member documenting one of the refusal carriers points at the reason a caller switches on.
    /// </summary>
    /// <remarks>
    /// Naming the type alone would leave a reader knowing something typed is thrown and not what to do with
    /// it. The enum is the thing the carrier exists for, so a file that documents the carrier documents the
    /// reason beside it.
    /// </remarks>
    [TestMethod]
    public void EveryDocumentedRefusalCarrierPointsAtItsReason()
    {
        (string Carrier, string Reason)[] pairs =
        [
            ("StateRestoreException", "StateRestoreRefusal"),
            ("ConsensusRefusedException", "ConsensusRefusal")
        ];

        List<string> unreasoned = [];

        foreach (string file in SourceFiles())
        {
            string source = File.ReadAllText(file);

            foreach ((string carrier, string reason) in pairs)
            {
                //The carrier's own declaration documents the type itself rather than a member that raises it.
                if (Path.GetFileNameWithoutExtension(file) == carrier || Path.GetFileNameWithoutExtension(file) == reason)
                {
                    continue;
                }

                if (!Regex.IsMatch(source, @"throw new " + carrier + @"\b"))
                {
                    continue;
                }

                if (!source.Contains("cref=\"" + reason + ".", StringComparison.Ordinal))
                {
                    unreasoned.Add($"{Path.GetFileName(file)} raises {carrier} without naming a {reason} member.");
                }
            }
        }

        Assert.IsEmpty(unreasoned, string.Join(" ", unreasoned));
    }


    /// <summary>Every shipped source file of the library.</summary>
    /// <returns>Their paths.</returns>
    /// <remarks>
    /// Generated and intermediate output is excluded, because it carries copies whose staleness says nothing
    /// about the source this check is over.
    /// </remarks>
    private static IEnumerable<string> SourceFiles()
    {
        string root = SourceRoot();

        return Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }


    /// <summary>The repository's source directory, found by walking up from the test assembly.</summary>
    /// <returns>That directory.</returns>
    private static string SourceRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "src");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("The repository's src directory was not found above the test assembly, so the documented exception contract cannot be read.");
    }
}
