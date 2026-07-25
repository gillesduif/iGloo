using FluentAssertions;
using Igloo.Core.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Igloo.Core.Tests;

public sealed class DistroLoaderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "igloo-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private void WriteManifest(string folder, string json)
    {
        var dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "distro.json"), json);
    }

    private static string ValidManifest(string id) => $$"""
        {
          "id": "{{id}}",
          "displayName": "Test Distro",
          "description": "A test distro.",
          "iso": { "downloadUrl": "https://example.org/x.iso", "sha256": "abc" }
        }
        """;

    private DistroLoader LoadAll()
    {
        var loader = new DistroLoader(NullLogger<DistroLoader>.Instance);
        loader.Load(_root);
        return loader;
    }

    [Fact]
    public void Valid_manifest_is_loaded_and_stamped_with_its_source_directory()
    {
        WriteManifest("test-distro", ValidManifest("test-distro"));

        var loader = LoadAll();

        var manifest = loader.LoadedDistros.Should().ContainSingle().Subject;
        manifest.Id.Should().Be("test-distro");
        manifest.SourceDirectory.Should().Be(Path.GetFullPath(Path.Combine(_root, "test-distro")));
    }

    [Fact]
    public void Underscore_folders_are_skipped()
    {
        WriteManifest("_template", ValidManifest("_template"));
        WriteManifest("real", ValidManifest("real"));

        LoadAll().LoadedDistros.Should().ContainSingle().Which.Id.Should().Be("real");
    }

    [Fact]
    public void Malformed_manifest_does_not_abort_loading_the_rest()
    {
        WriteManifest("broken", "{ this is not json ");
        WriteManifest("healthy", ValidManifest("healthy"));

        LoadAll().LoadedDistros.Should().ContainSingle().Which.Id.Should().Be("healthy");
    }

    [Fact]
    public void Folders_without_a_manifest_are_skipped()
    {
        Directory.CreateDirectory(Path.Combine(_root, "empty-folder"));
        WriteManifest("real", ValidManifest("real"));

        LoadAll().LoadedDistros.Should().ContainSingle().Which.Id.Should().Be("real");
    }

    [Fact]
    public void Missing_root_directory_yields_an_empty_catalog()
    {
        var loader = new DistroLoader(NullLogger<DistroLoader>.Instance);

        loader.Load(Path.Combine(_root, "does-not-exist"));

        loader.LoadedDistros.Should().BeEmpty();
    }

    [Fact]
    public void Reload_replaces_the_previous_catalog()
    {
        WriteManifest("first", ValidManifest("first"));
        var loader = LoadAll();

        Directory.Delete(Path.Combine(_root, "first"), recursive: true);
        WriteManifest("second", ValidManifest("second"));
        loader.Load(_root);

        loader.LoadedDistros.Should().ContainSingle().Which.Id.Should().Be("second");
    }
}
