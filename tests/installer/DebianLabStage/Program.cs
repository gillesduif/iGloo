using System.Text.Json;
using System.Reflection;
using System.Globalization;
using System.Security.Cryptography;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Igloo.Core.Preparation;
using Igloo.Distro.Debian;
using Igloo.Migration;
using Microsoft.Extensions.Logging.Abstractions;

if (args is ["--inject-initrd", var rawInitrd, var outputInitrd,
    var providerStage, var preflightAssemblyPath])
{
    // Exercise the product's initrd writer with the current provider payload.
    // This only creates a new disposable lab file; it does not prepare a disk.
    var bootSpec = new DebianPlugin().GetInstallerBootSpec();
    if (bootSpec.ConfigDelivery != ConfigDelivery.InjectIntoInitrd ||
        bootSpec.InitrdConfigPath != "preseed.cfg" ||
        bootSpec.InitrdExtraPaths is not { Count: 4 })
        throw new InvalidDataException("Debian's initrd delivery recipe changed.");
    var rawPath = Path.GetFullPath(rawInitrd);
    var outputPath = Path.GetFullPath(outputInitrd);
    var stage = Path.GetFullPath(providerStage);
    if (!File.Exists(rawPath) || !Directory.Exists(stage) ||
        File.Exists(outputPath) ||
        string.Equals(rawPath, outputPath, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("The pinned raw initrd, exact stage or new lab output is unavailable.");
    await using (var source = new FileStream(rawPath, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        var observed = Convert.ToHexString(await SHA256.HashDataAsync(source));
        if (!string.Equals(observed, bootSpec.InitrdSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Debian hd-media initrd differs from the pinned provider source.");
    }
    var names = new[] { bootSpec.InitrdConfigPath }.Concat(bootSpec.InitrdExtraPaths).ToArray();
    var injected = new List<(string Name, byte[] Contents)>();
    foreach (var relative in names)
    {
        var path = Path.GetFullPath(Path.Join(stage, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(stage + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(path) ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("A Debian initrd stage file is unavailable or redirected.");
        injected.Add((Path.GetFileName(relative), await File.ReadAllBytesAsync(path)));
    }
    var assembly = Assembly.LoadFrom(Path.GetFullPath(preflightAssemblyPath));
    var writer = assembly.GetType("Igloo.Preflight.DirectInstallService")?
        .GetMethod("AppendFilesToInitrd", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException("Current DirectInstallService initrd writer is unavailable.");
    var temporary = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
    try
    {
        File.Copy(rawPath, temporary);
        writer.Invoke(null, [temporary, injected]);
        File.Move(temporary, outputPath);
    }
    finally
    {
        if (File.Exists(temporary)) File.Delete(temporary);
    }
    Console.WriteLine($"Debian product initrd writer staged {injected.Count} current provider files.");
    return;
}

if (args is ["--bind-seed", var seedRoot, var seedDiskGuid, var seedPartitionGuid])
{
    // The caller verifies the mounted disposable seed's observed GPT identity.
    // Use the product binder so selected files and browser profiles are covered.
    string[] agentNames =
    [
        "agent.py", "first-boot.sh", "display-apply.py", "display-apply-gnome.py",
        "igloo_boot.py", "igloo-first-boot.service",
        "grub-theme-stylish-1080p.tar.gz", "grub-theme-stylish-4k.tar.gz",
    ];
    var root = Path.GetFullPath(seedRoot);
    if (!Directory.Exists(root) || File.Exists(Path.Join(root, SeedIdentityWriter.FileName)))
        throw new InvalidDataException("The Debian lab seed must be mounted and unbound.");
    var identity = await SeedIdentityWriter.WriteAsync(root, Guid.Parse(seedDiskGuid),
        Guid.Parse(seedPartitionGuid), agentNames.Select(name => "igloo-agent/" + name).ToArray());
    Console.WriteLine($"Debian lab seed bound: {identity.Files.Count} hashed inputs.");
    return;
}

if (args is ["--boot-config", var preflightAssembly, var outputConfig,
    var seedDisk, var seedPartition, var linuxEsp])
{
    var assembly = Assembly.LoadFrom(Path.GetFullPath(preflightAssembly));
    var renderer = assembly.GetType("Igloo.Preflight.DirectInstallService")?
        .GetMethod("BuildGrubConfig", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException("Current DirectInstallService boot renderer is unavailable.");
    var rendered = (string?)renderer.Invoke(null,
        [new DebianPlugin().GetInstallerBootSpec(), "en_US.UTF-8", "us",
         Guid.Parse(seedDisk), Guid.Parse(seedPartition), Guid.Parse(linuxEsp)])
        ?? throw new InvalidDataException("Debian boot renderer produced no configuration.");
    await File.WriteAllTextAsync(Path.GetFullPath(outputConfig), rendered);
    Console.WriteLine($"Debian DirectInstall GRUB config staged: {rendered.Length} characters.");
    return;
}

if (args.Length == 9 && args[0] == "--lab-exact-config")
{
    // Disposable native installer probe only. This does not manufacture a
    // Windows reviewed checkpoint or grant product storage/boot authority.
    var stage = Path.GetFullPath(args[2]);
    var reviewedManifestPath = Path.GetFullPath(args[1]);
    if (!Directory.Exists(stage) || !File.Exists(Path.Join(stage, "preseed.cfg")) ||
        !File.Exists(Path.Join(stage, "migration-manifest.json")) ||
        !File.Exists(reviewedManifestPath))
        throw new InvalidDataException("The exact lab stage or manifest is unavailable.");
    var stagedManifestBytes = await File.ReadAllBytesAsync(Path.Join(stage, "migration-manifest.json"));
    var reviewedManifestBytes = await File.ReadAllBytesAsync(reviewedManifestPath);
    if (!stagedManifestBytes.AsSpan().SequenceEqual(reviewedManifestBytes))
        throw new InvalidDataException("The Debian lab staged manifest changed.");
    var reviewedManifest = JsonSerializer.Deserialize<MigrationManifest>(
        reviewedManifestBytes,
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidDataException("The Debian lab manifest is empty.");
    var extent = new ReviewedInstallerFreeExtentV1(
        Guid.Parse(args[3]), Guid.Parse(args[4]), Guid.Parse(args[5]),
        ulong.Parse(args[6], CultureInfo.InvariantCulture),
        ulong.Parse(args[7], CultureInfo.InvariantCulture),
        ulong.Parse(args[8], CultureInfo.InvariantCulture));
    var config = await new DebianPlugin().RenderReviewedInstallerConfigAsync(reviewedManifest, extent);
    if (config.FileName != "preseed.cfg" || config.Extras.Count != 4 ||
        config.Extras.Select(item => item.RelativePath).Order(StringComparer.Ordinal)
            .SequenceEqual(new[]
            {
                "igloo-initrd/igloo-bound-free-choices.sh",
                "igloo-initrd/igloo-bound-free-do-option.sh",
                "igloo-initrd/igloo-bound-free-setup.sh",
                "igloo-initrd/igloo-verify-seed.py",
            }.Order(StringComparer.Ordinal)) is false)
        throw new InvalidDataException("The Debian lab provider artifact set changed.");
    await File.WriteAllBytesAsync(Path.Join(stage, config.FileName), config.Contents.ToArray());
    foreach (var extra in config.Extras)
        await File.WriteAllBytesAsync(Path.Join(stage, extra.RelativePath.Replace('/',
            Path.DirectorySeparatorChar)), extra.Contents.ToArray());
    Console.WriteLine($"Debian lab exact extent rendered: {extent.OffsetBytes}+{extent.SizeBytes} bytes.");
    return;
}

if (args.Length != 2)
    throw new ArgumentException("Usage: DebianLabStage <manifest-json> <existing-empty-output-directory> | --lab-exact-config <manifest> <stage> <generation> <disk-guid> <esp-partuuid> <offset> <size> <root-minimum> | --inject-initrd <pinned-raw-initrd> <new-output> <stage> <preflight-dll> | --boot-config <preflight-dll> <new-config> <seed-disk-guid> <seed-partuuid> <esp-partuuid> | --bind-seed <mounted-seed-root> <disk-guid> <partition-guid>");

var manifestPath = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
if (!File.Exists(manifestPath) || !Directory.Exists(output) ||
    Directory.EnumerateFileSystemEntries(output).Any() ||
    string.Equals(Path.GetDirectoryName(manifestPath), output, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Debian lab inputs must be separate and output must be empty.");

var manifest = JsonSerializer.Deserialize<MigrationManifest>(
    await File.ReadAllBytesAsync(manifestPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
    ?? throw new InvalidDataException("Debian lab manifest is empty.");
if (manifest.DistroId != "debian")
    throw new InvalidDataException("Debian lab manifest has the wrong distribution ID.");

var provider = new DebianPlugin();
await PluginArtifactWriter.WriteAsync(provider, manifest, output, NullLogger.Instance);
await File.WriteAllBytesAsync(Path.Join(output, "migration-manifest.json"),
    await File.ReadAllBytesAsync(manifestPath));
Console.WriteLine($"Debian provider staged {Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).Count()} files.");
