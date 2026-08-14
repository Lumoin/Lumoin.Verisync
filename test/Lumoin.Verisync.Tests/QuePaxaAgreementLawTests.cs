using CsCheck;
using Lumoin.Verisync.Core;
using System.Collections.Immutable;
using System.Globalization;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// The slice's real gate: the executable analogue of the model's agreement invariant. A generated schedule
/// interleaves two or three proposers over three or five recorders, one protocol step at a time against a
/// chosen recorder subset, and agreement is checked over the decisions the whole run produced.
/// </summary>
/// <remarks>
/// <para>
/// Three properties of the schedule generator are load-bearing. Subsets are drawn at EXACTLY quorum size,
/// because a full-set subset reduces contention to nothing while a sub-quorum subset only wastes steps.
/// Every schedule opens with a forced contention prefix, so the recorders hold divergent first proposals by
/// construction rather than by luck. And no round value is ever stepped twice: exactly-quorum subsets mean
/// a quorum miss never arises, so there is nothing to recover from, and a proposer that receives an
/// exhausted budget halts. That last one is not a convenience - re-stepping a round is outside the checked
/// behaviour, so a harness that retried would be generating schedules the model says nothing about and
/// calling the result evidence.
/// </para>
/// <para>
/// WHAT THIS HARNESS CANNOT REACH AT ALL, stated here because a green run must not be read as matching the
/// model's exhaustive state space. A step records at its chosen recorders ATOMICALLY, so one proposer's
/// per-recorder records can never straddle another proposer's step on an overlapping recorder. The module
/// interleaves at the level of a single request and admits reply sets this surface cannot produce. The
/// inclusion runs in the safe direction - every behaviour here is a module behaviour - so a green run is
/// sound evidence about strictly fewer schedules, and the missing interleavings belong to the asynchronous
/// node that comes later.
/// </para>
/// </remarks>
[TestClass]
internal sealed class QuePaxaAgreementLawTests
{
    /// <summary>
    /// Identities are built from fixed bytes so that A sorts below B by lexicographic byte order.
    /// </summary>
    /// <remarks>
    /// This is not tidiness: the hazard below diverges ONLY when the losing proposer's lane sorts ABOVE the
    /// winner's, so with generated identities it would fail on a correct implementation about half the time.
    /// </remarks>
    private static ProposerLane LaneA { get; } = ProposerLane.For(Replica(1));
    private static ProposerLane LaneB { get; } = ProposerLane.For(Replica(2));
    private static ProposerLane LaneC { get; } = ProposerLane.For(Replica(3));
    private static ProposerLane LaneD { get; } = ProposerLane.For(Replica(4));

    private static ImmutableArray<ProposerLane> Lanes { get; } = [LaneA, LaneB, LaneC];

    private static Gen<(int RecorderCount, int ProposerCount, int[] Seeds, ulong PrioritySeed)> GenSchedule { get; } =
        Gen.Select(
            Gen.Int[0, 1],
            Gen.Int[2, 3],
            Gen.Int[0, 1_000_000].Array[2, 20],
            Gen.Int[1, 1_000_000],
            static (size, proposers, seeds, prioritySeed) => (size == 0 ? 3 : 5, proposers, seeds, (ulong)prioritySeed));


    public TestContext TestContext { get; set; } = null!;


    /// <summary>
    /// A1 AGREEMENT. No two decided outcomes carry different values, with every recorder configured with the
    /// same leader, over every generated schedule.
    /// </summary>
    [TestMethod]
    public void NoTwoDecisionsCarryDifferentValuesWhenTheRecordersAgreeOnTheLeader()
    {
        GenSchedule.Sample(static input =>
        {
            RunResult result = RunSchedule(input.RecorderCount, input.ProposerCount, leadered: true, input.Seeds, input.PrioritySeed);

            AssertAtMostOneDecidedValue(result);
        });
    }


