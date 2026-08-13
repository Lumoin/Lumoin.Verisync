using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// An in-memory cluster of QuePaxa recorder hosts for a versioned register, serving every endpoint
/// synchronously so that a test observes exactly what a write put on the wire.
/// </summary>
/// <typeparam name="TValue">The application value type.</typeparam>
/// <remarks>
/// <para>
/// It sits beside <see cref="InterleavedQuePaxaCluster{TValue}"/> rather than extending it. That bench is
/// per-instance and typed to the bare message family, and it is what the interleaving laws are pinned
/// against; this one is typed to the envelope and drives the host above the node, so neither bench can
/// change under the other's laws.
/// </para>
/// <para>
/// DISSEMINATION IS EXPLICIT HERE BECAUSE IT IS EXPLICIT IN A DEPLOYMENT. A host learns a committed record
/// only when a test tells it to, which is what lets a test hold some hosts back and watch a write fail to
/// gather a quorum — the liveness cost the single-live-instance rule buys its safety with.
/// </para>
/// </remarks>
internal sealed class VersionedQuePaxaCluster<TValue>
{
    public VersionedQuePaxaCluster(QuePaxaLeaderSchedule schedule, int hostCount, VersionedValue<TValue>? committed = null)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentOutOfRangeException.ThrowIfLessThan(hostCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(hostCount, schedule.Schedule.Order.Length);

        Schedule = schedule;
        Genesis = QuePaxaConfiguration.CreateGenesis(schedule.Schedule.Order);
        Hosts = new QuePaxaVersionedNode<TValue>[hostCount];
        ServedCounts = new int[hostCount];
        Partitioned = new bool[hostCount];
        for(int index = 0; index < hostCount; index++)
        {
            Hosts[index] = new QuePaxaVersionedNode<TValue>(Genesis, schedule.Schedule.Order[index], committed);
        }
    }


    public QuePaxaLeaderSchedule Schedule { get; }

    /// <summary>
    /// The genesis membership every host of this cluster runs under, minted from the agreed order so that a
    /// register over the same order stamps the value the hosts derive.
    /// </summary>
    public QuePaxaConfiguration Genesis { get; }

    public int HostCount => Hosts.Length;

    /// <summary>The number of requests each host actually recorded, which a fast path leaves at one per host.</summary>
    public IReadOnlyList<int> Served => ServedCounts;

    /// <summary>The versions a host refused to serve, which is how a test observes the single-live-instance rule.</summary>
    public IReadOnlyList<RegisterVersion> Declined => DeclinedVersions;

    /// <summary>
    /// The proposal keys the hosts were asked to record, paired with the version they arrived at, in arrival
    /// order. A key repeats across the steps of one attempt, so a test counting attempts reads the distinct
    /// pairs.
    /// </summary>
    public IReadOnlyList<(RegisterVersion Version, ProposalKey Key)> Recorded => RecordedKeys;


    private QuePaxaVersionedNode<TValue>[] Hosts { get; }

    private List<RegisterVersion> DeclinedVersions { get; } = [];

    private List<(RegisterVersion Version, ProposalKey Key)> RecordedKeys { get; } = [];

    private int[] ServedCounts { get; }

    private bool[] Partitioned { get; }


    public QuePaxaVersionedNode<TValue> Host(int index) => Hosts[index];


    public void Partition(int index) => Partitioned[index] = true;


    public void Heal(int index) => Partitioned[index] = false;


    /// <summary>Tells one host about a committed record, which is the dissemination a deployment owes.</summary>
    public bool LearnAt(int index, VersionedValue<TValue> committed) => Hosts[index].Learn(committed);


    /// <summary>Tells the host that is <paramref name="member"/> about a committed record.</summary>
    /// <param name="member">The member to tell.</param>
    /// <param name="committed">A decided record.</param>
    /// <returns><see langword="true"/> when the record advanced that host, and <see langword="false"/> when it did not or when no host of this cluster is that member.</returns>
    public bool LearnAtMember(ReplicaId member, VersionedValue<TValue> committed)
    {
        int index = IndexOf(member);

        return index >= 0 && Hosts[index].Learn(committed);
    }


    /// <summary>Tells every host about a committed record.</summary>
    public void LearnAll(VersionedValue<TValue> committed)
    {
        foreach(QuePaxaVersionedNode<TValue> host in Hosts)
        {
            _ = host.Learn(committed);
        }
    }


    /// <summary>
    /// Resolves the endpoint of one member, which is this bench's
    /// <see cref="ResolveRecorderEndpointDelegate{TValue}"/>.
    /// </summary>
    /// <param name="member">The member to reach.</param>
    /// <returns>That member's endpoint.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no host of this cluster is that member, which is how the resolver reports one it cannot resolve.</exception>
    public VersionedRecorderEndpointDelegate<VersionedValue<TValue>> Resolve(ReplicaId member)
    {
        int index = IndexOf(member);
        if(index < 0)
        {
            throw new InvalidOperationException($"No host of this cluster is {member}.");
        }

        return Endpoints()[index];
    }


    /// <summary>
    /// Resolves the catch-up reader of one member, which is this bench's
    /// <see cref="ResolveCommittedRecordReaderDelegate{TValue}"/>.
    /// </summary>
    /// <param name="member">The member to ask.</param>
    /// <returns>That member's reader.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no host of this cluster is that member.</exception>
    public ReadCommittedRecordDelegate<TValue> ResolveReader(ReplicaId member)
    {
        int index = IndexOf(member);
        if(index < 0)
        {
            throw new InvalidOperationException($"No host of this cluster is {member}.");
        }

        return _ => new ValueTask<VersionedValue<TValue>?>(Hosts[index].Committed);
    }


    public VersionedRecorderEndpointDelegate<VersionedValue<TValue>>[] Endpoints()
    {
        var endpoints = new VersionedRecorderEndpointDelegate<VersionedValue<TValue>>[Hosts.Length];
        for(int index = 0; index < Hosts.Length; index++)
        {
            int host = index;
            endpoints[index] = (request, _) =>
            {
                if(Partitioned[host])
                {
                    throw new IOException($"Host {host} is partitioned.");
                }

                //A refusal also throws, so recording it here tells a decline apart from a partition.
                if(request.Version != Hosts[host].LiveVersion)
                {
                    DeclinedVersions.Add(request.Version);
                }

                RecordedKeys.Add((request.Version, request.Request.Proposal.Key));

                VersionedRecordReply<VersionedValue<TValue>> reply = Hosts[host].Handle(request);
                ServedCounts[host]++;

                return new ValueTask<VersionedRecordReply<VersionedValue<TValue>>>(reply);
            };
        }

        return endpoints;
    }


    /// <summary>The index of the host that is <paramref name="member"/>, or a negative value when none is.</summary>
    private int IndexOf(ReplicaId member)
    {
        for(int index = 0; index < Hosts.Length; index++)
        {
            if(Hosts[index].Self.Equals(member))
            {
                return index;
            }
        }

        return -1;
    }
}
