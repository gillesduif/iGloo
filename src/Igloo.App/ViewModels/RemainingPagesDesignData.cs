using System.Collections.ObjectModel;
using System.Windows.Input;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;

namespace Igloo.App.ViewModels;

/// <summary>Design-time stand-in for <see cref="DistroSelectionViewModel"/>.</summary>
public sealed class DistroSelectionDesignData
{
    private static DistroManifest Manifest(string id, string name, string description,
        string desktop) => new()
        {
            Id = id,
            DisplayName = name,
            Description = description,
            DefaultDesktopEnvironment = desktop,
            Status = "available",
            Iso = new DistroIsoSpec
            {
                DownloadUrl = new Uri($"https://example.invalid/{id}.iso"),
                Sha256 = new string('0', 64),
            },
        };

    // Recommended, All, then the desktop environments the catalog offers -
    // see DistroSelectionViewModel.Rebuild.
    public IReadOnlyList<string> Categories { get; } =
        [DistroSelectionViewModel.RecommendedCategory, DistroSelectionViewModel.AllCategory,
         "Cinnamon", "GNOME", "KDE Plasma"];

    // Settable: the ComboBox binds SelectedCategory TwoWay.
    public string SelectedCategory { get; set; } = "Recommended";

    public IReadOnlyList<DistroListItem> DistroItems { get; }

    // Settable: the ListBox binds SelectedItem TwoWay.
    public DistroListItem SelectedItem { get; set; }

    public bool CanProceed => SelectedItem is { IsCompatible: true, IsComingSoon: false };

    public DistroSelectionDesignData()
    {
        DistroItems =
        [
            new DistroListItem(
                Manifest("linuxmint-cinnamon", "Linux Mint",
                    "Linux Mint 22.3 with the Cinnamon desktop. The classic recommendation for Windows switchers: a familiar taskbar-and-menu layout, conservative updates on an Ubuntu LTS base, and multimedia support that works out of the box.",
                    "Cinnamon"),
                IsCompatible: true, IncompatibilityReason: null, IsComingSoon: false,
                IsRecommended: true),
            new DistroListItem(
                Manifest("debian", "Debian",
                    "Debian 13 \"Trixie\" with the GNOME desktop. The universal operating system: legendary stability, a vast package archive, and a volunteer project that has been the bedrock of the Linux world for three decades. Installed offline from the live image, so no network is needed until first boot.",
                    "GNOME"),
                IsCompatible: true, IncompatibilityReason: null, IsComingSoon: false),
            new DistroListItem(
                Manifest("manjaro", "Manjaro",
                    "Arch-based rolling release with a curated update stream.", "KDE Plasma"),
                IsCompatible: false,
                IncompatibilityReason: "Requires Secure Boot to be disabled",
                IsComingSoon: true),
        ];
        SelectedItem = DistroItems[0];
    }
}

/// <summary>Design-time stand-in for <see cref="IsoAcquisitionViewModel"/>.</summary>
/// <remarks>Set Phase to preview each glyph; IsRunning drives the comet ring.</remarks>
public sealed class IsoAcquisitionDesignData
{
    public bool IsRunning { get; set; } = true;
    public bool IsComplete { get; set; }
    public bool HasError { get; set; }
    public string ErrorMessage { get; set; } = "The mirror did not respond.";

    public IsoAcquisitionPhase Phase { get; set; } = IsoAcquisitionPhase.Downloading;

    // Derived exactly as IsoAcquisitionViewModel does, so changing Phase above moves
    // the glyph and the caption together.
    public string PhaseDisplay => Phase switch
    {
        IsoAcquisitionPhase.ResolvingMirror => "Resolving mirror\u2026",
        IsoAcquisitionPhase.Downloading => "Downloading\u2026",
        IsoAcquisitionPhase.VerifyingSha256 => "Verifying SHA-256\u2026",
        IsoAcquisitionPhase.VerifyingGpg => "Verifying GPG signature\u2026",
        IsoAcquisitionPhase.Complete => "Complete",
        _ => string.Empty,
    };

    public long BytesCompleted { get; } = 1_932_735_283;
    public long BytesTotal { get; } = 3_006_477_107;
    public double ProgressPercent { get; } = 64;
    public bool IsProgressIndeterminate { get; }

    public IsoAcquisitionResult Result { get; } =
        new(@"C:\Users\Gilles\AppData\Local\Igloo\iso-cache\debian\debian-13.1.0-amd64-netinst.iso",
            Sha256Verified: true, GpgVerified: true, SizeBytes: 3_006_477_107);

    public ICommand AcquireCancelCommand { get; } = new DesignCommand();
}

