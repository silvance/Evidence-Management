using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Emc.Application.Tests;

/// <summary>
/// Checks, from source, the parts of the air-gap constraint that can be checked from source:
/// the SDK is pinned exactly; lock files exist for every project; the offline NuGet
/// configuration cannot reach the Internet; no package version floats; and the web project
/// references no remote asset. See docs/air-gapped-build-and-maintenance.md.
///
/// [CONTROL] - a program constraint, not an AR 195-5 requirement.
/// </summary>
public class OfflineBuildTests
{
    private static readonly string Root = FindRepositoryRoot();

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Emc.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Emc.sln not found above the test directory.");
    }

    private static IEnumerable<string> ProjectFiles()
        => Directory.EnumerateFiles(Root, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !p.Contains("dependency-bundle"));

    [Fact]
    public void TheSdkIsPinnedExactly_AndDoesNotRollForward()
    {
        var text = File.ReadAllText(Path.Combine(Root, "global.json"));

        Assert.Matches("\"version\"\\s*:\\s*\"\\d+\\.\\d+\\.\\d+\"", text);
        Assert.Contains("\"rollForward\": \"disable\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryProjectHasACommittedLockFile()
    {
        foreach (var project in ProjectFiles())
        {
            var lockFile = Path.Combine(Path.GetDirectoryName(project)!, "packages.lock.json");
            Assert.True(File.Exists(lockFile), $"Missing packages.lock.json beside {Path.GetFileName(project)}.");
        }
    }

    [Fact]
    public void LockFilesAreEnabledForTheWholeSolution()
    {
        var props = File.ReadAllText(Path.Combine(Root, "Directory.Build.props"));

        Assert.Contains("<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>", props, StringComparison.Ordinal);
        Assert.Contains("RestoreLockedMode", props, StringComparison.Ordinal);
    }

    [Fact]
    public void NoPackageVersionFloats()
    {
        foreach (var project in ProjectFiles())
        {
            var doc = XDocument.Load(project);
            foreach (var reference in doc.Descendants("PackageReference"))
            {
                var version = reference.Attribute("Version")?.Value ?? reference.Element("Version")?.Value;
                Assert.False(string.IsNullOrWhiteSpace(version), $"{Path.GetFileName(project)}: {reference.Attribute("Include")?.Value} has no version.");
                Assert.DoesNotContain("*", version, StringComparison.Ordinal);
                Assert.DoesNotContain("[", version, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void TheOfflineNuGetConfigurationCannotReachTheInternet()
    {
        var doc = XDocument.Load(Path.Combine(Root, "NuGet.Offline.Config"));

        var sources = doc.Root!.Element("packageSources")!;
        Assert.NotNull(sources.Element("clear"));

        foreach (var add in sources.Elements("add"))
        {
            var value = add.Attribute("value")!.Value;
            Assert.False(value.StartsWith("http", StringComparison.OrdinalIgnoreCase), $"Offline source is a URL: {value}");
            Assert.False(Path.IsPathRooted(value), $"Offline source is machine-specific: {value}");
        }

        Assert.Single(sources.Elements("add"));

        var audit = doc.Root.Element("auditSources");
        Assert.NotNull(audit);
        Assert.NotNull(audit!.Element("clear"));
        Assert.Empty(audit.Elements("add"));

        Assert.DoesNotContain("password", File.ReadAllText(Path.Combine(Root, "NuGet.Offline.Config")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheWebProjectReferencesNoRemoteAsset()
    {
        // CDN scripts, web fonts, remote stylesheets, remote imports: none. Comments and the
        // launch-settings file are not assets and are excluded.
        var web = Path.Combine(Root, "src", "Emc.Web");
        var files = Directory.EnumerateFiles(web, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".css", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

        var remote = new Regex(
            "(src|href)\\s*=\\s*[\"'](https?:)?//|url\\(\\s*[\"']?(https?:)?//|@import\\s+[\"'(]*https?://|fonts\\.googleapis|cdn\\.|cdnjs|jsdelivr|unpkg",
            RegexOptions.IgnoreCase);

        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.TrimStart().StartsWith("/*", StringComparison.Ordinal) || line.TrimStart().StartsWith("*", StringComparison.Ordinal)
                    || line.TrimStart().StartsWith("@*", StringComparison.Ordinal) || line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                Assert.False(remote.IsMatch(line), $"{Path.GetRelativePath(Root, file)}:{i + 1} references a remote asset: {line.Trim()}");
            }
        }
    }

    [Fact]
    public void TheDependencyBundleIsNotCommitted()
    {
        var gitignore = File.ReadAllText(Path.Combine(Root, ".gitignore"));
        Assert.Contains("dependency-bundle/*", gitignore, StringComparison.Ordinal);
    }
}
