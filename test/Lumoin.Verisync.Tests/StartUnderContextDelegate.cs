using System.Threading.Tasks;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// A call started with a <see cref="PostCountingSynchronizationContext"/> current for its synchronous
/// part, so a continuation it later reaches can only resume on that context by having captured it.
/// </summary>
/// <typeparam name="TTask">The task type the call returns.</typeparam>
/// <returns>The task the call returned.</returns>
internal delegate TTask StartUnderContextDelegate<TTask>() where TTask : Task;
