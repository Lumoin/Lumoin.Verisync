using CsCheck;
using Lumoin.Verisync.Core;
using System.Collections.Immutable;
using System.Globalization;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Algorithm 3's laws as CsCheck properties over a CONSTRUCTIVE walk. A flat random sequence of
/// (step, proposal) pairs is the wrong generator here: a naturally increasing step sequence never produces
/// a stale record, rarely produces two records at one step, and almost never produces the specific shapes
/// the prior-aggregate and first-proposal laws need. The walk is therefore built from segments that reach
/// one region each - a same-step batch, an advance by exactly one, an advance by two or more, and a stale
/// replay at a step the walk has already left - and every walk opens with a seeded prefix that reaches all
/// four on its own. The generated walk is the sweep; the deterministic pins below are the evidence.
/// </summary>
[TestClass]
internal sealed class IntervalSummaryRegisterLawTests
{
    /// <summary>
    /// Identities from fixed bytes rather than from a generator, so the owner half of every key tie-break is a
    /// property of the test rather than of the run.
    /// </summary>
    private static ProposerLane LaneA { get; } = ProposerLane.For(Replica(1));
    private static ProposerLane LaneB { get; } = ProposerLane.For(Replica(2));

    private static RecorderStep Four { get; } = RecorderStep.RoundOnePhaseZero;

    private static Gen<ImmutableArray<WalkOp>> GenWalk { get; } =
        Gen.Int[0, 100_000].Array[0, 12].Select(static seeds => BuildWalk(seeds));

    /// <summary>
    /// The order-independence law's quantifier is over permutations of one key-distinct set, which the walk
    /// generator does not supply, so it gets its own generator.
    /// </summary>
    private static Gen<PrioritizedProposal<string>[]> GenSameStepSet { get; } =
        Gen.Int[0, 500].Array[2, 4].Select(static seeds => BuildDistinctSet(seeds));


    public TestContext TestContext { get; set; } = null!;


    /// <summary>
    /// L1 MONOTONE STEP. Over any walk the register's step never decreases, and the step after a record is at
    /// least the requested one, which is Lemma C.2 and is what makes catch-up the only alternative to
    /// advancing.
    /// </summary>
    [TestMethod]
    public void TheRegisterStepNeverDecreasesOverAWalk()
    {
        GenWalk.Sample(static walk =>
        {
            foreach(Observation observation in Replay(walk))
            {
                Assert.IsTrue(observation.After.Step >= observation.Before.Step, "The register step decreased.");
                Assert.IsTrue(observation.After.Step >= observation.Op.Step, "The register step fell below the requested step.");
            }
        });
    }


    /// <summary>
    /// L1 REACH, DETERMINISTIC. A record below the current step is the only shape that could make the step
    /// decrease, so the law says nothing unless the walk reaches one.
    /// </summary>
    [TestMethod]
    public void TheMonotoneStepLawReachesTheBelowStepRegionByConstruction()
    {
        ImmutableArray<Observation> observations = Replay(BuildWalk([]));

        int belowStepRecords = 0;
        foreach(Observation observation in observations)
        {
            if(observation.Op.Step < observation.Before.Step)
            {
                belowStepRecords++;
                Assert.AreEqual(observation.Before.Step, observation.After.Step);
            }
        }

        Assert.IsGreaterThan(0, belowStepRecords, "The seeded prefix must record below the current step on its own.");
    }


    /// <summary>
    /// L2 FIRST IS THE FIRST. Within a step the first proposal recorded there stays first until the step
    /// advances; the advancing proposal becomes the new first.
    /// </summary>
    /// <remarks>
    /// This is Algorithm 3 literally, where F_c is assigned only on the advancing branch.
    /// </remarks>
    [TestMethod]
    public void FirstIsTheProposalThatAdvancedTheStepAndHoldsUntilTheStepAdvancesAgain()
    {
        GenWalk.Sample(static walk =>
        {
            foreach(Observation observation in Replay(walk))
            {
                if(observation.After.Step > observation.Before.Step)
                {
                    Assert.AreEqual(observation.Op.Proposal, observation.After.First);
                }
                else
                {
                    Assert.AreEqual(observation.Before.First, observation.After.First);
                }
            }
        });
    }


