using System.Runtime.InteropServices;

namespace Hdiff.UI.Worker;

/// <summary>
/// Minimal early-bound portion of HwpObject.tlb needed for a memory-only text
/// scan. The interface GUID and DISPIDs are from the Hancom HwpObject type
/// library. Keeping this small declaration in source avoids shipping a
/// machine-specific PIA while still allowing C# to marshal GetText's out BSTR.
/// </summary>
[ComImport]
[Guid("5E6A8276-CF1C-42B8-BCED-319548B02AF6")]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
internal interface IHwpTextScanner
{
    [DispId(10017)]
    [return: MarshalAs(UnmanagedType.VariantBool)]
    bool InitScan(
        [MarshalAs(UnmanagedType.Struct)] object? option,
        [MarshalAs(UnmanagedType.Struct)] object? range,
        [MarshalAs(UnmanagedType.Struct)] object? startParagraph,
        [MarshalAs(UnmanagedType.Struct)] object? startPosition,
        [MarshalAs(UnmanagedType.Struct)] object? endParagraph,
        [MarshalAs(UnmanagedType.Struct)] object? endPosition);

    [DispId(10018)]
    void ReleaseScan();

    [DispId(10019)]
    int GetText([MarshalAs(UnmanagedType.BStr)] out string text);
}
