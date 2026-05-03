using System.Text.Json.Serialization;
using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

/// <summary>Source-generated JSON metadata for the test message and CRDT state types, giving AOT-safe serialization.</summary>
[JsonSerializable(typeof(SampleMessage))]
[JsonSerializable(typeof(GCounterState))]
[JsonSerializable(typeof(RgaState<string>))]
internal sealed partial class SampleJsonContext: JsonSerializerContext;
