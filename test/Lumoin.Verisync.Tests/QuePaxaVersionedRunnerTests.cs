using Lumoin.Verisync.Core;
using Lumoin.Verisync.Json;
using System.Buffers;
using System.Collections.Immutable;
using System.Text.Json;
using System.Threading.Channels;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// The versioned runner's suite. The subjects are the durability gate a reply waits on, the decline that
/// faults one call while the loop keeps serving, the learn that rides the same queue, and the abandonment
/// that faults every unanswered call when the loop ends — each pinned against the runner as a transport
/// host drives it, with every wait bounded so a wedged loop goes red rather than hanging the suite.
/// </summary>
[TestClass]
internal sealed class QuePaxaVersionedRunnerTests
{
    public TestContext TestContext { get; set; } = null!;

    private static ReplicaId First { get; } = Replica(1);
    private static ReplicaId Second { get; } = Replica(2);
    private static ReplicaId Third { get; } = Replica(3);

    /// <summary>
    /// The membership every record in this suite carries, minted from the agreed order the hosts run under.
    /// </summary>
    private static QuePaxaConfiguration Configuration { get; } = QuePaxaConfiguration.CreateGenesis([First, Second, Third]);

    private static RecorderStep Four { get; } = RecorderStep.RoundOnePhaseZero;
    private static RecorderStep Five { get; } = RecorderStep.FromRoundAndPhase(1, 1);

    private static TimeSpan Bounded { get; } = TimeSpan.FromSeconds(10);


