using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using SourceGenAuditor.Cli;
using SourceGenAuditor.Core.Protocol;
using Xunit;

namespace SourceGenAuditor.Tests.Protocol;

public sealed class WorkerControlProtocolTests
{
    [Theory]
    [InlineData("UserCancellation")]
    [InlineData("Timeout")]
    public async Task ExactClosedControlEnvelopeIsAccepted(string reason)
    {
        using MemoryStream stream = new(Frame(JsonSerializer.SerializeToUtf8Bytes(new
        {
            protocolVersion = 1,
            type = "cancel",
            sequence = 0,
            payload = new { reason },
        })));

        Assert.Equal(reason, await WorkerControlProtocol.ReadSingleAsync(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MalformedDuplicateUnknownInvalidUtf8AndBadLengthsFailClosed()
    {
        await AssertInvalid(Frame(Encoding.UTF8.GetBytes("{")));
        await AssertInvalid(Frame(Encoding.UTF8.GetBytes(
            "{\"protocolVersion\":1,\"protocolVersion\":1,\"type\":\"cancel\",\"sequence\":0,\"payload\":{\"reason\":\"Timeout\"}}")));
        await AssertInvalid(Frame(Encoding.UTF8.GetBytes(
            "{\"protocolVersion\":1,\"type\":\"cancel\",\"sequence\":0,\"payload\":{\"reason\":\"Timeout\",\"extra\":true}}")));
        await AssertInvalid(Frame([0xff]));
        await AssertInvalid([0, 0]);

        byte[] truncatedBody = new byte[6];
        BinaryPrimitives.WriteUInt32BigEndian(truncatedBody, 10);
        await AssertInvalid(truncatedBody);

        byte[] oversize = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(oversize, WorkerProtocolEmitter.MaximumFrameBytes + 1u);
        await AssertInvalid(oversize);
    }

    [Fact]
    public async Task SecondFrameAndTrailingBytesFailClosed()
    {
        byte[] valid = Frame(Encoding.UTF8.GetBytes(
            "{\"protocolVersion\":1,\"type\":\"cancel\",\"sequence\":0,\"payload\":{\"reason\":\"Timeout\"}}"));
        await AssertInvalid(valid.Concat(valid).ToArray());
        await AssertInvalid(valid.Concat(new byte[] { 0 }).ToArray());
    }

    private static async Task AssertInvalid(byte[] bytes)
    {
        using MemoryStream stream = new(bytes, writable: false);
        await Assert.ThrowsAsync<InvalidDataException>(() => WorkerControlProtocol.ReadSingleAsync(
            stream,
            TestContext.Current.CancellationToken));
    }

    private static byte[] Frame(byte[] body)
    {
        byte[] framed = new byte[body.Length + 4];
        BinaryPrimitives.WriteUInt32BigEndian(framed, checked((uint)body.Length));
        body.CopyTo(framed, 4);
        return framed;
    }
}
