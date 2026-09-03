namespace SourceGenAuditor.Core.Canonicalization;

public sealed class CanonicalizationException : Exception
{
    public CanonicalizationException(string message)
        : base(message)
    {
    }

    public CanonicalizationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class UnsupportedLocationKindException : Exception
{
    public UnsupportedLocationKindException(string message)
        : base(message)
    {
    }
}
