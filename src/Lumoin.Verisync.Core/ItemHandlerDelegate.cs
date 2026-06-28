namespace Lumoin.Verisync.Core;

/// <summary>
/// Handles one item yielded by <see cref="ItemStreamChannelReader{TItem}"/>. The item is <em>borrowed</em> for
/// the duration of the call: any pooled bytes it views are released as soon as the handler returns, so a
/// handler that must retain an item copies or interns it before returning rather than stashing the value.
/// </summary>
/// <typeparam name="TItem">The item type.</typeparam>
/// <param name="item">The decoded item, passed by read-only reference to avoid copying a value-type item.</param>
public delegate void ItemHandlerDelegate<TItem>(in TItem item);
