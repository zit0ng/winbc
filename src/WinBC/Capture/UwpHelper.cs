using System.Runtime.InteropServices;

namespace WinBC.Capture;

/// <summary>读取 UWP/商店应用的 AUMID（用于开机后用 explorer shell:AppsFolder\AUMID 拉起）。</summary>
internal static class UwpHelper
{
    private static readonly Guid IID_IPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    private static readonly Guid FMTID_AppUserModel = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    private const uint PID_AppUserModel_ID = 5;
    private const ushort VT_LPWSTR = 31;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(2)] public ushort r1;
        [FieldOffset(4)] public ushort r2;
        [FieldOffset(6)] public ushort r3;
        [FieldOffset(8)] public IntPtr pointerValue;
        [FieldOffset(8)] public long longValue;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PROPERTYKEY pkey);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
        [PreserveSig] int Commit();
    }

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore propertyStore);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pvar);

    /// <summary>获取窗口的 AppUserModelID；失败返回 null。</summary>
    public static string? TryGetAppUserModelId(IntPtr hwnd)
    {
        IPropertyStore? store = null;
        try
        {
            var iid = IID_IPropertyStore;
            int hr = SHGetPropertyStoreForWindow(hwnd, ref iid, out store);
            if (hr != 0 || store is null) return null;

            var key = new PROPERTYKEY { fmtid = FMTID_AppUserModel, pid = PID_AppUserModel_ID };
            hr = store.GetValue(ref key, out PROPVARIANT pv);
            if (hr != 0) return null;
            try
            {
                if (pv.vt != VT_LPWSTR || pv.pointerValue == IntPtr.Zero) return null;
                return Marshal.PtrToStringUni(pv.pointerValue);
            }
            finally
            {
                PropVariantClear(ref pv);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (store is not null) Marshal.ReleaseComObject(store);
        }
    }

    /// <summary>把 AUMID 转成可以用 explorer.exe 打开的 shell 目标。</summary>
    public static string ToShellTarget(string aumid) => $@"shell:AppsFolder\{aumid}";
}
