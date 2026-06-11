using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// A deterministic in-process cluster test bench: N acceptor nodes reached through endpoints that can be
/// partitioned. A partitioned endpoint fails the request (modelling an unreachable peer), so the proposer's
/// fault tolerance and quorum behaviour can be tested without sockets or timing.
/// </summary>
/// <typeparam name="TValue">The register value type.</typeparam>
internal sealed class SimulatedCluster<TValue>
{
    private ConsensusNode<TValue>[] Nodes { get; }
    private HashSet<int> Partitioned { get; } = [];


    public SimulatedCluster(int nodeCount)
    {
        Nodes = new ConsensusNode<TValue>[nodeCount];
        for(int i = 0; i < nodeCount; i++)
        {
            Nodes[i] = new ConsensusNode<TValue>();
        }
    }


    public int NodeCount => Nodes.Length;


    public ConsensusNode<TValue> Node(int index) => Nodes[index];


    public void Partition(int index) => Partitioned.Add(index);


    public void Heal(int index) => Partitioned.Remove(index);


    public FastProposer<TValue> CreateProposer()
    {
        var endpoints = new ConsensusEndpointDelegate<TValue>[Nodes.Length];
        for(int i = 0; i < Nodes.Length; i++)
        {
            int index = i;
            endpoints[i] = (request, _) => Partitioned.Contains(index)
                ? throw new IOException($"acceptor {index} is partitioned")
                : ValueTask.FromResult(Nodes[index].Handle(request));
        }

        return new FastProposer<TValue>(endpoints);
    }
}
