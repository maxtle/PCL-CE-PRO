using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using PCL.Core.App.Essentials;
using PCL.Core.App.Localization;
using PCL.Core.Utils.OS;

namespace PCL;

public partial class PageToolsMemoryOptimize : IPageView
{
    public PageToolsMemoryOptimize()
    {
        InitializeComponent();
        RefreshMemoryInfo();
    }

    public void OnNavigatedTo(object? parameter) => RefreshMemoryInfo();

    public void OnNavigatedFrom()
    {
    }

    #region 内存信息

    private static long SafeWorkingSet(Process process)
    {
        try
        {
            return process.WorkingSet64;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    ///     获取所有 Java 进程（Minecraft 运行在 java / javaw 上）。
    /// </summary>
    private static Process[] GetJavaProcesses()
    {
        try
        {
            return Process
                .GetProcesses()
                .Where(process =>
                {
                    try
                    {
                        var name = process.ProcessName;
                        return name.Equals("java", StringComparison.OrdinalIgnoreCase)
                               || name.Equals("javaw", StringComparison.OrdinalIgnoreCase);
                    }
                    catch
                    {
                        return false;
                    }
                })
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 MB";
        const double mb = 1024d * 1024d;
        return bytes >= mb * 1024
            ? $"{bytes / mb / 1024:0.00} GB"
            : $"{bytes / mb:0.0} MB";
    }

    private void RefreshMemoryInfo()
    {
        try
        {
            // 物理内存整体占用（老版本内存优化针对的是系统物理内存，不仅限于 MC）
            var total = MemoryInterop.GetTotalPhysicalMemory();
            var available = MemoryInterop.GetAvailablePhysicalMemory();
            var used = total > available ? total - available : 0;
            var percent = total > 0 ? used * 100d / total : 0;
            LabPhysicalMemory.Text =
                $"{FormatBytes((long)used)} / {FormatBytes((long)total)}（{percent:0}%）";

            LabLauncherMemory.Text = FormatBytes(SafeWorkingSet(Process.GetCurrentProcess()));

            var javaProcesses = GetJavaProcesses();
            if (javaProcesses.Length == 0)
            {
                LabGameMemory.Text = Lang.Text("Tools.MemoryOptimize.NoGameRunning");
            }
            else
            {
                var javaTotal = javaProcesses.Sum(SafeWorkingSet);
                LabGameMemory.Text = $"{FormatBytes(javaTotal)}（{javaProcesses.Length}）";
            }
        }
        catch (Exception ex)
        {
            Common.Log(ex, "读取内存信息失败");
        }
    }

    #endregion

    #region 内存优化

    private async void BtnOptimize_Click(object sender, MouseButtonEventArgs e)
    {
        BtnOptimize.IsEnabled = false;
        LabResult.Text = Lang.Text("Tools.MemoryOptimize.Optimizing");
        try
        {
            var before = (long)MemoryInterop.GetAvailablePhysicalMemory();
            long freed;

            if (ProcessInterop.IsAdmin())
            {
                // 已具备管理员权限，直接执行系统级内存优化（老版本 PCL 的实现）
                await Task.Run(MemoryInterop.PerformSystemMemoryOptimize);
                freed = (long)MemoryInterop.GetAvailablePhysicalMemory() - before;
            }
            else if (await TryPromoteOptimizeAsync())
            {
                // 通过 PCL 提权服务在管理员进程中完成优化
                freed = (long)MemoryInterop.GetAvailablePhysicalMemory() - before;
            }
            else
            {
                // 提权被拒绝或失败，降级为普通权限下的工作集清理
                Common.Log("[MemoryOptimize] 提权未成功，降级为普通权限优化");
                var fallback = await Task.Run(MemoryInterop.OptimizeAllProcesses);
                freed = fallback.FreedBytes;
            }
            RefreshMemoryInfo();
            LabResult.Text = Lang.Text("Tools.MemoryOptimize.Result", FormatBytes(freed));
            Common.Log($"[MemoryOptimize] 优化完成，物理内存释放约 {FormatBytes(freed)}");
        }
        catch (Exception ex)
        {
            Common.Log(ex, "内存优化失败", Common.LogLevel.Feedback,
                userSummary: Lang.Text("Tools.MemoryOptimize.Failed"));
            LabResult.Text = Lang.Text("Tools.MemoryOptimize.Failed");
        }
        finally
        {
            BtnOptimize.IsEnabled = true;
        }
    }

    /// <summary>
    ///     通过 PCL 提权服务在管理员进程中执行内存优化。
    /// </summary>
    /// <returns>提权进程返回 <c>ok</c> 时为 <c>true</c>。</returns>
    private static async Task<bool> TryPromoteOptimizeAsync()
    {
        var completion = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        PromoteService.Append("memory-optimize", result => completion.TrySetResult(result));
        if (!PromoteService.Activate())
        {
            Common.Log("[MemoryOptimize] 提权进程启动失败");
            return false;
        }

        var finished = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(60)));
        if (finished != completion.Task)
        {
            Common.Log("[MemoryOptimize] 等待提权操作超时");
            return false;
        }

        var result = await completion.Task;
        Common.Log($"[MemoryOptimize] 提权进程返回：{result}");
        return result == "ok";
    }

    #endregion
}
