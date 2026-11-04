using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Igloo.Core.Preparation;
using Igloo.Distro.LinuxmintCinnamon;
using Igloo.Migration;
using Microsoft.Extensions.Logging.Abstractions;

if (args.Length == 4 && args[0] == "bind-seed")
{
    string[] agentNames =
    [
        "agent.py", "first-boot.sh", "display-apply.py", "display-apply-gnome.py",
        "igloo_boot.py", "igloo-first-boot.service", "igloo-verify-seed.py",
        "igloo-bootstrap.sh", "igloo-bootstrap.service",
        "grub-theme-stylish-1080p.tar.gz", "grub-theme-stylish-4k.tar.gz",
    ];
    var identity = await SeedIdentityWriter.WriteAsync(args[1], Guid.Parse(args[2]),
        Guid.Parse(args[3]), agentNames.Select(name => "igloo-agent/" + name).ToArray());
    Console.WriteLine($"Mint seed bound to GPT {identity.DiskGuid}/{identity.PartitionGuid}; {identity.Files.Count} hashed inputs");
    return;
}

if (args.Length == 8 && args[0] == "render-reviewed")
{
    var stagedRoot = Path.GetFullPath(args[1]);
    if (!Directory.Exists(stagedRoot) ||
        (File.GetAttributes(stagedRoot) & FileAttributes.ReparsePoint) != 0 ||
        File.Exists(Path.Join(stagedRoot, "igloo-seed-identity.json")))
        throw new InvalidOperationException("Reviewed Mint rendering requires an unbound ordinary staging directory.");

    var manifestFile = Path.Join(stagedRoot, "migration-manifest.json");
    var reviewedManifest = JsonSerializer.Deserialize<MigrationManifest>(await File.ReadAllBytesAsync(manifestFile))
        ?? throw new InvalidDataException("Mint staged manifest is empty.");
    var receipt = new ReviewedInstallerFreeExtentV1(
        Guid.Parse(args[2]), Guid.Parse(args[3]), Guid.Parse(args[4]),
        ulong.Parse(args[5], CultureInfo.InvariantCulture),
        ulong.Parse(args[6], CultureInfo.InvariantCulture),
        ulong.Parse(args[7], CultureInfo.InvariantCulture));
    var reviewedPlugin = new LinuxmintCinnamonPlugin();
    var reviewed = await reviewedPlugin.RenderReviewedInstallerConfigAsync(reviewedManifest, receipt);
    string[] expected =
    [
        "preseed.cfg", "igloo-partman.sh", "igloo-bound-free-choices.sh",
        "igloo-bound-free-do-option.sh", "igloo-bound-efi-fstab.sh",
    ];
    var content = new Dictionary<string, byte[]>(StringComparer.Ordinal)
    {
        [reviewed.FileName] = reviewed.Contents.ToArray(),
    };
    foreach (var extra in reviewed.Extras)
        if (!content.TryAdd(extra.RelativePath, extra.Contents.ToArray()))
            throw new InvalidDataException("Mint reviewed output has duplicate files.");
    if (!content.Keys.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal)) ||
        content.Values.Any(bytes => bytes.Length == 0))
        throw new InvalidDataException("Mint reviewed output differs from the five declared installer files.");

    foreach (var name in expected)
    {
        var destination = Path.Join(stagedRoot, name);
        if (!File.Exists(destination) || (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"Mint staged file is missing or redirected: {name}");
        var temporary = destination + ".reviewed-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, content[name]);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        Console.WriteLine($"{name}: {Convert.ToHexString(SHA256.HashData(content[name]))}");
    }
    Console.WriteLine($"Mint reviewed free extent {receipt.OffsetBytes}+{receipt.SizeBytes} staged for {receipt.TargetDiskGuid}");
    return;
}

if (args.Length != 3)
    throw new ArgumentException("Usage: MintLabStage <empty-output-directory> <manifest-json> <selected-source-folder> | bind-seed <seed-root> <disk-guid> <partition-guid> | render-reviewed <stage-root> <generation-guid> <target-guid> <linux-esp-guid> <free-offset-bytes> <free-size-bytes> <root-minimum-bytes>");

