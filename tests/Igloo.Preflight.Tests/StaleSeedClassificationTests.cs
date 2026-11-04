using Igloo.Core.Preparation;
using Xunit;

namespace Igloo.Preflight.Tests;

// Beta Track B-01: only a volume carrying iGloo's own files is ever disposable on retry.
public sealed class StaleSeedClassificationTests
{
    [Fact]
    public void Only_a_seed_with_igloo_marker_files_is_recognised_as_ours()
    {
        var root = Path.Join(Path.GetTempPath(), "igloo-seed-marker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Join(root, "driver.inf"), "vendor");
            Assert.False(DirectInstallService.IsIglooSeedVolume(root));

            Directory.CreateDirectory(Path.Join(root, "igloo-boot"));
            Assert.True(DirectInstallService.IsIglooSeedVolume(root));
            Directory.Delete(Path.Join(root, "igloo-boot"));

            File.WriteAllText(Path.Join(root, DirectInstallService.SeedReuseReceiptFileName), "{}");
            Assert.True(DirectInstallService.IsIglooSeedVolume(root));
            File.Delete(Path.Join(root, DirectInstallService.SeedReuseReceiptFileName));

            File.WriteAllText(Path.Join(root, SeedIdentityWriter.FileName), "{}");
            Assert.True(DirectInstallService.IsIglooSeedVolume(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
