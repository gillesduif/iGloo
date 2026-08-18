using System.Runtime.InteropServices;

namespace Igloo.Preflight;

// Minimal hand-written interop for Shell.Application. tlbimp is unavailable under
// the .NET SDK build (MSB4803), so these replace the generated interop assembly.
//
// Every interface is IsIDispatch on purpose. All three are dual in the type library,
// so QueryInterface succeeds and the CLR then dispatches by name - which is the only
// reason a three-member subset is safe. Leave the attribute off and the default is
// dual: the CLR builds a vtable from these members alone and calls land on the wrong
// slots, silently. IIDs read from HKCR\Interface, not from documentation.

[ComImport]
[Guid("D8F015C0-C278-11CE-A49E-444553540000")]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
internal interface IShellDispatch
{
    [return: MarshalAs(UnmanagedType.IDispatch)]
    object Windows();
}

[ComImport]
[Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85")]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
internal interface IShellWindows
{
    int Count { get; }

    [return: MarshalAs(UnmanagedType.IDispatch)]
    object Item([MarshalAs(UnmanagedType.Struct)] object index);
}

[ComImport]
[Guid("D30C1661-CDAF-11D0-8A3E-00C04FC9E26E")]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
internal interface IWebBrowser2
{
    string LocationURL { get; }

    void Quit();
}
