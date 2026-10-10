using System.IO.Compression;
using System.Text.RegularExpressions;
using Xunit;

namespace RustTrafilatura.Tests;

/// <summary>
/// Checks the packed <c>.nupkg</c> itself: what a consumer installs has to carry the native library
/// for every RID that was staged, this repository's licence and notices and the README at its root,
/// and both library assets, and it must not carry the build stamp that sits beside each staged
/// library.
/// </summary>
/// <remarks>
/// The category trait keeps this class out of the default run: it is the one test that needs a
/// release artifact, and a checkout that has built the native library but never packed has none, so
/// running it by default would make the ordinary build-then-test flow depend on the packaging step.
/// scripts/release.* runs it explicitly after packing, where the package exists and a missing RID, a
/// missing license, a missing asset or a packed build stamp fails the release rather than the suite.
/// The assertions are driven by the staging tree rather than by a list written here, so the test
/// checks the package against what was actually built.
/// </remarks>
[Trait("Category", "Package")]
public sealed class PackageTests
{
    private const string PackageVersion = "2.2.8";

    private const string PackageRelativePath = "artifacts/RustTrafilatura." + PackageVersion + ".nupkg";

    /// <summary>
    /// The files the package carries at its root beside the two managed assets: this repository's MIT
    /// text and its third-party notices.
    /// </summary>
    private static readonly string[] PackageNoticeFiles = ["LICENSE", "THIRD-PARTY-NOTICES.md"];

    /// <summary>
    /// The library file one RID stages, or null for a directory name that is not a RID this package
    /// carries. The name differs per platform, so a RID folder is only a package RID when it is one
    /// of these.
    /// </summary>
    private static string? StagedLibrary(string rid) => rid switch
    {
        "win-x64" => "rust_trafilatura_ffi.dll",
        "linux-x64" or "linux-arm64" => "librust_trafilatura_ffi.so",
        "osx-x64" or "osx-arm64" => "librust_trafilatura_ffi.dylib",
        _ => null,
    };

    /// <summary>The repository root, found by walking up from the test assembly's own directory.</summary>
    private static string RepositoryRoot()
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

    [Fact]
    public void Package_Contains_AllRidsAndLicenses()
    {
        var root = RepositoryRoot();
        var package = Path.Combine(root, PackageRelativePath);
        Assert.True(
            File.Exists(package),
            $"The package to check is not at {package}. Run pwsh -File scripts/release.ps1 (or bash scripts/release.sh), which packs it and then runs this check.");

        var staged = StagedRids(root).ToList();

        var entries = new List<string>();
        using (var archive = ZipFile.OpenRead(package))
        {
            foreach (var entry in archive.Entries)
            {
                entries.Add(entry.FullName);
            }

            Assert.Contains(entries, name => name == "README.md");
            foreach (var notice in PackageNoticeFiles)
            {
                Assert.Contains(entries, name => name == notice);
            }

            // The build stamp beside each staged library is build evidence, not package content: a
            // package that carried one would ship the RID, toolchain and source digests of the build.
            Assert.DoesNotContain(entries, name => name.EndsWith("build-stamp.txt", StringComparison.Ordinal));

            foreach (var rid in staged)
            {
                var expected = $"runtimes/{rid}/native/{StagedLibrary(rid)}";
                Assert.Contains(entries, name => name == expected);
            }

            // The two assets are the whole managed surface of the package, one per target framework.
            foreach (var framework in new[] { "net8.0", "net10.0" })
            {
                Assert.Contains(entries, name => name == $"lib/{framework}/RustTrafilatura.dll");
            }
        }
    }

    /// <summary>
    /// The package's licence metadata states MIT by expression rather than by a licence file, so a
    /// consumer's tooling reads an SPDX id instead of free text.
    /// </summary>
    /// <remarks>
    /// The expression is read from the <c>.nuspec</c> inside the archive rather than from the project
    /// file: the project is the input, and only the packed metadata is what a consumer's tooling
    /// reads. A project-level assertion would still pass if the entry never reached the package.
    /// </remarks>
    [Fact]
    public void Package_Declares_License_As_Mit_Expression()
    {
        var root = RepositoryRoot();
        var package = Path.Combine(root, PackageRelativePath);
        Assert.True(
            File.Exists(package),
            $"The package to check is not at {package}. Run pwsh -File scripts/release.ps1 (or bash scripts/release.sh), which packs it and then runs this check.");

        string nuspec;
        using (var archive = ZipFile.OpenRead(package))
        {
            var entry = Assert.Single(
                archive.Entries,
                candidate => candidate.FullName.EndsWith(".nuspec", StringComparison.Ordinal));
            using var reader = new StreamReader(entry.Open());
            nuspec = reader.ReadToEnd();
        }

        var expression = Regex.Match(
            nuspec,
            @"<license\s+type=""expression""\s*>(?<id>[^<]*)</license>",
            RegexOptions.IgnoreCase);
        Assert.True(
            expression.Success,
            $"The .nuspec declares no licence expression. Apache-2.0 AND BSD-3-Clause was the expression this package used before this repository's own code became MIT; a package that states neither dropped the metadata. The <license> element is: {Regex.Match(nuspec, @"<license[^>]*>[^<]*</license>", RegexOptions.Singleline).Value}");

        Assert.Equal("MIT", expression.Groups["id"].Value.Trim());

        // NuGet writes a <licenseUrl> beside every expression, derived from the SPDX id; that is its
        // own compatibility element, not a fallback this project chose. What must not appear is a
        // licence *file* reference, because that would mean the expression is gone and the terms are
        // back to free text with no SPDX id for a consumer's tooling to read.
        Assert.DoesNotContain(@"type=""file""", nuspec, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Apache-2.0", nuspec, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BSD-3-Clause", nuspec, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The RIDs staged under the package project, read from the staging tree the release scripts pack
    /// rather than from a list repeated here, so the expected entries are the libraries that were
    /// actually built rather than the package's whole RID contract.
    /// </summary>
    private static IEnumerable<string> StagedRids(string root)
    {
        var runtimes = Path.Combine(root, "dotnet", "src", "RustTrafilatura", "runtimes");
        if (!Directory.Exists(runtimes))
        {
            yield break;
        }

        foreach (var directory in Directory.EnumerateDirectories(runtimes))
        {
            var rid = Path.GetFileName(directory);
            var library = StagedLibrary(rid);
            if (library is not null && File.Exists(Path.Combine(directory, "native", library)))
            {
                yield return rid;
            }
        }
    }
}
