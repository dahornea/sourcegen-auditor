using SourceGenAuditor.Core.Evaluation;

namespace SourceGenAuditor.Cli;

public static class ExitCodeMapper
{
    public static int FromVerdict(AssertionOutcome verdict) => verdict switch
    {
        AssertionOutcome.PASS => 0,
        AssertionOutcome.FAIL => 1,
        AssertionOutcome.UNKNOWN => 2,
        AssertionOutcome.ERROR => 3,
        _ => 3,
    };
}