    /// <summary>
    /// L2 REACH, DETERMINISTIC AND DISCRIMINATING. The second record at the step must carry a GREATER key
    /// than the first: that is the only shape that kills an implementation writing First = Best(First, p),
    /// because a lower second key leaves such an implementation looking correct.
    /// </summary>
    [TestMethod]
    public void TheFirstIsTheFirstLawReachesTheGreaterSecondKeyRegionByConstruction()
    {
        PrioritizedProposal<string> first = Proposal(5, LaneA, "first");
        PrioritizedProposal<string> greater = Proposal(9, LaneB, "greater");

        (IntervalSummaryRegister<string> once, _) = IntervalSummaryRegister<string>.Initial.Record(Four, first);
        (IntervalSummaryRegister<string> twice, _) = once.Record(Four, greater);

        Assert.IsTrue(greater.Key > first.Key, "The pin must record a greater key second or it discriminates nothing.");
        Assert.AreEqual(first, twice.First);
        Assert.AreEqual(greater, twice.CurrentAggregate);
    }


    /// <summary>
    /// L3 AGGREGATE IS THE MAXIMUM. After any walk the current aggregate is the greatest key recorded at the
    /// current step.
    /// </summary>
    /// <remarks>
    /// The batch segment is what makes this say anything: one record per step would make the aggregate
    /// trivially equal to the first.
    /// </remarks>
    [TestMethod]
    public void TheCurrentAggregateIsTheGreatestKeyRecordedAtTheCurrentStep()
    {
        GenWalk.Sample(static walk =>
        {
            PrioritizedProposal<string>? expected = null;
            foreach(Observation observation in Replay(walk))
            {
                if(observation.After.Step > observation.Before.Step)
                {
                    expected = observation.Op.Proposal;
                }
                else if(observation.Op.Step == observation.Before.Step && (expected is null || observation.Op.Proposal.Key > expected.Key))
                {
                    expected = observation.Op.Proposal;
                }

                Assert.AreEqual(expected, observation.After.CurrentAggregate);
            }
        });
    }


    /// <summary>
    /// L3 REACH, DETERMINISTIC. Three records at one step in a non-monotone key order, so neither the first
    /// nor the last arrival is the maximum and only a real fold answers correctly.
    /// </summary>
    [TestMethod]
    public void TheAggregateMaximumLawReachesTheSameStepBatchByConstruction()
    {
        PrioritizedProposal<string>[] batch = [Proposal(5, LaneA, "a"), Proposal(9, LaneB, "b"), Proposal(7, LaneA, "c")];

        IntervalSummaryRegister<string> register = FoldAtOneStep(batch);

        Assert.AreEqual(batch[1], register.CurrentAggregate);
        Assert.AreEqual(batch[0], register.First);
    }


    /// <summary>
    /// L4 PRIOR-AGGREGATE EXACTNESS. Advancing from s to s + 1 carries the aggregate at s; advancing by more
    /// clears it, because the proposer never gathered the skipped step and a carried value from it would come
    /// from a step no quorum served.
    /// </summary>
    [TestMethod]
    public void AdvancingByOneCarriesTheAggregateAndAdvancingByMoreClearsIt()
    {
        GenWalk.Sample(static walk =>
        {
            foreach(Observation observation in Replay(walk))
            {
                if(observation.After.Step <= observation.Before.Step)
                {
                    Assert.AreEqual(observation.Before.PriorAggregate, observation.After.PriorAggregate);

                    continue;
                }

                if(observation.Op.Step.IsNextAfter(observation.Before.Step))
                {
                    Assert.AreEqual(observation.Before.CurrentAggregate, observation.After.PriorAggregate);
                }
                else
                {
                    Assert.IsNull(observation.After.PriorAggregate);
                }
            }
        });
    }


