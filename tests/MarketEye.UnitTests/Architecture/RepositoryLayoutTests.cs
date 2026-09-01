using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace MarketEye.UnitTests.Architecture;

/// <summary>
/// PLAN.md §2 states MarketEye.Domain has zero dependencies, and §2's reference graph is what
/// keeps "AI is at the edge" structural rather than aspirational. Stated constraints erode;
/// asserted ones fail the build.
/// </summary>
public class RepositoryLayoutTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MarketEye.sln")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the test must be able to locate the repository root");
        return dir!.FullName;
    }

    private static XDocument Project(string relativePath) =>
        XDocument.Load(Path.Combine(RepoRoot(), relativePath));

    private static string[] ReferencedProjects(XDocument csproj) =>
        csproj.Descendants("ProjectReference")
              .Select(e => Path.GetFileNameWithoutExtension(
                  (e.Attribute("Include")?.Value ?? string.Empty).Replace('\\', '/')))
              .ToArray();

    [Fact]
    public void Domain_has_no_package_dependencies()
    {
        var csproj = Project("src/MarketEye.Domain/MarketEye.Domain.csproj");
        csproj.Descendants("PackageReference").Should().BeEmpty(
            "PLAN.md §2 specifies MarketEye.Domain as having zero dependencies");
    }

    [Fact]
    public void Domain_has_no_project_dependencies()
    {
        ReferencedProjects(Project("src/MarketEye.Domain/MarketEye.Domain.csproj"))
            .Should().BeEmpty("the domain sits at the bottom of the §2 reference graph");
    }

    [Fact]
    public void Application_depends_only_on_Domain()
    {
        ReferencedProjects(Project("src/MarketEye.Application/MarketEye.Application.csproj"))
            .Should().BeEquivalentTo(["MarketEye.Domain"]);
    }

    [Fact]
    public void Ai_does_not_reach_into_Infrastructure()
    {
        // §5.1: everything downstream of the validator is deterministic. MarketEye.Ai must not
        // acquire a route to the database, or "AI at the edge" stops being enforceable.
        ReferencedProjects(Project("src/MarketEye.Ai/MarketEye.Ai.csproj"))
            .Should().NotContain("MarketEye.Infrastructure");
    }

    [Fact]
    public void Domain_and_Application_do_not_reference_EntityFrameworkCore()
    {
        foreach (var proj in new[]
                 {
                     "src/MarketEye.Domain/MarketEye.Domain.csproj",
                     "src/MarketEye.Application/MarketEye.Application.csproj",
                 })
        {
            Project(proj).Descendants("PackageReference")
                .Select(e => e.Attribute("Include")?.Value ?? string.Empty)
                .Should().NotContain(n => n.StartsWith("Microsoft.EntityFrameworkCore"),
                    "persistence concerns belong in MarketEye.Infrastructure (§2)");
        }
    }
}