    [TestMethod]
    public async Task AReplyIsWithheldUntilThePersistReturns()
    {
        QuePaxaVersionedNode<string> host = new(Schedule(), Record(4UL, Second));
        QuePaxaVersionedRunner<string> runner = new(host);
        GatedStore store = new();
        Task run = runner.RunAsync(store.PersistAsync, TestContext.CancellationToken);

        Task<VersionedRecordReply<VersionedValue<string>>> call = runner.RecordAsync(Request(5UL, ProposalPriority.Lowest, Second, "a"), TestContext.CancellationToken).AsTask();

        await store.Entered.WaitAsync(TestContext.CancellationToken).WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.IsFalse(call.IsCompleted);

        store.Release.Release();
        VersionedRecordReply<VersionedValue<string>> reply = await call.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(new RegisterVersion(5UL), reply.Version);
        Assert.HasCount(1, store.States);

        runner.Complete();
        await run.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task ARequestThatChangesNothingCostsNoFurtherWriteAndIsStillAnswered()
    {
        QuePaxaVersionedNode<string> host = new(Schedule(), Record(4UL, Second));
        QuePaxaVersionedRunner<string> runner = new(host);
        RecordingStore store = new();
        Task run = runner.RunAsync(store.PersistAsync, TestContext.CancellationToken);

        VersionedRecordRequest<VersionedValue<string>> request = Request(5UL, ProposalPriority.Lowest, Second, "a");
        VersionedRecordReply<VersionedValue<string>> firstReply = await runner.RecordAsync(request, TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);
        VersionedRecordReply<VersionedValue<string>> secondReply = await runner.RecordAsync(request, TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);

        //Two replies from one durable write: the redelivery changed nothing and the state it rests on was
        //already made durable, so a runner persisting per request rather than per changed state fails here.
        Assert.AreEqual(firstReply.Reply.Step, secondReply.Reply.Step);
        Assert.HasCount(1, store.States);

        runner.Complete();
        await run.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task ARedeliveryAfterAFailedPersistWritesAgainBeforeItAnswers()
    {
        QuePaxaLeaderSchedule schedule = Schedule();
        QuePaxaVersionedNode<string> host = new(schedule, Record(4UL, Second));
        QuePaxaVersionedRunner<string> failed = new(host);
        FailingStore failing = new();
        Task failedRun = failed.RunAsync(failing.PersistAsync, TestContext.CancellationToken);

        VersionedRecordRequest<VersionedValue<string>> request = Request(5UL, ProposalPriority.Lowest, Second, "a");
        Task<VersionedRecordReply<VersionedValue<string>>> failedCall = failed.RecordAsync(request, TestContext.CancellationToken).AsTask();

        InvalidOperationException fault = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => failedCall.WaitAsync(Bounded, TestContext.CancellationToken)).ConfigureAwait(false);
        Assert.IsInstanceOfType<IOException>(fault.InnerException);
        _ = await Assert.ThrowsExactlyAsync<IOException>(() => failedRun.WaitAsync(Bounded, TestContext.CancellationToken)).ConfigureAwait(false);
        Assert.AreEqual(1, failing.Attempts);

        QuePaxaRecorder<VersionedValue<string>> afterFailure = host.Recorder;
        QuePaxaVersionedRunner<string> fresh = new(host);
        RecordingStore store = new();
        Task freshRun = fresh.RunAsync(store.PersistAsync, TestContext.CancellationToken);

        VersionedRecordReply<VersionedValue<string>> reply = await fresh.RecordAsync(request, TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);

        //The redelivery changed nothing, so the same-instance premise the retried write rests on is pinned
        //inline: a gate comparing around the request rather than against what was written would skip here.
        Assert.AreSame(afterFailure, host.Recorder);
        Assert.HasCount(1, store.States);
        Assert.AreEqual(new RegisterVersion(5UL), reply.Version);

        VersionedRecordReply<VersionedValue<string>> third = await fresh.RecordAsync(request, TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(reply.Reply.Step, third.Reply.Step);
        Assert.HasCount(1, store.States);

        fresh.Complete();
        await freshRun.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task AFailedPersistFaultsTheCallInsteadOfAnsweringIt()
    {
        QuePaxaVersionedNode<string> host = new(Schedule(), Record(4UL, Second));
        QuePaxaVersionedRunner<string> runner = new(host);
        FailingStore store = new();
        Task run = runner.RunAsync(store.PersistAsync, TestContext.CancellationToken);

        Task<VersionedRecordReply<VersionedValue<string>>> call = runner.RecordAsync(Request(5UL, ProposalPriority.Lowest, Second, "a"), TestContext.CancellationToken).AsTask();

        InvalidOperationException fault = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => call.WaitAsync(Bounded, TestContext.CancellationToken)).ConfigureAwait(false);

        Assert.IsInstanceOfType<IOException>(fault.InnerException);
        Assert.AreEqual(TaskStatus.Faulted, call.Status);
        _ = await Assert.ThrowsExactlyAsync<IOException>(() => run.WaitAsync(Bounded, TestContext.CancellationToken)).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task AFailedPersistFaultsEveryQueuedCallInsteadOfLeavingThemSilent()
    {
        QuePaxaVersionedNode<string> host = new(Schedule(), Record(4UL, Second));
        QuePaxaVersionedRunner<string> runner = new(host);
        FailingStore store = new();

        Task<VersionedRecordReply<VersionedValue<string>>> first = runner.RecordAsync(Request(5UL, ProposalPriority.Lowest, Second, "a"), TestContext.CancellationToken).AsTask();
        Task<VersionedRecordReply<VersionedValue<string>>> second = runner.RecordAsync(Request(5UL, new ProposalPriority(7), Second, "b"), TestContext.CancellationToken).AsTask();
        Task<VersionedRecordReply<VersionedValue<string>>> third = runner.RecordAsync(Request(5UL, new ProposalPriority(9), Second, "c"), TestContext.CancellationToken).AsTask();

        Task run = runner.RunAsync(store.PersistAsync, TestContext.CancellationToken);

        //Bounded waits turn a runner that abandons nothing into a red rather than a hang.
        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => first.WaitAsync(Bounded, TestContext.CancellationToken)).ConfigureAwait(false);
        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => second.WaitAsync(Bounded, TestContext.CancellationToken)).ConfigureAwait(false);
        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => third.WaitAsync(Bounded, TestContext.CancellationToken)).ConfigureAwait(false);
        _ = await Assert.ThrowsExactlyAsync<IOException>(() => run.WaitAsync(Bounded, TestContext.CancellationToken)).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task ADeclineFaultsOnlyItsOwnCallAndTheHostKeepsServing()
    {
        QuePaxaVersionedNode<string> host = new(Schedule(), Record(4UL, Second));
        QuePaxaVersionedRunner<string> runner = new(host);
        RecordingStore store = new();
        Task run = runner.RunAsync(store.PersistAsync, TestContext.CancellationToken);

        _ = await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
            () => runner.RecordAsync(Request(7UL, ProposalPriority.Lowest, Second, "above"), TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken)).ConfigureAwait(false);

        VersionedRecordReply<VersionedValue<string>> reply = await runner.RecordAsync(Request(5UL, ProposalPriority.Lowest, Second, "live"), TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(new RegisterVersion(5UL), reply.Version);

        runner.Complete();
        await run.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task ADeclineIsAFaultAndNeverAReply()
    {
        QuePaxaVersionedNode<string> host = new(Schedule(), Record(4UL, Second));
        QuePaxaVersionedRunner<string> runner = new(host);
        Task run = runner.RunAsync(cancellationToken: TestContext.CancellationToken);

        Task<VersionedRecordReply<VersionedValue<string>>> declined = runner.RecordAsync(Request(7UL, ProposalPriority.Lowest, Second, "above"), TestContext.CancellationToken).AsTask();

        ArgumentOutOfRangeException fault = await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
            () => declined.WaitAsync(Bounded, TestContext.CancellationToken)).ConfigureAwait(false);

        //The prohibition is absent code: no runner-defined decline type may exist, so the fault is exactly
        //the host's own exception and no version travels in a typed field a wire host could serialize.
        Assert.AreEqual(TaskStatus.Faulted, declined.Status);
        foreach(System.Reflection.PropertyInfo property in fault.GetType().GetProperties())
        {
            Assert.AreNotEqual(typeof(RegisterVersion), property.PropertyType);
        }

        runner.Complete();
        await run.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task AnInMemoryLearnStillCostsNoWrite()
    {
        QuePaxaVersionedNode<string> host = new(Schedule(), Record(4UL, Second));
        QuePaxaVersionedRunner<string> runner = new(host);
        RecordingStore store = new();
        Task run = runner.RunAsync(store.PersistAsync, TestContext.CancellationToken);

        bool advanced = await runner.LearnAsync(Record(5UL, Third), LearnDurability.InMemory, TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);

        Assert.IsTrue(advanced);
        Assert.AreEqual(new RegisterVersion(6UL), host.LiveVersion);

        //The no-write assertion runs only after the loop has drained, because a learn's completion is set
        //before the dispatch finishes and an assert racing the loop can miss a write the mutant just made.
        runner.Complete();
        await run.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.IsEmpty(store.States);
    }


    [TestMethod]
    public async Task ALearnedRecordIsDurableBeforeTheFirstReplyThatDependsOnIt()
    {
        QuePaxaVersionedNode<string> host = new(Schedule(), Record(4UL, Second));
        QuePaxaVersionedRunner<string> runner = new(host);
        GatedStore store = new();
        Task run = runner.RunAsync(store.PersistAsync, TestContext.CancellationToken);

        VersionedValue<string> learned = Record(5UL, Third);
        Assert.IsTrue(await runner.LearnAsync(learned, LearnDurability.InMemory, TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false));

        Task<VersionedRecordReply<VersionedValue<string>>> call = runner.RecordAsync(Request(6UL, ProposalPriority.Lowest, Third, "a"), TestContext.CancellationToken).AsTask();

        await store.Entered.WaitAsync(TestContext.CancellationToken).WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.IsFalse(call.IsCompleted);

        store.Release.Release();
        VersionedRecordReply<VersionedValue<string>> reply = await call.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(new RegisterVersion(6UL), reply.Version);
        Assert.HasCount(1, store.States);
        Assert.AreSame(learned, store.States[0].Committed);
        Assert.AreEqual(new RegisterVersion(6UL), store.States[0].RecorderVersion);

        runner.Complete();
        await run.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task ARestoredHostCostsNoWriteForARequestItAlreadyRecorded()
    {
        QuePaxaLeaderSchedule schedule = Schedule();
        QuePaxaVersionedNode<string> host = new(schedule, Record(4UL, Second));
        QuePaxaVersionedRunner<string> runner = new(host);
        RecordingStore store = new();
        Task run = runner.RunAsync(store.PersistAsync, TestContext.CancellationToken);

        VersionedRecordRequest<VersionedValue<string>> request = Request(5UL, ProposalPriority.Lowest, Second, "a");
        _ = await runner.RecordAsync(request, TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);
        runner.Complete();
        await run.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.HasCount(1, store.States);

        QuePaxaVersionedNode<string> restored = QuePaxaVersionedNode<string>.FromState(schedule, store.States[0]);
        QuePaxaVersionedRunner<string> fresh = new(restored);
        RecordingStore after = new();
        Task freshRun = fresh.RunAsync(after.PersistAsync, TestContext.CancellationToken);

        VersionedRecordReply<VersionedValue<string>> reply = await fresh.RecordAsync(request, TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(new RegisterVersion(5UL), reply.Version);
        Assert.IsEmpty(after.States);

        fresh.Complete();
        await freshRun.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task ARepeatedDisseminationIsAdoptedSilently()
    {
        QuePaxaVersionedNode<string> host = new(Schedule(), Record(4UL, Second));
        QuePaxaVersionedRunner<string> runner = new(host);
        RecordingStore store = new();
        Task run = runner.RunAsync(store.PersistAsync, TestContext.CancellationToken);

        Assert.IsTrue(await runner.LearnAsync(Record(5UL, Third), LearnDurability.InMemory, TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false));
        Assert.IsFalse(await runner.LearnAsync(Record(5UL, Third), LearnDurability.InMemory, TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false));
        Assert.IsFalse(await runner.LearnAsync(Record(4UL, Second), LearnDurability.InMemory, TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false));

        Assert.AreEqual(new RegisterVersion(6UL), host.LiveVersion);

        //Asserted after the drain for the same reason the lone-learn vector is: no assert may race the loop.
        runner.Complete();
        await run.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.IsEmpty(store.States);
    }


    [TestMethod]
    public async Task OverlappingCallsAreServedOneAtATimeAndEachGetsItsOwnReply()
    {
        QuePaxaVersionedNode<string> host = new(Schedule(), Record(4UL, Second));
        QuePaxaVersionedRunner<string> runner = new(host);
        RecordingStore store = new();

        Task<VersionedRecordReply<VersionedValue<string>>> first = runner.RecordAsync(Request(5UL, Four, ProposalPriority.Lowest, Second, "a"), TestContext.CancellationToken).AsTask();
        Task<VersionedRecordReply<VersionedValue<string>>> second = runner.RecordAsync(Request(5UL, Five, ProposalPriority.Lowest, Second, "a"), TestContext.CancellationToken).AsTask();

        Task run = runner.RunAsync(store.PersistAsync, TestContext.CancellationToken);

        //Each task's reply carries the step its own call produced, which is the per-call correlation the
        //endpoint delegate demands, and the persisted states advance one at a time in arrival order.
        Assert.AreEqual(Four, (await first.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false)).Reply.Step);
        Assert.AreEqual(Five, (await second.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false)).Reply.Step);
        Assert.HasCount(2, store.States);
        Assert.AreEqual(Four, store.States[0].Recorder.Step);
        Assert.AreEqual(Five, store.States[1].Recorder.Step);

        runner.Complete();
        await run.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task ACallCancelledByItsOwnTokenCompletesInsteadOfHanging()
    {
        QuePaxaVersionedNode<string> host = new(Schedule(), Record(4UL, Second));
        QuePaxaVersionedRunner<string> runner = new(host);
        GatedStore store = new();
        Task run = runner.RunAsync(store.PersistAsync, TestContext.CancellationToken);

        Task<VersionedRecordReply<VersionedValue<string>>> held = runner.RecordAsync(Request(5UL, Four, ProposalPriority.Lowest, Second, "a"), TestContext.CancellationToken).AsTask();
        await store.Entered.WaitAsync(TestContext.CancellationToken).WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);

        using CancellationTokenSource cancellation = new();
        Task<VersionedRecordReply<VersionedValue<string>>> cancelled = runner.RecordAsync(Request(5UL, Five, ProposalPriority.Lowest, Second, "a"), cancellation.Token).AsTask();
        await cancellation.CancelAsync().ConfigureAwait(false);

        _ = await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => cancelled.WaitAsync(Bounded, TestContext.CancellationToken)).ConfigureAwait(false);

        //The cancelled call's queued work still runs, which is the safe direction: recording it twice is
        //the identity, while dropping it would make the host's state depend on a caller's patience.
        store.Release.Release();
        _ = await held.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);
        await store.Entered.WaitAsync(TestContext.CancellationToken).WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);
        store.Release.Release();

        runner.Complete();
        await run.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.HasCount(2, store.States);
    }