/// <summary>Design-time stand-in for <see cref="FileStagingViewModel"/>.</summary>
public sealed class FileStagingDesignData
{
    public bool IsRunning { get; set; } = true;
    public bool IsComplete { get; set; }
    public bool HasError { get; set; }
    public string ErrorMessage { get; set; } = "Access to Documents was denied.";

    public FileStagingPhase Phase { get; set; } = FileStagingPhase.Copying;

    public string PhaseDisplay => Phase switch
    {
        FileStagingPhase.Scanning => "Scanning files\u2026",
        FileStagingPhase.Copying => "Copying files\u2026",
        FileStagingPhase.Generating => "Generating installer configuration\u2026",
        FileStagingPhase.Complete => "Complete",
        _ => string.Empty,
    };
    public string CurrentFile { get; } = @"Documents\Projects\iGloo\notes.md";

    public long BytesCopied { get; } = 8_589_934_592;
    public long BytesTotal { get; } = 21_474_836_480;
    public double ProgressPercent { get; } = 40;
    public bool IsProgressIndeterminate { get; }

    public FileStagingResult Result { get; } =
        new(@"C:\Users\Gilles\AppData\Local\Igloo\staging\debian", 21_474_836_480, 18_442);

    public ICommand StageCancelCommand { get; } = new DesignCommand();
}

/// <summary>Design-time stand-in for <see cref="UsbWriterViewModel"/>.</summary>
public sealed class UsbWriterDesignData
{
    public bool IsRunning { get; set; }
    public bool IsComplete { get; set; }
    public bool IsEnumerating { get; set; }
    public bool IsCancelable { get; set; } = true;
    public bool HasError { get; set; }
    public string ErrorMessage { get; set; } = "The drive was removed during the write.";
    public string ErrorDetail { get; set; } = "Win32 error 433: the device is not ready.";
    public bool HasErrorDetail => !string.IsNullOrEmpty(ErrorDetail);

    public string PhaseDisplay { get; set; } = "Writing installer image\u2026";
    public long BytesWritten { get; }
    public long BytesTotal { get; } = 3_006_477_107;
    public double ProgressPercent { get; }
    public bool IsProgressIndeterminate { get; }

    public string GrubPatchNote { get; } = "";
    public bool HasGrubPatchNote => !string.IsNullOrEmpty(GrubPatchNote);

    public ObservableCollection<UsbDriveInfo> Drives { get; } =
    [
        new UsbDriveInfo(1, "SanDisk Ultra USB 3.0", 32_010_928_128, @"\\.\PHYSICALDRIVE1"),
        new UsbDriveInfo(2, "Kingston DataTraveler", 15_502_147_584, @"\\.\PHYSICALDRIVE2"),
    ];

    // Settable: the ListBox binds SelectedDrive TwoWay.
    public UsbDriveInfo? SelectedDrive { get; set; }

    public ICommand RefreshDrivesCommand { get; } = new DesignCommand();
    public ICommand WriteCommand { get; } = new DesignCommand();
    public ICommand WriteCancelCommand { get; } = new DesignCommand();

    public UsbWriterDesignData() => SelectedDrive = Drives[0];
}

/// <summary>Design-time stand-in for <see cref="WelcomeViewModel"/>.</summary>
public sealed class WelcomeDesignData
{
    // Labels and ids copied from WelcomeViewModel, so the preview shows the real quiz.
    public IReadOnlyList<QuizOption> UseOptions { get; } =
    [
        new(DistroRecommender.UseEveryday, "Everyday & web"),
        new(DistroRecommender.UseGaming,   "Gaming"),
        new(DistroRecommender.UseWork,     "Work & school"),
        new(DistroRecommender.UseTinker,   "Tinkering & code"),
    ];

    public IReadOnlyList<QuizOption> StyleOptions { get; } =
    [
        new(DistroRecommender.StyleFamiliar, "Familiar, like Windows"),
        new(DistroRecommender.StyleFresh,    "Fresh & modern"),
    ];

    public IReadOnlyList<QuizOption> UpdateOptions { get; } =
    [
        new(DistroRecommender.UpdatesStable, "Rock-solid stable"),
        new(DistroRecommender.UpdatesLatest, "Latest & greatest"),
    ];

    // Settable: each RadioButton group binds its selection TwoWay.
    public string SelectedUse { get; set; } = DistroRecommender.UseEveryday;
    public string SelectedStyle { get; set; } = DistroRecommender.StyleFamiliar;
    public string SelectedUpdates { get; set; } = DistroRecommender.UpdatesStable;
}
