using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using PCL.Core.App.Essentials;

namespace PCL.Core.Utils.OS;

/// <summary>
///     进程与系统内存操作相关的系统调用。
/// </summary>
public static partial class MemoryInterop
{
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessSetQuota = 0x0100;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyWorkingSet(IntPtr hProcess);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    #region 物理内存信息

    /// <summary>
    ///     获取当前可用的物理内存（字节）。
    /// </summary>
    public static ulong GetAvailablePhysicalMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? status.AvailPhys : 0;
    }

    /// <summary>
    ///     获取物理内存总量（字节）。
    /// </summary>
    public static ulong GetTotalPhysicalMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? status.TotalPhys : 0;
    }

    #endregion

    #region 系统级内存优化（老版本 PCL 的实现）

    // 系统内存列表操作命令（NtSetSystemInformation 的 SystemMemoryListInformation）
    private const int SystemMemoryListInformation = 80;

    /// <summary>清空所有进程的工作集。</summary>
    private const int MemoryEmptyWorkingSets = 2;

    /// <summary>刷新已修改页面列表。</summary>
    private const int MemoryFlushModifiedList = 3;

    /// <summary>清空系统待机内存列表（释放量最大的一项）。</summary>
    private const int MemoryPurgeStandbyList = 4;

    private const string PrivilegeProfileSingleProcess = "SeProfileSingleProcessPrivilege";
    private const uint SePrivilegeEnabled = 0x00000002;

    /// <summary>
    ///     LUID：由两个 4 字节字段组成，整体按 4 字节对齐。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public Luid LuidValue;
        public uint Attributes;
    }

    // 注意：PCL.Core 启用了 DisableRuntimeMarshalling，必须使用源生成的 LibraryImport，
    // 否则 SetLastError 会在运行期抛出 MarshalDirectiveException。
    [LibraryImport("ntdll.dll")]
    private static partial int NtSetSystemInformation(
        int systemInformationClass,
        IntPtr systemInformation,
        int systemInformationLength);

    [LibraryImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LookupPrivilegeValue(
        string? systemName, string name, out Luid luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AdjustTokenPrivileges(
        IntPtr tokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TokenPrivileges newState,
        int bufferLength,
        IntPtr previousState,
        IntPtr returnLength);

    /// <summary>
    ///     启用当前进程令牌中的指定特权。
    /// </summary>
    private static void EnablePrivilege(string privilegeName)
    {
        using var identity = WindowsIdentity.GetCurrent(
            TokenAccessLevels.Query | TokenAccessLevels.AdjustPrivileges);

        if (!LookupPrivilegeValue(null, privilegeName, out var luid))
            throw new InvalidOperationException(
                string.Format(CultureInfo.InvariantCulture,
                    "获取内存优化特权失败（错误代码：{0}）", Marshal.GetLastWin32Error()));

        var newState = new TokenPrivileges
        {
            PrivilegeCount = 1,
            LuidValue = luid,
            Attributes = SePrivilegeEnabled
        };

        Marshal.SetLastPInvokeError(0);
        var ok = AdjustTokenPrivileges(
            identity.Token,
            false,
            ref newState,
            Marshal.SizeOf<TokenPrivileges>(),
            IntPtr.Zero,
            IntPtr.Zero);

        // 特权可能因权限不足未被真正授予，此处必须检查 LastError
        var error = Marshal.GetLastWin32Error();
        if (!ok || error != 0)
            throw new InvalidOperationException(
                string.Format(CultureInfo.InvariantCulture,
                    "启用内存优化特权失败（错误代码：{0}）", error));
    }

    /// <summary>
    ///     执行系统级内存优化（老版本 PCL 内存优化的真实实现）。<br />
    ///     依次清空所有进程工作集、刷新已修改页面、清空系统待机列表，
    ///     因此物理内存占用可下降约三分之一，且影响范围不限于 Minecraft。<br />
    ///     需要管理员权限（依赖 <c>SeProfileSingleProcessPrivilege</c> 特权）。
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">当前进程没有管理员权限。</exception>
    public static void PerformSystemMemoryOptimize()
    {
        if (!ProcessInterop.IsAdmin())
            throw new UnauthorizedAccessException(
                "内存优化需要管理员权限。\r\n" +
                "可以右键 PCL，打开 属性 → 兼容性 → 以管理员身份运行此程序。");

        EnablePrivilege(PrivilegeProfileSingleProcess);

        for (var command = MemoryEmptyWorkingSets; command <= MemoryPurgeStandbyList; command++)
        {
            var value = command;
            var handle = GCHandle.Alloc(value, GCHandleType.Pinned);
            try
            {
                var status = NtSetSystemInformation(
                    SystemMemoryListInformation, handle.AddrOfPinnedObject(), sizeof(int));
                if (status != 0)
                    throw new InvalidOperationException(
                        string.Format(CultureInfo.InvariantCulture,
                            "内存优化操作 {0} 失败（NTSTATUS：0x{1:X8}）", command, status));
            }
            finally
            {
                handle.Free();
            }
        }
    }

    /// <summary>
    ///     内存优化提权操作：供 PCL 提权进程通过管道调用。
    /// </summary>
    /// <returns><c>ok</c> 表示成功，其余为错误描述。</returns>
    [PromoteOperation("memory-optimize")]
    public static string? OptimizeMemoryOperation(string? arg)
    {
        try
        {
            PerformSystemMemoryOptimize();
            return "ok";
        }
        catch (Exception ex)
        {
            return "error: " + ex.Message;
        }
    }

    #endregion

    #region 工作集清理（无管理员权限时的降级方案）

    /// <summary>
    ///     清理指定进程的工作集，将物理内存归还给系统。
    /// </summary>
    /// <param name="processId">目标进程 ID。</param>
    /// <returns>是否成功（系统关键进程通常会因权限不足而失败）。</returns>
    public static bool EmptyWorkingSetOfProcess(int processId)
    {
        if (processId <= 0)
            return false;

        var handle = OpenProcess(
            ProcessQueryInformation | ProcessSetQuota, false, (uint)processId);
        if (handle == IntPtr.Zero)
            return false;

        try
        {
            return EmptyWorkingSet(handle);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    ///     内存优化结果。
    /// </summary>
    /// <param name="Handled">成功清理的进程数。</param>
    /// <param name="Skipped">因权限不足等原因跳过的进程数。</param>
    /// <param name="FreedBytes">实际回收的物理内存（按可用物理内存差值计算）。</param>
    public readonly record struct OptimizeResult(int Handled, int Skipped, long FreedBytes);

    /// <summary>
    ///     遍历所有进程清理工作集。<br />
    ///     这是没有管理员权限时的降级方案，效果弱于 <see cref="PerformSystemMemoryOptimize" />。
    /// </summary>
    public static OptimizeResult OptimizeAllProcesses()
    {
        var before = GetAvailablePhysicalMemory();

        var handled = 0;
        var skipped = 0;
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch
        {
            return new OptimizeResult(0, 0, 0);
        }

        foreach (var process in processes)
        {
            try
            {
                if (EmptyWorkingSetOfProcess(process.Id))
                    handled++;
                else
                    skipped++;
            }
            catch
            {
                skipped++;
            }
            finally
            {
                process.Dispose();
            }
        }

        var after = GetAvailablePhysicalMemory();
        var freed = (long)after - (long)before;
        return new OptimizeResult(handled, skipped, freed > 0 ? freed : 0);
    }

    #endregion
}
