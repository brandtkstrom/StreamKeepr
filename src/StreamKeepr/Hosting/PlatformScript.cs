using System.Security.Cryptography;
using System.Text;

namespace StreamKeepr.Hosting;

/// <summary>
/// Resolves a committed POSIX fixture for the host OS. Windows cannot execute a script
/// directly, so it needs a shim; this is the one place that locates bash and writes one.
/// </summary>
internal static class PlatformScript
{
    /// <summary>
    /// Returns a path the host OS can start. Off Windows the script is returned unchanged; on
    /// Windows a <c>.cmd</c> shim forwards every argument and the exit code to bash, so the
    /// committed script stays the single implementation.
    /// </summary>
    public static string ResolveForHost(string scriptPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptPath);
        if (!File.Exists(scriptPath))
            throw new FileNotFoundException($"fixture script not found at {scriptPath}", scriptPath);

        if (!OperatingSystem.IsWindows()
            || Path.GetExtension(scriptPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return scriptPath;
        }

        return WriteBashShim(scriptPath);
    }

    /// <summary>
    /// Finds a real bash. Some machines also carry the WSL stub, which cannot run the fixtures,
    /// so a Git for Windows installation wins over PATH.
    /// </summary>
    public static string FindBash()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (string candidate in new[]
                 {
                     Path.Combine(programFiles, "Git", "bin", "bash.exe"),
                     Path.Combine(programFiles, "Git", "usr", "bin", "bash.exe"),
                 })
        {
            if (File.Exists(candidate))
                return candidate;
        }

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(path))
        {
            foreach (string directory in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrEmpty(directory) || IsWslStubDirectory(directory))
                    continue;

                foreach (string name in new[] { "bash.exe", "bash" })
                {
                    string candidate = Path.Combine(directory, name);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
        }

        throw new InvalidOperationException(
            "The fake process fixtures need bash on Windows. Install Git for Windows " +
            "(or another bash) and put bash.exe on PATH.");
    }

    /// <summary>
    /// The WSL launcher is also called bash.exe but runs Linux paths, so it cannot start these
    /// scripts. Skip it rather than hand back a shim that fails at preflight.
    /// </summary>
    private static bool IsWslStubDirectory(string directory)
    {
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        return directory.StartsWith(system, StringComparison.OrdinalIgnoreCase)
            || directory.Contains("\\WindowsApps", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Disposable shims for this process only; another process cannot reuse or poison them.</summary>
    private static readonly Lazy<string> ShimDirectory = new(CreateShimDirectory);

    private static string WriteBashShim(string scriptPath)
    {
        string bash = FindBash();
        string shimDirectory = ShimDirectory.Value;

        // Content-address the name so two scripts that share a file name cannot collide.
        string key = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(scriptPath + "\n" + bash)))[..16];
        string shim = Path.Combine(shimDirectory, $"{Path.GetFileName(scriptPath)}-{key}.cmd");
        if (File.Exists(shim))
            return shim;

        string content =
            "@echo off\r\n" +
            $"\"{bash}\" \"{scriptPath.Replace('\\', '/')}\" %*\r\n";
        string temp = shim + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, content);
        try
        {
            File.Move(temp, shim, overwrite: true);
        }
        catch (IOException) when (File.Exists(shim))
        {
            // Another caller created the identical shim first.
            File.Delete(temp);
        }

        return shim;
    }

    private static string CreateShimDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "streamkeepr-script-shims",
            $"{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDelete(directory);
        return directory;
    }

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a fixture or scanner may still hold a shim at shutdown.
        }
    }
}
