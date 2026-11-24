using System.Reflection;
using Xunit.Sdk;

namespace StreamKeepr.E2E;

/// <summary>
/// Best-effort record of whether the running test threw, for xUnit v2, which has no test
/// context and cannot report the outcome from <see cref="IAsyncLifetime.DisposeAsync"/>.
/// <see cref="AppDomain.FirstChanceException"/> fires for every throw, so only the exception
/// types that fail a journey count: an xUnit assertion failure, a Playwright assertion or
/// timeout, or this harness's own guard failure.
///
/// The marker lives in an <see cref="AsyncLocal{T}"/> so a parallel test's throw cannot mark
/// this test as failed. <see cref="CaptureFailureAttribute"/> starts a marker before the test
/// body and clears it after.
/// </summary>
internal sealed class FailureCapture
{
    private static readonly AsyncLocal<FailureCapture?> Local = new();

    static FailureCapture()
    {
        AppDomain.CurrentDomain.FirstChanceException += (_, args) =>
        {
            if (IsJourneyFailure(args.Exception))
                Local.Value?.Mark(args.Exception);
        };
    }

    private FailureCapture(string declaringType, string testName)
    {
        FileStem = $"{declaringType}.{testName}-{Guid.NewGuid().ToString("N")[..8]}";
    }

    public static FailureCapture? Current => Local.Value;

    public string FileStem { get; }

    public Exception? Exception { get; private set; }

    public bool Failed => Exception is not null;

    public static void Begin(MethodInfo method)
    {
        ArgumentNullException.ThrowIfNull(method);
        string declaringType = method.DeclaringType?.Name ?? "UnknownType";
        Local.Value = new FailureCapture(declaringType, method.Name);
    }

    public static void End() => Local.Value = null;

    private void Mark(Exception exception) => Exception ??= exception;

    private static bool IsJourneyFailure(Exception exception) =>
        exception is XunitException or TimeoutException or InvalidOperationException
            or Microsoft.Playwright.PlaywrightException;
}

/// <summary>
/// Starts <see cref="FailureCapture"/> for each test case. Apply to every journey class:
/// a session without a marker cannot know it failed, so it discards its trace.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
internal sealed class CaptureFailureAttribute : BeforeAfterTestAttribute
{
    public override void Before(MethodInfo methodUnderTest) => FailureCapture.Begin(methodUnderTest);

    public override void After(MethodInfo methodUnderTest) => FailureCapture.End();
}
