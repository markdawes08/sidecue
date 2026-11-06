using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace Sidecue.Architecture.Tests;

public sealed class ProjectBoundaryTests
{
    private static readonly Dictionary<string, string[]> AllowedReferences = new()
    {
        ["Sidecue.Core"] = [],
        ["Sidecue.Application"] = ["Sidecue.Core"],
        ["Sidecue.Windows"] = ["Sidecue.Core", "Sidecue.Application"],
        ["Sidecue.Infrastructure"] = ["Sidecue.Core", "Sidecue.Application"],
        ["Sidecue.Desktop"] = ["Sidecue.Core", "Sidecue.Application", "Sidecue.Windows", "Sidecue.Infrastructure"]
    };

    [Theory]
    [InlineData("Sidecue.Core")]
    [InlineData("Sidecue.Application")]
    [InlineData("Sidecue.Windows")]
    [InlineData("Sidecue.Infrastructure")]
    [InlineData("Sidecue.Desktop")]
    public void ProjectReferencesFollowTheArchitecture(string projectName)
    {
        var projectPath = Path.Combine(RepositoryRoot(), "src", projectName, projectName + ".csproj");
        var project = XDocument.Load(projectPath);
        var references = project.Descendants("ProjectReference")
            .Select(reference => (string?)reference.Attribute("Include"));

        Assert.All(references, reference =>
        {
            Assert.NotNull(reference);
            // Project files use either slash convention, including on non-Windows runners.
            var referencedProject = Path.GetFileNameWithoutExtension(reference.Replace('\\', '/'));
            Assert.Contains(referencedProject, AllowedReferences[projectName]);
        });
    }

    [Theory]
    [InlineData("Sidecue.Core")]
    [InlineData("Sidecue.Application")]
    public void PureLayersRestoreWithoutPlatformOrExternalDependencies(string projectName)
    {
        var assetsPath = Path.Combine(RepositoryRoot(), "src", projectName, "obj", "project.assets.json");
        using var assets = JsonDocument.Parse(File.ReadAllText(assetsPath));
        var frameworks = assets.RootElement.GetProperty("project").GetProperty("frameworks");

        var framework = Assert.Single(frameworks.EnumerateObject());
        Assert.Equal("net10.0", framework.Name);
        var frameworkReferences = framework.Value.GetProperty("frameworkReferences");
        Assert.Equal("Microsoft.NETCore.App", Assert.Single(frameworkReferences.EnumerateObject()).Name);

        // These layers start BCL-only. A future domain package needs explicit review
        // here, so transitive Windows, UI, storage or provider SDKs cannot slip in.
        var packages = assets.RootElement.GetProperty("libraries").EnumerateObject()
            .Where(library => library.Value.GetProperty("type").GetString() == "package")
            .Select(library => library.Name);
        Assert.Empty(packages);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")) &&
                Directory.Exists(Path.Combine(directory.FullName, "src")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Run architecture tests from a built Sidecue checkout.");
    }
}
