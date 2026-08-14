using Lumoin.Verisync.Core;
using System.Globalization;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// The priority lattice's unit suite. Every <see cref="ulong"/> is a representable priority, so nothing is
/// validated at construction; what is constrained is what may be DRAWN, and the drawing path is pinned
/// here: rejection sampling excludes both reserved endpoints exactly, the eight entropy bytes are read
/// little-endian, and the production source is one delegate instance rather than a fresh one per read.
/// </summary>
[TestClass]
internal sealed class ProposalPriorityTests
{
    public TestContext TestContext { get; set; } = null!;


    [TestMethod]
    public void TheThreeNamedPrioritiesAreZeroOneAndTheWholeWord()
    {
        Assert.AreEqual(0UL, ProposalPriority.None.Value);
        Assert.AreEqual(1UL, ProposalPriority.Lowest.Value);
        Assert.AreEqual(ulong.MaxValue, ProposalPriority.Reserved.Value);
    }


    [TestMethod]
    public void ClassificationPartitionsTheWholeWordIntoNoneOrdinaryAndReserved()
    {
        Assert.IsTrue(ProposalPriority.None.IsNone);
        Assert.IsFalse(ProposalPriority.None.IsOrdinary);
        Assert.IsFalse(ProposalPriority.None.IsReserved);

        Assert.IsTrue(ProposalPriority.Lowest.IsOrdinary);
        Assert.IsFalse(ProposalPriority.Lowest.IsNone);
        Assert.IsFalse(ProposalPriority.Lowest.IsReserved);

        Assert.IsTrue(ProposalPriority.Reserved.IsReserved);
        Assert.IsFalse(ProposalPriority.Reserved.IsOrdinary);
        Assert.IsFalse(ProposalPriority.Reserved.IsNone);

        //The ordinary range is closed at both ends: one below the reserved endpoint is still ordinary.
        var justBelowReserved = new ProposalPriority(ProposalPriority.Reserved.Value - 1);
        Assert.IsTrue(justBelowReserved.IsOrdinary);
        Assert.IsFalse(justBelowReserved.IsReserved);
    }


