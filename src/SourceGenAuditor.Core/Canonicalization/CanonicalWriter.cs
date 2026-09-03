using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SourceGenAuditor.Core.Canonicalization;

internal sealed class CanonicalWriter
{
    internal static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly MemoryStream stream = new();

    public void WriteByte(byte value) => stream.WriteByte(value);

    public void WriteBoolean(bool value) => stream.WriteByte(value ? (byte)1 : (byte)0);

    public void WriteUInt64(ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        stream.Write(bytes);
    }

    public void WriteFrame(ReadOnlySpan<byte> value)
    {
        WriteUInt64(checked((ulong)value.Length));
        stream.Write(value);
    }

    public void WriteFrame(string value) => WriteFrame(Encode(value));

    public void WriteNullableString(string? value)
    {
        if (value is null)
        {
            stream.WriteByte(0);
            return;
        }

        stream.WriteByte(1);
        WriteFrame(value);
    }

    public void WriteNonNullString(string? value, string fieldName)
    {
        if (value is null)
        {
            throw new CanonicalizationException($"The publicly observable {fieldName} value is unexpectedly null.");
        }

        stream.WriteByte(1);
        WriteFrame(value);
    }

    public void WriteSequence<T>(IReadOnlyCollection<T> values, Func<T, byte[]> encode)
    {
        WriteUInt64(checked((ulong)values.Count));
        foreach (T value in values)
        {
            WriteFrame(encode(value));
        }
    }

    public byte[] ToArray() => stream.ToArray();

    public static byte[] Encode(string value)
    {
        try
        {
            return StrictUtf8.GetBytes(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new CanonicalizationException("A string contains Unicode that strict UTF-8 cannot encode.", exception);
        }
    }

    public static string HashHex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static int CompareBytes(byte[] left, byte[] right) => left.AsSpan().SequenceCompareTo(right);
}