    /// <summary>
    /// A1R REACH, DETERMINISTIC. Without it a green A1 is indistinguishable from a harness that never split a
    /// quorum.
    /// </summary>
    /// <remarks>
    /// The prefix alone leaves the recorders holding divergent first proposals, and the next fast-path
    /// evaluation across the split is refused while the same evaluation inside one side still fires.
    /// </remarks>
    [TestMethod]
    public void TheContentionPrefixReachesTheSplitQuorumRegionByConstruction()
    {
        QuePaxaRegister<string> register = QuePaxaRegister<string>.LedBy(3, LaneA);
        var source = new SeededPrioritySource(4242);

        ImmutableArray<int> lowWindow = [0, 1];
        ImmutableArray<int> highWindow = [1, 2];
        (register, QuePaxaStepOutcome<string> leaderOutcome) = register.Step(QuePaxaRound<string>.Begin(LaneA, LaneA, "a"), lowWindow, source.Next);
        (register, _) = register.Step(QuePaxaRound<string>.Begin(LaneB, LaneB, "b"), highWindow, source.Next);

        //The prefix is what makes the split: the configured leader's reserved claim stands at recorders zero
        //and one, and the rival's declined claim is the first proposal at recorder two.
        Assert.AreEqual(QuePaxaStepKind.Decided, leaderOutcome.Kind);
        Assert.AreEqual(register.Recorders[0].Register.First, register.Recorders[1].Register.First);
        Assert.AreNotEqual(register.Recorders[0].Register.First, register.Recorders[2].Register.First);
        Assert.AreEqual(ProposalPriority.Reserved, register.Recorders[0].Register.First!.Key.Priority);
        Assert.AreEqual(ProposalPriority.Lowest, register.Recorders[2].Register.First!.Key.Priority);

        //A quorum spanning the split sees two different first proposals, so the whole-proposal identical test
        //refuses the fast path.
        (_, QuePaxaStepOutcome<string> acrossTheSplit) = register.Step(QuePaxaRound<string>.Begin(LaneC, LaneA, "c"), [0, 2], source.Next);
        Assert.AreNotEqual(QuePaxaStepKind.Decided, acrossTheSplit.Kind);

        //The contrast that makes the refusal mean something: a quorum wholly inside the leader's side sees a
        //uniform reserved first and does decide, so the refusal above is caused by the split and by nothing
        //else.
        (_, QuePaxaStepOutcome<string> insideOneSide) = register.Step(QuePaxaRound<string>.Begin(LaneD, LaneA, "d"), [0, 1], source.Next);
        Assert.AreEqual(QuePaxaStepKind.Decided, insideOneSide.Kind);
        Assert.AreEqual("a", insideOneSide.DecidedValue);
        Assert.AreEqual(LaneA, insideOneSide.DecidedBy);
    }


    /// <summary>
    /// A2 VALIDITY. Every decided value is one some proposer began with, so nothing is invented by the fold,
    /// by the carry, or by a restamped priority.
    /// </summary>
    [TestMethod]
    public void EveryDecidedValueIsOneSomeProposerBeganWith()
    {
        GenSchedule.Sample(static input =>
        {
            RunResult result = RunSchedule(input.RecorderCount, input.ProposerCount, leadered: true, input.Seeds, input.PrioritySeed);

            foreach(Decision decision in result.Decisions)
            {
                Assert.Contains(decision.Value, result.BegunValues, "A value was decided that no proposer began with.");
            }
        });
    }


    /// <summary>
    /// A3 THE CARRY. A proposer that does not decide carries the greatest key it observed: at a phase-zero
    /// step the greatest first proposal across the recorders it reached, and at a phase-three step the
    /// greatest prior aggregate.
    /// </summary>
    /// <remarks>
    /// A later round therefore cannot regress to a lower key.
    /// </remarks>
    [TestMethod]
    public void ANonDecidingStepCarriesTheGreatestKeyItObserved()
    {
        GenSchedule.Sample(static input =>
        {
            RunResult result = RunSchedule(input.RecorderCount, input.ProposerCount, leadered: true, input.Seeds, input.PrioritySeed);

            foreach(CarryObservation carry in result.Carries)
            {
                Assert.AreEqual(carry.GreatestObservedKey, carry.CarriedKey, "A carry took something other than the greatest key it observed.");
            }
        });
    }


