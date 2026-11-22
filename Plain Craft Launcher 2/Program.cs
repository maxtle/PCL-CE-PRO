using System.Diagnostics;
using System.Windows.Input;
using PCL.Core.App;
using PCL.Core.App.Essentials;
using PCL.Core.App.IoC;
using PCL.Core.Utils.OS;
using PCL.Core.Wpf;

namespace PCL;

internal static class Program
{
    /// <summary>
    ///     Program startup point
    /// </summary>
    [STAThread]
    public static void Main()
    {
        if (Basics.CommandLineArguments.Contains("--console")) KernelInterop.AllocateConsole();

        // 内存优化子进程：必须在单实例转发之前处理，
        // 否则会被当作重复启动而把参数转发给已有实例，导致优化根本不执行。
        if (Basics.CommandLineArguments.Length > 0 && Basics.CommandLineArguments[0] == "--memory")
        {
            try
            {
                var before = (long)MemoryInterop.GetAvailablePhysicalMemory();
                MemoryInterop.PerformSystemMemoryOptimize();
                var after = (long)MemoryInterop.GetAvailablePhysicalMemory();
                Environment.Exit((int)Math.Clamp((after - before) / 1024, 0, int.MaxValue));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                // 便于排查：GUI 子进程没有控制台，把错误落到临时文件
                try
                {
                    System.IO.File.WriteAllText(
                        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pcl-memory-optimize-error.txt"),
                        ex.ToString());
                }
                catch
                {
                    // 写日志失败不影响退出码
                }

                Environment.Exit(-1);
            }

            return;
        }
#if DEBUG
        if (Basics.CommandLineArguments.Contains("--debug"))
        {
            Console.WriteLine("Waiting for debugger...");
            while (!Debugger.IsAttached) Thread.Sleep(50);
        }
#endif
        // 上一次运行若请求过「重置本机设置」，必须在配置服务初始化之前处理：
        // 强杀/崩溃时 ProcessExit 兜底不会触发，只能靠标记文件在启动期补上。
        TryApplyPendingConfigReset();
        Console.WriteLine("Welcome to PCL-CE PRO!");
        // Preloading tasks
        ApplicationService.Loading = static () =>
        {
            var app = new Application();
            app.InitializeComponent();
            return new WpfApplicationHost(app);
        };
        // 注册 Core 弹窗服务的界面呈现器
        DialogPresenter.Install();
        HintPresenter.Install();
        MainWindowService.Loading = static () => new WpfWindowHost(new FormMain());
        // From dotnet/wpf #2393: fix tablet devices broken on .NET Core 3.0+

        _ = Tablet.TabletDevices;
        // PCL.Core 通过 PlatformServices 与 UI 解耦，需在生命周期启动前注册 WPF 实现
        PlatformRegistration.Register();
        // PCL.Core.Wpf 中的生命周期服务不在 PCL.Core 的编译单元内，需要显式注册
        LifecycleRegistration.Register();
        // 加载框架的 UI 通知由 WPF 层接管（任务栏、下载按钮、左侧任务列表）
        LoadingUi.Current = new LoadingUiBridge();
        LauncherUi.Interaction = new WpfUserInteraction();
        DownloadUserInteraction.Current = new WpfDownloadInteraction();
        UpdateUi.Current = new WpfUpdateUi();
        AnnouncementActions.Current = new WpfAnnouncementActions();
        WatcherUi.Current = new WpfWatcherUi();
        MediaService.Current = new WpfMediaService();
        CrashAnalysisService.Current = new WpfCrashAnalysisService();
        CompNavigation.Current = new WpfCompNavigationService();
        CompUi.Current = new WpfCompUi();
        CompLocalUi.Current = new WpfCompLocalUi();
        MemorySettings.Current = new WpfMemorySettings();
        LaunchUi.Current = new WpfLaunchUi();
        LoginProfile.Current = new WpfProfileUi();
        LogFilter.Current = new WpfLogFilter();
        RestrictedFeatures.Current = new WpfRestrictedFeatures();
        ModpackUi.Current = new WpfModpackUi();
        EventUi.Current = new WpfEventUi();
        LauncherState.InstanceSelectionChanged += _ => PageDownloadCompDetail.cachedFolder.Clear();

        // 业务层日志的 UI 提示桥接：Launcher.Core 只负责记录，弹窗/提示仍由 WPF 层处理
        LauncherCommon.UiLogHandler = static (ex, text, level, title, userSummary) =>
        {
            if (ex is null) Common.Log(text, (Common.LogLevel)level, title, userSummary);
            else Common.Log(ex, text, (Common.LogLevel)level, title, userSummary);
        };
        // Start lifecycle
        Lifecycle.OnInitialize();
    }

    /// <summary>
    ///     处理上一次运行留下的「重置本机设置」标记：删除数据目录下的本机配置文件，然后清除标记。
    ///     <para>
    ///         调用时机必须在配置服务初始化之前，否则读取到的仍是旧配置。
    ///         目录基准不依赖 <c>Paths</c>（配置未初始化时不可靠），直接由可执行文件位置推导。
    ///         任何异常都只记录，绝不允许阻塞启动。
    ///     </para>
    /// </summary>
    private static void TryApplyPendingConfigReset()
    {
        try
        {
            var dataDir = GetDataDirectory();
            var markerPath = System.IO.Path.Combine(dataDir, PageSetupUpdate.ConfigResetMarkerName);
            if (!System.IO.File.Exists(markerPath)) return;

            string[] configFiles =
            [
                System.IO.Path.Combine(dataDir, "config.v1.yml"),
                System.IO.Path.Combine(dataDir, "config.v1.yml.bak"),
                System.IO.Path.Combine(dataDir, "config.v1.json")
            ];
            foreach (var file in configFiles)
            {
                try
                {
                    if (System.IO.File.Exists(file)) System.IO.File.Delete(file);
                }
                catch (Exception ex)
                {
                    WriteEarlyError("删除本机配置文件失败：" + file, ex);
                }
            }

            try
            {
                System.IO.File.Delete(markerPath);
            }
            catch (Exception ex)
            {
                // 标记删不掉会导致每次启动都重删配置，但比不删更安全，仅记录
                WriteEarlyError("删除重置标记失败：" + markerPath, ex);
                return;
            }

            Console.WriteLine("[Setup] 已按上次请求重置本机设置");
        }
        catch (Exception ex)
        {
            WriteEarlyError("处理本机设置重置标记失败", ex);
        }
    }

    /// <summary>
    ///     与 <c>PCL.Core.App.Paths</c> 的数据目录规则保持一致，但不触发配置初始化。
    /// </summary>
    private static string GetDataDirectory()
    {
        var baseDir = System.IO.Path.GetDirectoryName(Environment.ProcessPath)
                      ?? Environment.CurrentDirectory;
        var dataDir = System.IO.Path.Combine(baseDir, "PCL");
#if DEBUG
        // Debug 构建下 Paths 允许用环境变量重定向数据目录，这里同步处理以保持两边指向同一处
        var overridePath = Environment.GetEnvironmentVariable("PCL_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath)) dataDir = overridePath;
#endif
        return dataDir;
    }

    /// <summary>
    ///     启动早期的错误输出。此时日志服务尚未初始化，只能走控制台，并且不能再抛异常。
    /// </summary>
    private static void WriteEarlyError(string message, Exception ex)
    {
        try
        {
            Console.Error.WriteLine($"[Setup] {message}：{ex.Message}");
        }
        catch
        {
            // 无控制台时忽略
        }
    }
}