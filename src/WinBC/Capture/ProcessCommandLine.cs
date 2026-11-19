using System.Runtime.InteropServices;
using System.Text;
using WinBC.Native;

namespace WinBC.Capture;

/// <summary>
/// 通过读取目标进程 PEB 拿命令行（比 WMI 快两个数量级，关机时也来得及）。
/// 读不到（权限不足/位数不匹配）时静默返回 null，由调用方降级到窗口标题推断。
/// </summary>
internal static class ProcessCommandLine
{
    private const int ProcessBasicInformation = 0;

    // 这些偏移依架构而定
    private static bool IsTargetX64(int pid)
    {
        IntPtr h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return true; // 拿不到就按本进程位宽猜
        try
        {
            if (NativeMethods.IsWow64Process2(h, out ushort machine, out _))
                return machine == 0; // 0 = IMAGE_FILE_MACHINE_UNKNOWN，表示与调用方同位数
            if (NativeMethods.IsWow64Process(h, out bool wow)) return !wow;
            return true;
        }
        catch
        {
            return true;
        }
        finally
        {
            NativeMethods.CloseHandle(h);
        }
    }

    /// <summary>返回进程命令行（含 exe 全路径），失败返回 null。</summary>
    public static string? TryGet(int pid)
    {
        if (pid <= 4) return null;
        bool is64;
        try { is64 = Environment.Is64BitProcess && IsTargetX64(pid); }
        catch { is64 = Environment.Is64BitProcess; }

        IntPtr h = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_INFORMATION | NativeMethods.PROCESS_VM_READ, false, pid);
        if (h == IntPtr.Zero)
            h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION | NativeMethods.PROCESS_VM_READ, false, pid);
        if (h == IntPtr.Zero) return null;

        try
        {
            var pbi = new NativeMethods.PROCESS_BASIC_INFORMATION();
            int status = NativeMethods.NtQueryInformationProcess(h, ProcessBasicInformation,
                ref pbi, Marshal.SizeOf<NativeMethods.PROCESS_BASIC_INFORMATION>(), out _);
            if (status != 0 || pbi.PebBaseAddress == IntPtr.Zero) return null;

            // PEB->ProcessParameters：x64 偏移 0x20，x86 偏移 0x10
            int ppOffset = is64 ? 0x20 : 0x10;
            if (!NativeMethods.ReadProcessMemory(h, pbi.PebBaseAddress + ppOffset,
                    out IntPtr pp, (IntPtr)(is64 ? 8 : 4), out IntPtr read) || read == IntPtr.Zero)
                return null;
            if (pp == IntPtr.Zero) return null;

            // RTL_USER_PROCESS_PARAMETERS.CommandLine：x64 偏移 0x70，x86 偏移 0x40
            int cmdOffset = is64 ? 0x70 : 0x40;
            var us = new NativeMethods.UNICODE_STRING();
            int usSize = is64 ? 16 : 8;
            var raw = new byte[usSize];
            if (!NativeMethods.ReadProcessMemory(h, pp + cmdOffset, raw, (IntPtr)usSize, out read) || read == IntPtr.Zero)
                return null;

            us.Length = BitConverter.ToUInt16(raw, 0);
            us.MaximumLength = BitConverter.ToUInt16(raw, 2);
            us.Buffer = is64
                ? (IntPtr)BitConverter.ToInt64(raw, 8)
                : (IntPtr)BitConverter.ToInt32(raw, 4);

            if (us.Buffer == IntPtr.Zero || us.Length == 0 || us.Length > 0x8000) return null;

            var buf = new byte[us.Length];
            if (!NativeMethods.ReadProcessMemory(h, us.Buffer, buf, (IntPtr)buf.Length, out read) || read == IntPtr.Zero)
                return null;

            return Encoding.Unicode.GetString(buf).TrimEnd('\0');
        }
        catch
        {
            return null;
        }
        finally
        {
            NativeMethods.CloseHandle(h);
        }
    }
}
