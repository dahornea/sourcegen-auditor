using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using SourceGenAuditor.Core.Protocol;

namespace SourceGenAuditor.Cli;

internal static class WorkerControlProtocol
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static async Task<string?> ReadSingleAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] prefix = new byte[4];
        int prefixRead = await ReadExactAsync(stream, prefix, cancellationToken).ConfigureAwait(false);
        if (prefixRead == 0)
        {
            return null;
        }

        if (prefixRead != prefix.Length)
        {
            throw new InvalidDataException("The control frame prefix was truncated.");
        }

        uint length = BinaryPrimitives.ReadUInt32BigEndian(prefix);
        if (length is 0 || length > WorkerProtocolEmitter.MaximumFrameBytes)
        {
            throw new InvalidDataException("The control frame length is invalid.");
        }

        byte[] body = new byte[length];
        if (await ReadExactAsync(stream, body, cancellationToken).ConfigureAwait(false) != body.Length)
        {
            throw new InvalidDataException("The control frame body was truncated.");
        }

        JsonElement root;
        try
        {
            _ = StrictUtf8.GetString(body);
            using JsonDocument document = JsonDocument.Parse(body, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
            ValidateClosedObject(document.RootElement, "protocolVersion", "type", "sequence", "payload");
            root = document.RootElement.Clone();
        }
        catch (Exception exception) when (exception is DecoderFallbackException or JsonException)
        {
            throw new InvalidDataException("The control frame is not strict UTF-8 JSON.", exception);
        }

        if (root.GetProperty("protocolVersion").ValueKind != JsonValueKind.Number ||
            !root.GetProperty("protocolVersion").TryGetInt32(out int version) || version != 1 ||
            root.GetProperty("type").ValueKind != JsonValueKind.String ||
            root.GetProperty("type").GetString() != "cancel" ||
            root.GetProperty("sequence").ValueKind != JsonValueKind.Number ||
            !root.GetProperty("sequence").TryGetInt32(out int sequence) || sequence != 0)
        {
            throw new InvalidDataException("The control envelope is invalid.");
        }

        JsonElement payload = root.GetProperty("payload");
        ValidateClosedObject(payload, "reason");
        JsonElement reasonElement = payload.GetProperty("reason");
        string? reason = reasonElement.ValueKind == JsonValueKind.String ? reasonElement.GetString() : null;
        if (reason is not ("UserCancellation" or "Timeout"))
        {
            throw new InvalidDataException("The control cancellation reason is invalid.");
        }

        byte[] trailing = new byte[1];
        if (await stream.ReadAsync(trailing, cancellationToken).ConfigureAwait(false) != 0)
        {
            throw new InvalidDataException("A second control frame or trailing bytes followed the cancellation frame.");
        }

        return reason;
    }

    private static void ValidateClosedObject(JsonElement value, params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("A control protocol value is not an object.");
        }

        HashSet<string> actual = new(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!actual.Add(property.Name))
            {
                throw new InvalidDataException("A control protocol object contains a duplicate property.");
            }
        }

        if (actual.Count != expected.Length || expected.Any(name => !actual.Contains(name)))
        {
            throw new InvalidDataException("A control protocol object has missing or unknown properties.");
        }
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        return offset;
    }
}
