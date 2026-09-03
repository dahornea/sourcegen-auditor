namespace SourceGenAuditor.Core.Scenario;

public sealed class ScenarioValidationException : Exception
{
    public ScenarioValidationException(string message)
        : base(message)
    {
    }

    public ScenarioValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