    /// <summary>
    /// L4 REACH, DETERMINISTIC AND DISCRIMINATING, and it must satisfy three conditions at once or it proves
    /// nothing: key 5 then key 9 at step s, then an advance to s + 1 carrying a key that differs from both.
    /// </summary>
    /// <remarks>
    /// An implementation carrying First yields 5 and one carrying the advancing proposal yields 7, so only the
    /// aggregate yields 9.
    /// </remarks>
    [TestMethod]
    public void ThePriorAggregateLawReachesTheExactCarryRegionByConstruction()
    {
        PrioritizedProposal<string> lower = Proposal(5, LaneA, "lower");
        PrioritizedProposal<string> greater = Proposal(9, LaneB, "greater");
        PrioritizedProposal<string> advancing = Proposal(7, LaneA, "advancing");

        (IntervalSummaryRegister<string> once, _) = IntervalSummaryRegister<string>.Initial.Record(Four, lower);
        (IntervalSummaryRegister<string> twice, _) = once.Record(Four, greater);
        (IntervalSummaryRegister<string> advanced, _) = twice.Record(Four.Next(), advancing);

        Assert.AreNotEqual(lower.Key, advancing.Key);
        Assert.AreNotEqual(greater.Key, advancing.Key);
        Assert.AreEqual(greater, advanced.PriorAggregate);

        //The skipping direction, so the pin covers both arms of the rule rather than only the carry.
        (IntervalSummaryRegister<string> skipped, _) = twice.Record(Four.Next().Next(), advancing);
        Assert.IsNull(skipped.PriorAggregate);
    }


    /// <summary>
    /// L5 ORDER INDEPENDENCE. For a set of key-distinct proposals at one step, every permutation yields the
    /// same aggregate.
    /// </summary>
    /// <remarks>
    /// Keeping the incumbent on an exact key tie is what makes the fold order-independent under the
    /// uniqueness contract, and order-dependent without it.
    /// </remarks>
    [TestMethod]
    public void EveryPermutationOfAKeyDistinctSetFoldsToTheSameAggregate()
    {
        GenSameStepSet.Sample(static set =>
        {
            PrioritizedProposal<string> expected = set[0];
            foreach(PrioritizedProposal<string> proposal in set)
            {
                if(proposal.Key > expected.Key)
                {
                    expected = proposal;
                }
            }

            foreach(PrioritizedProposal<string>[] permutation in Permutations([.. set]))
            {
                Assert.AreEqual(expected, FoldAtOneStep(permutation).CurrentAggregate);
            }
        });
    }


    /// <summary>
    /// L5 REACH, DETERMINISTIC. The law is vacuous over a one-element set and over a single ordering, so the
    /// builder's minimum input must already produce a key-distinct pair and both of its orderings.
    /// </summary>
    [TestMethod]
    public void TheOrderIndependenceLawReachesTheMultiPermutationRegionByConstruction()
    {
        PrioritizedProposal<string>[] set = BuildDistinctSet([0, 0]);

        Assert.HasCount(2, set);
        Assert.AreNotEqual(set[0].Key, set[1].Key);

        List<PrioritizedProposal<string>[]> permutations = Permutations([.. set]);
        Assert.HasCount(2, permutations);
        Assert.AreEqual(FoldAtOneStep(permutations[0]).CurrentAggregate, FoldAtOneStep(permutations[1]).CurrentAggregate);
    }


    /// <summary>
    /// L5N NON-VACUITY FOR L5, DIRECTED RATHER THAN GENERATED. Random 64-bit priorities never collide, so a
    /// generated shared-key region would be reached only by a rejection filter, and a filter that reaches its
    /// region a tenth of a percent of the time turns a green run into seed luck.
    /// </summary>
    /// <remarks>
    /// An existential law gets a CONSTRUCTED witness. What this does NOT pin is whether Best keeps the
    /// incumbent or the newcomer on an exact key tie: both directions produce differing permutations, the
    /// difference is unobservable under the uniqueness contract, and no test here decides it.
    /// </remarks>
    [TestMethod]
    public void TwoValuesSharingOneKeyMakeTheFoldOrderDependent()
    {
        ProposalKey shared = new(new ProposalPriority(42), LaneA);
        PrioritizedProposal<string> first = new(shared, "v1");
        PrioritizedProposal<string> second = new(shared, "v2");

        PrioritizedProposal<string>? forward = FoldAtOneStep([first, second]).CurrentAggregate;
        PrioritizedProposal<string>? backward = FoldAtOneStep([second, first]).CurrentAggregate;

        Assert.AreEqual(first.Key, second.Key);
        Assert.AreNotEqual(first, second);
        Assert.AreNotEqual(forward, backward);
    }