    [TestMethod]
    public async Task TheLoopRunsOnceAndARestartTakesAFreshRunner()
    {
        QuePaxaVersionedNode<string> host = new(Schedule(), Record(4UL, Second));
        QuePaxaVersionedRunner<string> runner = new(host);
        Task run = runner.RunAsync(cancellationToken: TestContext.CancellationToken);

        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runner.RunAsync(cancellationToken: TestContext.CancellationToken).WaitAsync(Bounded, TestContext.CancellationToken)).ConfigureAwait(false);

        runner.Complete();
        await run.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);

        //With the loop ended the node's claim is released, so the once-only guard is the only rule that can
        //refuse here: a reordering that took the claim before the guard would be invisible without this arm.
        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runner.RunAsync(cancellationToken: TestContext.CancellationToken).WaitAsync(Bounded, TestContext.CancellationToken)).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task WorkQueuedAfterCompletionFailsFastInsteadOfHanging()
    {
        QuePaxaVersionedNode<string> host = new(Schedule(), Record(4UL, Second));
        QuePaxaVersionedRunner<string> runner = new(host);
        Task run = runner.RunAsync(cancellationToken: TestContext.CancellationToken);
        runner.Complete();
        await run.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);

        _ = await Assert.ThrowsExactlyAsync<ChannelClosedException>(
            () => runner.RecordAsync(Request(5UL, ProposalPriority.Lowest, Second, "a"), TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken)).ConfigureAwait(false);
        _ = await Assert.ThrowsExactlyAsync<ChannelClosedException>(
            () => runner.LearnAsync(Record(5UL, Third), LearnDurability.InMemory, TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken)).ConfigureAwait(false);
        _ = await Assert.ThrowsExactlyAsync<ChannelClosedException>(
            () => runner.MakeDurableAsync(TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken)).ConfigureAwait(false);

        //The same fail-fast holds after a loop that ended on a failed write, so a producer never parks on a
        //runner that will not dispatch again.
        QuePaxaVersionedNode<string> second = new(Schedule(), Record(4UL, Second));
        QuePaxaVersionedRunner<string> failed = new(second);
        FailingStore store = new();
        Task failedRun = failed.RunAsync(store.PersistAsync, TestContext.CancellationToken);
        Task<VersionedRecordReply<VersionedValue<string>>> call = failed.RecordAsync(Request(5UL, ProposalPriority.Lowest, Second, "a"), TestContext.CancellationToken).AsTask();

        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => call.WaitAsync(Bounded, TestContext.CancellationToken)).ConfigureAwait(false);
        _ = await Assert.ThrowsExactlyAsync<IOException>(() => failedRun.WaitAsync(Bounded, TestContext.CancellationToken)).ConfigureAwait(false);
        _ = await Assert.ThrowsExactlyAsync<ChannelClosedException>(
            () => failed.RecordAsync(Request(5UL, ProposalPriority.Lowest, Second, "b"), TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken)).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task QueuedWorkTakesEffectOnlyWhenTheLoopDispatchesIt()
    {
        QuePaxaVersionedNode<string> host = new(Schedule(), Record(4UL, Second));
        QuePaxaVersionedRunner<string> runner = new(host);
        VersionedValue<string>? committed = host.Committed;
        QuePaxaRecorder<VersionedValue<string>> recorder = host.Recorder;

        Task<VersionedRecordReply<VersionedValue<string>>> call = runner.RecordAsync(Request(5UL, ProposalPriority.Lowest, Second, "a"), TestContext.CancellationToken).AsTask();
        Task<bool> learn = runner.LearnAsync(Record(5UL, Third), LearnDurability.InMemory, TestContext.CancellationToken).AsTask();

        Assert.IsFalse(call.IsCompleted);
        Assert.IsFalse(learn.IsCompleted);
        Assert.AreSame(committed, host.Committed);
        Assert.AreSame(recorder, host.Recorder);

        Task run = runner.RunAsync(cancellationToken: TestContext.CancellationToken);

        _ = await call.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.IsTrue(await learn.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false));
        Assert.AreEqual(new RegisterVersion(6UL), host.LiveVersion);

        runner.Complete();
        await run.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task AHostRestartedFromWhatTheRunnerWroteAnswersTheSameWay()
    {
        QuePaxaLeaderSchedule schedule = Schedule();
        QuePaxaVersionedNode<string> host = new(schedule, Record(4UL, Second));
        QuePaxaVersionedRunner<string> runner = new(host);
        SerializingStore store = new();
        Task run = runner.RunAsync(store.PersistAsync, TestContext.CancellationToken);

        VersionedRecordRequest<VersionedValue<string>> request = Request(5UL, ProposalPriority.Lowest, Second, "a");
        VersionedRecordReply<VersionedValue<string>> before = await runner.RecordAsync(request, TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);
        runner.Complete();
        await run.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);

        QuePaxaVersionedNodeState<string> state = QuePaxaMessageJson.CreateVersionedNodeStateDeserializer(ReadValue)(new ReadOnlySequence<byte>(store.Bytes!));
        QuePaxaVersionedNode<string> restored = QuePaxaVersionedNode<string>.FromState(schedule, state);
        QuePaxaVersionedRunner<string> fresh = new(restored);
        RecordingStore after = new();
        Task freshRun = fresh.RunAsync(after.PersistAsync, TestContext.CancellationToken);

        VersionedRecordReply<VersionedValue<string>> replayed = await fresh.RecordAsync(request, TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(before.Version, replayed.Version);
        Assert.AreEqual(before.Reply.Step, replayed.Reply.Step);
        Assert.AreEqual(before.Reply.First.Key, replayed.Reply.First.Key);
        Assert.IsEmpty(after.States);

        fresh.Complete();
        await freshRun.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task NullPersistDelegateAnswersImmediatelyAndCheckpointsAreNoOps()
    {
        QuePaxaVersionedNode<string> host = new(Schedule(), Record(4UL, Second));
        QuePaxaVersionedRunner<string> runner = new(host);
        Task run = runner.RunAsync(cancellationToken: TestContext.CancellationToken);

        VersionedRecordReply<VersionedValue<string>> reply = await runner.RecordAsync(Request(5UL, ProposalPriority.Lowest, Second, "a"), TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);
        await runner.MakeDurableAsync(TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(new RegisterVersion(5UL), reply.Version);

        runner.Complete();
        await run.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task ADeclineOnAHostPastTheFailedWriteCostsNoWriteAndLeavesTheStateAlone()
    {
        QuePaxaVersionedNode<string> host = new(Schedule(), Record(4UL, Second));
        QuePaxaVersionedRunner<string> runner = new(host);
        RecordingStore store = new();
        Task run = runner.RunAsync(store.PersistAsync, TestContext.CancellationToken);

        //The learn moves the host past its durable baseline without a checkpoint, so the no-write assertion
        //below reaches the arm where a wrongly-placed gate would actually fire. The durability must stay
        //in-memory: a durable learn checkpoints the host and deletes the vector's whole subject.
        Assert.IsTrue(await runner.LearnAsync(Record(5UL, Third), LearnDurability.InMemory, TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false));
        VersionedValue<string>? committed = host.Committed;
        QuePaxaRecorder<VersionedValue<string>> recorder = host.Recorder;

        _ = await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
            () => runner.RecordAsync(Request(5UL, ProposalPriority.Lowest, Second, "closed"), TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken)).ConfigureAwait(false);

        Assert.IsEmpty(store.States);
        Assert.AreSame(committed, host.Committed);
        Assert.AreSame(recorder, host.Recorder);

        runner.Complete();
        await run.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task ASpentHostDeclinesEachCallAndTheLoopKeepsServing()
    {
        QuePaxaVersionedNode<string> host = new(Schedule(), Record(4UL, Second));
        QuePaxaVersionedRunner<string> runner = new(host);
        RecordingStore store = new();
        Task run = runner.RunAsync(store.PersistAsync, TestContext.CancellationToken);

        //The durability must stay in-memory: a durable learn into the spent range fires the gate, the snapshot
        //throws, and the loop this vector needs alive ends before it can decline anything.
        Assert.IsTrue(await runner.LearnAsync(new VersionedValue<string>(RegisterVersion.MaxValue, Third, Configuration, "spent"), LearnDurability.InMemory, TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false));

        //The spent-range refusal is a different exception type than the version mismatch, and both must
        //fault their own call: a decline filter narrowed to the range type would end the loop here instead.
        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => runner.RecordAsync(Request(5UL, ProposalPriority.Lowest, Second, "a"), TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken)).ConfigureAwait(false);
        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => runner.RecordAsync(Request(6UL, ProposalPriority.Lowest, Second, "b"), TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken)).ConfigureAwait(false);

        Assert.IsEmpty(store.States);

        runner.Complete();
        await run.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task RunnerCancellationCancelsPendingCallsRatherThanFaultingThem()
    {
        QuePaxaVersionedNode<string> host = new(Schedule(), Record(4UL, Second));
        QuePaxaVersionedRunner<string> runner = new(host);
        GatedStore store = new();
        using CancellationTokenSource cancellation = new();
        Task run = runner.RunAsync(store.PersistAsync, cancellation.Token);

        Task<VersionedRecordReply<VersionedValue<string>>> held = runner.RecordAsync(Request(5UL, Four, ProposalPriority.Lowest, Second, "a"), TestContext.CancellationToken).AsTask();
        await store.Entered.WaitAsync(TestContext.CancellationToken).WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);
        Task<VersionedRecordReply<VersionedValue<string>>> queued = runner.RecordAsync(Request(5UL, Five, ProposalPriority.Lowest, Second, "a"), TestContext.CancellationToken).AsTask();

        await cancellation.CancelAsync().ConfigureAwait(false);

        //A stopped host reads as cancelled and a refusing one as faulted, and a caller must be able to tell
        //them apart, so neither pending call may surface the stop as a fault.
        TaskCanceledException first = await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => held.WaitAsync(Bounded, TestContext.CancellationToken)).ConfigureAwait(false);
        TaskCanceledException second = await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => queued.WaitAsync(Bounded, TestContext.CancellationToken)).ConfigureAwait(false);

        Assert.AreEqual(cancellation.Token, first.CancellationToken);
        Assert.AreEqual(cancellation.Token, second.CancellationToken);

        //The loop's own task surfaces whichever cancellation exception the interrupted await threw, so the
        //exact subtype is the await's business; the pending calls above are the exact-type contract.
        _ = await Assert.ThrowsAsync<OperationCanceledException>(() => run.WaitAsync(Bounded, TestContext.CancellationToken)).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task ADeclinedCallKeepsItsFaultWhenTheLoopLaterEnds()
    {
        QuePaxaVersionedNode<string> host = new(Schedule(), Record(4UL, Second));
        QuePaxaVersionedRunner<string> runner = new(host);
        FailingStore store = new();
        Task run = runner.RunAsync(store.PersistAsync, TestContext.CancellationToken);

        Task<VersionedRecordReply<VersionedValue<string>>> declined = runner.RecordAsync(Request(7UL, ProposalPriority.Lowest, Second, "above"), TestContext.CancellationToken).AsTask();
        _ = await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => declined.WaitAsync(Bounded, TestContext.CancellationToken)).ConfigureAwait(false);

        Task<VersionedRecordReply<VersionedValue<string>>> failing = runner.RecordAsync(Request(5UL, ProposalPriority.Lowest, Second, "a"), TestContext.CancellationToken).AsTask();
        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => failing.WaitAsync(Bounded, TestContext.CancellationToken)).ConfigureAwait(false);
        _ = await Assert.ThrowsExactlyAsync<IOException>(() => run.WaitAsync(Bounded, TestContext.CancellationToken)).ConfigureAwait(false);

        //The abandonment must not rewrite a call that already answered for itself.
        _ = await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => declined.WaitAsync(Bounded, TestContext.CancellationToken)).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task NullArgumentsAreRefused()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => _ = new QuePaxaVersionedRunner<string>(null!));

        QuePaxaVersionedNode<string> host = new(Schedule(), Record(4UL, Second));
        QuePaxaVersionedRunner<string> runner = new(host);

        _ = await Assert.ThrowsExactlyAsync<ArgumentNullException>(async () => _ = await runner.RecordAsync(null!, TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        _ = await Assert.ThrowsExactlyAsync<ArgumentNullException>(async () => _ = await runner.LearnAsync(null!, LearnDurability.InMemory, TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task ASecondRunnerOverAnOwnedNodeIsRefusedAndTheFirstKeepsServing()
    {
        QuePaxaVersionedNode<string> host = new(Schedule(), Record(4UL, Second));
        QuePaxaVersionedRunner<string> owner = new(host);
        QuePaxaVersionedRunner<string> intruder = new(host);
        Task run = owner.RunAsync(cancellationToken: TestContext.CancellationToken);

        //The intruder's own once-only guard is untouched, so the node's claim is the only rule that can
        //refuse this loop and a sibling guard cannot stand in for it. The wait is bounded like every other in
        //this suite, because a loop that is admitted rather than refused never completes on its own and an
        //unbounded await would wedge here instead of reddening.
        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => intruder.RunAsync(cancellationToken: TestContext.CancellationToken).WaitAsync(Bounded, TestContext.CancellationToken)).ConfigureAwait(false);

        //A direct call reads whether the refused claim survived, which serving cannot: the loop drives the
        //ungated cores and would keep answering over a node whose claim the intruder had dropped.
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = host.Handle(Request(5UL, ProposalPriority.Lowest, Second, "direct")));

        VersionedRecordReply<VersionedValue<string>> reply = await owner.RecordAsync(Request(5UL, ProposalPriority.Lowest, Second, "a"), TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(new RegisterVersion(5UL), reply.Version);

        owner.Complete();
        await run.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task AFreshRunnerTakesTheNodeOnceTheFirstLoopHasEnded()
    {
        QuePaxaVersionedNode<string> host = new(Schedule(), Record(4UL, Second));
        QuePaxaVersionedRunner<string> completed = new(host);
        Task completedRun = completed.RunAsync(cancellationToken: TestContext.CancellationToken);
        completed.Complete();
        await completedRun.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);

        //The claim is released before the loop's task completes, which is what lets the fresh runner take it.
        QuePaxaVersionedRunner<string> afterDrain = new(host);
        Task afterDrainRun = afterDrain.RunAsync(cancellationToken: TestContext.CancellationToken);
        VersionedRecordReply<VersionedValue<string>> served = await afterDrain.RecordAsync(Request(5UL, ProposalPriority.Lowest, Second, "a"), TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(new RegisterVersion(5UL), served.Version);

        afterDrain.Complete();
        await afterDrainRun.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);

        //The failure exit takes the same path, which is the shape the documented restart after a failed write
        //rests on: the release rides a finally rather than the drained end of the loop.
        QuePaxaVersionedNode<string> failedHost = new(Schedule(), Record(4UL, Second));
        QuePaxaVersionedRunner<string> failed = new(failedHost);
        FailingStore failing = new();
        Task failedRun = failed.RunAsync(failing.PersistAsync, TestContext.CancellationToken);
        Task<VersionedRecordReply<VersionedValue<string>>> failedCall = failed.RecordAsync(Request(5UL, ProposalPriority.Lowest, Second, "a"), TestContext.CancellationToken).AsTask();

        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => failedCall.WaitAsync(Bounded, TestContext.CancellationToken)).ConfigureAwait(false);
        _ = await Assert.ThrowsExactlyAsync<IOException>(() => failedRun.WaitAsync(Bounded, TestContext.CancellationToken)).ConfigureAwait(false);

        QuePaxaVersionedRunner<string> afterFailure = new(failedHost);
        RecordingStore store = new();
        Task afterFailureRun = afterFailure.RunAsync(store.PersistAsync, TestContext.CancellationToken);
        VersionedRecordReply<VersionedValue<string>> retried = await afterFailure.RecordAsync(Request(5UL, ProposalPriority.Lowest, Second, "a"), TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(new RegisterVersion(5UL), retried.Version);

        afterFailure.Complete();
        await afterFailureRun.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task ARefusedSecondRunAsyncDoesNotReleaseTheOwnersClaim()
    {
        QuePaxaVersionedNode<string> host = new(Schedule(), Record(4UL, Second));
        QuePaxaVersionedRunner<string> owner = new(host);
        Task run = owner.RunAsync(cancellationToken: TestContext.CancellationToken);

        //Both refusals are awaited under a bound, because a call admitted where it should have been refused
        //returns a loop that runs until the queue closes, and an unbounded await would wedge here instead of
        //reddening.
        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => owner.RunAsync(cancellationToken: TestContext.CancellationToken).WaitAsync(Bounded, TestContext.CancellationToken)).ConfigureAwait(false);

        //A once-only refusal that reached the finally would drop the claim the running loop still holds, so
        //the second runner is what reads whether the refusal path released anything.
        QuePaxaVersionedRunner<string> intruder = new(host);

        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => intruder.RunAsync(cancellationToken: TestContext.CancellationToken).WaitAsync(Bounded, TestContext.CancellationToken)).ConfigureAwait(false);

        owner.Complete();
        await run.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task ADirectCheckpointOnAnOwnedHostIsRefused()
    {
        QuePaxaVersionedNode<string> host = new(Schedule(), Record(4UL, Second));
        QuePaxaVersionedRunner<string> runner = new(host);
        RecordingStore store = new();
        Task run = runner.RunAsync(store.PersistAsync, TestContext.CancellationToken);

        //The learn leaves the host owing a write, so the refused checkpoint is observable as a write that
        //never happened rather than as one the gate would have skipped anyway.
        Assert.IsTrue(await runner.LearnAsync(Record(5UL, Third), LearnDurability.InMemory, TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false));

        //The delegate is non-null, so the null guard cannot be the rule that fires here.
        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await host.MakeDurableAsync(store.PersistAsync, TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

        runner.Complete();
        await run.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.IsEmpty(store.States);

        //The same call on the now-unowned host succeeds, so ownership is the only difference between them.
        await host.MakeDurableAsync(store.PersistAsync, TestContext.CancellationToken).ConfigureAwait(false);

        Assert.HasCount(1, store.States);
    }


    [TestMethod]
    public async Task ADirectHandleOnAnOwnedHostIsRefused()
    {
        QuePaxaVersionedNode<string> host = new(Schedule(), Record(4UL, Second));
        QuePaxaVersionedRunner<string> runner = new(host);
        Task run = runner.RunAsync(cancellationToken: TestContext.CancellationToken);
        QuePaxaRecorder<VersionedValue<string>> before = host.Recorder;

        //The request names the LIVE version, so neither the version refusal nor the spent-range one can fire
        //and the latch is the only rule left. A vector that tripped a sibling guard would pin nothing.
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = host.Handle(Request(5UL, ProposalPriority.Lowest, Second, "a")));
        Assert.AreSame(before, host.Recorder);

        runner.Complete();
        await run.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);

        _ = host.Handle(Request(5UL, ProposalPriority.Lowest, Second, "a"));

        Assert.AreNotSame(before, host.Recorder);
    }


    [TestMethod]
    public async Task ADirectLearnOnAnOwnedHostIsRefused()
    {
        QuePaxaVersionedNode<string> host = new(Schedule(), Record(4UL, Second));
        QuePaxaVersionedRunner<string> runner = new(host);
        Task run = runner.RunAsync(cancellationToken: TestContext.CancellationToken);
        VersionedValue<string>? committed = host.Committed;
        VersionedValue<string> advancing = Record(5UL, Third);

        //The record is non-null and strictly newer, so neither the null guard nor the non-advancing arm is
        //what refuses it.
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = host.Learn(advancing));
        Assert.AreSame(committed, host.Committed);

        runner.Complete();
        await run.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);

        Assert.IsTrue(host.Learn(advancing));
        Assert.AreSame(advancing, host.Committed);
    }


    [TestMethod]
    public async Task ToStateOnAnOwnedHostIsRefused()
    {
        QuePaxaVersionedNode<string> host = new(Schedule(), Record(4UL, Second));
        QuePaxaVersionedRunner<string> runner = new(host);
        Task run = runner.RunAsync(cancellationToken: TestContext.CancellationToken);

        //The host is not spent, so the snapshot's own live-version throw cannot fire and the latch is the
        //only rule left. A snapshot is the one composite read on this surface: it pairs a record with the
        //register of the instance that record implies, and a learn replaces those two in two stores, so a
        //reader beside the loop can produce exactly the torn pairing FromState exists to refuse.
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = host.ToState());

        runner.Complete();
        await run.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);

        QuePaxaVersionedNodeState<string> snapshot = host.ToState();

        Assert.AreSame(host.Committed, snapshot.Committed);
        Assert.AreEqual(new RegisterVersion(5UL), snapshot.RecorderVersion);
    }


    [TestMethod]
    public async Task ADurableLearnIsPersistedBeforeItCompletes()
    {
        QuePaxaVersionedNode<string> host = new(Schedule(), Record(4UL, Second));
        QuePaxaVersionedRunner<string> runner = new(host);
        GatedStore store = new();
        Task run = runner.RunAsync(store.PersistAsync, TestContext.CancellationToken);

        VersionedValue<string> learned = Record(5UL, Third);
        Task<bool> learn = runner.LearnAsync(learned, LearnDurability.Durable, TestContext.CancellationToken).AsTask();

        await store.Entered.WaitAsync(TestContext.CancellationToken).WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.IsFalse(learn.IsCompleted);

        store.Release.Release();

        //Order, not count: the adoption is reported only after the write the caller asked for has returned.
        Assert.IsTrue(await learn.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false));
        Assert.HasCount(1, store.States);
        Assert.AreSame(learned, store.States[0].Committed);
        Assert.AreEqual(new RegisterVersion(6UL), store.States[0].RecorderVersion);

        runner.Complete();
        await run.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task ACallerDrivenCheckpointAfterAnInMemoryLearnIsStillTheEscapeHatch()
    {
        QuePaxaVersionedNode<string> host = new(Schedule(), Record(4UL, Second));
        QuePaxaVersionedRunner<string> runner = new(host);
        RecordingStore store = new();
        Task run = runner.RunAsync(store.PersistAsync, TestContext.CancellationToken);

        VersionedValue<string> learned = Record(5UL, Third);
        Assert.IsTrue(await runner.LearnAsync(learned, LearnDurability.InMemory, TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false));
        await runner.MakeDurableAsync(TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);

        //The durability parameter added an option rather than replacing the checkpoint, which stays the path
        //for a host that learns in memory and acknowledges the dissemination later.
        Assert.HasCount(1, store.States);
        Assert.AreSame(learned, store.States[0].Committed);

        runner.Complete();
        await run.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task ADurableLearnThatFailsToPersistFaultsInsteadOfReportingAdoption()
    {
        QuePaxaVersionedNode<string> host = new(Schedule(), Record(4UL, Second));
        QuePaxaVersionedRunner<string> runner = new(host);
        FailingStore store = new();
        Task run = runner.RunAsync(store.PersistAsync, TestContext.CancellationToken);

        Task<bool> learn = runner.LearnAsync(Record(5UL, Third), LearnDurability.Durable, TestContext.CancellationToken).AsTask();

        //The fail-closed arm: a learn whose write failed never reports an adoption a crash would lose, on any
        //path, abandonment included.
        InvalidOperationException fault = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => learn.WaitAsync(Bounded, TestContext.CancellationToken)).ConfigureAwait(false);

        Assert.IsInstanceOfType<IOException>(fault.InnerException);
        Assert.AreEqual(TaskStatus.Faulted, learn.Status);
        _ = await Assert.ThrowsExactlyAsync<IOException>(() => run.WaitAsync(Bounded, TestContext.CancellationToken)).ConfigureAwait(false);
        Assert.AreEqual(1, store.Attempts);
    }


    [TestMethod]
    public async Task ADurableLearnOnAHostThatAlreadyOwesNothingCostsNoWrite()
    {
        QuePaxaVersionedNode<string> host = new(Schedule(), Record(4UL, Second));
        QuePaxaVersionedRunner<string> runner = new(host);
        RecordingStore store = new();
        Task run = runner.RunAsync(store.PersistAsync, TestContext.CancellationToken);

        _ = await runner.RecordAsync(Request(5UL, ProposalPriority.Lowest, Second, "a"), TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.HasCount(1, store.States);

        Assert.IsFalse(await runner.LearnAsync(Record(4UL, Second), LearnDurability.Durable, TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false));

        //The count assertion runs only after the drain, because a learn's completion is set before its
        //dispatch finishes and an assert racing the loop can miss a write the gate should have skipped.
        runner.Complete();
        await run.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.HasCount(1, store.States);
    }


    [TestMethod]
    public async Task ADurableLearnThatAdvancesNothingStillWritesWhatIsOwed()
    {
        QuePaxaVersionedNode<string> host = new(Schedule(), Record(4UL, Second));
        QuePaxaVersionedRunner<string> runner = new(host);
        RecordingStore store = new();
        Task run = runner.RunAsync(store.PersistAsync, TestContext.CancellationToken);

        //The in-memory learn leaves the host past its durable baseline, so the durable learn of the SAME
        //record advances nothing and still owes a write. The sibling vector pins the zero-cost arm, where
        //the host owes nothing; this one pins that the durability is what runs the gate and not the
        //adoption, which a dispatch gating the checkpoint on the adoption would fail while passing there.
        VersionedValue<string> learned = Record(5UL, Third);
        Assert.IsTrue(await runner.LearnAsync(learned, LearnDurability.InMemory, TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false));
        Assert.IsFalse(await runner.LearnAsync(learned, LearnDurability.Durable, TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false));

        runner.Complete();
        await run.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);

        Assert.HasCount(1, store.States);
        Assert.AreSame(learned, store.States[0].Committed);
    }


    [TestMethod]
    public async Task ADurableLearnUnderANullPersistDelegateCompletesSuccessfully()
    {
        QuePaxaVersionedNode<string> host = new(Schedule(), Record(4UL, Second));
        QuePaxaVersionedRunner<string> runner = new(host);
        Task run = runner.RunAsync(cancellationToken: TestContext.CancellationToken);

        //A delegate-less run reproduces the in-memory behavior for every producer, so the durable arm is a
        //no-op here rather than a null dereference.
        Assert.IsTrue(await runner.LearnAsync(Record(5UL, Third), LearnDurability.Durable, TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false));
        Assert.AreEqual(new RegisterVersion(6UL), host.LiveVersion);

        runner.Complete();
        await run.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task ADurableLearnIntoTheSpentRangeEndsTheLoopAsACheckpointDoes()
    {
        QuePaxaVersionedNode<string> host = new(Schedule(), Record(4UL, Second));
        QuePaxaVersionedRunner<string> runner = new(host);
        RecordingStore store = new();
        Task run = runner.RunAsync(store.PersistAsync, TestContext.CancellationToken);

        Task<bool> learn = runner.LearnAsync(new VersionedValue<string>(RegisterVersion.MaxValue, Third, Configuration, "spent"), LearnDurability.Durable, TestContext.CancellationToken).AsTask();

        //The gate fires on a host that serves no version, the snapshot throws, and the loop ends with the
        //learn faulted: the checkpoint's terminal contract reached one call earlier than it was.
        InvalidOperationException fault = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => learn.WaitAsync(Bounded, TestContext.CancellationToken)).ConfigureAwait(false);

        Assert.IsInstanceOfType<InvalidOperationException>(fault.InnerException);
        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => run.WaitAsync(Bounded, TestContext.CancellationToken)).ConfigureAwait(false);
        Assert.IsEmpty(store.States);
    }


    [TestMethod]
    public async Task AnUndefinedLearnDurabilityIsRefused()
    {
        QuePaxaVersionedNode<string> host = new(Schedule(), Record(4UL, Second));
        QuePaxaVersionedRunner<string> runner = new(host);
        RecordingStore store = new();
        Task run = runner.RunAsync(store.PersistAsync, TestContext.CancellationToken);
        VersionedValue<string>? committed = host.Committed;

        //The refusal is at the call site and enqueues nothing, so a value cast in from a wire or a
        //configuration cannot take the in-memory arm and lose the crash safety the caller asked for.
        _ = await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
            async () => _ = await runner.LearnAsync(Record(5UL, Third), (LearnDurability)7, TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

        Assert.AreSame(committed, host.Committed);
        Assert.IsTrue(await runner.LearnAsync(Record(5UL, Third), LearnDurability.InMemory, TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false));

        runner.Complete();
        await run.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task ACatchUpReadPersistsBeforeItRepublishes()
    {
        QuePaxaVersionedNode<string> host = new(Schedule(), Record(4UL, Second));
        QuePaxaVersionedRunner<string> runner = new(host);
        GatedStore store = new();
        Task run = runner.RunAsync(store.PersistAsync, TestContext.CancellationToken);

        VersionedValue<string> learned = Record(5UL, Third);
        Assert.IsTrue(await runner.LearnAsync(learned, LearnDurability.InMemory, TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false));

        Task<VersionedValue<string>?> read = runner.ReadCommittedAsync(TestContext.CancellationToken).AsTask();

        await store.Entered.WaitAsync(TestContext.CancellationToken).WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.IsFalse(read.IsCompleted);

        store.Release.Release();
        VersionedValue<string>? reported = await read.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);

        //Order, not count: what leaves for a peer to build the next version on is a value the store holds.
        Assert.AreSame(learned, reported);
        Assert.HasCount(1, store.States);
        Assert.AreSame(learned, store.States[0].Committed);

        runner.Complete();
        await run.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task ACatchUpReadOnAHostThatOwesNothingCostsNoWrite()
    {
        VersionedValue<string> committed = Record(4UL, Second);
        QuePaxaVersionedNode<string> host = new(Schedule(), committed);
        QuePaxaVersionedRunner<string> runner = new(host);
        RecordingStore store = new();
        Task run = runner.RunAsync(store.PersistAsync, TestContext.CancellationToken);

        _ = await runner.RecordAsync(Request(5UL, ProposalPriority.Lowest, Second, "a"), TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.HasCount(1, store.States);

        VersionedValue<string>? reported = await runner.ReadCommittedAsync(TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreSame(committed, reported);

        //The count assertion runs only after the drain, for the reason the learn vectors state: a call's
        //completion is not a loop barrier and a demoted read's extra write can land after the assert read.
        runner.Complete();
        await run.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.HasCount(1, store.States);
    }


    [TestMethod]
    public async Task ACatchUpReadIsQueuedRatherThanServedFromTheProducer()
    {
        QuePaxaVersionedNode<string> host = new(Schedule(), Record(4UL, Second));
        QuePaxaVersionedRunner<string> runner = new(host);
        QuePaxaRecorder<VersionedValue<string>> recorder = host.Recorder;

        Task<VersionedValue<string>?> read = runner.ReadCommittedAsync(TestContext.CancellationToken).AsTask();

        //A read answered from the producer thread would complete here, off the loop and off the gate.
        Assert.IsFalse(read.IsCompleted);
        Assert.AreSame(recorder, host.Recorder);

        Task run = runner.RunAsync(cancellationToken: TestContext.CancellationToken);

        Assert.AreSame(host.Committed, await read.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false));

        runner.Complete();
        await run.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task ACatchUpReadThatFailsToPersistFaultsInsteadOfRepublishing()
    {
        QuePaxaVersionedNode<string> host = new(Schedule(), Record(4UL, Second));
        QuePaxaVersionedRunner<string> runner = new(host);
        FailingStore store = new();
        Task run = runner.RunAsync(store.PersistAsync, TestContext.CancellationToken);

        Assert.IsTrue(await runner.LearnAsync(Record(5UL, Third), LearnDurability.InMemory, TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false));

        Task<VersionedValue<string>?> read = runner.ReadCommittedAsync(TestContext.CancellationToken).AsTask();

        //The prohibition's positive vector: no record is produced for this read on any path, abandonment
        //included, so a republish can never escape a failed write.
        InvalidOperationException fault = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => read.WaitAsync(Bounded, TestContext.CancellationToken)).ConfigureAwait(false);

        Assert.IsInstanceOfType<IOException>(fault.InnerException);
        _ = await Assert.ThrowsExactlyAsync<IOException>(() => run.WaitAsync(Bounded, TestContext.CancellationToken)).ConfigureAwait(false);
        Assert.AreEqual(TaskStatus.Faulted, read.Status);
    }


    [TestMethod]
    public async Task ACatchUpReadOnASpentHostEndsTheLoopAsACheckpointDoes()
    {
        QuePaxaVersionedNode<string> host = new(Schedule(), Record(4UL, Second));
        QuePaxaVersionedRunner<string> runner = new(host);
        RecordingStore store = new();
        Task run = runner.RunAsync(store.PersistAsync, TestContext.CancellationToken);

        //The learn puts the host past its baseline at the last version, so the read owes a write and the
        //snapshot it would take serves no version.
        Assert.IsTrue(await runner.LearnAsync(new VersionedValue<string>(RegisterVersion.MaxValue, Third, Configuration, "spent"), LearnDurability.InMemory, TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false));

        Task<VersionedValue<string>?> read = runner.ReadCommittedAsync(TestContext.CancellationToken).AsTask();

        //Same terminal-by-design contract the checkpoint carries: the loop ends through the snapshot's throw
        //with the read faulted, which is safe because such a host declines every call without a write and a
        //deployment retires a spent key.
        InvalidOperationException fault = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => read.WaitAsync(Bounded, TestContext.CancellationToken)).ConfigureAwait(false);

        Assert.IsInstanceOfType<InvalidOperationException>(fault.InnerException);
        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => run.WaitAsync(Bounded, TestContext.CancellationToken)).ConfigureAwait(false);
        Assert.IsEmpty(store.States);
    }


    [TestMethod]
    public async Task ACatchUpReadIsARecordReader()
    {
        QuePaxaLeaderSchedule schedule = Schedule();
        QuePaxaVersionedNode<string> host = new(schedule, Record(4UL, Second));
        QuePaxaVersionedRunner<string> runner = new(host);
        Task run = runner.RunAsync(cancellationToken: TestContext.CancellationToken);

        VersionedValue<string> learned = Record(5UL, Third);
        Assert.IsTrue(await runner.LearnAsync(learned, LearnDurability.InMemory, TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false));

        //The conversion is the contract: a durability knob on the signature would break this assignment, and
        //a wire host's catch-up wiring is this one line.
        ReadCommittedRecordDelegate<string> reader = runner.ReadCommittedAsync;
        VersionedRecorderEndpointDelegate<VersionedValue<string>>[] endpoints = [runner.RecordAsync];
        ReadCommittedRecordDelegate<string>[] readers = [reader];
        QuePaxaVersionedRegister<string> register = new(
            endpoints,
            First,
            schedule,
            ProposalPriority.Cryptographic,
            attemptsPerRecorder: 1,
            TimeProvider.System,
            readCommittedRecords: readers);

        VersionedValue<string>? caughtUp = await register.ReadAsync(TestContext.CancellationToken).WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreSame(learned, caughtUp);
        Assert.AreEqual(new RegisterVersion(6UL), register.NextVersion);

        runner.Complete();
        await run.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task AReadSkipsAHostWhoseRunnerStoppedAndStillEndsOnTheCallersOwnCancellation()
    {
        QuePaxaLeaderSchedule schedule = Schedule();
        QuePaxaVersionedNode<string> stopping = new(schedule, Record(4UL, Second));
        QuePaxaVersionedRunner<string> stoppingRunner = new(stopping);
        GatedStore held = new();
        using CancellationTokenSource stoppingToken = new();
        Task stoppingRun = stoppingRunner.RunAsync(held.PersistAsync, stoppingToken.Token);

        //Parking the stopping host's loop inside a held write is what leaves the register's read pending on
        //it, so the stop reaches the reader as the read's own cancellation rather than as a closed channel.
        Task<VersionedRecordReply<VersionedValue<string>>> parked = stoppingRunner.RecordAsync(Request(5UL, ProposalPriority.Lowest, Second, "park"), TestContext.CancellationToken).AsTask();
        await held.Entered.WaitAsync(TestContext.CancellationToken).WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);

        QuePaxaVersionedNode<string> surviving = new(schedule, Record(4UL, Second));
        QuePaxaVersionedRunner<string> survivingRunner = new(surviving);
        Task survivingRun = survivingRunner.RunAsync(cancellationToken: TestContext.CancellationToken);
        VersionedValue<string> learned = Record(5UL, Third);
        Assert.IsTrue(await survivingRunner.LearnAsync(learned, LearnDurability.InMemory, TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false));

        VersionedRecorderEndpointDelegate<VersionedValue<string>>[] endpoints = [stoppingRunner.RecordAsync];
        ReadCommittedRecordDelegate<string>[] readers = [stoppingRunner.ReadCommittedAsync, survivingRunner.ReadCommittedAsync];
        QuePaxaVersionedRegister<string> register = new(
            endpoints,
            First,
            schedule,
            ProposalPriority.Cryptographic,
            attemptsPerRecorder: 1,
            TimeProvider.System,
            readCommittedRecords: readers);

        //The caller's own token is never signalled here, so the cancellation the stopped host answers with is
        //that host's unavailability wearing a cancellation's type; a reader that rethrew it would abort the
        //catch-up at every host after it and learn nothing from any of them.
        Task<VersionedValue<string>?> catchUp = register.ReadAsync(CancellationToken.None);
        await stoppingToken.CancelAsync().ConfigureAwait(false);

        Assert.AreSame(learned, await catchUp.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false));

        _ = await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => parked.WaitAsync(Bounded, TestContext.CancellationToken)).ConfigureAwait(false);
        _ = await Assert.ThrowsAsync<OperationCanceledException>(() => stoppingRun.WaitAsync(Bounded, TestContext.CancellationToken)).ConfigureAwait(false);
        survivingRunner.Complete();
        await survivingRun.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);

        //The filter's other arm: a cancellation that IS the caller's own signal still ends the round, so a
        //caller that stopped asking is not handed a stale answer assembled from the hosts after it.
        QuePaxaVersionedNode<string> blocking = new(schedule, Record(4UL, Second));
        QuePaxaVersionedRunner<string> blockingRunner = new(blocking);
        GatedStore blocked = new();
        Task blockingRun = blockingRunner.RunAsync(blocked.PersistAsync, TestContext.CancellationToken);
        Task<VersionedRecordReply<VersionedValue<string>>> blockedCall = blockingRunner.RecordAsync(Request(5UL, ProposalPriority.Lowest, Second, "park"), TestContext.CancellationToken).AsTask();
        await blocked.Entered.WaitAsync(TestContext.CancellationToken).WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);

        VersionedRecorderEndpointDelegate<VersionedValue<string>>[] blockingEndpoints = [blockingRunner.RecordAsync];
        ReadCommittedRecordDelegate<string>[] blockingReaders = [blockingRunner.ReadCommittedAsync];
        QuePaxaVersionedRegister<string> caller = new(
            blockingEndpoints,
            First,
            schedule,
            ProposalPriority.Cryptographic,
            attemptsPerRecorder: 1,
            TimeProvider.System,
            readCommittedRecords: blockingReaders);

        using CancellationTokenSource callerToken = new();
        Task<VersionedValue<string>?> ownCancellation = caller.ReadAsync(callerToken.Token);
        await callerToken.CancelAsync().ConfigureAwait(false);

        _ = await Assert.ThrowsAsync<OperationCanceledException>(() => ownCancellation.WaitAsync(Bounded, TestContext.CancellationToken)).ConfigureAwait(false);

        blocked.Release.Release();
        _ = await blockedCall.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);
        blockingRunner.Complete();
        await blockingRun.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task ACatchUpReadAfterCompletionFailsFastInsteadOfHanging()
    {
        QuePaxaVersionedNode<string> host = new(Schedule(), Record(4UL, Second));
        QuePaxaVersionedRunner<string> runner = new(host);
        Task run = runner.RunAsync(cancellationToken: TestContext.CancellationToken);
        runner.Complete();
        await run.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);

        //The fourth producer fails fast like the other three rather than parking on a loop that will never
        //dispatch it.
        _ = await Assert.ThrowsExactlyAsync<ChannelClosedException>(
            () => runner.ReadCommittedAsync(TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken)).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task ACatchUpReadPendingWhenTheRunnerStopsIsCancelledRatherThanFaulted()
    {
        QuePaxaVersionedNode<string> host = new(Schedule(), Record(4UL, Second));
        QuePaxaVersionedRunner<string> runner = new(host);
        GatedStore store = new();
        using CancellationTokenSource cancellation = new();
        Task run = runner.RunAsync(store.PersistAsync, cancellation.Token);

        Task<VersionedRecordReply<VersionedValue<string>>> held = runner.RecordAsync(Request(5UL, Four, ProposalPriority.Lowest, Second, "a"), TestContext.CancellationToken).AsTask();
        await store.Entered.WaitAsync(TestContext.CancellationToken).WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);
        Task<VersionedValue<string>?> queued = runner.ReadCommittedAsync(TestContext.CancellationToken).AsTask();

        await cancellation.CancelAsync().ConfigureAwait(false);

        //Without an abandonment arm of its own the pending read hangs, which the bounded wait turns into a red
        //rather than a hung suite.
        TaskCanceledException cancelled = await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => queued.WaitAsync(Bounded, TestContext.CancellationToken)).ConfigureAwait(false);

        Assert.AreEqual(cancellation.Token, cancelled.CancellationToken);
        _ = await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => held.WaitAsync(Bounded, TestContext.CancellationToken)).ConfigureAwait(false);
        _ = await Assert.ThrowsAsync<OperationCanceledException>(() => run.WaitAsync(Bounded, TestContext.CancellationToken)).ConfigureAwait(false);
    }


    private static VersionedRecordRequest<VersionedValue<string>> Request(ulong version, ProposalPriority priority, ReplicaId owner, string value)
    {
        return Request(version, Four, priority, owner, value);
    }


    private static VersionedRecordRequest<VersionedValue<string>> Request(ulong version, RecorderStep step, ProposalPriority priority, ReplicaId owner, string value)
    {
        RegisterVersion at = new(version);
        VersionedValue<string> record = new(at, owner, Configuration, value);
        PrioritizedProposal<VersionedValue<string>> proposal = new(new ProposalKey(priority, ProposerLane.For(owner)), record);

        return new VersionedRecordRequest<VersionedValue<string>>(at, new RecordRequest<VersionedValue<string>>(step, proposal));
    }


    private static VersionedValue<string> Record(ulong version, ReplicaId writer)
    {
        return new VersionedValue<string>(new RegisterVersion(version), writer, Configuration, "committed");
    }


    private static QuePaxaLeaderSchedule Schedule()
    {
        ImmutableArray<ReplicaId> order = [First, Second, Third];

        return new QuePaxaLeaderSchedule(HedgingSchedule.Create(order, TimeSpan.FromMilliseconds(10)));
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }


    private static void WriteValue(Utf8JsonWriter writer, string value) => writer.WriteStringValue(value);


    private static string ReadValue(JsonElement element) => element.GetString()!;


    /// <summary>A store that records every state it is asked to persist and completes immediately.</summary>
    private sealed class RecordingStore
    {
        public List<QuePaxaVersionedNodeState<string>> States { get; } = [];

        public ValueTask PersistAsync(QuePaxaVersionedNodeState<string> state, CancellationToken cancellationToken)
        {
            States.Add(state);

            return ValueTask.CompletedTask;
        }
    }


    /// <summary>A store that signals entry and holds each write until the test releases it.</summary>
    private sealed class GatedStore
    {
        public SemaphoreSlim Entered { get; } = new(0);

        public SemaphoreSlim Release { get; } = new(0);

        public List<QuePaxaVersionedNodeState<string>> States { get; } = [];

        public async ValueTask PersistAsync(QuePaxaVersionedNodeState<string> state, CancellationToken cancellationToken)
        {
            Entered.Release();
            await Release.WaitAsync(cancellationToken).ConfigureAwait(false);
            States.Add(state);
        }
    }


    /// <summary>A store whose every write fails, counting the attempts.</summary>
    private sealed class FailingStore
    {
        public int Attempts { get; private set; }

        public ValueTask PersistAsync(QuePaxaVersionedNodeState<string> state, CancellationToken cancellationToken)
        {
            Attempts++;

            return ValueTask.FromException(new IOException("The durable store is unavailable."));
        }
    }


    /// <summary>A store that writes each state through the JSON codec, keeping the last encoding.</summary>
    private sealed class SerializingStore
    {
        public byte[]? Bytes { get; private set; }

        public ValueTask PersistAsync(QuePaxaVersionedNodeState<string> state, CancellationToken cancellationToken)
        {
            var buffer = new ArrayBufferWriter<byte>();
            QuePaxaMessageJson.CreateVersionedNodeStateSerializer<string>(WriteValue)(state, buffer);
            Bytes = buffer.WrittenSpan.ToArray();

            return ValueTask.CompletedTask;
        }
    }
}
