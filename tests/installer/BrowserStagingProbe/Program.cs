using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Igloo.Migration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BrowserStagingProbe;

internal static class Program
{
    private static readonly string[] DatabaseNames = ["History", "Favicons", "Top Sites", "Network/Cookies"];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [STAThread]
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The probe window must show any failure, including an unwritable evidence destination.")]
    private static void Main()
    {
        var output = new TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 16,
            Margin = new Thickness(20),
            Text = "Running the Edge portable-profile staging check...\n\nPlease leave this window open until the result appears.",
        };
        var window = new Window
        {
            Title = "iGloo Browser Staging Probe",
            Width = 900,
            Height = 650,
            Content = output,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        var running = true;
        window.Closing += (_, args) => args.Cancel = running;
        window.Loaded += async (_, _) =>
        {
            ProbeReport? report = null;
            try
            {
                report = await RunAsync().ConfigureAwait(true);
                // Keep the completed result visible even if the launch folder
                // is a read-only VM share or neither evidence destination works.
                output.Text = FormatReport(report) + "Saving evidence...";
                window.Title = report.Status + " - iGloo Browser Staging Probe";
                var name = "browser-staging-probe-" +
                    DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture) + ".json";
                var saved = await SaveReportAsync(report, name).ConfigureAwait(true);
                output.Text = FormatReport(report) + (saved.Path is null
                    ? "EVIDENCE NOT SAVED\n" + saved.Warning
                    : "Evidence saved:\n" + saved.Path +
                        (saved.Warning is null ? "" : "\n\n" + saved.Warning));
            }
            catch (Exception error)
            {
                if (report is not null)
                    output.Text = FormatReport(report) + "EVIDENCE NOT SAVED\n" + SafeError(error);
                else
                {
                    output.Text = "PROBE FAILED\n\n" + SafeError(error);
                    window.Title = "PROBE FAILED - iGloo Browser Staging Probe";
                }
            }
            finally
            {
                running = false;
            }
        };
        new Application().Run(window);
    }

    private static string FormatReport(ProbeReport report) => report.Status + "\n\n" +
        $"Portable files staged: {report.StagedFileCount}\n" +
        $"Bytes staged: {report.StagedBytes}\n" +
        "Observed source files: " + (report.SourceUnchanged ? "unchanged" : "changed or could not be verified") + "\n" +
        $"Test copies removed: {report.TestCopiesRemoved}\n\n" +
        (report.Error is null ? "" : report.Error + "\n\n") +
        "This checks browser staging only. It does not test installation or boot.\n\n";

    private static async Task<(string? Path, string? Warning)> SaveReportAsync(ProbeReport report, string name)
    {
        var json = JsonSerializer.Serialize(report, JsonOptions);
        var besideProgram = Path.Join(AppContext.BaseDirectory, name);
        string warning;
        try
        {
            await File.WriteAllTextAsync(besideProgram, json).ConfigureAwait(false);
            return (besideProgram, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            warning = "Could not save beside the program.\n" + SafeError(error);
        }

        var candidates = new List<string>();
        const string evidenceShare = @"\\VBoxSvr\igloo-repair-evidence";
        if (Directory.Exists(evidenceShare)) candidates.Add(evidenceShare);
        candidates.Add(Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Igloo", "diagnostics", "browser-probe"));
        foreach (var folder in candidates)
        {
            try
            {
                Directory.CreateDirectory(folder);
                var fallback = Path.Join(folder, name);
                await File.WriteAllTextAsync(fallback, json).ConfigureAwait(false);
                return (fallback, warning);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                warning += "\nFallback evidence destination unavailable.\n" + SafeError(error);
            }
        }
        return (null, warning);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Probe failures are recorded, and only the returned staging generation is cleaned up.")]
    private static async Task<ProbeReport> RunAsync()
    {
        var started = DateTimeOffset.UtcNow;
        var source = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Edge", "User Data");
        var profile = Path.Join(source, "Default");
        var relative = Path.GetRelativePath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), source);
        var distroId = "browser-probe-" + Guid.NewGuid().ToString("N");
        var before = await Task.Run(() => Observe(profile)).ConfigureAwait(false);
        FileStagingResult? result = null;
        string? errorText = null;
        var removed = false;
        try
        {
            var service = new FileStagingService(NullLogger<FileStagingService>.Instance);
            result = await service.StageAsync(new FileStagingRequest(distroId, [])
            {
                BrowserRelativePaths = [relative],
                BrowserProfiles = [new BrowserMigration
                {
                    Name = "Microsoft Edge", Engine = "chromium", SourceRelativePath = relative,
                }],
            }, null).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            errorText = SafeError(error);
        }
        finally
        {
            if (result is not null)
            {
                try
                {
                    // This API validates the exact generated path and refuses redirects.
                    FileStagingService.RemoveIncompleteGeneration(result.StagingDirectory, distroId);
                    removed = !Directory.Exists(result.StagingDirectory);
                }
                catch (Exception cleanup)
                {
                    errorText = "Test-copy cleanup failed: " + SafeError(cleanup);
                }
            }
        }

        var after = await Task.Run(() => Observe(profile)).ConfigureAwait(false);
        var unchanged = before.Count == after.Count && before.All(item =>
            item.Value.Error is null && after.TryGetValue(item.Key, out var value) && value == item.Value);
        var status = result is null || !removed || errorText is not null ? "FAIL" :
            !unchanged ? "INCONCLUSIVE - source changed during the check" :
            result.FileCount == 0 ? "NO PORTABLE FILES TO VERIFY" : "PASS";
        var assemblyHash = await Task.Run(() => HashFile(typeof(FileStagingService).Assembly.Location)).ConfigureAwait(false);
        return new ProbeReport(started, DateTimeOffset.UtcNow, status, result?.FileCount ?? 0,
            result?.TotalBytesCopied ?? 0, unchanged, removed, errorText, assemblyHash, before, after);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Each failed observation is recorded and prevents a PASS result.")]
    private static Dictionary<string, SourceObservation> Observe(string profile)
    {
        var paths = new[] { "Bookmarks" }.Concat(DatabaseNames.SelectMany(name =>
            new[] { name, name + "-journal", name + "-wal" }));
        var observations = new Dictionary<string, SourceObservation>(StringComparer.Ordinal);
        foreach (var relative in paths)
        {
            try
            {
                var path = Path.Join(profile, Path.Join(relative.Split('/')));
                FileAttributes attributes;
                try { attributes = File.GetAttributes(path); }
                catch (FileNotFoundException)
                {
                    observations.Add(relative, new(false, null, null, null));
                    continue;
                }
                catch (DirectoryNotFoundException)
                {
                    observations.Add(relative, new(false, null, null, null));
                    continue;
                }
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                    throw new IOException("Source observation requires a regular file.");
                var metadata = relative.EndsWith("-journal", StringComparison.Ordinal)
                    ? ReadJournalMetadata(path) : (Header: (string?)null, Footer: (string?)null, HasFooter: (bool?)null);
                observations.Add(relative, new(true, new FileInfo(path).Length, HashFile(path), null,
                    metadata.Header, metadata.Footer, metadata.HasFooter));
            }
            catch (Exception error)
            {
                observations.Add(relative, new(true, null, null, SafeError(error)));
            }
        }
        return observations;
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static (string? Header, string? Footer, bool? HasFooter) ReadJournalMetadata(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Span<byte> header = stackalloc byte[28];
        var count = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        var headerHex = Convert.ToHexString(header[..count]);
        if (stream.Length < 16) return (headerHex, null, false);
        Span<byte> footer = stackalloc byte[16];
        stream.Position = stream.Length - footer.Length;
        stream.ReadExactly(footer);
        var hasFooter = footer[8..].SequenceEqual(Convert.FromHexString("D9D505F920A163D7"));
        // An ordinary journal ends in page contents. Emit footer bytes only when
        // its marker proves these bytes are the length/checksum/magic metadata.
        return (headerHex, hasFooter ? Convert.ToHexString(footer) : null, hasFooter);
    }

    private static string SafeError(Exception error)
    {
        var messages = new List<string>();
        var userRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            var message = current.Message.Replace(userRoot, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
            messages.Add(current.GetType().Name + ": " + message);
        }
        return string.Join(Environment.NewLine, messages);
    }

    private sealed record SourceObservation(bool Exists, long? Length, string? Sha256, string? Error,
        string? JournalHeaderHex = null, string? SuperJournalFooterHex = null, bool? HasSuperJournalFooter = null);

    private sealed record ProbeReport(DateTimeOffset StartedUtc, DateTimeOffset CompletedUtc, string Status,
        int StagedFileCount, long StagedBytes, bool SourceUnchanged, bool TestCopiesRemoved, string? Error,
        string MigrationAssemblySha256, Dictionary<string, SourceObservation> Before,
        Dictionary<string, SourceObservation> After);
}
