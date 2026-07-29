using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FluentAssertions;
using Xunit;

namespace Igloo.Preflight.Tests;

/// <summary>
/// Pins the interop layout DisplayLayoutReader depends on.
/// </summary>
/// <remarks>
/// DISPLAY_DEVICEW is size-prefixed and Win32 writes 840 bytes into it. What matters is
/// not just that the size is right, but that the MARSHALLER agrees with it - the copy
/// back into managed memory uses the marshalled layout, so a mismatch corrupts every
/// field after the first string.
///
/// That is what happened with [InlineArray]: the runtime layout was correct, but the
/// marshaller sized each inline array as one element, giving 424 bytes. StateFlags was
/// then read from the wrong offset, no adapter ever tested as attached to the desktop,
/// and the display layout arrived on the Linux side as an empty list - with nothing in
/// any log to explain it. ByValTStr is the marshaller's own mechanism for inline strings,
/// so the definition, Marshal.SizeOf and the copy-back all agree.
/// </remarks>
public class DisplayLayoutReaderTests
{
    // Mirror of the interop struct as DisplayLayoutReader declares it. A deliberate copy:
    // the original is private, and what is under test is how the MARSHALLER lays this out.
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

    [Fact]
    public void Marshalled_size_matches_Win32_DISPLAY_DEVICEW()
    {
        // 4 (cb) + 64 (32 wchar) + 256 (128 wchar) + 4 (flags) + 256 + 256 = 840
        Marshal.SizeOf<DisplayDevice>().Should().Be(840);
    }

    [Fact]
    public void StateFlags_sits_where_Win32_puts_it()
    {
        // The field the old layout corrupted. If this offset drifts, adapters stop
        // testing as attached to the desktop and the reader silently returns nothing -
        // so it is asserted directly rather than inferred from the total size.
        Marshal.OffsetOf<DisplayDevice>(nameof(DisplayDevice.StateFlags))
            .ToInt32().Should().Be(4 + 64 + 256);
    }

    [Fact]
    public void Inline_arrays_would_marshal_to_the_wrong_size()
    {
        // Documents the trap rather than leaving it to be rediscovered: an otherwise
        // identical struct built from [InlineArray] does NOT marshal to 840.
        Marshal.SizeOf<InlineArrayVariant>().Should().NotBe(840,
            "the marshaller sizes each [InlineArray] as a single element - use ByValTStr for interop");
    }

    [InlineArray(32)] private struct Char32 { private char _element0; }
    [InlineArray(128)] private struct Char128 { private char _element0; }

    [StructLayout(LayoutKind.Sequential)]
    private struct InlineArrayVariant
    {
        public int cb;
        public Char32 DeviceName;
        public Char128 DeviceString;
        public int StateFlags;
        public Char128 DeviceID;
        public Char128 DeviceKey;
    }
}
