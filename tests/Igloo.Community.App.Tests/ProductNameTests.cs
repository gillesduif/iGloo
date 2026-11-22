using System.IO;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Igloo.Community.App.Tests;

// The program is called iGloo Community wherever Windows shows it; what other code finds it by keeps its name.
public sealed class ProductNameTests
{
    private const string Name = "iGloo Community";

    private static string Repository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "Igloo.sln")))
            directory = directory.Parent;
        directory.Should().NotBeNull("the names are read from the repository");
        return directory!.FullName;
    }

    private static string[] Installer() =>
        File.ReadAllLines(Path.Join(Repository(), "installer", "iGloo.iss"));

    [Fact]
    public void Setup_the_Start_menu_and_Installed_apps_show_the_full_name()
    {
        var script = Installer();

        script.Should().Contain("AppName=" + Name).And.Contain("AppVerName=" + Name);
        script.Should().Contain("Name: \"{autoprograms}\\" + Name + "\"; Filename: \"{app}\\iGloo.exe\"");
        script.Should().Contain("Name: \"{autodesktop}\\" + Name + "\"; Filename: \"{app}\\iGloo.exe\"; Tasks: desktopicon");
        script.Should().Contain(line => line.Contains("Description: \"Launch " + Name + "\"", StringComparison.Ordinal));
        // No shortcut is left under the old name, and those of earlier builds go at an upgrade.
        script.Should().NotContain(line => line.StartsWith("Name: \"{group}", StringComparison.Ordinal));
        script.Should().Contain("Type: filesandordirs; Name: \"{autoprograms}\\iGloo\"");
        script.Should().Contain("Type: files; Name: \"{autodesktop}\\iGloo.lnk\"");
    }

    [Fact]
    public void An_upgrade_still_finds_the_installation_of_an_earlier_build()
    {
        var script = Installer();

        // The same identity and folder: the sign-in task and the uninstaller of an earlier build point at them.
        script.Should().Contain("AppId={{7B4E9C2A-6F3D-4A1B-9E5C-2D8F0A3B6C71}");
        script.Should().Contain("DefaultDirName={autopf}\\iGloo");
        script.Should().Contain("UninstallDisplayIcon={app}\\iGloo.exe");
        script.Should().Contain(line => line.Contains("/Delete /TN \"\"iGloo boot entry cleanup\"\" /F", StringComparison.Ordinal));
    }

    [Fact]
    public void The_setup_file_is_named_after_the_release_in_the_project_file()
    {
        var project = XDocument.Load(Path.Join(Repository(), "src", "Igloo.Community.App", "Igloo.Community.App.csproj"));
        string Property(string name) => project.Descendants(name).Single().Value;
        var release = Property("IglooRelease");

        release.Should().MatchRegex("^[a-z0-9][a-z0-9.-]*$", "it becomes part of a file name");
        var script = Installer();
        script.Should().Contain("OutputBaseFilename=iGloo-Community-{#IglooVersion}");
        script.Should().Contain("  #define IglooVersion \"" + release + "\"");
        File.ReadAllText(Path.Join(Repository(), "installer", "build-setup.bat"))
            .Should().Contain(".Project.PropertyGroup.IglooRelease").And.Contain("iGloo-Community-%VER%.exe");
        // The program file keeps its name; Windows shows the product name for it.
        Property("AssemblyName").Should().Be("iGloo");
        Property("Product").Should().Be(Name);
        Property("AssemblyTitle").Should().Be(Name);
    }

    [Fact]
    public void Every_window_carries_the_full_name_in_the_taskbar()
    {
        var app = Path.Join(Repository(), "src", "Igloo.Community.App");

        foreach (var window in new[] { "MainWindow.xaml", Path.Join("Views", "FluentMessageBox.xaml") })
            File.ReadAllText(Path.Join(app, window)).Should().Contain("Title=\"" + Name + "\"");
    }
}
