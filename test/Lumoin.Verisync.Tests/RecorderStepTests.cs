using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// The threshold logical clock's unit suite. A step is <c>4 * round + phase</c> over a bounded range, the
/// bound is enforced on the constructor and on a <c>with</c> expression alike, and the successor fails
/// closed at the top of the range so that an exhausted budget cannot be stepped past.
/// </summary>
[TestClass]
internal sealed class RecorderStepTests
{
    /// <summary>
    /// THE BOUND IS A LITERAL AND NOT A RESTATEMENT OF THE CONSTANT. Every other assertion about the range is
    /// written relative to MaxRound, so lowering the constant would keep them all true while quietly shortening
    /// the protocol's budget and diverging from both reference implementations, which take 256 complete rounds.
    /// </summary>
    [TestMethod]
    public void TheStepBudgetIsTwoHundredAndFiftySixCompleteRounds()
    {
        //The bound is read back through the step rather than off the constant, because an assertion whose
        //operands are both compile-time constants is folded and stops testing anything.
        Assert.AreEqual(1027, RecorderStep.MaxValue.Value);
        Assert.AreEqual(256, RecorderStep.MaxValue.Round);
        Assert.AreEqual(3, RecorderStep.MaxValue.Phase);
        Assert.AreEqual(4, RecorderStep.RoundOnePhaseZero.Value);
    }


    /// <summary>
    /// THE ROUND BOUND IS CHECKED ON THE ROUND AND BEFORE THE ARITHMETIC. Deleting that check leaves every
    /// ordinary rejection still throwing, because a wrapped or scaled result usually lands outside the value
    /// range and the value validator catches it.
    /// </summary>
    /// <remarks>
    /// This round is the case that does not: four times two to the thirtieth is zero modulo two to the
    /// thirty-second, so without the round check the call would silently return a legal step two.
    /// </remarks>
    [TestMethod]
    public void AnOverflowingRoundIsRejectedOnTheRoundRatherThanWrappingToALegalStep()
    {
        ArgumentOutOfRangeException thrown = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = RecorderStep.FromRoundAndPhase(1 << 30, 2));

