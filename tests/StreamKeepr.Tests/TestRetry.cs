namespace StreamKeepr.Tests;

/// <summary>Retry delays for stores built directly in tests: never wait on wall-clock time.</summary>
internal static class TestRetry
{
    public static readonly Func<TimeSpan, CancellationToken, Task> Immediate =
        static (_, _) => Task.CompletedTask;
}