    /// <summary>
    /// L6 STALE IS INERT. A record below the current step returns the same instance and a summary equal to the
    /// one the previous record returned: a value tagged below the current step is obsolete, and the register
    /// still answers with its summary rather than refusing.
    /// </summary>
    [TestMethod]
    public void ARecordBelowTheCurrentStepIsInertAndStillAnswers()
    {
        GenWalk.Sample(static walk =>
        {
            ImmutableArray<Observation> observations = Replay(walk);
            for(int i = 0; i < observations.Length; i++)
            {
                Observation observation = observations[i];
                if(observation.Op.Step >= observation.Before.Step)
                {
                    continue;
                }

                Assert.IsTrue(observation.SameInstance, "A stale record returned a different register instance.");
                Assert.AreEqual(
                    new RecordSummary<string>(observation.Before.Step, observation.Before.First, observation.Before.PriorAggregate),
                    observation.Summary);

                if(i > 0)
                {
                    Assert.AreEqual(observations[i - 1].Summary, observation.Summary);
                }
            }
        });
    }


    /// <summary>
    /// L6 REACH, DETERMINISTIC. The stale replay has to land at a step the walk has genuinely left, and it has
    /// to carry a key high enough that an implementation folding it anyway would be caught.
    /// </summary>
    [TestMethod]
    public void TheStaleLawReachesTheInertRegionByConstruction()
    {
        (IntervalSummaryRegister<string> atFour, _) = IntervalSummaryRegister<string>.Initial.Record(Four, Proposal(5, LaneA, "a"));
        (IntervalSummaryRegister<string> atFive, RecordSummary<string> summaryAtFive) = atFour.Record(Four.Next(), Proposal(9, LaneB, "b"));

        (IntervalSummaryRegister<string> afterStale, RecordSummary<string> staleSummary) = atFive.Record(Four, Proposal(9_999, LaneA, "stale"));

        Assert.AreSame(atFive, afterStale);
        Assert.AreEqual(summaryAtFive, staleSummary);
        Assert.AreEqual(Proposal(9, LaneB, "b"), afterStale.CurrentAggregate);
    }


    /// <summary>
    /// L7 IDENTITY IS EXACT. At the register's own step the register returns ITSELF exactly when the fold would
    /// have kept the incumbent, and a new instance exactly when it would not.
    /// </summary>
    /// <remarks>
    /// That is not tidiness: the recorder decides whether it changed by reference and the node above it
    /// decides whether to persist by the same test, so an implementation allocating unconditionally would make
    /// every retransmission on a lossy link cost a durable write that makes nothing durable. The summary is
    /// asserted alongside, because a register that returned itself and answered with a stale summary would
    /// satisfy the identity half alone.
    /// </remarks>
    [TestMethod]
    public void AtItsOwnStepTheRegisterReturnsItselfExactlyWhenTheFoldKeepsTheIncumbent()
    {
        GenWalk.Sample(static walk =>
        {
            foreach(Observation observation in Replay(walk))
            {
                if(observation.Op.Step != observation.Before.Step)
                {
                    continue;
                }

                bool foldKeepsTheIncumbent = observation.Before.CurrentAggregate is not null
                    && observation.Before.CurrentAggregate.Key >= observation.Op.Proposal.Key;

                Assert.AreEqual(foldKeepsTheIncumbent, observation.SameInstance, "The same-instance predicate and the fold's outcome disagreed.");
                Assert.AreEqual(
                    new RecordSummary<string>(observation.After.Step, observation.After.First, observation.After.PriorAggregate),
                    observation.Summary);
            }
        });
    }


    /// <summary>
    /// L7 REACH, DETERMINISTIC AND DISCRIMINATING. A two-sided implication says nothing unless both sides are
    /// reached: a same-step record whose key LOSES the fold must return the same instance, and one whose key
    /// WINS it must not.
    /// </summary>
    /// <remarks>
    /// The exact-key tie is pinned here rather than in the walk, because the walk's keys are distinct by
    /// construction — and an exact key with a DIFFERENT VALUE is the only case that observes the fold's tie
    /// direction at all, since a strictly lower key returns the incumbent whichever way the tie is broken.
    /// </remarks>
    [TestMethod]
    public void TheSameInstanceLawReachesBothFoldDirectionsByConstruction()
    {
        PrioritizedProposal<string> incumbent = Proposal(5, LaneA, "incumbent");
        PrioritizedProposal<string> tiedButDifferent = new(new ProposalKey(new ProposalPriority(5), LaneA), "different");
        (IntervalSummaryRegister<string> once, _) = IntervalSummaryRegister<string>.Initial.Record(Four, incumbent);

        (IntervalSummaryRegister<string> higher, _) = once.Record(Four, Proposal(9, LaneB, "higher"));
        (IntervalSummaryRegister<string> lower, _) = once.Record(Four, Proposal(3, LaneB, "lower"));
        (IntervalSummaryRegister<string> tied, _) = once.Record(Four, tiedButDifferent);

        Assert.AreEqual(incumbent.Key, tiedButDifferent.Key);
        Assert.AreNotEqual(incumbent, tiedButDifferent);
        Assert.AreNotSame(once, higher);
        Assert.AreSame(once, lower);
        Assert.AreSame(once, tied);
        Assert.AreEqual(incumbent, tied.CurrentAggregate);
    }