    /// <summary>
    /// A4 LEADERLESS AGREEMENT. Every reserved claim is declined at every recorder, so no first proposal ever
    /// carries the reserved priority and the fast path can never fire.
    /// </summary>
    /// <remarks>
    /// The step-four decision is ASSERTED absent rather than assumed absent.
    /// </remarks>
    [TestMethod]
    public void LeaderlessRecordersAgreeAndNeverDecideAtStepFour()
    {
        GenSchedule.Sample(static input =>
        {
            RunResult result = RunSchedule(input.RecorderCount, input.ProposerCount, leadered: false, input.Seeds, input.PrioritySeed);

            AssertAtMostOneDecidedValue(result);
            foreach(Decision decision in result.Decisions)
            {
                Assert.AreNotEqual(RecorderStep.RoundOnePhaseZero, decision.At, "A leaderless register decided on the fast path.");
            }
        });
    }


    /// <summary>
    /// A4 REACH, DETERMINISTIC. The leaderless law would be vacuous over a run that decided nothing at all, so
    /// one fixed schedule is pinned to reach a decision, and to reach it in the ordinary phases.
    /// </summary>
    [TestMethod]
    public void TheLeaderlessScheduleReachesADecisionByConstruction()
    {
        int[] seeds = [3, 11, 5, 8, 2, 14, 7, 1, 9, 4, 12, 6];

        RunResult result = RunSchedule(3, 2, leadered: false, seeds, 20_260_807UL);

        TestContext.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"recorders=3, proposers=2, steps={result.Steps}, decisions={result.Decisions.Length}, carries={result.Carries.Length}"));

        Assert.IsGreaterThan(0, result.Decisions.Length, "The leaderless pin must reach a decision or the law it guards is vacuous.");
        foreach(Decision decision in result.Decisions)
        {
            Assert.AreNotEqual(RecorderStep.RoundOnePhaseZero, decision.At);
        }
    }


    /// <summary>
    /// A5 THE HAZARD, PINNED AS A FULLY DETERMINISTIC NEGATIVE rather than generated, because the divergence
    /// holds only on a narrow family of schedules and most schedules converge, so a generated version would be
    /// seed luck at a few percent.
    /// </summary>
    /// <remarks>
    /// A &lt; B IS LOAD-BEARING AND THE TEST IS BUILT ON IT. When the fast path is refused, the fall-through
    /// takes the GREATEST first proposal, and two reserved-priority proposals tie on priority, so the proposer
    /// identifier settles it. With A &lt; B the fall-through hands B its OWN proposal and B goes on to decide
    /// its own value against the value A already decided. With the order reversed the same schedule CONVERGES,
    /// because the fall-through would hand B the value A had decided. That is not a weakness of the pin: it is
    /// the mechanism itself, where the outcome is settled by the proposer identifier rather than by which
    /// proposal a majority actually recorded first.
    /// </remarks>
    [TestMethod]
    public void DisagreeingRecordersDivergeWhenTheLosingProposersLaneSortsAboveTheWinners()
    {
        Assert.IsTrue(LaneA < LaneB, "The divergence this test pins exists only while A sorts below B.");

        //Recorders zero and two are configured with A, recorder one with B, which is exactly the deployment
        //failure the agreed configuration exists to prevent.
        ImmutableArray<QuePaxaRecorder<string>> recorders =
        [
            QuePaxaRecorder<string>.LedBy(LaneA),
            QuePaxaRecorder<string>.LedBy(LaneB),
            QuePaxaRecorder<string>.LedBy(LaneA)
        ];
        QuePaxaRegister<string> register = QuePaxaRegister<string>.FromRecorders(recorders);

        //Every step below is at phase zero with a leadership claim, or at phase one or two, so no priority is
        //ever drawn; a source that throws proves the whole scenario is constructed rather than sampled.
        ProposalPrioritySourceDelegate never = static () => throw new InvalidOperationException("The hazard scenario draws no priority.");

        (register, QuePaxaStepOutcome<string> aAtFour) = register.Step(QuePaxaRound<string>.Begin(LaneA, LaneA, "a"), [0, 2], never);

        Assert.AreEqual(QuePaxaStepKind.Decided, aAtFour.Kind);
        Assert.AreEqual("a", aAtFour.DecidedValue);
        Assert.AreEqual(LaneA, aAtFour.DecidedBy);
        Assert.AreEqual(RecorderStep.RoundOnePhaseZero, aAtFour.DecidedAt);

        //Recorder zero declined B's claim and still holds A's reserved first; recorder one honoured B. The
        //firsts differ, so the whole-proposal identical test refuses. ASSERTING the refusal is what pins the
        //mechanism rather than the outcome.
        (register, QuePaxaStepOutcome<string> bAtFour) = register.Step(QuePaxaRound<string>.Begin(LaneB, LaneB, "b"), [0, 1], never);

        PrioritizedProposal<string> declinedAtZero = register.Recorders[0].Register.First!;
        PrioritizedProposal<string> honouredAtOne = register.Recorders[1].Register.First!;
        QuePaxaRound<string> bRoundAtFive = bAtFour.Next!;

        Assert.AreNotEqual(QuePaxaStepKind.Decided, bAtFour.Kind);
        Assert.AreEqual(QuePaxaStepKind.Advanced, bAtFour.Kind);
        Assert.AreEqual(ProposalPriority.Reserved, declinedAtZero.Key.Priority);
        Assert.AreEqual(LaneA, declinedAtZero.Key.Owner);
        Assert.AreEqual(ProposalPriority.Reserved, honouredAtOne.Key.Priority);
        Assert.AreEqual(LaneB, honouredAtOne.Key.Owner);

        //The fall-through takes the greatest first, and on the priority tie the lane decides: B's own
        //proposal wins because B sorts above A.
        Assert.AreEqual(LaneB, bRoundAtFive.Proposal.Key.Owner);
        Assert.AreEqual("b", bRoundAtFive.Proposal.Value);

        (register, QuePaxaStepOutcome<string> bAtFive) = register.Step(bRoundAtFive, [0, 1], never);
        Assert.AreEqual(QuePaxaStepKind.Advanced, bAtFive.Kind);

        (_, QuePaxaStepOutcome<string> bAtSix) = register.Step(bAtFive.Next!, [0, 1], never);

        Assert.AreEqual(QuePaxaStepKind.Decided, bAtSix.Kind);
        Assert.AreEqual("b", bAtSix.DecidedValue);
        Assert.AreEqual(LaneB, bAtSix.DecidedBy);
        Assert.AreEqual(RecorderStep.FromRoundAndPhase(1, 2), bAtSix.DecidedAt);

        //THE DIVERGENCE. Two decisions, two values, one register: recorder-side agreement on the leader is
        //what stands between a deployment and this.
        Assert.AreNotEqual(aAtFour.DecidedValue, bAtSix.DecidedValue);
    }


    private static void AssertAtMostOneDecidedValue(RunResult result)
    {
        string? decided = null;
        foreach(Decision decision in result.Decisions)
        {
            if(decided is null)
            {
                decided = decision.Value;

                continue;
            }

            Assert.AreEqual(decided, decision.Value, "Two decisions carried different values.");
        }
    }


    /// <summary>
    /// One run of one schedule.
    /// </summary>
    /// <remarks>
    /// Proposers zero and one each believe they lead while only proposer zero is ever configured, which is the
    /// model's two-believed-leaders-against-one-configured-leader shape and the state the green configurations
    /// reach constantly; any further proposer believes proposer zero leads.
    /// </remarks>
    private static RunResult RunSchedule(int recorderCount, int proposerCount, bool leadered, int[] seeds, ulong prioritySeed)
    {
        QuePaxaRegister<string> register = leadered
            ? QuePaxaRegister<string>.LedBy(recorderCount, Lanes[0])
            : QuePaxaRegister<string>.WithRecorders(recorderCount);

        var source = new SeededPrioritySource(prioritySeed);
        var rounds = new QuePaxaRound<string>?[proposerCount];
        var values = new string[proposerCount];
        for(int i = 0; i < proposerCount; i++)
        {
            values[i] = $"v{i}";
            rounds[i] = QuePaxaRound<string>.Begin(Lanes[i], i < 2 ? Lanes[i] : Lanes[0], values[i]);
        }

        var state = new RunState(rounds, ImmutableArray.CreateBuilder<Decision>(), ImmutableArray.CreateBuilder<CarryObservation>());

        //THE FORCED CONTENTION PREFIX. The two windows overlap in exactly one recorder, so the recorders hold
        //divergent firsts before any generated step runs. At three recorders these are literally {0, 1} and
        //{1, 2}; at five they are the same shape at quorum width, because a sub-quorum prefix would spend the
        //opening on a quorum miss the harness is built to avoid.
        register = StepOnce(register, state, 0, Window(0, register.Quorum), source);
        register = StepOnce(register, state, 1, Window(recorderCount - register.Quorum, register.Quorum), source);

        foreach(int seed in seeds)
        {
            var active = new List<int>(proposerCount);
            for(int i = 0; i < proposerCount; i++)
            {
                if(state.Rounds[i] is not null)
                {
                    active.Add(i);
                }
            }

            if(active.Count == 0)
            {
                break;
            }

            int picked = active[(seed / 3) % active.Count];
            register = StepOnce(register, state, picked, Subset(recorderCount, register.Quorum, seed + 1), source);
        }

        return new RunResult(state.Decisions.ToImmutable(), [.. values], state.Carries.ToImmutable(), state.Steps);
    }


    private static QuePaxaRegister<string> StepOnce(QuePaxaRegister<string> register, RunState state, int proposer, ImmutableArray<int> indices, SeededPrioritySource source)
    {
        QuePaxaRound<string>? round = state.Rounds[proposer];
        if(round is null)
        {
            return register;
        }

        (QuePaxaRegister<string> after, QuePaxaStepOutcome<string> outcome) = register.Step(round, indices, source.Next);
        state.Steps++;

        //Exactly-quorum subsets remove the quorum miss rather than recover from it, so seeing one means the
        //harness stopped being the harness the schedule rules describe.
        Assert.AreNotEqual(QuePaxaStepKind.QuorumMissed, outcome.Kind, "An exactly-quorum subset missed the quorum.");
        Assert.AreEqual(indices.Length, outcome.SummaryCount);

        switch(outcome.Kind)
        {
            case QuePaxaStepKind.Decided:
            {
                state.Decisions.Add(new Decision(round.Proposer, outcome.DecidedBy!.Value, outcome.DecidedValue!, outcome.DecidedAt));
                state.Rounds[proposer] = null;

                break;
            }

            case QuePaxaStepKind.Advanced:
            {
                CarryObservation? carry = ObserveCarry(after, round, outcome, indices);
                if(carry is not null)
                {
                    state.Carries.Add(carry);
                }

                state.Rounds[proposer] = outcome.Next;

                break;
            }

            case QuePaxaStepKind.CaughtUp:
            {
                state.Rounds[proposer] = outcome.Next;

                break;
            }

            default:
            {
                //An exhausted budget is terminal for the instance, so the proposer halts rather than retries.
                state.Rounds[proposer] = null;

                break;
            }
        }

        return after;
    }


    /// <summary>
    /// The two carrying phases, read from the recorders the step reached AFTER it recorded: phase zero takes
    /// the greatest first proposal and phase three the greatest prior aggregate.
    /// </summary>
    /// <remarks>
    /// Phases one and two carry nothing, and a phase-three step whose recorders hold no prior aggregate leaves
    /// the template unchanged, so both yield no observation.
    /// </remarks>
    private static CarryObservation? ObserveCarry(QuePaxaRegister<string> after, QuePaxaRound<string> round, QuePaxaStepOutcome<string> outcome, ImmutableArray<int> indices)
    {
        if(round.Step.Phase != 0 && round.Step.Phase != 3)
        {
            return null;
        }

        ProposalKey? greatest = null;
        foreach(int index in indices)
        {
            QuePaxaRecorder<string> recorder = after.Recorders[index];
            if(recorder.Step != round.Step)
            {
                return null;
            }

            PrioritizedProposal<string>? observed = round.Step.Phase == 0 ? recorder.Register.First : recorder.Register.PriorAggregate;
            if(observed is not null && (greatest is null || observed.Key > greatest.Value))
            {
                greatest = observed.Key;
            }
        }

        return greatest is null ? null : new CarryObservation(round.Step, outcome.Next!.Proposal.Key, greatest.Value);
    }


    private static ImmutableArray<int> Window(int start, int count)
    {
        var builder = ImmutableArray.CreateBuilder<int>(count);
        for(int i = 0; i < count; i++)
        {
            builder.Add(start + i);
        }

        return builder.ToImmutable();
    }


    /// <summary>
    /// A partial Fisher-Yates draw of exactly quorum many distinct indices.
    /// </summary>
    /// <remarks>
    /// Xorshift32 rather than System.Random so that a seed printed by a failing run replays the identical
    /// subset anywhere.
    /// </remarks>
    private static ImmutableArray<int> Subset(int recorderCount, int quorum, int seed)
    {
        int[] pool = new int[recorderCount];
        for(int i = 0; i < recorderCount; i++)
        {
            pool[i] = i;
        }

        uint state = seed == 0 ? 2463534242u : (uint)seed;
        var builder = ImmutableArray.CreateBuilder<int>(quorum);
        for(int i = 0; i < quorum; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            int pick = i + (int)(state % (uint)(recorderCount - i));
            (pool[i], pool[pick]) = (pool[pick], pool[i]);
            builder.Add(pool[i]);
        }

        return builder.ToImmutable();
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }


    private sealed record Decision(ProposerLane Observer, ProposerLane Owner, string Value, RecorderStep At);


    private sealed record CarryObservation(RecorderStep Step, ProposalKey CarriedKey, ProposalKey GreatestObservedKey);


    private sealed record RunResult(
        ImmutableArray<Decision> Decisions,
        ImmutableArray<string> BegunValues,
        ImmutableArray<CarryObservation> Carries,
        int Steps);


    private sealed class RunState
    {
        public RunState(QuePaxaRound<string>?[] rounds, ImmutableArray<Decision>.Builder decisions, ImmutableArray<CarryObservation>.Builder carries)
        {
            Rounds = rounds;
            Decisions = decisions;
            Carries = carries;
        }


        public QuePaxaRound<string>?[] Rounds { get; }


        public ImmutableArray<Decision>.Builder Decisions { get; }


        public ImmutableArray<CarryObservation>.Builder Carries { get; }


        public int Steps { get; set; }
    }


    /// <summary>
    /// Xorshift64 rather than the cryptographic source: every priority in a run is reproducible from its seed,
    /// so a failing schedule replays the identical draws.
    /// </summary>
    private sealed class SeededPrioritySource
    {
        private ulong state;

        public SeededPrioritySource(ulong seed) => state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;


        public int DrawCount { get; private set; }


        public ProposalPriority Next()
        {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            DrawCount++;

            //The two reserved endpoints are excluded, so the source honours the delegate's contract exactly.
            ulong value = state == 0 || state == ulong.MaxValue ? 0x0123_4567_89AB_CDEFUL : state;

            return new ProposalPriority(value);
        }
    }
}
