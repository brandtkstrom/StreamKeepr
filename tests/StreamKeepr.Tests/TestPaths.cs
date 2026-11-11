using System.Diagnostics;

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

        if (!OperatingSystem.IsWindows())
        {
            UnixFileMode mode = File.GetUnixFileMode(path);
            UnixFileMode execute = UnixFileMode.UserExecute
                | UnixFileMode.GroupExecute
                | UnixFileMode.OtherExecute;
            Assert.True(
                (mode & execute) != 0,
                $"fake process script is not executable: {path}");
        }

        return path;
    }

    public static string RequireExecutable(string executable, string message)
    {
        string? path = FindExecutable(executable);
        Assert.True(path is not null, message);
        return path!;
    }

    public static string? FindExecutable(string executable)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return null;

        foreach (string directory in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrEmpty(directory))
                continue;

            string candidate = Path.Combine(directory, executable);
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

        return null;
    }
}
