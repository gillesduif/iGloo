using System.Xml.Linq;
using Xunit;

namespace Igloo.Fleet.Tests;

public sealed class ArchitectureTests
{
    [Fact]
    public void ProjectReferencesRespectProductBoundaries()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Join(root.FullName, "Igloo.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        var allowed = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Igloo.Core"] = [],
            ["Igloo.Preflight"] = ["Igloo.Core"],
            ["Igloo.Iso"] = ["Igloo.Core"],
            ["Igloo.Migration"] = ["Igloo.Core"],
            ["Igloo.UsbWriter"] = ["Igloo.Core"],
            ["Igloo.Community.App"] = ["Igloo.Core", "Igloo.Preflight", "Igloo.Iso", "Igloo.Migration", "Igloo.UsbWriter"],
            ["Igloo.Fleet.Contracts"] = [],
            ["Igloo.Fleet.Domain"] = ["Igloo.Fleet.Contracts"],
            ["Igloo.Fleet.Agent"] = ["Igloo.Fleet.Contracts", "Igloo.Fleet.Domain", "Igloo.Core", "Igloo.Preflight"],
            ["Igloo.Fleet.Server"] = ["Igloo.Fleet.Domain", "Igloo.Fleet.Persistence"],
            ["Igloo.Fleet.Persistence"] = ["Igloo.Fleet.Domain"],
            ["Igloo.Fleet.Web"] = ["Igloo.Fleet.Contracts"],
        };
        foreach (var (name, references) in allowed)
        {
            var project = XDocument.Load(Path.Join(root!.FullName, "src", name, name + ".csproj"));
            var actual = project.Descendants("ProjectReference")
                .Select(e => Path.GetFileNameWithoutExtension(e.Attribute("Include")!.Value)).ToArray();
            Assert.Equal(references.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));
            if (name.StartsWith("Igloo.Fleet", StringComparison.Ordinal))
                Assert.DoesNotContain(project.Descendants("UseWPF"), e => e.Value == "true");
        }
        foreach (var path in Directory.EnumerateFiles(Path.Join(root!.FullName, "distros"), "*.csproj", SearchOption.AllDirectories))
        {
            var references = XDocument.Load(path).Descendants("ProjectReference")
                .Select(e => Path.GetFileNameWithoutExtension(e.Attribute("Include")!.Value));
            Assert.All(references, reference => Assert.Equal("Igloo.Core", reference));
        }
    }
}