var output = Path.GetFullPath(args[0]);
var manifestPath = Path.GetFullPath(args[1]);
var selectedSourceFolder = Path.GetFullPath(args[2]);
if (!Directory.Exists(output) || Directory.EnumerateFileSystemEntries(output).Any())
    throw new InvalidOperationException("Mint lab output must be an existing empty directory.");
if (!File.Exists(manifestPath) || Path.GetFullPath(Path.GetDirectoryName(manifestPath)!) == output)
    throw new InvalidOperationException("Manifest input must exist outside the output directory.");
if (!Directory.Exists(selectedSourceFolder) ||
    !string.Equals(Path.GetFileName(selectedSourceFolder), "Documents", StringComparison.Ordinal))
    throw new InvalidOperationException("The selected lab source must be an existing Documents folder.");

var manifestBytes = await File.ReadAllBytesAsync(manifestPath);
var manifest = JsonSerializer.Deserialize<MigrationManifest>(manifestBytes)
    ?? throw new InvalidDataException("Manifest JSON is empty.");
if (manifest.DistroId != "linuxmint-cinnamon")
    throw new InvalidDataException("The manifest must select linuxmint-cinnamon.");

var provider = new LinuxmintCinnamonPlugin();
var labDistroId = "igloo-mint-lab-" + Guid.NewGuid().ToString("N");
var staged = await new FileStagingService(NullLogger<FileStagingService>.Instance).StageAsync(
    new FileStagingRequest(labDistroId, [selectedSourceFolder]), progress: null);
try
{
    var stagedManifest = manifest with
    {
        Files = manifest.Files with
        {
            StagingPath = staged.StagingDirectory,
            TotalBytes = staged.TotalBytesCopied,
            IncludedFolders = ["Documents"],
            Folders = [new MigrationFolder { Name = "Documents", SourceRelativePath = "Documents" }],
        },
    };
    StageSelectedMedia(staged.StagingDirectory, stagedManifest.Wallpaper?.FileName,
        stagedManifest.Wallpaper?.OriginalPath, wallpaper: true);
    StageSelectedMedia(staged.StagingDirectory, stagedManifest.AccountPicture?.FileName,
        stagedManifest.AccountPicture?.OriginalPath, wallpaper: false);
    await PluginArtifactWriter.WriteAsync(provider, stagedManifest, staged.StagingDirectory, NullLogger.Instance);
    await File.WriteAllTextAsync(Path.Join(staged.StagingDirectory, "migration-manifest.json"),
        JsonSerializer.Serialize(stagedManifest));
    foreach (var source in Directory.EnumerateFiles(staged.StagingDirectory, "*", SearchOption.AllDirectories))
    {
        var relative = Path.GetRelativePath(staged.StagingDirectory, source);
        var destination = Path.Join(output, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination);
    }
}
finally
{
    // StageAsync generated a fresh, uniquely named lab path, so only this
    // invocation's local snapshot is removed after the isolated VM copy exists.
    Directory.Delete(staged.StagingDirectory, recursive: true);
    Directory.Delete(Path.GetDirectoryName(staged.StagingDirectory)!);
}

var boot = provider.GetInstallerBootSpec();
Console.WriteLine($"Mint provider staged {Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).Count()} files");
Console.WriteLine($"Kernel: {string.Join(", ", boot.KernelIsoPaths)}");
Console.WriteLine($"Initrd: {string.Join(", ", boot.InitrdIsoPaths)}");
Console.WriteLine($"Kernel command line: {boot.KernelCmdline}");

static void StageSelectedMedia(string stagingDirectory, string? fileName,
    string? sourcePath, bool wallpaper)
{
    if (fileName is null)
        return;
    if (wallpaper ? !Regex.IsMatch(fileName, @"\Aigloo-wallpaper\.[A-Za-z0-9]{2,8}\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        : !string.Equals(fileName, "igloo-avatar.jpg", StringComparison.Ordinal))
        throw new InvalidDataException("The selected Mint lab image has an unsafe staged name.");
    if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath) ||
        (File.GetAttributes(sourcePath) & FileAttributes.ReparsePoint) != 0 ||
        new FileInfo(sourcePath).Length == 0)
        throw new InvalidDataException($"The selected Mint lab image {fileName} has no regular source file.");
    File.Copy(sourcePath, Path.Join(stagingDirectory, fileName));
}
