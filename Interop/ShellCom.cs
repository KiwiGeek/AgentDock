using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace AgentDock.Interop;

// Shell COM interfaces, declared with source-generated COM interop (Native AOT compatible).
// Only the vtable slots we call need real signatures, but every slot before them must be
// declared in order.

[GeneratedComInterface, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
internal partial interface IShellItem
{
    void BindToHandler(nint bindContext, in Guid handler, in Guid riid, out IEnumShellItems result);
    void GetParent(out nint parent);
    void GetDisplayName(uint sigdn, out nint name);
}

[GeneratedComInterface, Guid("70629033-e363-4a28-a567-0db78006e6d7")]
internal partial interface IEnumShellItems
{
    [PreserveSig]
    int Next(uint count, out IShellItem? item, out uint fetched);
}

[GeneratedComInterface, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
internal partial interface IShellItemImageFactory
{
    [PreserveSig]
    int GetImage(SIZE size, int flags, out nint hbitmap);
}

[GeneratedComInterface, Guid("92ca9dcd-5622-4bba-a805-5e9f541bd8c9")]
internal partial interface IObjectArray
{
    void GetCount(out uint count);
    void GetAt(uint index, in Guid riid, out nint item);
}

[GeneratedComInterface, Guid("5632b1a4-e38a-400a-928a-d4cd63230295")]
internal partial interface IObjectCollection : IObjectArray
{
    void AddObject(IShellLinkW item);
    void AddFromArray(IObjectArray source);
    void RemoveObjectAt(uint index);
    void Clear();
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16), Guid("6332debf-87b5-4670-90c0-5e57b408a49e")]
internal partial interface ICustomDestinationList
{
    void SetAppID(string appId);
    void BeginList(out uint minSlots, in Guid riid, out nint removedItems);
    [PreserveSig]
    int AppendCategory(string category, IObjectArray items);
    void AppendKnownCategory(int category);
    void AddUserTasks(IObjectArray tasks);
    void CommitList();
    void GetRemovedDestinations(in Guid riid, out nint items);
    void DeleteList(string? appId);
    void AbortList();
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16), Guid("000214f9-0000-0000-c000-000000000046")]
internal partial interface IShellLinkW
{
    void GetPath(nint file, int cch, nint findData, uint flags);
    void GetIDList(out nint pidl);
    void SetIDList(nint pidl);
    void GetDescription(nint buffer, int cch);
    void SetDescription(string description);
    void GetWorkingDirectory(nint buffer, int cch);
    void SetWorkingDirectory(string directory);
    void GetArguments(nint buffer, int cch);
    void SetArguments(string arguments);
    void GetHotkey(out ushort hotkey);
    void SetHotkey(ushort hotkey);
    void GetShowCmd(out int showCmd);
    void SetShowCmd(int showCmd);
    void GetIconLocation(nint buffer, int cch, out int index);
    void SetIconLocation(string path, int index);
    void SetRelativePath(string path, uint reserved);
    void Resolve(nint hwnd, uint flags);
    void SetPath(string path);
}

[StructLayout(LayoutKind.Sequential)]
internal struct PROPERTYKEY
{
    public Guid fmtid;
    public uint pid;
}

/// <summary>Just enough of PROPVARIANT to carry a string (VT_LPWSTR).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PROPVARIANT
{
    public ushort vt;
    public ushort reserved1, reserved2, reserved3;
    public nint value;
    public nint value2;
}

[GeneratedComInterface, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
internal partial interface IPropertyStore
{
    void GetCount(out uint count);
    void GetAt(uint index, out PROPERTYKEY key);
    void GetValue(in PROPERTYKEY key, out PROPVARIANT value);
    void SetValue(in PROPERTYKEY key, in PROPVARIANT value);
    void Commit();
}

internal static partial class ShellCom
{
    public static readonly Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    public static readonly Guid IID_IEnumShellItems = new("70629033-e363-4a28-a567-0db78006e6d7");
    public static readonly Guid IID_IShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");
    public static readonly Guid IID_IObjectArray = new("92ca9dcd-5622-4bba-a805-5e9f541bd8c9");
    public static readonly Guid IID_IObjectCollection = new("5632b1a4-e38a-400a-928a-d4cd63230295");
    public static readonly Guid IID_ICustomDestinationList = new("6332debf-87b5-4670-90c0-5e57b408a49e");
    public static readonly Guid IID_IShellLinkW = new("000214f9-0000-0000-c000-000000000046");

    public static readonly Guid CLSID_DestinationList = new("77f10cf0-3db5-4966-b520-b7c54fd35ed6");
    public static readonly Guid CLSID_EnumerableObjectCollection = new("2d3468c1-36a7-43b6-ac24-d3f02fd9607a");
    public static readonly Guid CLSID_ShellLink = new("00021401-0000-0000-c000-000000000046");

    public static readonly Guid BHID_EnumItems = new("94f60519-2850-4924-aa5a-d15e84868039");
    public static readonly PROPERTYKEY PKEY_Title = new() { fmtid = new("f29f85e0-4ff9-1068-ab91-08002b27b3d9"), pid = 2 };

    public const uint SIGDN_NORMALDISPLAY = 0;
    public const uint SIGDN_PARENTRELATIVEPARSING = 0x80018001;
    public const int SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_ICONONLY = 0x4;
    public const uint CLSCTX_INPROC_SERVER = 1;
    public const ushort VT_LPWSTR = 31;

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SHCreateItemFromParsingName(string path, nint bindContext, in Guid riid, out IShellItem item);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16, EntryPoint = "SHCreateItemFromParsingName")]
    public static partial int SHCreateImageFactory(string path, nint bindContext, in Guid riid, out IShellItemImageFactory item);

    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid riid, out ICustomDestinationList result);

    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid riid, out IObjectCollection result);

    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid riid, out IShellLinkW result);

    /// <summary>Reads a shell item's display name and frees the native string.</summary>
    public static string? DisplayName(IShellItem item, uint sigdn)
    {
        try
        {
            item.GetDisplayName(sigdn, out var native);
            try { return Marshal.PtrToStringUni(native); }
            finally { Marshal.FreeCoTaskMem(native); }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }
}
