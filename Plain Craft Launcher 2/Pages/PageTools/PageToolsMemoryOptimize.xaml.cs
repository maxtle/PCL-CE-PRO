using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PCL.Core.App;
using PCL.Core.App.Essentials;
using PCL.Core.App.Localization;
using PCL.Core.Utils.OS;

namespace PCL;

public partial class PageToolsMemoryOptimize : IPageView
{
    private static readonly int[] IntervalMinutes = [5, 10, 15, 30, 60, 120];

    /// <summary>初始化或按配置回填控件时为 true，避免把回填动作当成用户修改写回配置。</summary>
    private bool _autoInitializing;

    public PageToolsMemoryOptimize()
    {
        InitializeComponent();
        InitAutoCleanControls();
        RefreshMemoryInfo();
    }

    public void OnNavigatedTo(object? parameter)
    {
        RefreshMemoryInfo();
        UpdateAutoStatus();
    }

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
    #region 智能清理

    private void InitAutoCleanControls()
    {
        _autoInitializing = true;
        try
        {
            CheckAutoClean.Checked = Config.Tool.AutoMemoryCleanEnabled;

            ComboAutoMode.SelectedIndex = Math.Clamp(Config.Tool.AutoMemoryCleanMode, 0, 1);

            // 滑块本身范围 0–45，实际阈值 = 50 + Value，从而把可选区间限制在 50%–95%
            SliderThreshold.Value =
                Math.Clamp(Config.Tool.AutoMemoryCleanThreshold, 50, 95) - 50;

            ComboInterval.ItemsSource = IntervalMinutes
                .Select(minutes => Lang.Text("Tools.MemoryOptimize.Auto.Minutes", minutes))
                .ToList();
            var intervalIndex = Array.IndexOf(IntervalMinutes, Config.Tool.AutoMemoryCleanInterval);
            ComboInterval.SelectedIndex = intervalIndex >= 0 ? intervalIndex : Array.IndexOf(IntervalMinutes, 30);

            UpdateAutoVisibility();
            UpdateThresholdLabel();
            UpdateAutoStatus();
        }
        finally
        {
            _autoInitializing = false;
        }
    }

    private void UpdateAutoVisibility()
    {
        var enabled = CheckAutoClean.Checked == true;
        PanAutoOptions.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;

        var byInterval = ComboAutoMode.SelectedIndex == 1;
        PanThreshold.Visibility = byInterval ? Visibility.Collapsed : Visibility.Visible;
        PanInterval.Visibility = byInterval ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateThresholdLabel()
    {
        // Value 为 0–45，显示时加回 50
        LabThreshold.Text = Lang.Text("Tools.MemoryOptimize.Auto.ThresholdValue", 50 + SliderThreshold.Value);
    }

    private void UpdateAutoStatus()
    {
        if (CheckAutoClean.Checked != true)
        {
            LabAutoStatus.Text = "";
            return;
        }

        var last = AutoMemoryCleaner.LastCleanTime;
        LabAutoStatus.Text = last == DateTime.MinValue
            ? Lang.Text("Tools.MemoryOptimize.Auto.NeverCleaned")
            : Lang.Text("Tools.MemoryOptimize.Auto.LastClean", last.ToLocalTime().ToString("HH:mm:ss"));
    }

    private void CheckAutoClean_Change(object senderRaw, bool user)
    {
        if (_autoInitializing)
            return;

        var enabled = CheckAutoClean.Checked == true;
        Config.Tool.AutoMemoryCleanEnabled = enabled;
        UpdateAutoVisibility();
        UpdateAutoStatus();
        AutoMemoryCleaner.Apply();
        Common.Log($"[AutoMemoryClean] 用户{(enabled ? "启用" : "关闭")}智能清理");
    }

    private void ComboAutoMode_Change(object senderRaw, SelectionChangedEventArgs e)
    {
        if (_autoInitializing || ComboAutoMode.SelectedIndex < 0)
            return;

        Config.Tool.AutoMemoryCleanMode = ComboAutoMode.SelectedIndex;
        UpdateAutoVisibility();
        AutoMemoryCleaner.Apply();
    }

    private void SliderThreshold_Change(object senderRaw, bool user)
    {
        UpdateThresholdLabel();
        if (_autoInitializing)
            return;

        Config.Tool.AutoMemoryCleanThreshold = 50 + SliderThreshold.Value;
        AutoMemoryCleaner.Apply();
    }

    private void ComboInterval_Change(object senderRaw, SelectionChangedEventArgs e)
    {
        if (_autoInitializing || ComboInterval.SelectedIndex < 0)
            return;

        Config.Tool.AutoMemoryCleanInterval = IntervalMinutes[ComboInterval.SelectedIndex];
        AutoMemoryCleaner.Apply();
    }

    #endregion

}
