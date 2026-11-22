using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;

namespace Igloo.One.App;

/// <summary>Installs a pinned, local English speech model after the user chooses the microphone.</summary>
internal static class OneVoiceModelStore
{
    private const string ModelName = "vosk-model-small-en-us-0.15";
    private const string ArchiveSha256 = "30F26242C4EB449F948E42CB302DD7A686CB29A3423A8367F99FF41780942498";
    private static readonly Uri ArchiveUri = new("https://alphacephei.com/vosk/models/vosk-model-small-en-us-0.15.zip");
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(3) };

    public static async Task<string> EnsureAsync(Action<string> report, CancellationToken cancellationToken)
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "iGloo", "One", "VoiceModels");
        string modelPath = Path.Combine(root, ModelName);
        string marker = Path.Combine(modelPath, ".archive-sha256");
        if (File.Exists(marker) &&
            string.Equals((await File.ReadAllTextAsync(marker, cancellationToken).ConfigureAwait(false)).Trim(),
                ArchiveSha256, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(Path.Combine(modelPath, "am", "final.mdl")) &&
            File.Exists(Path.Combine(modelPath, "graph", "HCLr.fst")))
            return modelPath;

        if (Directory.Exists(modelPath))
            throw new InvalidDataException("The cached voice model is incomplete. Remove its VoiceModels folder and retry.");

        Directory.CreateDirectory(root);
        string temporary = Path.Combine(root, "install-" + Guid.NewGuid().ToString("N"));
        string archive = Path.Combine(temporary, "model.zip");
        try
        {
            Directory.CreateDirectory(temporary);
            report("Downloading the offline English voice model (40 MB)…");
            using HttpResponseMessage response = await Client.GetAsync(ArchiveUri,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            FileStream target = new(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous);
            await using (source.ConfigureAwait(false))
            await using (target.ConfigureAwait(false))
            {
                byte[] buffer = new byte[81920];
                long received = 0;
                int lastPercent = -1;
                while (await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) is int read && read > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    received += read;
                    if (response.Content.Headers.ContentLength is long total && total > 0)
                    {
                        int percent = (int)(received * 100 / total);
                        if (percent >= lastPercent + 10)
                        {
                            lastPercent = percent;
                            report($"Preparing offline voice recognition… {percent}%");
                        }
                    }
                }
            }

            report("Verifying offline voice model…");
            FileStream verify = File.OpenRead(archive);
            await using (verify.ConfigureAwait(false))
            {
                string actual = Convert.ToHexString(await SHA256.HashDataAsync(verify, cancellationToken)
                    .ConfigureAwait(false));
                if (!string.Equals(actual, ArchiveSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The voice model download did not match its pinned checksum.");
            }

            report("Installing offline voice model…");
            string extraction = Path.Combine(temporary, "extracted");
            await ZipFile.ExtractToDirectoryAsync(archive, extraction, cancellationToken)
                .ConfigureAwait(false);
            string extractedModel = Path.Combine(extraction, ModelName);
            if (!File.Exists(Path.Combine(extractedModel, "am", "final.mdl")) ||
                !File.Exists(Path.Combine(extractedModel, "graph", "HCLr.fst")))
                throw new InvalidDataException("The voice model archive is incomplete.");
            await File.WriteAllTextAsync(Path.Combine(extractedModel, ".archive-sha256"),
                ArchiveSha256, cancellationToken).ConfigureAwait(false);
            Directory.Move(extractedModel, modelPath);
            return modelPath;
        }
        finally
        {
            // This name is generated inside the dedicated VoiceModels cache; never remove another path.
            string resolved = Path.GetFullPath(temporary);
            if (resolved.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolved))
                Directory.Delete(resolved, recursive: true);
        }
    }
}
