using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Igloo.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Igloo.Preflight;

/// <summary>
/// Reads the Windows desktop layout - per monitor resolution, refresh rate, rotation
/// and position - so the first-boot agent can reproduce it on Linux.
/// </summary>
/// <remarks>
/// Why this exists: a portrait monitor coming up landscape, or a 144 Hz panel stuck at
/// 60, is one of the first things a switcher notices, and it reads as "Linux is broken"
/// rather than "a setting did not carry over".
///
/// The interesting problem is IDENTITY. Windows names displays <c>\\.\DISPLAY1</c>,
/// Linux names them <c>DP-1</c> / <c>HDMI-A-1</c>, and neither order is stable across
/// boots - so matching by index would eventually rotate the wrong screen. What both
/// sides can see is the monitor's own EDID, and Windows exposes its identifying part in
/// the monitor device id (<c>MONITOR\GSM5B09\...</c>): a three-letter PnP manufacturer
/// code plus a product code. Linux can derive the same pair from
/// <c>/sys/class/drm/*/edid</c>, which makes the match reliable.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class DisplayLayoutReader
{
    public static IReadOnlyList<DisplayInfo> Read(ILogger logger)
    {
        var displays = new List<DisplayInfo>();
        try
        {
            // Scale lives on the HMONITOR, not in EnumDisplayDevices - read it up
            // front, keyed by the same GDI device name the adapter loop below uses.
            var scaleByDevice = ReadScaleFactors(logger);

            var adaptersSeen = 0;
            for (uint adapterIndex = 0; ; adapterIndex++)
            {
                var adapter = new DisplayDevice { cb = DisplayDeviceSize };
                if (!EnumDisplayDevicesW(null, adapterIndex, ref adapter, 0))
                    break;
                adaptersSeen++;

                // Skip adapters that are not part of the desktop (mirroring drivers,
                // detached outputs): they have no layout worth carrying over.
                const int AttachedToDesktop = 0x00000001;
                if ((adapter.StateFlags & AttachedToDesktop) == 0)
                    continue;

                var deviceName = adapter.DeviceName;

                var mode = new DevMode { dmSize = (ushort)DevModeSize };
                const int EnumCurrentSettings = -1;
                if (!EnumDisplaySettingsW(deviceName, EnumCurrentSettings, ref mode))
                {
                    logger.LogWarning("Could not read current settings for {Device}", deviceName);
                    continue;
                }

                const int PrimaryDevice = 0x00000004;
                displays.Add(new DisplayInfo
                {
                    PnpId = ReadMonitorPnpId(deviceName),
                    WidthPx = (int)mode.dmPelsWidth,
                    HeightPx = (int)mode.dmPelsHeight,
                    RefreshHz = (int)mode.dmDisplayFrequency,
                    // DMDO_DEFAULT/90/180/270 are 0..3; express as real degrees so the
                    // Linux side does not have to know the Windows enum.
                    RotationDegrees = (int)mode.dmDisplayOrientation * 90,
                    PositionX = mode.dmPositionX,
                    PositionY = mode.dmPositionY,
                    ScalePercent = scaleByDevice.TryGetValue(deviceName, out var scale) ? scale : 0,
                    IsPrimary = (adapter.StateFlags & PrimaryDevice) != 0,
                });
            }

            // Report the empty case loudly. Silence here is what let this feature fail
            // completely for weeks: the layout simply never arrived, and neither the
            // Windows log nor the Linux agent had anything to say about why.
            if (displays.Count == 0)
                logger.LogWarning(
                    "No displays captured ({Seen} adapter(s) enumerated) - the desktop layout "
                    + "will not be migrated", adaptersSeen);

            foreach (var d in displays)
                logger.LogInformation(
                    "Display {Pnp}: {W}x{H}@{Hz}Hz rot={Rot} at ({X},{Y}) scale={Scale}%{Primary}",
                    d.PnpId ?? "unknown", d.WidthPx, d.HeightPx, d.RefreshHz,
                    d.RotationDegrees, d.PositionX, d.PositionY,
                    d.ScalePercent > 0 ? d.ScalePercent : 100,
                    d.IsPrimary ? " PRIMARY" : "");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException or OverflowException)
        {
            logger.LogWarning(ex, "Display layout read failed (non-fatal) - the desktop "
                                  + "layout will not be migrated");
        }
        return displays;
    }

    /// <summary>
    /// Returns the monitor's PnP id (e.g. "GSM5B09") for <paramref name="adapterDeviceName"/>,
    /// which is the part of the EDID Linux can also see.
    /// </summary>
    private static string? ReadMonitorPnpId(string adapterDeviceName)
    {
        var monitor = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
        if (!EnumDisplayDevicesW(adapterDeviceName, 0, ref monitor, 0))
            return null;

        // DeviceID looks like: MONITOR\GSM5B09\{4d36e96e-...}\0001
        var parts = (monitor.DeviceID ?? string.Empty).Split('\\');
        return parts.Length >= 2 && parts[1].Length > 0 ? parts[1] : null;
    }

    /// <summary>
    /// Maps each GDI device name (<c>\\.\DISPLAYn</c>) to its Windows display scaling
    /// in percent. EnumDisplayDevices has no scale information; scale lives on the
    /// HMONITOR, reached via EnumDisplayMonitors + GetMonitorInfoEx - whose szDevice
    /// is the same device name the adapter loop uses, so the two join up.
    /// GetScaleFactorForMonitor returns the SCALE_FACTOR enum, whose values ARE the
    /// percent (100/125/150/...). Missing entries mean "unknown" - callers treat
    /// them as 100.
    /// </summary>
    private static Dictionary<string, int> ReadScaleFactors(ILogger logger)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMonitor, _, _, _) =>
            {
                var info = new MonitorInfoEx { cbSize = Marshal.SizeOf<MonitorInfoEx>() };
                if (!GetMonitorInfoW(hMonitor, ref info) || string.IsNullOrEmpty(info.szDevice))
                    return true; // keep enumerating
                // HRESULT 0 = S_OK. The enum value is the scale in percent.
                if (GetScaleFactorForMonitor(hMonitor, out var scale) == 0 && scale > 0)
                    map[info.szDevice] = scale;
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or Win32Exception)
        {
            logger.LogWarning(ex, "Could not read per-monitor scale factors - assuming 100%");
        }
        return map;
    }

    //   Struct sizes
    // Both Win32 structs are size-prefixed: the API validates the caller-supplied cb /
    // dmSize and refuses the call if it disagrees with the version it expects.
    //
    // Marshal.SizeOf is correct here BECAUSE the strings are ByValTStr - the marshaller
    // understands those, so the size it reports is the size it actually marshals. That
    // agreement is the whole point; it did not hold when these were inline arrays, and
    // the mismatch silently produced an empty display list (see the struct definitions).
    // The tests pin the expected values so a layout change cannot pass unnoticed.
    private static readonly int DisplayDeviceSize = Marshal.SizeOf<DisplayDevice>();   // 840
    private static readonly int DevModeSize = Marshal.SizeOf<DevMode>();               // 220

    //   P/Invoke

    // DllImport rather than LibraryImport, deliberately: both structs embed fixed-size
    // strings, which the source generator cannot marshal (SYSLIB1051). The alternative
    // it suggests - DisableRuntimeMarshalling - is an ASSEMBLY-wide switch that would
    // change how every other P/Invoke in this project marshals, to serve two calls.
    // Classic runtime marshalling is still fully supported and is the right tool here.
    [DllImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", SetLastError = true,
        CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevicesW(
        string? device, uint deviceIndex, ref DisplayDevice displayDevice, uint flags);

    [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", SetLastError = true,
        CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettingsW(string deviceName, int modeNum, ref DevMode devMode);

    // Per-monitor scale enumeration. The callback is a plain delegate - classic
    // runtime marshalling handles it, and the call is synchronous so nothing has to
    // be kept alive beyond it.
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(
        IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MonitorInfoEx lpmi);

    // SHCore. Returns an HRESULT; the out value is a SCALE_FACTOR enum whose numeric
    // values are the scale in percent (100, 125, 150, ...).
    [DllImport("SHCore.dll")]
    private static extern int GetScaleFactorForMonitor(IntPtr hMon, out int deviceScaleFactor);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    // MONITORINFOEXW. Only szDevice is consumed (it is the same "\\.\DISPLAYn" name
    // EnumDisplayDevices reports), but the full struct must be marshalled correctly
    // for cbSize validation to pass.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    // ByValTStr, NOT [InlineArray].
    //
    // Inline arrays give the right RUNTIME layout but the interop marshaller does not
    // understand them: it sizes each one as a single element, making the marshalled
    // struct 424 bytes where Win32 writes 840. Every field after the first string then
    // lands at the wrong offset - StateFlags in particular read as garbage, so no
    // adapter ever tested as attached to the desktop and the reader returned an empty
    // list without a single error anywhere.
    //
    // ByValTStr is the marshaller's own mechanism for inline fixed-size strings, so
    // Marshal.SizeOf, the field offsets and the copy-back all agree with the Win32
    // definition. It requires classic DllImport (LibraryImport's generator rejects it),
    // which is why these P/Invokes stay DllImport.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    /// <remarks>
    /// The union in DEVMODE overlaps the printer fields with dmPosition/dmDisplayOrientation;
    /// laid out explicitly here so the display members land on the right offsets.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }
}
