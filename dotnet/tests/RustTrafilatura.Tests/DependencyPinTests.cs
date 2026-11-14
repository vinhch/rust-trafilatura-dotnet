using System.Text.RegularExpressions;
using Xunit;

namespace RustTrafilatura.Tests;

/// <summary>
/// Pins whose version is bounded by a property of the build rather than by Dependabot's own rules.
/// </summary>
public sealed class DependencyPinTests
{
    private const string SourceLinkPackage = "Microsoft.SourceLink.GitHub";

    // 11.x has no net8.0 dependency group and pulls System.IO.Hashing 11, which warns that it does not
    // support the net8.0 asset.
    private const int FirstSourceLinkMajorWithoutNet8 = 11;

    /// <summary>
    /// While <c>net8.0</c> is a target framework, SourceLink stays below 11. Dependabot ignores its majors
    /// (.github/dependabot.yml), so this fails a hand edit that crosses the bound.
    /// </summary>
    [Fact]
    public void SourceLink_Major_Supports_Net8_While_It_Is_Targeted()
    {
        var root = Repository.Root();
        var props = Path.Combine(root, "dotnet", "Directory.Build.props");
        var csproj = Path.Combine(root, "dotnet", "src", "RustTrafilatura", "RustTrafilatura.csproj");

        var frameworks = Regex.Match(File.ReadAllText(props), @"<TargetFrameworks>(?<list>[^<]*)</TargetFrameworks>");
        if (!frameworks.Success)
        {
            throw new InvalidOperationException(
                $"No <TargetFrameworks> element in {props}, so whether net8.0 is targeted cannot be read.");
        }

        var reference = Regex.Match(
            File.ReadAllText(csproj),
            $@"<PackageReference Include=""{Regex.Escape(SourceLinkPackage)}"" Version=""(?<version>[^""]+)""");
        if (!reference.Success)
        {
            throw new InvalidOperationException(
                $"No {SourceLinkPackage} PackageReference with a literal Version in {csproj}, so its major cannot be read.");
        }

        var version = reference.Groups["version"].Value;
        var major = Regex.Match(version, @"^(?<major>\d+)\.");
        if (!major.Success)
        {
            throw new InvalidOperationException($"{SourceLinkPackage} Version \"{version}\" in {csproj} does not start with a major version number.");
        }

        var targetsNet8 = frameworks.Groups["list"].Value.Split(';').Contains("net8.0");
        var sourceLinkMajor = int.Parse(major.Groups["major"].Value, System.Globalization.CultureInfo.InvariantCulture);

        Assert.False(
            targetsNet8 && sourceLinkMajor >= FirstSourceLinkMajorWithoutNet8,
            $"{SourceLinkPackage} {version} does not support net8.0, which {props} still targets. " +
            "Keep it below 11, or drop net8.0 first: AGENTS.md, row \"SourceLink major\".");
    }
}