    [TestMethod]
    public void DrawOrdinaryNeverReturnsNoneOrReservedOverALargeSample()
    {
        //A seeded generator rather than the cryptographic source, so a failing run replays exactly.
        var entropy = new SeededEntropySource(0x51ED_C0DE_1234_5678UL);
        const int SampleSize = 100_000;
        ulong lowest = ulong.MaxValue;
        ulong highest = 0;
        for(int i = 0; i < SampleSize; i++)
        {
            ProposalPriority priority = ProposalPriority.DrawOrdinary(entropy.Fill);

            Assert.IsTrue(priority.IsOrdinary);
            Assert.IsFalse(priority.IsNone);
            Assert.IsFalse(priority.IsReserved);
            lowest = Math.Min(lowest, priority.Value);
            highest = Math.Max(highest, priority.Value);
        }

        TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"draws={SampleSize}, fills={entropy.FillCount}, lowest={lowest}, highest={highest}"));
    }


    [TestMethod]
    public void DrawOrdinaryRejectsBothReservedEndpointsDeterministically()
    {
        //Scripted entropy walks the rejection path rather than waiting for it: all-zero bytes read as None,
        //all-one bytes read as Reserved, and only the third fill is a legal draw. Three fills prove the loop
        //rejects rather than merely never being seen to accept an endpoint.
        var entropy = new ScriptedEntropySource(
            [0, 0, 0, 0, 0, 0, 0, 0],
            [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF],
            [7, 0, 0, 0, 0, 0, 0, 0]);

        ProposalPriority priority = ProposalPriority.DrawOrdinary(entropy.Fill);

        Assert.AreEqual(7UL, priority.Value);
        Assert.IsTrue(priority.IsOrdinary);
        Assert.AreEqual(3, entropy.FillCount);
    }


    [TestMethod]
    public void DrawOrdinaryReadsTheEntropyBytesAsALittleEndianWord()
    {
        //The byte order is part of the contract because Q2 puts the same word on a wire; a big-endian read
        //would produce a different priority from the same entropy and silently change every tie-break.
        var entropy = new ScriptedEntropySource([0x02, 0x01, 0, 0, 0, 0, 0, 0]);

        ProposalPriority priority = ProposalPriority.DrawOrdinary(entropy.Fill);

        Assert.AreEqual(0x0102UL, priority.Value);
        Assert.AreEqual(1, entropy.FillCount);
    }


    [TestMethod]
    public void DrawOrdinaryWithANullEntropyDelegateThrows()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => _ = ProposalPriority.DrawOrdinary(null!));
    }


    [TestMethod]
    public void TheCryptographicSourceIsOneInstanceAndDrawsOrdinaryPriorities()
    {
        //An auto-property with an initializer hands back the same delegate every read; an expression-bodied
        //getter would mint a new one per read, which is a different thing and is not what is wanted. The two
        //reads go through locals because an assertion over two identical expressions is folded to a constant
        //by the analyzers and would stop testing anything.
        ProposalPrioritySourceDelegate firstRead = ProposalPriority.Cryptographic;
        ProposalPrioritySourceDelegate secondRead = ProposalPriority.Cryptographic;
        Assert.AreSame(firstRead, secondRead);

        ProposalPrioritySourceDelegate source = ProposalPriority.Cryptographic;
        for(int i = 0; i < 1_000; i++)
        {
            Assert.IsTrue(source().IsOrdinary);
        }
    }


    [TestMethod]
    public void ComparisonOrdersByValue()
    {
        var low = new ProposalPriority(10);
        var high = new ProposalPriority(20);

        //Reflexivity is asserted through a separately constructed equal priority rather than through the same
        //variable twice, because comparing one variable with itself is a compile error under the analyzers
        //and would in any case test the variable rather than the relation.
        var alsoLow = new ProposalPriority(10);

        Assert.IsTrue(low < high);
        Assert.IsTrue(low <= high);
        Assert.IsTrue(high > low);
        Assert.IsTrue(high >= low);
        Assert.IsTrue(low <= alsoLow);
        Assert.IsTrue(low >= alsoLow);
        Assert.IsFalse(low > high);
        Assert.IsLessThan(0, low.CompareTo(high));
        Assert.IsGreaterThan(0, high.CompareTo(low));
        Assert.AreEqual(0, low.CompareTo(alsoLow));

        //None is the identity of the aggregate and sorts below every ordinary priority; Reserved sorts above.
        Assert.IsTrue(ProposalPriority.None < ProposalPriority.Lowest);
        Assert.IsTrue(ProposalPriority.Lowest < high);
        Assert.IsTrue(high < ProposalPriority.Reserved);
    }


    /// <summary>
    /// Entropy scripted fill by fill, so the rejection path is walked rather than waited for.
    /// </summary>
    /// <remarks>
    /// The last fill repeats once the script runs out, which keeps a defective implementation from spinning
    /// forever.
    /// </remarks>
    private sealed class ScriptedEntropySource
    {
        private int index;

        public ScriptedEntropySource(params byte[][] fills) => Fills = fills;


        public int FillCount { get; private set; }


        private byte[][] Fills { get; }


        public void Fill(Span<byte> destination)
        {
            byte[] fill = Fills[Math.Min(index, Fills.Length - 1)];
            index++;
            FillCount++;
            fill.AsSpan(0, destination.Length).CopyTo(destination);
        }
    }


    /// <summary>
    /// Xorshift64 rather than System.Random: the sequence is deterministic across runtimes and platforms, so a
    /// seed printed by a failing run replays the identical sample anywhere.
    /// </summary>
    /// <remarks>
    /// State must be nonzero.
    /// </remarks>
    private sealed class SeededEntropySource
    {
        private ulong state;

        public SeededEntropySource(ulong seed) => state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;


        public int FillCount { get; private set; }


        public void Fill(Span<byte> destination)
        {
            FillCount++;
            for(int i = 0; i < destination.Length; i++)
            {
                state ^= state << 13;
                state ^= state >> 7;
                state ^= state << 17;
                destination[i] = (byte)state;
            }
        }
    }
}