    /// <summary>
    /// The seeded prefix carries the whole construction: every generated walk begins with it, so every sample
    /// reaches the same-step batch, the exact-carry advance, the skipping advance, and the stale replay
    /// whatever the sampled segments turn out to be.
    /// </summary>
    /// <remarks>
    /// Without this the laws above would be sweeping regions they reach only by luck.
    /// </remarks>
    [TestMethod]
    public void TheSeededPrefixReachesEveryRegionByConstruction()
    {
        ImmutableArray<Observation> observations = Replay(BuildWalk([]));

        int sameStepWithGreaterKey = 0;
        int advanceByOne = 0;
        int advanceByMore = 0;
        int stale = 0;
        foreach(Observation observation in observations)
        {
            if(observation.Op.Step < observation.Before.Step)
            {
                stale++;
            }
            else if(observation.Op.Step == observation.Before.Step)
            {
                if(observation.Before.First is not null && observation.Op.Proposal.Key > observation.Before.First.Key)
                {
                    sameStepWithGreaterKey++;
                }
            }
            else if(observation.Op.Step.IsNextAfter(observation.Before.Step))
            {
                advanceByOne++;
            }
            else
            {
                advanceByMore++;
            }
        }

        TestContext.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"prefix records={observations.Length}, sameStepGreaterKey={sameStepWithGreaterKey}, advanceByOne={advanceByOne}, advanceByMore={advanceByMore}, stale={stale}"));

        Assert.IsGreaterThan(0, sameStepWithGreaterKey);
        Assert.IsGreaterThan(0, advanceByOne);
        Assert.IsGreaterThan(0, advanceByMore);
        Assert.IsGreaterThan(0, stale);
    }


    /// <summary>
    /// The walk is replayed once and every law reads the same observation list, so no law can silently drive a
    /// different sequence from the one its neighbours checked.
    /// </summary>
    private static ImmutableArray<Observation> Replay(ImmutableArray<WalkOp> ops)
    {
        var observations = ImmutableArray.CreateBuilder<Observation>(ops.Length);
        IntervalSummaryRegister<string> register = IntervalSummaryRegister<string>.Initial;
        foreach(WalkOp op in ops)
        {
            IntervalSummaryRegister<string> before = register;
            (IntervalSummaryRegister<string> after, RecordSummary<string> summary) = before.Record(op.Step, op.Proposal);
            observations.Add(new Observation(op, before, after, summary, ReferenceEquals(before, after)));
            register = after;
        }

        return observations.ToImmutable();
    }


    private static ImmutableArray<WalkOp> BuildWalk(int[] segmentSeeds)
    {
        var ops = ImmutableArray.CreateBuilder<WalkOp>();
        var left = new List<RecorderStep>();
        RecorderStep current = Four;
        int sequence = 0;

        //The seeded prefix, in this order: a same-step batch whose SECOND key is greater than its first, an
        //advance by exactly one carrying a key that differs from both, a stale replay at the step just left,
        //and an advance by two. The priorities are small and fixed so they never collide with a generated
        //one, which keeps every key in the walk distinct.
        ops.Add(new WalkOp(current, PrefixProposal(5, LaneA, ref sequence)));
        ops.Add(new WalkOp(current, PrefixProposal(9, LaneB, ref sequence)));
        left.Add(current);
        current = current.Next();
        ops.Add(new WalkOp(current, PrefixProposal(7, LaneA, ref sequence)));
        ops.Add(new WalkOp(left[0], PrefixProposal(3, LaneB, ref sequence)));
        left.Add(current);
        current = Advance(current, 2);
        ops.Add(new WalkOp(current, PrefixProposal(4, LaneA, ref sequence)));

        foreach(int seed in segmentSeeds)
        {
            switch(seed % 4)
            {
                case 0:
                {
                    int batch = 1 + ((seed / 4) % 3);
                    for(int i = 0; i < batch; i++)
                    {
                        ops.Add(new WalkOp(current, GeneratedProposal(seed + (7 * i), ref sequence)));
                    }

                    break;
                }

                case 1:
                {
                    left.Add(current);
                    current = current.Next();
                    ops.Add(new WalkOp(current, GeneratedProposal(seed, ref sequence)));

                    break;
                }

                case 2:
                {
                    left.Add(current);
                    current = Advance(current, 2 + ((seed / 4) % 3));
                    ops.Add(new WalkOp(current, GeneratedProposal(seed, ref sequence)));

                    break;
                }

                default:
                {
                    RecorderStep stale = left[(seed / 4) % left.Count];
                    ops.Add(new WalkOp(stale, GeneratedProposal(seed, ref sequence)));

                    break;
                }
            }
        }

        return ops.ToImmutable();
    }


    /// <summary>
    /// Keys are distinct across a whole walk by construction: the sequence number is unique per record and
    /// occupies the low three digits, so the seed decides the ordering while the sequence decides identity.
    /// </summary>
    private static PrioritizedProposal<string> GeneratedProposal(int seed, ref int sequence)
    {
        sequence++;
        ulong priority = 1_000UL + ((ulong)(seed % 997) * 1_000UL) + (ulong)sequence;

        return Proposal(priority, (seed & 1) == 0 ? LaneA : LaneB, string.Create(CultureInfo.InvariantCulture, $"g{sequence}"));
    }


    private static PrioritizedProposal<string> PrefixProposal(ulong priority, ProposerLane owner, ref int sequence)
    {
        sequence++;

        return Proposal(priority, owner, string.Create(CultureInfo.InvariantCulture, $"p{sequence}"));
    }


    private static PrioritizedProposal<string>[] BuildDistinctSet(int[] seeds)
    {
        var set = new PrioritizedProposal<string>[seeds.Length];
        for(int i = 0; i < seeds.Length; i++)
        {
            //The index occupies the low digits, so two equal seeds still yield two distinct keys and the set
            //is key-distinct whatever was sampled.
            ulong priority = 1UL + ((ulong)seeds[i] * 1_000UL) + (ulong)i;
            set[i] = Proposal(priority, (i & 1) == 0 ? LaneA : LaneB, string.Create(CultureInfo.InvariantCulture, $"s{i}"));
        }

        return set;
    }


    private static List<PrioritizedProposal<string>[]> Permutations(PrioritizedProposal<string>[] items)
    {
        var results = new List<PrioritizedProposal<string>[]>();
        Permute(items, 0, results);

        return results;
    }


    private static void Permute(PrioritizedProposal<string>[] items, int index, List<PrioritizedProposal<string>[]> results)
    {
        if(index == items.Length)
        {
            results.Add([.. items]);

            return;
        }

        for(int i = index; i < items.Length; i++)
        {
            (items[index], items[i]) = (items[i], items[index]);
            Permute(items, index + 1, results);
            (items[index], items[i]) = (items[i], items[index]);
        }
    }


    private static IntervalSummaryRegister<string> FoldAtOneStep(IEnumerable<PrioritizedProposal<string>> proposals)
    {
        IntervalSummaryRegister<string> register = IntervalSummaryRegister<string>.Initial;
        foreach(PrioritizedProposal<string> proposal in proposals)
        {
            (register, _) = register.Record(Four, proposal);
        }

        return register;
    }


    private static RecorderStep Advance(RecorderStep step, int by)
    {
        RecorderStep advanced = step;
        for(int i = 0; i < by; i++)
        {
            advanced = advanced.Next();
        }

        return advanced;
    }


    private static PrioritizedProposal<string> Proposal(ulong priority, ProposerLane owner, string value)
    {
        return new PrioritizedProposal<string>(new ProposalKey(new ProposalPriority(priority), owner), value);
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }


    private sealed record WalkOp(RecorderStep Step, PrioritizedProposal<string> Proposal);


    private sealed record Observation(
        WalkOp Op,
        IntervalSummaryRegister<string> Before,
        IntervalSummaryRegister<string> After,
        RecordSummary<string> Summary,
        bool SameInstance);
}
