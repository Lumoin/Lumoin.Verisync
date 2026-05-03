using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Text;
using System.Threading.Tasks;
using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class MessageChannelTests
{
    public TestContext TestContext { get; set; } = null!;

    private static SerializeMessageDelegate<string> SerializeUtf8 { get; } =
        (message, output) => output.Write(Encoding.UTF8.GetBytes(message));

    private static DeserializeMessageDelegate<string> DeserializeUtf8 { get; } =
        payload => Encoding.UTF8.GetString(payload.ToArray());


    [TestMethod]
    public async Task RoundTripsFramedMessagesOverInMemoryPipe()
    {
        Pipe pipe = new();
        MessageChannelWriter<string> writer = new(pipe.Writer, SerializeUtf8);
        MessageChannelReader<string> reader = new(pipe.Reader, DeserializeUtf8);

        await writer.WriteAsync("alpha", TestContext.CancellationToken).ConfigureAwait(false);
        await writer.WriteAsync("", TestContext.CancellationToken).ConfigureAwait(false);
        await writer.WriteAsync("a longer message with spaces", TestContext.CancellationToken).ConfigureAwait(false);
        await writer.CompleteAsync().ConfigureAwait(false);

        List<string> received = await ReadAll(reader).ConfigureAwait(false);

        string[] expected = ["alpha", "", "a longer message with spaces"];
        CollectionAssert.AreEqual(expected, received.ToArray());
    }


    [TestMethod]
    public async Task EmptyChannelYieldsNoMessages()
    {
        Pipe pipe = new();
        MessageChannelWriter<string> writer = new(pipe.Writer, SerializeUtf8);
        MessageChannelReader<string> reader = new(pipe.Reader, DeserializeUtf8);

        await writer.CompleteAsync().ConfigureAwait(false);

        List<string> received = await ReadAll(reader).ConfigureAwait(false);

        Assert.HasCount(0, received);
    }


    [TestMethod]
    public void ConstructorsRejectNullArguments()
    {
        Pipe pipe = new();

        Assert.ThrowsExactly<ArgumentNullException>(() => new MessageChannelWriter<string>(null!, SerializeUtf8));
        Assert.ThrowsExactly<ArgumentNullException>(() => new MessageChannelWriter<string>(pipe.Writer, null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => new MessageChannelReader<string>(null!, DeserializeUtf8));
        Assert.ThrowsExactly<ArgumentNullException>(() => new MessageChannelReader<string>(pipe.Reader, null!));
    }


    private async Task<List<string>> ReadAll(MessageChannelReader<string> reader)
    {
        var received = new List<string>();
        await foreach(string message in reader.ReadAllAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            received.Add(message);
        }

        return received;
    }
}