        Assert.AreEqual("round", thrown.ParamName);
    }


    [TestMethod]
    public void FromRoundAndPhaseRoundTripsAgainstRoundAndPhaseAcrossTheWholeLegalRange()
    {
        for(int round = 0; round <= RecorderStep.MaxRound; round++)
        {
            for(int phase = 0; phase < 4; phase++)
            {
                RecorderStep step = RecorderStep.FromRoundAndPhase(round, phase);

                Assert.AreEqual(round, step.Round);
                Assert.AreEqual(phase, step.Phase);
                Assert.AreEqual((4 * round) + phase, step.Value);
            }
        }
    }


    [TestMethod]
    public void MaxValueIsRoundTwoHundredFiftySixPhaseThreeAndIsExhausted()
    {
        Assert.AreEqual(RecorderStep.MaxRound, RecorderStep.MaxValue.Round);
        Assert.AreEqual(3, RecorderStep.MaxValue.Phase);
        Assert.AreEqual((4 * RecorderStep.MaxRound) + 3, RecorderStep.MaxValue.Value);
        Assert.IsTrue(RecorderStep.MaxValue.IsExhausted);
    }


    [TestMethod]
    public void NextAtMaxValueThrows()
    {
        //The throw is the fail-closed backstop behind the IsExhausted check callers are expected to make.
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = RecorderStep.MaxValue.Next());
    }


    [TestMethod]
    public void NextBelowMaxValueIsTheImmediateSuccessor()
    {
        RecorderStep step = RecorderStep.RoundOnePhaseZero;
        RecorderStep next = step.Next();

        Assert.AreEqual(step.Value + 1, next.Value);
        Assert.IsTrue(next.IsNextAfter(step));
        Assert.IsFalse(step.IsExhausted);
    }


    [TestMethod]
    public void IsNextAfterHoldsOnlyForTheImmediateSuccessor()
    {
        RecorderStep step = RecorderStep.RoundOnePhaseZero;

        Assert.IsTrue(step.Next().IsNextAfter(step));
        Assert.IsFalse(step.Next().Next().IsNextAfter(step));
        Assert.IsFalse(step.IsNextAfter(step));
        Assert.IsFalse(step.IsNextAfter(step.Next()));
    }


    [TestMethod]
    public void FromRoundAndPhaseRejectsANegativeRound()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = RecorderStep.FromRoundAndPhase(-1, 0));
    }


    [TestMethod]
    public void FromRoundAndPhaseRejectsAPhaseOutsideZeroToThree()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = RecorderStep.FromRoundAndPhase(1, -1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = RecorderStep.FromRoundAndPhase(1, 4));
    }


    [TestMethod]
    public void FromRoundAndPhaseRejectsARoundAboveMaxRound()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = RecorderStep.FromRoundAndPhase(RecorderStep.MaxRound + 1, 0));
    }


    [TestMethod]
    public void FromRoundAndPhaseRejectsARoundLargeEnoughToWrapTheStepArithmetic()
    {
        //The round bound is stated on the round and checked BEFORE the arithmetic: 4 * round + phase wraps
        //negative for a large round in an unchecked context, so a validator running after the multiplication
        //would either raise the wrong complaint or none at all.
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = RecorderStep.FromRoundAndPhase(int.MaxValue, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = RecorderStep.FromRoundAndPhase(int.MaxValue / 2, 3));
    }


    [TestMethod]
    public void TheConstructorRejectsAValueOutsideTheLegalRange()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = new RecorderStep(-1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = new RecorderStep(RecorderStep.MaxValue.Value + 1));
    }


    [TestMethod]
    public void AWithExpressionCarryingAnOutOfRangeValueThrowsInBothDirections()
    {
        //A positional record struct suppresses validation unless the accessor validates too, so the bound is
        //pinned on the copy path in both directions rather than on the constructor alone.
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = RecorderStep.RoundOnePhaseZero with { Value = -1 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = RecorderStep.RoundOnePhaseZero with { Value = RecorderStep.MaxValue.Value + 1 });
    }


    [TestMethod]
    public void AWithExpressionCarryingALegalValueIsAccepted()
    {
        RecorderStep step = RecorderStep.RoundOnePhaseZero with { Value = 9 };

        Assert.AreEqual(9, step.Value);
        Assert.AreEqual(2, step.Round);
        Assert.AreEqual(1, step.Phase);
    }


    [TestMethod]
    public void TheDefaultIsZeroAndZeroIsALegalStep()
    {
        //The zero value must be legal because no accessor can defend a default; it is the step of a register
        //that was never written, and no request ever carries it.
        Assert.AreEqual(RecorderStep.Zero, default(RecorderStep));
        Assert.AreEqual(0, RecorderStep.Zero.Value);
        Assert.AreEqual(0, RecorderStep.Zero.Round);
        Assert.AreEqual(0, RecorderStep.Zero.Phase);
        Assert.IsFalse(RecorderStep.Zero.IsExhausted);
    }


    [TestMethod]
    public void RoundOnePhaseZeroIsStepFourAndIsTheProtocolsFirstStep()
    {
        Assert.AreEqual(4, RecorderStep.RoundOnePhaseZero.Value);
        Assert.AreEqual(1, RecorderStep.RoundOnePhaseZero.Round);
        Assert.AreEqual(0, RecorderStep.RoundOnePhaseZero.Phase);
        Assert.IsTrue(RecorderStep.RoundOnePhaseZero > RecorderStep.Zero);
    }


    [TestMethod]
    public void ComparisonOrdersByValue()
    {
        RecorderStep four = RecorderStep.RoundOnePhaseZero;
        RecorderStep five = four.Next();

        //Reflexivity is asserted through a separately constructed equal step rather than through the same
        //variable twice, because comparing one variable with itself is a compile error under the analyzers
        //and would in any case test the variable rather than the relation.
        RecorderStep alsoFour = RecorderStep.FromRoundAndPhase(1, 0);

        Assert.IsTrue(four < five);
        Assert.IsTrue(four <= five);
        Assert.IsTrue(five > four);
        Assert.IsTrue(five >= four);
        Assert.IsTrue(four <= alsoFour);
        Assert.IsTrue(four >= alsoFour);
        Assert.IsFalse(four < alsoFour);
        Assert.IsFalse(four > alsoFour);
        Assert.IsGreaterThan(0, five.CompareTo(four));
        Assert.IsLessThan(0, four.CompareTo(five));
        Assert.AreEqual(0, four.CompareTo(alsoFour));
    }
}
