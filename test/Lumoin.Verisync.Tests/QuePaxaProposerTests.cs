using Lumoin.Verisync.Core;
using System.Globalization;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// The asynchronous proposer's suite, driven by scripted endpoints rather than by the interleaving bench: the
/// rules below are about WHAT THE PROPOSER PUTS ON THE WIRE and WHEN IT STOPS WAITING, and a bench that
/// delivers messages in a sampled order cannot pin either. Every endpoint answers, faults, or hangs on the
/// test's instruction, and every assertion reads the requests the endpoints actually received.
/// </summary>
/// <remarks>
/// <para>
/// EVERY AWAIT OF A PROPOSAL IS BOUNDED BY AN EXPLICIT TIMEOUT AND ITS COMPLETION IS ASSERTED. Several of the
/// rules here — acting on the first quorum rather than on every endpoint, and the terminal condition — turn a
/// wrong implementation into a HANG rather than into a failure, and a hung suite reports nothing at all.
/// </para>
/// <para>
/// A faulting endpoint returns a FAULTED task rather than throwing synchronously, because the faulted task is
/// the shape the proposer's contract names: the endpoint's result is materialized once with
/// <c>AsTask</c> and classified as cancelled, faulted, or a result.
/// </para>
/// </remarks>
[TestClass]
internal sealed class QuePaxaProposerTests
{
    /// <summary>
    /// Identities from fixed bytes so that A sorts below B, and no assertion below depends on which way a
    /// generated pair happened to sort.
    /// </summary>
    private static ProposerLane LaneA { get; } = ProposerLane.For(Replica(1));
    private static ProposerLane LaneB { get; } = ProposerLane.For(Replica(2));

    private static RecorderStep Four { get; } = RecorderStep.RoundOnePhaseZero;
    private static RecorderStep Five { get; } = RecorderStep.FromRoundAndPhase(1, 1);
    private static RecorderStep Six { get; } = RecorderStep.FromRoundAndPhase(1, 2);

    private static TimeSpan ProposalTimeout { get; } = TimeSpan.FromSeconds(30);


    public TestContext TestContext { get; set; } = null!;


