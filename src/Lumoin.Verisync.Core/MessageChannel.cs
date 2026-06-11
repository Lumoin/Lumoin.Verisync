namespace Lumoin.Verisync.Core;

/// <summary>
/// Shared framing constants for <see cref="MessageChannelReader{TMessage}"/> and
/// <see cref="MessageChannelWriter{TMessage}"/>.
/// </summary>
public static class MessageChannel
{
    /// <summary>
    /// The default maximum frame payload length in bytes (16 MiB). The length prefix is read from the
    /// wire before any payload byte is trusted, so a bound is what stops a hostile or corrupt peer from
    /// committing the reader to an arbitrarily large allocation with a four-byte header.
    /// </summary>
    public const int DefaultMaxFrameLength = 16 * 1024 * 1024;
}
