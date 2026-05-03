namespace Lumoin.Verisync.Tests;

/// <summary>A representative wire message used to exercise the channel serialization adapters.</summary>
internal sealed record SampleMessage(int Sequence, string Payload);
