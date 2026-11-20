using System.ComponentModel;
using StreamKeepr.Infrastructure.Media;

namespace StreamKeepr.Tests.Media;

public sealed class ProcessIdentityTests
{
    private static readonly DateTime Captured = new(2026, 11, 10, 16, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void A_missing_process_means_the_pid_is_gone()
    {
        ProcessIdentity identity = Inspect(_ => throw new ArgumentException("no such process"));

        Assert.Equal(ProcessIdentityKind.Gone, identity.Kind);
        Assert.True(identity.IsVerifiedGone);
    }

    [Fact]
    public void The_same_start_time_means_the_captured_process_is_still_alive()
    {
        ProcessIdentity identity = Inspect(_ => Captured);

        Assert.Equal(ProcessIdentityKind.Alive, identity.Kind);
        Assert.False(identity.IsVerifiedGone);
    }

    [Fact]
    public void A_different_start_time_means_the_pid_was_reused()
    {
        ProcessIdentity identity = Inspect(_ => Captured.AddMinutes(1));

        Assert.Equal(ProcessIdentityKind.Reused, identity.Kind);
        Assert.True(identity.IsVerifiedGone);
    }

    [Theory]
    [MemberData(nameof(UnreadableIdentityErrors))]
    public void An_unreadable_identity_fails_closed(Exception error)
    {
        ProcessIdentity identity = Inspect(_ => throw error);

        Assert.Equal(ProcessIdentityKind.Unknown, identity.Kind);
        Assert.False(identity.IsVerifiedGone);
    }

    [Fact]
    public void A_missing_pid_or_start_time_is_unknown()
    {
        Assert.Equal(
            ProcessIdentityKind.Unknown,
            ProcessIdentityInspector.Inspect(null, Captured, _ => Captured).Kind);
        Assert.Equal(
            ProcessIdentityKind.Unknown,
            ProcessIdentityInspector.Inspect(4321, null, _ => Captured).Kind);
    }

    public static TheoryData<Exception> UnreadableIdentityErrors => new()
    {
        new Win32Exception(5),
        new UnauthorizedAccessException("denied"),
        new NotSupportedException("remote process"),
    };

    private static ProcessIdentity Inspect(Func<int, DateTime> readStartTime) =>
        ProcessIdentityInspector.Inspect(4321, Captured, readStartTime);
}
