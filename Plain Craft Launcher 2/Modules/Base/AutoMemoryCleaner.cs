using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.App;
using PCL.Core.Utils.OS;

namespace PCL;

/// <summary>
///     智能内存清理：按「内存占用阈值」或「时间间隔」自动清理进程工作集。
/// </summary>
/// <remarks>
///     <para>
///         设计取舍：后台任务只用 <see cref="MemoryInterop.OptimizeAllProcesses" />（仅清理工作集），
///         不使用需要管理员权限的 <see cref="MemoryInterop.PerformSystemMemoryOptimize" />。
///         后者会清空系统待机列表并影响所有进程，效果虽强但侵入性高，不适合定时自动执行。
///     </para>
///     <para>
///         为什么必须限制频率：清理工作集的本质是把进程的物理内存页换出到页面文件，
///         之后这些页被访问时会触发缺页中断，进程会变慢。过于频繁的清理会让整个系统
///         陷入「清理 → 换入 → 再清理」的抖动，反而降低性能，因此设置了最小间隔与空闲检测。
///     </para>
/// </remarks>
public static partial class AutoMemoryCleaner
{
    /// <summary>检查周期（秒）。不需要更密，最小清理间隔以分钟计。</summary>
    private const int TickSeconds = 30;

    /// <summary>两次清理之间的最小间隔（秒），防止配置不当导致频繁清理。</summary>
    private const int MinIntervalSeconds = 60;

    /// <summary>执行清理前要求用户空闲的时长（秒），避免在操作过程中造成卡顿。</summary>
    private const int RequiredIdleSeconds = 60;

    private static readonly object Sync = new();
    private static Timer? _timer;
    private static DateTime _lastClean = DateTime.MinValue;
    private static DateTime _lastSkipLog = DateTime.MinValue;
    private static volatile bool _running;

    /// <summary>最近一次自动清理的时间，从未清理过则为 <see cref="DateTime.MinValue" />。</summary>
    public static DateTime LastCleanTime
    {
        get
        {
            lock (Sync) return _lastClean;
        }
    }

    /// <summary>
    ///     按当前配置启动或停止定时器。配置变更后应再次调用以应用新设置。
    /// </summary>
    public static void Apply()
    {
        lock (Sync)
        {
            if (!Config.Tool.AutoMemoryCleanEnabled)
            {
                _timer?.Dispose();
                _timer = null;
                Common.Log("[AutoMemoryClean] 未启用，已停止");
                return;
            }

            _timer ??= new Timer(_ => Tick(), null, TickSeconds * 1000, TickSeconds * 1000);
            Common.Log(
                $"[AutoMemoryClean] 已启用，模式={Config.Tool.AutoMemoryCleanMode}，" +
                $"阈值={Config.Tool.AutoMemoryCleanThreshold}%，" +
                $"间隔={Config.Tool.AutoMemoryCleanInterval} 分钟");
        }
    }

    /// <summary>停止定时器（应用退出时调用）。</summary>
    public static void Stop()
    {
        lock (Sync)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    private static void Tick()
    {
        if (_running)
            return;

        try
        {
            if (!Config.Tool.AutoMemoryCleanEnabled)
                return;

            var now = DateTime.UtcNow;

            // 最小间隔保护：无论如何两次清理至少相隔 1 分钟
            if ((now - LastCleanTime).TotalSeconds < MinIntervalSeconds)
                return;

            var mode = Config.Tool.AutoMemoryCleanMode;
            bool shouldClean;
            string reason;

            if (mode == 1)
            {
                // 按时间间隔
                var intervalMinutes = Math.Clamp(Config.Tool.AutoMemoryCleanInterval, 1, 720);
                var elapsed = (now - LastCleanTime).TotalMinutes;
                shouldClean = elapsed >= intervalMinutes;
                reason = $"已过 {elapsed:0.0} 分钟（阈值 {intervalMinutes} 分钟）";
            }
            else
            {
                // 按物理内存占用百分比
                var threshold = Math.Clamp(Config.Tool.AutoMemoryCleanThreshold, 50, 95);
                var total = MemoryInterop.GetTotalPhysicalMemory();
                var available = MemoryInterop.GetAvailablePhysicalMemory();
                if (total == 0)
                    return;

                var usedPercent = (total - available) * 100d / total;
                shouldClean = usedPercent >= threshold;
                reason = $"内存占用 {usedPercent:0.0}%（阈值 {threshold}%）";
            }

            if (!shouldClean)
                return;

            // 用户正在操作时跳过，下次再试，避免清理造成的瞬时卡顿影响体验
            if (!IsUserIdle(RequiredIdleSeconds))
            {
                if ((now - _lastSkipLog).TotalMinutes >= 5)
                {
                    _lastSkipLog = now;
                    Common.Log("[AutoMemoryClean] 已满足清理条件，但用户正在操作，本次跳过");
                }

                return;
            }

            _running = true;
            Common.Log($"[AutoMemoryClean] 开始清理：{reason}");

            Task.Run(() =>
            {
                try
                {
                    var before = (long)MemoryInterop.GetAvailablePhysicalMemory();
                    var result = MemoryInterop.OptimizeAllProcesses();
                    var freed = result.FreedBytes > 0
                        ? result.FreedBytes
                        : Math.Max(0, (long)MemoryInterop.GetAvailablePhysicalMemory() - before);

                    lock (Sync) _lastClean = DateTime.UtcNow;

                    Common.Log(
                        $"[AutoMemoryClean] 清理完成：处理 {result.Handled} 个进程，" +
                        $"跳过 {result.Skipped} 个，释放约 {freed / 1024d / 1024d:0.0} MB");
                }
                catch (Exception ex)
                {
                    Common.Log(ex, "[AutoMemoryClean] 自动清理失败");
                }
                finally
                {
                    _running = false;
                }
            });
        }
        catch (Exception ex)
        {
            _running = false;
            Common.Log(ex, "[AutoMemoryClean] 定时检查异常");
        }
    }

    #region 空闲检测

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    // 主程序工程未启用 unsafe，LibraryImport 的源生成无法使用，这里用传统 DllImport
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo lastInputInfo);

    /// <summary>
    ///     距上一次键盘或鼠标输入是否已超过指定时长。
    /// </summary>
    private static bool IsUserIdle(int seconds)
    {
        try
        {
            var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
            if (!GetLastInputInfo(ref info))
                return false;

            // Environment.TickCount 为 int 且会回绕，转为 uint 后相减可正确处理回绕
            var idleMs = unchecked((uint)Environment.TickCount - info.Time);
            return idleMs >= (uint)seconds * 1000;
        }
        catch
        {
            // 检测失败时保守处理：视为非空闲，跳过本次清理
            return false;
        }
    }

    #endregion
}
