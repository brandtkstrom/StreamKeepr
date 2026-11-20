using System.ComponentModel;
using System.Diagnostics;

namespace StreamKeepr.Infrastructure.Media;

/// <summary>What a captured recorder PID means when it is inspected again.</summary>
internal enum ProcessIdentityKind
{
    /// <summary>The PID no longer exists.</summary>
    Gone,

    /// <summary>The PID belongs to a different process (the original exited and the PID was reused).</summary>
    Reused,

    /// <summary>The captured process is still running.</summary>
    Alive,

    /// <summary>The identity could not be read; callers must fail closed.</summary>
    Unknown,
}

/// <summary>
/// The observed identity of a PID captured when a recorder tool started. A force kill is verified
/// only when the PID is gone or belongs to a different process; an unreadable identity is unknown,
/// never gone.
/// </summary>
internal readonly record struct ProcessIdentity(
    ProcessIdentityKind Kind,
    int? ProcessId,
    DateTime? CapturedStartTime,
    DateTime? ObservedStartTime,
    string Detail)
{
    /// <summary>True only when the captured process provably no longer owns the PID.</summary>
    public bool IsVerifiedGone =>
        Kind is ProcessIdentityKind.Gone or ProcessIdentityKind.Reused;
}

/// <summary>
/// Compares a captured PID and start time with the process table. Platform rules differ:
/// <see cref="Process.GetProcessById(int)"/> throws when the PID is expired, a reused PID resolves,
/// and reading <see cref="Process.StartTime"/> can be denied. Callers fail closed when the identity
/// cannot be read.
/// </summary>
internal static class ProcessIdentityInspector
{
    /// <summary>Inspects the real process table.</summary>
    public static ProcessIdentity Inspect(int? processId, DateTime? capturedStartTime) =>
        Inspect(processId, capturedStartTime, ReadStartTime);

    internal static ProcessIdentity Inspect(
        int? processId,
        DateTime? capturedStartTime,
        Func<int, DateTime> readStartTime)
    {
        if (processId is not { } pid || pid <= 0)
        {
            return new ProcessIdentity(
                ProcessIdentityKind.Unknown,
                processId,
                capturedStartTime,
                ObservedStartTime: null,
                "no captured PID");
        }

        if (capturedStartTime is not { } expected)
        {
            return new ProcessIdentity(
                ProcessIdentityKind.Unknown,
                pid,
                CapturedStartTime: null,
                ObservedStartTime: null,
                "no captured start time");
        }

        try
        {
            DateTime observed = readStartTime(pid);
            return observed == expected
                ? new ProcessIdentity(
                    ProcessIdentityKind.Alive,
                    pid,
                    expected,
                    observed,
                    $"PID {pid} is still alive (started {observed:O})")
                : new ProcessIdentity(
                    ProcessIdentityKind.Reused,
                    pid,
                    expected,
                    observed,
                    $"PID {pid} now belongs to a process started {observed:O}");
        }
        catch (ArgumentException)
        {
            // Process.GetProcessById throws ArgumentException for an expired PID.
            return new ProcessIdentity(
                ProcessIdentityKind.Gone, pid, expected, ObservedStartTime: null,
                $"PID {pid} no longer exists");
        }
        catch (InvalidOperationException)
        {
            // The process exited between the lookup and reading StartTime.
            return new ProcessIdentity(
                ProcessIdentityKind.Gone, pid, expected, ObservedStartTime: null,
                $"PID {pid} exited while its identity was read");
        }
        catch (Exception ex) when (IsUnreadable(ex))
        {
            return new ProcessIdentity(
                ProcessIdentityKind.Unknown, pid, expected, ObservedStartTime: null,
                $"PID {pid} identity could not be read: {ex.Message}");
        }
    }

    /// <summary>Errors that mean the process table is unreadable rather than the process being gone.</summary>
    internal static bool IsUnreadable(Exception exception) =>
        exception is Win32Exception or UnauthorizedAccessException or NotSupportedException;

    internal static DateTime ReadStartTime(int processId)
    {
        using Process process = Process.GetProcessById(processId);
        return process.StartTime;
    }
}
