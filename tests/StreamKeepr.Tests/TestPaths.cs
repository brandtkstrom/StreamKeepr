using StreamKeepr.Hosting;

namespace StreamKeepr.Tests;

/// <summary>Locates repository fixtures and tools without writing under the repo.</summary>
internal static class TestPaths
{
    public static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "StreamKeepr.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("could not locate the repository root from the test output");
    }

    public static string FixturePath(string name)
    {
        string path = Path.Combine(RepositoryRoot(), "tests", "fixtures", name);
        Assert.True(File.Exists(path), $"fake process script not found at {path}");
        Assert.False(
            File.GetAttributes(path).HasFlag(FileAttributes.Directory),
            $"fake process path is a directory: {path}");

        if (OperatingSystem.IsWindows())
            return PlatformScript.ResolveForHost(path);

        UnixFileMode mode = File.GetUnixFileMode(path);
        UnixFileMode execute = UnixFileMode.UserExecute
            | UnixFileMode.GroupExecute
            | UnixFileMode.OtherExecute;
        Assert.True(
            (mode & execute) != 0,
            $"fake process script is not executable: {path}");
        return path;
    }

    public static string RequireExecutable(string executable, string message) =>
        RequireExecutable(executable, message, Environment.GetEnvironmentVariable("PATH"));

    public static string RequireExecutable(string executable, string message, string? searchPath)
    {
        string? path = FindExecutable(executable, searchPath);
        Assert.True(path is not null, message);
        return path!;
    }

    public static string? FindExecutable(string executable) =>
        FindExecutable(executable, Environment.GetEnvironmentVariable("PATH"));

    /// <summary>Searches one explicit PATH-shaped list so tests never mutate the process environment.</summary>
    public static string? FindExecutable(string executable, string? searchPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        if (string.IsNullOrEmpty(searchPath))
            return null;

        foreach (string directory in searchPath.Split(Path.PathSeparator))
        {
            if (string.IsNullOrEmpty(directory))
                continue;

            foreach (string candidate in Candidates(directory, executable))
            {
                var file = new FileInfo(candidate);
                if (!file.Exists)
                    continue;

                // PATH may contain a stale symlink such as a removed Homebrew keg.
                FileSystemInfo resolved = file.LinkTarget is null
                    ? file
                    : file.ResolveLinkTarget(returnFinalTarget: true) ?? file;
                if (resolved.Exists)
                    return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns a path the current OS can start for a POSIX shell script, setting the Unix
    /// executable bit first. Windows cannot run a script directly, so it gets a shim there.
    /// </summary>
    public static string StartableScriptPath(string scriptPath)
    {
        if (OperatingSystem.IsWindows())
            return PlatformScript.ResolveForHost(scriptPath);

        File.SetUnixFileMode(
            scriptPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return scriptPath;
    }

    /// <summary>Yields the PATHEXT variants on Windows, then the bare name; off Windows only the bare name.</summary>
    private static IEnumerable<string> Candidates(string directory, string executable)
    {
        if (OperatingSystem.IsWindows() && !Path.HasExtension(executable))
        {
            foreach (string extension in ExecutableExtensions)
                yield return Path.Combine(directory, executable + extension);
        }

        yield return Path.Combine(directory, executable);
    }

    private static string[] ExecutableExtensions
    {
        get
        {
            string? pathext = Environment.GetEnvironmentVariable("PATHEXT");
            string[] extensions = string.IsNullOrWhiteSpace(pathext)
                ? [".com", ".exe", ".bat", ".cmd"]
                : pathext.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return [.. extensions.Where(extension => extension.StartsWith('.'))];
        }
    }
}
