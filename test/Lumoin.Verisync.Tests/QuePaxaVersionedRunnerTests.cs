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

        QuePaxaRecorder<VersionedValue<string>> afterFailure = host.Node.Recorder;
        QuePaxaVersionedRunner<string> fresh = new(host);
        RecordingStore store = new();
        Task freshRun = fresh.RunAsync(store.PersistAsync, TestContext.CancellationToken);

        VersionedRecordReply<VersionedValue<string>> reply = await fresh.RecordAsync(request, TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);

        //The redelivery changed nothing, so the same-instance premise the retried write rests on is pinned
        //inline: a gate comparing around the request rather than against what was written would skip here.
        Assert.AreSame(afterFailure, host.Node.Recorder);
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
    public async Task ALearnThatNoReplyDependsOnCostsNoWrite()
    {
        QuePaxaVersionedNode<string> host = new(Schedule(), Record(4UL, Second));
        QuePaxaVersionedRunner<string> runner = new(host);
        RecordingStore store = new();
        Task run = runner.RunAsync(store.PersistAsync, TestContext.CancellationToken);

        bool advanced = await runner.LearnAsync(Record(5UL, Third), TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);

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
        Assert.IsTrue(await runner.LearnAsync(learned, TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false));

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

        Assert.IsTrue(await runner.LearnAsync(Record(5UL, Third), TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false));
        Assert.IsFalse(await runner.LearnAsync(Record(5UL, Third), TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false));
        Assert.IsFalse(await runner.LearnAsync(Record(4UL, Second), TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false));

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

        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runner.RunAsync(cancellationToken: TestContext.CancellationToken)).ConfigureAwait(false);

        runner.Complete();
        await run.WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false);
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
            () => runner.LearnAsync(Record(5UL, Third), TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken)).ConfigureAwait(false);
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
        QuePaxaRecorder<VersionedValue<string>> recorder = host.Node.Recorder;

        Task<VersionedRecordReply<VersionedValue<string>>> call = runner.RecordAsync(Request(5UL, ProposalPriority.Lowest, Second, "a"), TestContext.CancellationToken).AsTask();
        Task<bool> learn = runner.LearnAsync(Record(5UL, Third), TestContext.CancellationToken).AsTask();

        Assert.IsFalse(call.IsCompleted);
        Assert.IsFalse(learn.IsCompleted);
        Assert.AreSame(committed, host.Committed);
        Assert.AreSame(recorder, host.Node.Recorder);

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
        //below reaches the arm where a wrongly-placed gate would actually fire.
        Assert.IsTrue(await runner.LearnAsync(Record(5UL, Third), TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false));
        VersionedValue<string>? committed = host.Committed;
        QuePaxaRecorder<VersionedValue<string>> recorder = host.Node.Recorder;

        _ = await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
            () => runner.RecordAsync(Request(5UL, ProposalPriority.Lowest, Second, "closed"), TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken)).ConfigureAwait(false);

        Assert.IsEmpty(store.States);
        Assert.AreSame(committed, host.Committed);
        Assert.AreSame(recorder, host.Node.Recorder);

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

        Assert.IsTrue(await runner.LearnAsync(new VersionedValue<string>(RegisterVersion.MaxValue, Third, "spent"), TestContext.CancellationToken).AsTask().WaitAsync(Bounded, TestContext.CancellationToken).ConfigureAwait(false));

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
        _ = await Assert.ThrowsExactlyAsync<ArgumentNullException>(async () => _ = await runner.LearnAsync(null!, TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
    }


    private static VersionedRecordRequest<VersionedValue<string>> Request(ulong version, ProposalPriority priority, ReplicaId owner, string value)
    {
        return Request(version, Four, priority, owner, value);
    }


    private static VersionedRecordRequest<VersionedValue<string>> Request(ulong version, RecorderStep step, ProposalPriority priority, ReplicaId owner, string value)
    {
        RegisterVersion at = new(version);
        VersionedValue<string> record = new(at, owner, value);
        PrioritizedProposal<VersionedValue<string>> proposal = new(new ProposalKey(priority, ProposerLane.For(owner)), record);

        return new VersionedRecordRequest<VersionedValue<string>>(at, new RecordRequest<VersionedValue<string>>(step, proposal));
    }


    private static VersionedValue<string> Record(ulong version, ReplicaId writer)
    {
        return new VersionedValue<string>(new RegisterVersion(version), writer, "committed");
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
