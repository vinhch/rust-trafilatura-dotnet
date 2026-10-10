using System.Text.RegularExpressions;

namespace RustTrafilatura.Tests;

/// <summary>
/// What the tests read out of the repository rather than out of the library: the root the file-reading
/// tests find their file from, and the version the crate declares.
/// </summary>
internal static class Repository
{
    /// <summary>The repository root, found by walking up from the test assembly's own directory.</summary>
    internal static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "dotnet", "RustTrafilatura.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"No repository root above the test assembly at {AppContext.BaseDirectory}: no directory on the way up holds dotnet/RustTrafilatura.sln.");
    }

    /// <summary>
    /// The version the crate declares, which is the version the native library reports: compiled into it
    /// from this same manifest through `CARGO_PKG_VERSION`.
    /// </summary>
    internal static string CrateVersion()
    {
        var manifest = Path.Combine(Root(), "rust", "Cargo.toml");
        var match = Regex.Match(File.ReadAllText(manifest), @"(?m)^version = ""(?<version>[^""]+)""$");
        if (!match.Success)
        {
            throw new InvalidOperationException(
                $"No version line in {manifest}, so the version the native library reports cannot be derived.");
        }

        return match.Groups["version"].Value;
    }

    /// <summary>
    /// The version of the `rust-trafilatura` core the binding links, which is the other version the native
    /// library reports. Read from the resolved entry in the lockfile, because that entry is what the build
    /// compiled against: the manifest's requirement is a range and would answer with the wrong string.
    /// </summary>
    internal static string CoreVersion()
    {
        var lockfile = Path.Combine(Root(), "rust", "Cargo.lock");
        var match = Regex.Match(
            File.ReadAllText(lockfile),
            @"\[\[package\]\]\r?\nname = ""rust-trafilatura""\r?\nversion = ""(?<version>[^""]+)""");
        if (!match.Success)
        {
            throw new InvalidOperationException(
                $"No rust-trafilatura [[package]] block in {lockfile}, so the core version the native library reports cannot be derived.");
        }

        return match.Groups["version"].Value;
    }
}