    /// <summary>
    /// THE MEMOIZATION LAW, and the slice cannot ship without it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A proposer may deliver a request to a recorder any number of times PROVIDED EVERY DELIVERY IS IDENTICAL
    /// — same step, same proposal, same priority — because the model admits at most one DISTINCT proposal per
    /// (proposer, recorder, step) and its send flag is what enforces that. The mechanism is that the
    /// per-recorder phase-zero draw is made once per (step, recorder) and the resulting request is retained
    /// for the lifetime of the step, so the re-send path cannot reach the priority source at all. That is a
    /// discipline held by one method's structure rather than a type-level guarantee, which is exactly why it
    /// needs a test: recorder two faults on its first attempt and answers on its second, and the two requests
    /// it received must be equal AS RECORDS.
    /// </para>
    /// <para>
    /// The scripted source carries exactly one draw per recorder, so an implementation that redrew on the
    /// re-send would run the source dry and fail loudly even if the equality assertion were deleted.
    /// </para>
    /// </remarks>
    [TestMethod]
    public async Task TheReSendCarriesTheIdenticalRequestAndDrawsNoFreshPriority()
    {
        ScriptedPrioritySource source = new(100, 101, 102);
        ScriptedRecorder[] recorders =
        [
            new(QuePaxaRecorder<string>.Leaderless, static (recorder, attempt, request, _) => attempt == 1 ? recorder.Answering(request) : Faulting()),
            new(QuePaxaRecorder<string>.Leaderless, static (recorder, attempt, request, _) => attempt == 0 ? recorder.Answering(request) : Faulting()),
            new(QuePaxaRecorder<string>.Leaderless, static (recorder, attempt, request, _) => attempt == 1 ? recorder.Answering(request) : Faulting())
        ];

        QuePaxaProposer<string> proposer = ProposerOver(recorders, LaneA, source.Next, attemptsPerRecorder: 2);

        QuePaxaOutcome<string> outcome = await AwaitProposalAsync(proposer.ProposeAsync(null, "a", TestContext.CancellationToken)).ConfigureAwait(false);

        List<RecordRequest<string>> resent = recorders[2].ReceivedAt(Four);

        Assert.HasCount(2, resent);
        Assert.AreEqual(resent[0], resent[1]);
        Assert.AreEqual(resent[0].Proposal.Key.Priority, resent[1].Proposal.Key.Priority);
        Assert.AreEqual(resent[0].Proposal.Key.Owner, resent[1].Proposal.Key.Owner);
        Assert.AreEqual(resent[0].Proposal.Value, resent[1].Proposal.Value);
        Assert.IsTrue(resent[0].Proposal.Key.Priority.IsOrdinary);

        //Recorder zero was re-sent too, and its retained request is its own rather than a copy of recorder
        //two's: the draw is PER RECORDER, so a single draw shared across recorders would collapse the
        //independence the liveness argument rests on.
        List<RecordRequest<string>> otherResent = recorders[0].ReceivedAt(Four);

        Assert.HasCount(2, otherResent);
        Assert.AreEqual(otherResent[0], otherResent[1]);
        Assert.AreNotEqual(otherResent[0].Proposal.Key.Priority, resent[0].Proposal.Key.Priority);

        //One draw per recorder for the phase-zero step, and none at all for the phase-one step that followed.
        Assert.AreEqual(proposer.RecorderCount, source.DrawCount);
        Assert.IsFalse(outcome.IsDecided);
        Assert.AreEqual(2, outcome.Steps);

        TestContext.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"recorders={proposer.RecorderCount}, draws={source.DrawCount}, resendsAtStepFour={resent.Count}, steps={outcome.Steps}"));
    }


    /// <summary>
    /// QUORUM LATENCY, NOT TOTAL LATENCY, and this is the arc's whole quantitative claim: a majority-sized fast
    /// quorum against Fast CASPaxos's supermajority.
    /// </summary>
    /// <remarks>
    /// The proposer acts on the FIRST quorum and does not wait for the remaining endpoints, which is
    /// model-legal because a proposer there acts on any majority subset of the replies it holds. The two
    /// never-completing endpoints are asserted STILL INCOMPLETE after the proposal returned, because an
    /// implementation that awaited every endpoint would otherwise only be caught by the timeout.
    /// </remarks>
    [TestMethod]
    public async Task TheProposalCompletesOnTheFirstQuorumWithoutWaitingForEveryRecorder()
    {
        SeededPrioritySource source = new(4242);
        ScriptedRecorder[] recorders =
        [
            new(QuePaxaRecorder<string>.Leaderless, static (recorder, _, request, _) => recorder.Answering(request)),
            new(QuePaxaRecorder<string>.Leaderless, static (recorder, _, request, _) => recorder.Answering(request)),
            new(QuePaxaRecorder<string>.Leaderless, static (recorder, _, request, _) => recorder.Answering(request)),
            new(QuePaxaRecorder<string>.Leaderless, static (recorder, _, _, _) => recorder.Hanging()),
            new(QuePaxaRecorder<string>.Leaderless, static (recorder, _, _, _) => recorder.Hanging())
        ];

        QuePaxaProposer<string> proposer = ProposerOver(recorders, LaneA, source.Next, attemptsPerRecorder: 2);

        Assert.AreEqual(3, proposer.Quorum);

        QuePaxaOutcome<string> outcome = await AwaitProposalAsync(proposer.ProposeAsync(null, "a", TestContext.CancellationToken)).ConfigureAwait(false);

        Assert.IsTrue(outcome.IsDecided);
        Assert.AreEqual("a", outcome.Value);
        Assert.AreEqual(LaneA, outcome.DecidedBy);
        Assert.AreEqual(Six, outcome.DecidedAt);
        Assert.AreEqual(3, outcome.Steps);

        //The slow recorders were asked at every step and answered at none, so the proposal outran them rather
        //than skipping them.
        int[] slow = [3, 4];
        foreach(int index in slow)
        {
            Assert.HasCount(outcome.Steps, recorders[index].Received);
            Assert.HasCount(outcome.Steps, recorders[index].Hung);
            foreach(TaskCompletionSource<RecordReply<string>> hung in recorders[index].Hung)
            {
                Assert.IsFalse(hung.Task.IsCompleted, "A never-completing endpoint finished, so the proposal did not act on the first quorum.");
            }
        }
    }


    /// <summary>
    /// THE RE-SEND IS PER RECORDER AND NOT GLOBAL, which is the direct pin on a deadlock.
    /// </summary>
    /// <remarks>
    /// Gating re-send eligibility on "no task is in flight" means a single faulting endpoint among three is
    /// never retried while the other two are outstanding, and one never-completing endpoint then hangs the
    /// proposal forever with a live majority available. Eligibility is therefore per recorder and the re-send
    /// happens from INSIDE the drain, which is what this test observes: at the moment recorder zero is asked a
    /// second time, neither of the other two has completed.
    /// </remarks>
    [TestMethod]
    public async Task AFaultingRecorderIsReSentWhileTheOtherRecordersAreStillOutstanding()
    {
        SeededPrioritySource source = new(97);
        TaskCompletionSource resent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool othersOutstandingAtReSend = false;
        ScriptedRecorder[] recorders = new ScriptedRecorder[3];

        recorders[0] = new ScriptedRecorder(QuePaxaRecorder<string>.Leaderless, (_, attempt, _, _) =>
        {
            if(attempt == 1)
            {
                othersOutstandingAtReSend = recorders[1].Hung.Count == 1
                    && recorders[2].Hung.Count == 1
                    && !recorders[1].Hung[0].Task.IsCompleted
                    && !recorders[2].Hung[0].Task.IsCompleted;
                resent.TrySetResult();
            }

            return Faulting();
        });

        recorders[1] = new ScriptedRecorder(QuePaxaRecorder<string>.Leaderless, static (recorder, attempt, request, _) => attempt == 0 ? recorder.Hanging() : recorder.Answering(request));
        recorders[2] = new ScriptedRecorder(QuePaxaRecorder<string>.Leaderless, static (recorder, attempt, request, _) => attempt == 0 ? recorder.Hanging() : recorder.Answering(request));

        QuePaxaProposer<string> proposer = ProposerOver(recorders, LaneA, source.Next, attemptsPerRecorder: 2);

        Task<QuePaxaOutcome<string>> proposal = proposer.ProposeAsync(null, "a", TestContext.CancellationToken);

        await resent.Task.WaitAsync(ProposalTimeout, TestContext.CancellationToken).ConfigureAwait(false);

        Assert.IsTrue(othersOutstandingAtReSend, "The faulting recorder was re-sent only after the other endpoints completed, so eligibility is gated globally.");
        Assert.HasCount(2, recorders[0].ReceivedAt(Four));

        //Releasing the two outstanding endpoints hands the proposal its quorum and it runs to a decision.
        for(int index = 1; index < recorders.Length; index++)
        {
            ScriptedRecorder recorder = recorders[index];
            recorder.Hung[0].SetResult(recorder.Node.Handle(recorder.Received[0]));
        }

        QuePaxaOutcome<string> outcome = await AwaitProposalAsync(proposal).ConfigureAwait(false);

        Assert.IsTrue(outcome.IsDecided);
        Assert.AreEqual("a", outcome.Value);
    }


    /// <summary>
    /// ATTEMPTS ARE PER STEP AND NOT PER ROUND. A recorder abandoned at the first step is asked again at the
    /// next one with a FULL budget, because a transport fault is a property of one exchange and not a verdict on
    /// the recorder.
    /// </summary>
    /// <remarks>
    /// An implementation that reset the budget once per round would leave a recorder that faulted early
    /// silently unreachable for three further steps, which no other test in this suite would notice.
    /// </remarks>
    [TestMethod]
    public async Task TheAttemptBudgetIsRefreshedAtEveryStepRatherThanOncePerRound()
    {
        SeededPrioritySource source = new(31);
        ScriptedRecorder[] recorders =
        [
            new(QuePaxaRecorder<string>.Leaderless, static (_, _, _, _) => Faulting()),
            new(QuePaxaRecorder<string>.Leaderless, static (recorder, _, request, _) => recorder.Answering(request)),
            new(QuePaxaRecorder<string>.Leaderless, static (recorder, _, request, _) => recorder.Answering(request))
        ];

        QuePaxaProposer<string> proposer = ProposerOver(recorders, LaneA, source.Next, attemptsPerRecorder: 2);

        QuePaxaOutcome<string> outcome = await AwaitProposalAsync(proposer.ProposeAsync(null, "a", TestContext.CancellationToken)).ConfigureAwait(false);

        Assert.IsTrue(outcome.IsDecided);
        Assert.AreEqual(3, outcome.Steps);
        Assert.HasCount(proposer.AttemptsPerRecorder, recorders[0].ReceivedAt(Four));
        Assert.HasCount(proposer.AttemptsPerRecorder, recorders[0].ReceivedAt(Five));
        Assert.HasCount(proposer.AttemptsPerRecorder, recorders[0].ReceivedAt(Six));

        //The two live recorders were asked once per step, because nothing they did spent an attempt.
        Assert.HasCount(outcome.Steps, recorders[1].Received);
        Assert.HasCount(outcome.Steps, recorders[2].Received);
    }


    /// <summary>
    /// ATTEMPT EXHAUSTION IS A TERMINAL CONDITION STATED INDEPENDENTLY OF THE IN-FLIGHT SET: no answer is
    /// pending and no recorder has attempts left.
    /// </summary>
    /// <remarks>
    /// With every endpoint faulting permanently the step reaches no recorder at all, the conclusion is a
    /// missed quorum, and the missed quorum carries no successor round so the loop ends. The exact send count
    /// is asserted because an off-by-one in the budget is invisible in an outcome.
    /// </remarks>
    [TestMethod]
    public async Task EveryEndpointFaultingEndsTheProposalAfterExactlyTheAttemptBudget()
    {
        ScriptedPrioritySource source = new(10, 11, 12);
        ScriptedRecorder[] recorders =
        [
            new(QuePaxaRecorder<string>.Leaderless, static (_, _, _, _) => Faulting()),
            new(QuePaxaRecorder<string>.Leaderless, static (_, _, _, _) => Faulting()),
            new(QuePaxaRecorder<string>.Leaderless, static (_, _, _, _) => Faulting())
        ];

        QuePaxaProposer<string> proposer = ProposerOver(recorders, LaneA, source.Next, attemptsPerRecorder: 3);

        QuePaxaOutcome<string> outcome = await AwaitProposalAsync(proposer.ProposeAsync(null, "a", TestContext.CancellationToken)).ConfigureAwait(false);

        Assert.IsFalse(outcome.IsDecided);
        Assert.AreEqual(1, outcome.Steps);
        Assert.AreEqual(RecorderStep.Zero, outcome.DecidedAt);
        Assert.IsNull(outcome.DecidedBy);

        foreach(ScriptedRecorder recorder in recorders)
        {
            Assert.HasCount(proposer.AttemptsPerRecorder, recorder.Received);
            Assert.HasCount(proposer.AttemptsPerRecorder, recorder.ReceivedAt(Four));
        }

        //Every re-send carried the retained request, so the whole step cost one draw per recorder.
        Assert.AreEqual(proposer.RecorderCount, source.DrawCount);
    }


    /// <summary>
    /// THE CALLER'S CANCELLATION PROPAGATES.
    /// </summary>
    /// <remarks>
    /// The proposer cannot interrupt its own wait, because the wait is on a completion race that takes no
    /// token, so responsiveness is the endpoint delegate's contract: an implementation MUST complete — with a
    /// result, a fault, or a cancellation — when the supplied token is signalled. Given an endpoint that
    /// honours it, a cancelled proposal reports cancellation rather than a quorum miss, because a quorum miss
    /// is a protocol outcome a caller may act on and a cancellation is not.
    /// </remarks>
    [TestMethod]
    public async Task ACancelledTokenPropagatesOutOfTheProposal()
    {
        SeededPrioritySource source = new(7);
        ScriptedRecorder[] recorders =
        [
            new(QuePaxaRecorder<string>.Leaderless, static (recorder, _, request, token) => token.IsCancellationRequested ? Cancelling(token) : recorder.Answering(request)),
            new(QuePaxaRecorder<string>.Leaderless, static (recorder, _, request, token) => token.IsCancellationRequested ? Cancelling(token) : recorder.Answering(request)),
            new(QuePaxaRecorder<string>.Leaderless, static (recorder, _, request, token) => token.IsCancellationRequested ? Cancelling(token) : recorder.Answering(request))
        ];

        QuePaxaProposer<string> proposer = ProposerOver(recorders, LaneA, source.Next, attemptsPerRecorder: 2);

        using CancellationTokenSource cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        await cancelled.CancelAsync().ConfigureAwait(false);

        Task<QuePaxaOutcome<string>> proposal = proposer.ProposeAsync(null, "a", cancelled.Token);

        await Assert.ThrowsAsync<OperationCanceledException>(() => proposal.WaitAsync(ProposalTimeout, TestContext.CancellationToken)).ConfigureAwait(false);
    }


    /// <summary>
    /// AN ENDPOINT'S OWN DEADLINE IS A TRANSPORT FAULT AND NOT THE CALLER'S CANCELLATION. An endpoint that
    /// imposes a deadline through a linked token reports cancellation on ITS token, and killing the proposal
    /// there would let one slow link cancel a proposal the caller never cancelled.
    /// </summary>
    /// <remarks>
    /// The classification is therefore on the caller's token: cancellation is rethrown only when the caller
    /// asked for it, and is otherwise a fault that spends an attempt and is re-sent. The other two recorders
    /// cannot form a quorum between them, so the retry is REQUIRED for the proposal to finish and the test
    /// cannot pass by accident.
    /// </remarks>
    [TestMethod]
    public async Task AnEndpointCancellingOnItsOwnTokenIsRetriedRatherThanPropagated()
    {
        SeededPrioritySource source = new(13);
        using CancellationTokenSource endpointDeadline = new();
        await endpointDeadline.CancelAsync().ConfigureAwait(false);
        CancellationToken deadline = endpointDeadline.Token;

        ScriptedRecorder[] recorders =
        [
            new(QuePaxaRecorder<string>.Leaderless, (recorder, attempt, request, _) => attempt == 0 ? Cancelling(deadline) : recorder.Answering(request)),
            new(QuePaxaRecorder<string>.Leaderless, static (recorder, _, request, _) => recorder.Answering(request)),
            new(QuePaxaRecorder<string>.Leaderless, static (recorder, _, _, _) => recorder.Hanging())
        ];

        QuePaxaProposer<string> proposer = ProposerOver(recorders, LaneA, source.Next, attemptsPerRecorder: 2);

        QuePaxaOutcome<string> outcome = await AwaitProposalAsync(proposer.ProposeAsync(null, "a", TestContext.CancellationToken)).ConfigureAwait(false);

        List<RecordRequest<string>> retried = recorders[0].ReceivedAt(Four);

        Assert.IsTrue(outcome.IsDecided);
        Assert.HasCount(2, retried);
        Assert.AreEqual(retried[0], retried[1]);
    }


    /// <summary>
    /// A TASK THAT ENDED CANCELLED IS A FAULT AND IS NEVER READ FOR A RESULT. A cancelled task has no fault and
    /// no exception of its own, so a two-way faulted-or-result split reads its result and surfaces an
    /// aggregate exception out of a proposal that should simply have retried.
    /// </summary>
    /// <remarks>
    /// The cancelled task here comes from a token that is not the caller's, so the only correct classification
    /// is a transport fault.
    /// </remarks>
    [TestMethod]
    public async Task ATaskThatEndsCancelledIsClassifiedAsAFaultRatherThanReadForAResult()
    {
        SeededPrioritySource source = new(17);
        using CancellationTokenSource foreign = new();
        await foreign.CancelAsync().ConfigureAwait(false);
        CancellationToken foreignToken = foreign.Token;

        ScriptedRecorder[] recorders =
        [
            new(QuePaxaRecorder<string>.Leaderless, (recorder, attempt, request, _) => attempt == 0
                ? new ValueTask<RecordReply<string>>(Task.FromCanceled<RecordReply<string>>(foreignToken))
                : recorder.Answering(request)),
            new(QuePaxaRecorder<string>.Leaderless, static (recorder, _, request, _) => recorder.Answering(request)),
            new(QuePaxaRecorder<string>.Leaderless, static (recorder, _, _, _) => recorder.Hanging())
        ];

        QuePaxaProposer<string> proposer = ProposerOver(recorders, LaneA, source.Next, attemptsPerRecorder: 2);

        QuePaxaOutcome<string> outcome = await AwaitProposalAsync(proposer.ProposeAsync(null, "a", TestContext.CancellationToken)).ConfigureAwait(false);

        Assert.IsTrue(outcome.IsDecided);
        Assert.HasCount(2, recorders[0].ReceivedAt(Four));
    }


    /// <summary>
    /// A NULL REPLY IS A FAULT AND NOT AN ANSWER. The endpoint delegate is typed non-nullable, but a
    /// nullable-oblivious host can return null across the seam, and a null summary reaching the conclusion is
    /// a state the conclusion refuses with an argument exception rather than a protocol outcome.
    /// </summary>
    /// <remarks>
    /// Treating it as a fault keeps the failure inside the transport, where a retry is the correct answer.
    /// </remarks>
    [TestMethod]
    public async Task ANullReplyIsTreatedAsAFaultRatherThanAsAnAnswer()
    {
        SeededPrioritySource source = new(23);
        ScriptedRecorder[] recorders =
        [
            new(QuePaxaRecorder<string>.Leaderless, static (recorder, attempt, request, _) => attempt == 0
                ? ValueTask.FromResult<RecordReply<string>>(null!)
                : recorder.Answering(request)),
            new(QuePaxaRecorder<string>.Leaderless, static (recorder, _, request, _) => recorder.Answering(request)),
            new(QuePaxaRecorder<string>.Leaderless, static (recorder, _, _, _) => recorder.Hanging())
        ];

        QuePaxaProposer<string> proposer = ProposerOver(recorders, LaneA, source.Next, attemptsPerRecorder: 2);

        QuePaxaOutcome<string> outcome = await AwaitProposalAsync(proposer.ProposeAsync(null, "a", TestContext.CancellationToken)).ConfigureAwait(false);

        Assert.IsTrue(outcome.IsDecided);
        Assert.HasCount(2, recorders[0].ReceivedAt(Four));
    }


    /// <summary>
    /// THE PROPOSER IS ONE-SHOT, and the latch is where the proposal key's uniqueness contract is enforced
    /// rather than merely documented.
    /// </summary>
    /// <remarks>
    /// A second proposal on the same lane reopens the round at the first step, where the phase-zero draw is
    /// fresh, so one proposer would hold two distinct keys at one (recorder, step four) pair — precisely the
    /// state the model's send flag forbids. The documented recovery is a FRESH LANE on a fresh proposer, which
    /// is the correct protocol reading rather than a workaround, because a retry under a new lane is a
    /// different proposer identity the model already handles.
    /// </remarks>
    [TestMethod]
    public async Task ASecondProposalAfterADecisionThrows()
    {
        ProposalPrioritySourceDelegate never = static () => throw new InvalidOperationException("The fast path draws no priority.");
        ScriptedRecorder[] recorders = LedRecorders(3, LaneA);
        QuePaxaProposer<string> proposer = ProposerOver(recorders, LaneA, never, attemptsPerRecorder: 2);

        QuePaxaOutcome<string> outcome = await AwaitProposalAsync(proposer.ProposeAsync(LaneA, "a", TestContext.CancellationToken)).ConfigureAwait(false);

        Assert.IsTrue(outcome.IsDecided);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await proposer.ProposeAsync(LaneA, "a", TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
    }


    /// <summary>
    /// THE MISSED-QUORUM CASE IS THE ONE A HOST WILL ACTUALLY REACH FOR, so it is a separate test.
    /// </summary>
    /// <remarks>
    /// A missed quorum is not terminal for the instance and a host reading the outcome will want to try again;
    /// retrying on this proposer is exactly the violation above, and the latch is what stops it.
    /// </remarks>
    [TestMethod]
    public async Task ASecondProposalAfterAMissedQuorumThrows()
    {
        SeededPrioritySource source = new(29);
        ScriptedRecorder[] recorders =
        [
            new(QuePaxaRecorder<string>.Leaderless, static (_, _, _, _) => Faulting()),
            new(QuePaxaRecorder<string>.Leaderless, static (_, _, _, _) => Faulting()),
            new(QuePaxaRecorder<string>.Leaderless, static (_, _, _, _) => Faulting())
        ];

        QuePaxaProposer<string> proposer = ProposerOver(recorders, LaneA, source.Next, attemptsPerRecorder: 1);

        QuePaxaOutcome<string> outcome = await AwaitProposalAsync(proposer.ProposeAsync(null, "a", TestContext.CancellationToken)).ConfigureAwait(false);

        Assert.IsFalse(outcome.IsDecided);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await proposer.ProposeAsync(null, "a", TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
    }


    /// <summary>
    /// THE FAST PATH COSTS ONE STEP, which is the reason the reserved priority exists. The configured leader
    /// that believes it leads claims the reserved priority, every recorder honours it, the gather is uniform,
    /// and the decision is taken at round one phase zero.
    /// </summary>
    /// <remarks>
    /// A source that throws on any draw proves the step consumed no entropy at all, because the reserved claim
    /// is not a draw.
    /// </remarks>
    [TestMethod]
    public async Task TheConfiguredAndBelievingLeaderDecidesInOneStepAtTheFirstStep()
    {
        ProposalPrioritySourceDelegate never = static () => throw new InvalidOperationException("The fast path draws no priority.");
        ScriptedRecorder[] recorders = LedRecorders(3, LaneA);
        QuePaxaProposer<string> proposer = ProposerOver(recorders, LaneA, never, attemptsPerRecorder: 2);

        QuePaxaOutcome<string> outcome = await AwaitProposalAsync(proposer.ProposeAsync(LaneA, "a", TestContext.CancellationToken)).ConfigureAwait(false);

        Assert.IsTrue(outcome.IsDecided);
        Assert.AreEqual("a", outcome.Value);
        Assert.AreEqual(LaneA, outcome.DecidedBy);
        Assert.AreEqual(RecorderStep.RoundOnePhaseZero, outcome.DecidedAt);
        Assert.AreEqual(1, outcome.Steps);

        foreach(ScriptedRecorder recorder in recorders)
        {
            Assert.HasCount(1, recorder.Received);
            Assert.AreEqual(ProposalPriority.Reserved, recorder.Received[0].Proposal.Key.Priority);
        }
    }


    /// <summary>
    /// A PROPOSER THAT DOES NOT CLAIM LEADERSHIP TAKES THREE STEPS, deciding at round one phase two.
    /// </summary>
    /// <remarks>
    /// That is the ordinary path and the contrast that makes the fast path measurable: one round trip against
    /// one round.
    /// </remarks>
    [TestMethod]
    public async Task AProposerWithoutALeadershipClaimDecidesAtRoundOnePhaseTwo()
    {
        SeededPrioritySource source = new(4711);
        ScriptedRecorder[] recorders = LeaderlessRecorders(3);
        QuePaxaProposer<string> proposer = ProposerOver(recorders, LaneA, source.Next, attemptsPerRecorder: 2);

        QuePaxaOutcome<string> outcome = await AwaitProposalAsync(proposer.ProposeAsync(null, "a", TestContext.CancellationToken)).ConfigureAwait(false);

        Assert.IsTrue(outcome.IsDecided);
        Assert.AreEqual("a", outcome.Value);
        Assert.AreEqual(Six, outcome.DecidedAt);
        Assert.AreEqual(3, outcome.Steps);

        //One draw per recorder at the phase-zero step and none afterwards, because phases one to three send
        //the template untouched.
        Assert.AreEqual(proposer.RecorderCount, source.DrawCount);
    }


    /// <summary>
    /// THE OUTCOME NAMES THE OWNER OF THE DECIDED PROPOSAL AND NOT THE PROPOSER THAT OBSERVED THE DECISION.
    /// </summary>
    /// <remarks>
    /// A caller reads it to learn that someone else's value was chosen and that it must re-read and
    /// re-propose; nothing else in this suite distinguishes the two, because an uncontended proposal carries
    /// its own value through. The second proposer here believes nothing, gathers the leader's uniform reserved
    /// firsts, and MUST decide the leader's value: a proposer-side check of the winner's owner against the
    /// proposer's own belief would refuse this decision and continue into a global state the checked
    /// configurations never visited.
    /// </remarks>
    [TestMethod]
    public async Task DecidedByReportsTheOwnerWhenTheProposerCarriesAnotherLanesValue()
    {
        ProposalPrioritySourceDelegate never = static () => throw new InvalidOperationException("The leader's fast path draws no priority.");
        SeededPrioritySource follower = new(613);
        ScriptedRecorder[] recorders = LedRecorders(3, LaneA);

        QuePaxaProposer<string> leader = ProposerOver(recorders, LaneA, never, attemptsPerRecorder: 2);
        QuePaxaOutcome<string> leaderOutcome = await AwaitProposalAsync(leader.ProposeAsync(LaneA, "a", TestContext.CancellationToken)).ConfigureAwait(false);

        Assert.IsTrue(leaderOutcome.IsDecided);

        QuePaxaProposer<string> other = ProposerOver(recorders, LaneB, follower.Next, attemptsPerRecorder: 2);
        QuePaxaOutcome<string> otherOutcome = await AwaitProposalAsync(other.ProposeAsync(null, "b", TestContext.CancellationToken)).ConfigureAwait(false);

        Assert.IsTrue(otherOutcome.IsDecided);
        Assert.AreEqual("a", otherOutcome.Value);
        Assert.AreEqual(LaneA, otherOutcome.DecidedBy);
        Assert.AreEqual(LaneB, other.Lane);
        Assert.AreNotEqual(other.Lane, otherOutcome.DecidedBy);
    }


    /// <summary>
    /// MORE ANSWERS THAN THE MAJORITY IS NORMAL, and the derived quorum is a FLOOR rather than an equality. A
    /// deployment sized above the minimum may hear from every recorder, and the conclusion must be the one it
    /// would have reached on a bare majority.
    /// </summary>
    /// <remarks>
    /// The quorum is derived from the recorder count rather than supplied by the caller precisely so that no
    /// caller can weaken it; nothing about that makes a wider answer set illegal.
    /// </remarks>
    [TestMethod]
    public async Task AllFiveOfFiveAnswersReachTheSameConclusionAsThree()
    {
        ProposalPrioritySourceDelegate never = static () => throw new InvalidOperationException("The fast path draws no priority.");

        ScriptedRecorder[] three = LedRecorders(3, LaneA);
        QuePaxaProposer<string> narrow = ProposerOver(three, LaneA, never, attemptsPerRecorder: 2);
        QuePaxaOutcome<string> narrowOutcome = await AwaitProposalAsync(narrow.ProposeAsync(LaneA, "a", TestContext.CancellationToken)).ConfigureAwait(false);

        ScriptedRecorder[] five = LedRecorders(5, LaneA);
        QuePaxaProposer<string> wide = ProposerOver(five, LaneA, never, attemptsPerRecorder: 2);
        QuePaxaOutcome<string> wideOutcome = await AwaitProposalAsync(wide.ProposeAsync(LaneA, "a", TestContext.CancellationToken)).ConfigureAwait(false);

        Assert.AreEqual(2, narrow.Quorum);
        Assert.AreEqual(3, wide.Quorum);
        Assert.AreEqual(narrowOutcome.IsDecided, wideOutcome.IsDecided);
        Assert.AreEqual(narrowOutcome.Value, wideOutcome.Value);
        Assert.AreEqual(narrowOutcome.DecidedBy, wideOutcome.DecidedBy);
        Assert.AreEqual(narrowOutcome.DecidedAt, wideOutcome.DecidedAt);
        Assert.AreEqual(narrowOutcome.Steps, wideOutcome.Steps);

        //Every one of the five was asked exactly once, so the wider set answered rather than being ignored.
        foreach(ScriptedRecorder recorder in five)
        {
            Assert.HasCount(1, recorder.Received);
        }
    }


    /// <summary>
    /// THE QUORUM IS A STRICT MAJORITY DERIVED FROM THE RECORDER COUNT. A strict majority is what the proofs
    /// need: two majorities intersect, and that intersection is what the agreement lemmas rest on.
    /// </summary>
    /// <remarks>
    /// A deployment sized above the minimum may use larger quorums and stays safe; it is never required to.
    /// </remarks>
    [TestMethod]
    public void TheQuorumIsAStrictMajorityAndTheConstructorRefusesDegenerateArguments()
    {
        SeededPrioritySource source = new(1);
        RecorderEndpointDelegate<string> endpoint = static (_, _) => Faulting();

        Assert.AreEqual(1, ProposerWith(1, source).Quorum);
        Assert.AreEqual(2, ProposerWith(2, source).Quorum);
        Assert.AreEqual(2, ProposerWith(3, source).Quorum);
        Assert.AreEqual(3, ProposerWith(4, source).Quorum);
        Assert.AreEqual(3, ProposerWith(5, source).Quorum);
        Assert.AreEqual(4, ProposerWith(7, source).Quorum);

        QuePaxaProposer<string> proposer = ProposerWith(3, source);

        Assert.AreEqual(3, proposer.RecorderCount);
        Assert.AreEqual(LaneA, proposer.Lane);
        Assert.AreEqual(2, proposer.AttemptsPerRecorder);

        Assert.ThrowsExactly<ArgumentNullException>(() => _ = new QuePaxaProposer<string>(null!, LaneA, source.Next, 2));
        Assert.ThrowsExactly<ArgumentException>(() => _ = new QuePaxaProposer<string>([], LaneA, source.Next, 2));
        Assert.ThrowsExactly<ArgumentNullException>(() => _ = new QuePaxaProposer<string>([endpoint], LaneA, null!, 2));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = new QuePaxaProposer<string>([endpoint], LaneA, source.Next, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = new QuePaxaProposer<string>([endpoint], LaneA, source.Next, -1));
    }


    /// <summary>
    /// Every await of a proposal is bounded and its completion is asserted, because a wrong implementation of
    /// the first-quorum rule or of the terminal condition hangs rather than fails, and a hung suite reports
    /// nothing at all.
    /// </summary>
    /// <remarks>
    /// A STEP STOPS WAITING ONCE THE QUORUM IS ARITHMETICALLY OUT OF REACH, not merely once nothing is
    /// pending. Five recorders need three answers; three refuse at once and the remaining two never complete,
    /// so the two that are still outstanding cannot carry the count to three however long they take. Waiting
    /// for them is waiting for an answer the step already has, and against an endpoint that never completes it
    /// is waiting for ever, so the terminal condition must count what is still reachable rather than test
    /// whether anything is still pending.
    /// </remarks>
    [TestMethod]
    public async Task AStepStopsWaitingOnceTheQuorumIsOutOfReachEvenWhileEndpointsAreOutstanding()
    {
        ScriptedRecorder[] recorders =
        [
            new(QuePaxaRecorder<string>.Leaderless, static (_, _, _, _) => Faulting()),
            new(QuePaxaRecorder<string>.Leaderless, static (_, _, _, _) => Faulting()),
            new(QuePaxaRecorder<string>.Leaderless, static (_, _, _, _) => Faulting()),
            new(QuePaxaRecorder<string>.Leaderless, static (recorder, _, _, _) => recorder.Hanging()),
            new(QuePaxaRecorder<string>.Leaderless, static (recorder, _, _, _) => recorder.Hanging())
        ];

        QuePaxaProposer<string> proposer = ProposerOver(recorders, LaneA, new ScriptedPrioritySource(10, 11, 12, 13, 14).Next, attemptsPerRecorder: 1);

        Assert.AreEqual(3, proposer.Quorum);

        QuePaxaOutcome<string> outcome = await AwaitProposalAsync(proposer.ProposeAsync(null, "a", TestContext.CancellationToken)).ConfigureAwait(false);

        Assert.IsFalse(outcome.IsDecided);
        Assert.AreEqual(1, outcome.Steps);

        //The two endpoints the proposal stopped waiting for are still outstanding, which is what says the exit
        //came from the arithmetic rather than from every endpoint having settled.
        foreach(int index in (int[])[3, 4])
        {
            Assert.HasCount(1, recorders[index].Hung);
            Assert.IsFalse(recorders[index].Hung[0].Task.IsCompleted);
        }
    }


    /// <summary>
    /// A RECORDER IS NOT WORTH ASKING AGAIN ONCE THE STEP HAS ITS QUORUM.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The drain visits every recorder, so a later index whose attempt faulted is reached after an earlier
    /// index has already completed the quorum; re-sending it puts a record request on the wire for a step that
    /// has concluded, and the step then abandons the answer unread. It costs up to one wasted round trip per
    /// unanswered recorder per step, which a real recorder serves in full, and it is the quorum-latency claim
    /// this type is built around being paid for with traffic the step cannot use.
    /// </para>
    /// <para>
    /// Every endpoint answers or faults SYNCHRONOUSLY, so all three tasks are complete before the first wait
    /// returns and the drain visits them in index order within one pass. That is what makes the ordering a
    /// construction rather than a race.
    /// </para>
    /// </remarks>
    [TestMethod]
    public async Task ARecorderIsNotAskedAgainOnceAnEarlierAnswerCompletedTheQuorum()
    {
        ScriptedRecorder[] recorders =
        [
            new(QuePaxaRecorder<string>.Leaderless, static (recorder, _, request, _) => recorder.Answering(request)),
            new(QuePaxaRecorder<string>.Leaderless, static (recorder, _, request, _) => recorder.Answering(request)),
            new(QuePaxaRecorder<string>.Leaderless, static (_, _, _, _) => Faulting())
        ];

        QuePaxaProposer<string> proposer = ProposerOver(recorders, LaneA, new ScriptedPrioritySource(10, 11, 12).Next, attemptsPerRecorder: 2);

        Assert.AreEqual(2, proposer.Quorum);

        QuePaxaOutcome<string> outcome = await AwaitProposalAsync(proposer.ProposeAsync(null, "a", TestContext.CancellationToken)).ConfigureAwait(false);

        //Recorders zero and one filled the quorum before the drain reached recorder two, so recorder two's
        //spent attempt buys nothing and its budget must stay unspent for this step.
        Assert.HasCount(1, recorders[2].ReceivedAt(Four));
        Assert.HasCount(1, recorders[0].ReceivedAt(Four));
        Assert.IsTrue(outcome.IsDecided);
    }


    /// <summary>
    /// CANCELLATION IS DECIDED BY THE TOKEN AND NOT BY WHAT A TRANSPORT THREW. An endpoint is entitled to answer
    /// a signalled token by aborting its connection, which surfaces as an ordinary transport fault and not as an
    /// OperationCanceledException.
    /// </summary>
    /// <remarks>
    /// Classifying only the exception shape would report a MISSED QUORUM for a cancelled proposal, and the two
    /// are not interchangeable to a caller: a missed quorum invites a fresh attempt on a fresh lane, where a
    /// cancellation must unwind.
    /// </remarks>
    [TestMethod]
    public async Task ACancellationThatSurfacesAsAnOrdinaryTransportFaultStillPropagates()
    {
        using CancellationTokenSource cancellation = new();
        ScriptedRecorder[] recorders =
        [
            new(QuePaxaRecorder<string>.Leaderless, (_, _, _, token) => Abort(cancellation, token)),
            new(QuePaxaRecorder<string>.Leaderless, (_, _, _, token) => Abort(cancellation, token)),
            new(QuePaxaRecorder<string>.Leaderless, (_, _, _, token) => Abort(cancellation, token))
        ];

        QuePaxaProposer<string> proposer = ProposerOver(recorders, LaneA, new ScriptedPrioritySource(10, 11, 12).Next, attemptsPerRecorder: 1);

        Task<QuePaxaOutcome<string>> proposal = proposer.ProposeAsync(null, "a", cancellation.Token);

        await Assert.ThrowsAsync<OperationCanceledException>(() => proposal).ConfigureAwait(false);

        //Every endpoint faulted with an IOException rather than with a cancellation, so nothing but the token
        //itself could have produced the outcome above.
        foreach(ScriptedRecorder recorder in recorders)
        {
            Assert.HasCount(1, recorder.Received);
        }
    }


    /// <summary>
    /// AN ENDPOINT THAT THROWS BEFORE IT RETURNS A TASK IS THE SAME TRANSPORT FAULT AS ONE WHOSE TASK FAULTS.
    /// </summary>
    /// <remarks>
    /// A host writing an endpoint by hand throws synchronously as readily as it returns a faulted task, and
    /// the two must not classify differently: an unconverted synchronous throw would escape the step and abort
    /// the proposal on a fault the attempt budget exists to absorb.
    /// </remarks>
    [TestMethod]
    public async Task AnEndpointThrowingSynchronouslyIsClassifiedAsATransportFaultAndRetried()
    {
        ScriptedRecorder[] recorders =
        [
            new(QuePaxaRecorder<string>.Leaderless, static (recorder, attempt, request, _) => attempt == 0 ? throw new IOException("The connection was refused before a task existed.") : recorder.Answering(request)),
            new(QuePaxaRecorder<string>.Leaderless, static (recorder, _, request, _) => recorder.Answering(request)),
            new(QuePaxaRecorder<string>.Leaderless, static (recorder, attempt, request, _) => attempt == 0 ? throw new IOException("The connection was refused before a task existed.") : recorder.Answering(request))
        ];

        QuePaxaProposer<string> proposer = ProposerOver(recorders, LaneA, new ScriptedPrioritySource(10, 11, 12).Next, attemptsPerRecorder: 2);

        QuePaxaOutcome<string> outcome = await AwaitProposalAsync(proposer.ProposeAsync(null, "a", TestContext.CancellationToken)).ConfigureAwait(false);

        //The synchronous throw was absorbed and retried rather than escaping, so the step gathered its quorum.
        Assert.HasCount(2, recorders[0].ReceivedAt(Four));
        Assert.AreEqual(recorders[0].ReceivedAt(Four)[0], recorders[0].ReceivedAt(Four)[1]);
        Assert.IsGreaterThanOrEqualTo(1, outcome.Steps);
    }


    /// <summary>
    /// A REPLY FROM BELOW THE STEP BEING GATHERED IS DISCARDED RATHER THAN COUNTED. Calls for consecutive steps
    /// overlap, because the endpoints outstanding when a quorum lands are abandoned rather than cancelled and
    /// the next step calls every recorder again; a reply carries the recorder's own step and not the step of the
    /// request it answers, so a transport that correlated by a single per-recorder slot hands the older call's
    /// reply to the newer call.
    /// </summary>
    /// <remarks>
    /// Counting it would put a stale aggregate into the conclusion, and at phase two that is a decision taken
    /// on a majority that never gathered at the deciding step.
    /// </remarks>
    [TestMethod]
    public async Task AReplyFromBelowTheGatheredStepIsDiscardedRatherThanCounted()
    {
        RecordReply<string> stale = new(Four, new PrioritizedProposal<string>(new ProposalKey(new ProposalPriority(7), LaneB), "stale"), null);
        ScriptedRecorder[] recorders =
        [
            new(QuePaxaRecorder<string>.Leaderless, static (recorder, _, request, _) => recorder.Answering(request)),
            new(QuePaxaRecorder<string>.Leaderless, static (recorder, _, request, _) => recorder.Answering(request)),
            new(QuePaxaRecorder<string>.Leaderless, (recorder, _, request, _) => request.Step > Four ? ValueTask.FromResult(stale) : recorder.Answering(request))
        ];

        QuePaxaProposer<string> proposer = ProposerOver(recorders, LaneA, new ScriptedPrioritySource(10, 11, 12).Next, attemptsPerRecorder: 1);

        QuePaxaOutcome<string> outcome = await AwaitProposalAsync(proposer.ProposeAsync(null, "a", TestContext.CancellationToken)).ConfigureAwait(false);

        //Handing the stale answer to the conclusion would have thrown on the step precondition rather than
        //deciding, so a clean decision is what says it was dropped.
        Assert.IsTrue(outcome.IsDecided);
        Assert.AreEqual("a", outcome.Value);
        Assert.AreEqual(Six, outcome.DecidedAt);
    }


    /// <summary>
    /// An endpoint that answers a signalled token by aborting its connection, which is what a real transport
    /// does and which carries no cancellation in its exception type.
    /// </summary>
    /// <remarks>
    /// The first call signals the token, so the cancellation lands MID-STEP rather than before it: an entry
    /// check cannot be what ends the proposal, and only the test after the gather can.
    /// </remarks>
    private static async ValueTask<RecordReply<string>> Abort(CancellationTokenSource cancellation, CancellationToken token)
    {
        if(!token.IsCancellationRequested)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
        }

        throw new IOException("The connection was aborted.");
    }


    private async Task<QuePaxaOutcome<string>> AwaitProposalAsync(Task<QuePaxaOutcome<string>> proposal)
    {
        QuePaxaOutcome<string> outcome = await proposal.WaitAsync(ProposalTimeout, TestContext.CancellationToken).ConfigureAwait(false);

        Assert.IsTrue(proposal.IsCompletedSuccessfully, "The proposal did not complete under its timeout.");

        return outcome;
    }


    private static QuePaxaProposer<string> ProposerWith(int recorderCount, SeededPrioritySource source)
    {
        var endpoints = new RecorderEndpointDelegate<string>[recorderCount];
        for(int i = 0; i < recorderCount; i++)
        {
            endpoints[i] = static (_, _) => Faulting();
        }

        return new QuePaxaProposer<string>(endpoints, LaneA, source.Next, 2);
    }


    private static QuePaxaProposer<string> ProposerOver(
        IReadOnlyList<ScriptedRecorder> recorders,
        ProposerLane lane,
        ProposalPrioritySourceDelegate drawPriority,
        int attemptsPerRecorder)
    {
        var endpoints = new RecorderEndpointDelegate<string>[recorders.Count];
        for(int i = 0; i < recorders.Count; i++)
        {
            endpoints[i] = recorders[i].Serve;
        }

        return new QuePaxaProposer<string>(endpoints, lane, drawPriority, attemptsPerRecorder);
    }


    private static ScriptedRecorder[] LedRecorders(int count, ProposerLane leader)
    {
        var recorders = new ScriptedRecorder[count];
        for(int i = 0; i < count; i++)
        {
            recorders[i] = new ScriptedRecorder(QuePaxaRecorder<string>.LedBy(leader), static (recorder, _, request, _) => recorder.Answering(request));
        }

        return recorders;
    }


    private static ScriptedRecorder[] LeaderlessRecorders(int count)
    {
        var recorders = new ScriptedRecorder[count];
        for(int i = 0; i < count; i++)
        {
            recorders[i] = new ScriptedRecorder(QuePaxaRecorder<string>.Leaderless, static (recorder, _, request, _) => recorder.Answering(request));
        }

        return recorders;
    }


    /// <summary>
    /// A transport failure, delivered as a FAULTED TASK rather than as a synchronous throw, because the faulted
    /// task is the shape the proposer's classification names.
    /// </summary>
    private static ValueTask<RecordReply<string>> Faulting()
    {
        return ValueTask.FromException<RecordReply<string>>(new IOException("The recorder is unreachable."));
    }


    /// <summary>
    /// A cancellation reported against a chosen token.
    /// </summary>
    /// <remarks>
    /// Which token it names is the whole subject of two tests: the caller's token means the proposal is
    /// cancelled, and any other token means one endpoint imposed its own deadline and the attempt should be
    /// re-sent.
    /// </remarks>
    private static ValueTask<RecordReply<string>> Cancelling(CancellationToken token)
    {
        return ValueTask.FromException<RecordReply<string>>(new OperationCanceledException(token));
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }


    /// <summary>
    /// How a scripted endpoint answers one attempt.
    /// </summary>
    /// <remarks>
    /// The attempt index counts every request this endpoint has ever been asked to serve, so a script can
    /// distinguish the first delivery of a step from its re-send.
    /// </remarks>
    private delegate ValueTask<RecordReply<string>> ScriptedAnswerDelegate(
        ScriptedRecorder recorder,
        int attempt,
        RecordRequest<string> request,
        CancellationToken cancellationToken);


    /// <summary>
    /// One scripted recorder endpoint over a real node: it logs every request it is asked to serve and then
    /// answers, faults, or hangs on the script's instruction.
    /// </summary>
    /// <remarks>
    /// Logging the request rather than the intent is the point, because the re-send rule is a statement about
    /// what reached the wire.
    /// </remarks>
    private sealed class ScriptedRecorder
    {
        public ScriptedRecorder(QuePaxaRecorder<string> recorder, ScriptedAnswerDelegate answer)
        {
            Node = new QuePaxaNode<string>(recorder);
            Answer = answer;
        }


        public QuePaxaNode<string> Node { get; }

        public List<RecordRequest<string>> Received { get; } = [];

        public List<TaskCompletionSource<RecordReply<string>>> Hung { get; } = [];


        private ScriptedAnswerDelegate Answer { get; }


        public ValueTask<RecordReply<string>> Serve(RecordRequest<string> request, CancellationToken cancellationToken)
        {
            Received.Add(request);

            return Answer(this, Received.Count - 1, request, cancellationToken);
        }


        public ValueTask<RecordReply<string>> Answering(RecordRequest<string> request)
        {
            return ValueTask.FromResult(Node.Handle(request));
        }


        /// <summary>
        /// An endpoint that neither answers nor faults.
        /// </summary>
        /// <remarks>
        /// The completion is retained so a test can assert it is still incomplete, which is how the
        /// first-quorum rule is observed rather than merely timed.
        /// </remarks>
        public ValueTask<RecordReply<string>> Hanging()
        {
            TaskCompletionSource<RecordReply<string>> completion = new();
            Hung.Add(completion);

            return new ValueTask<RecordReply<string>>(completion.Task);
        }


        public List<RecordRequest<string>> ReceivedAt(RecorderStep step)
        {
            return [.. Received.Where(request => request.Step == step)];
        }
    }


    /// <summary>
    /// A fixed sequence rather than a seeded stream: a scenario that runs past the end of its script consumed
    /// entropy it was not designed to, so the source fails loudly rather than letting the run drift into a
    /// different behaviour.
    /// </summary>
    /// <remarks>
    /// That is what makes "one draw per recorder per step" checkable twice over.
    /// </remarks>
    private sealed class ScriptedPrioritySource
    {
        private int index;

        public ScriptedPrioritySource(params ulong[] script) => Script = script;


        public int DrawCount { get; private set; }


        private ulong[] Script { get; }


        public ProposalPriority Next()
        {
            if(index >= Script.Length)
            {
                throw new InvalidOperationException("The scripted priority source ran out of draws.");
            }

            DrawCount++;

            return new ProposalPriority(Script[index++]);
        }
    }


}
