using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Utils.OS;

namespace PCL;

/// <summary>
/// 设置页面中的「详情」页。
/// <para>展示启动器版本、运行环境等信息，并提供清除缓存、日志与设置数据的功能。</para>
/// </summary>
public partial class PageSetupUpdate : IPageView
{
    /// <summary>
    /// 「待重置本机设置」标记文件名，位于数据目录内。
    /// <para>
    /// 用户在详情页请求重置设置时写入，由 <see cref="Program"/> 在下次启动、配置服务初始化之前读取并执行。
    /// 强杀、崩溃等场景下 <c>ProcessExit</c> 不会触发，全靠这个标记兜底。
    /// </para>
    /// </summary>
    internal const string ConfigResetMarkerName = ".config-reset-pending";

    /// <summary>缓存目录（相对启动器目录）。</summary>
    private static readonly string[] CacheDirectories =
    [
        Path.Combine(Common.ExePath, "PCL", "Cache"),
        Path.Combine(Common.pathTemp, "Cache")
    ];

    /// <summary>日志目录（相对启动器目录）。</summary>
    private static readonly string[] LogDirectories =
    [
        Path.Combine(Common.ExePath, "PCL", "Log"),
        Path.Combine(Common.ExePath, "Log")
    ];

    private bool _refreshed;

    /// <summary>是否已经注册「退出时再次删除配置文件」的回调。</summary>
    private static bool _configResetScheduled;

    public PageSetupUpdate()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
        Unloaded += PageSetupUpdate_Unloaded;
    }

    /// <summary>无法获取某项信息时展示的占位文本。</summary>
    private static string UnknownText => Lang.Text("Setup.Details.Version.Unknown");

    public void OnNavigatedTo(object? parameter) => Refresh();

    public void OnNavigatedFrom()
    {
    }

    private void PageSetupUpdate_Unloaded(object sender, RoutedEventArgs e) => _refreshed = false;

    private void Refresh()
    {
        if (_refreshed)
            return;
        _refreshed = true;
        PanBack.ScrollToHome();
        InitDetails();
    }

    /// <summary>填充版本信息卡片中的全部条目。</summary>
    private void InitDetails()
    {
        LabClearResult.Text = string.Empty;
        SetDetail(LabVersionName,
            () => "PCL CE " + PageSetupUpdateViewModel.VersionNameFormat(Common.VersionBaseName));
        SetDetail(LabVersionCode, () => Common.VersionCode.ToString(CultureInfo.InvariantCulture));
        SetDetail(LabVersionBranch, () => Common.VersionBranchName);
        SetDetail(LabDotNetVersion, GetDotNetVersionText);
        SetDetail(LabOsVersion, GetOsVersionText);
        SetDetail(LabArchitecture,
            () => $"{RuntimeInformation.OSArchitecture} / {RuntimeInformation.ProcessArchitecture}");
        SetDetail(LabScreenDpi, GetScreenDpiText);
        SetDetail(LabExePath, () => Common.ExePath);
        SetDetail(LabDataPath, () => Paths.Data);
        SetDetail(LabStartupCount, () => States.System.StartupCount.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// 为单个条目赋值。任何一项取值失败都不应导致整页报错，此时展示「未知」。
    /// </summary>
    private static void SetDetail(TextBlock target, Func<string> getter)
    {
        string text;
        try
        {
            text = getter();
        }
        catch (Exception ex)
        {
            Common.Log(ex, "获取详细信息失败：" + target.Name, Common.LogLevel.Debug);
            text = string.Empty;
        }

        target.Text = string.IsNullOrWhiteSpace(text) ? UnknownText : text;
    }

    /// <summary>
    /// 获取 .NET 运行时版本。
    /// <para>
    /// <c>FrameworkDescription</c> 形如 <c>.NET 10.0.15</c>，已包含版本号；
    /// 因此不再与 <see cref="Environment.Version"/> 拼接，否则会显示成 <c>10.0.15 .NET 10.0.15</c>。
    /// </para>
    /// </summary>
    private static string GetDotNetVersionText()
    {
        var framework = RuntimeInformation.FrameworkDescription;
        return string.IsNullOrWhiteSpace(framework) ? Environment.Version.ToString() : framework;
    }

    /// <summary>获取系统内核版本，避开兼容性设置对 Version API 的影响。</summary>
    private static string GetOsVersionText()
    {
        var version = NtInterop.GetCurrentOsVersion();
        return string.Format(CultureInfo.InvariantCulture, "Windows {0}.{1} (Build {2})",
            version.Major, version.Minor, version.Build);
    }

    /// <summary>获取系统 DPI 并换算为缩放百分比，例如 144 DPI 对应 150%。</summary>
    private static string GetScreenDpiText()
    {
        var dpi = WindowInterop.GetSystemDpi();
        var percent = (int)Math.Round(dpi * 100d / 96d);
        return string.Format(CultureInfo.InvariantCulture, "{0} ({1}%)", dpi, percent);
    }

    /// <summary>
    /// 清除数据按钮：收集勾选项，二次确认后执行清理。
    /// </summary>
    private void BtnClearData_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            var clearCache = CheckClearCache.Checked == true;
            var clearLog = CheckClearLog.Checked == true;
            var clearConfig = CheckClearConfig.Checked == true;

            if (!clearCache && !clearLog && !clearConfig)
            {
                DialogCompat.ShowMessageBox(
                    Lang.Text("Setup.Details.Data.NoneSelected"),
                    Lang.Text("Setup.Details.Data.Title"));
                return;
            }

            // 二次确认：明确列出将被删除的内容，且警告不可恢复
            var selectedItems = new List<string>();
            if (clearCache) selectedItems.Add("· " + Lang.Text("Setup.Details.Data.Cache"));
            if (clearLog) selectedItems.Add("· " + Lang.Text("Setup.Details.Data.Log"));
            if (clearConfig) selectedItems.Add("· " + Lang.Text("Setup.Details.Data.Config"));

            var confirmResult = DialogCompat.ShowMessageBox(
                Lang.Text("Setup.Details.Data.Confirm.Message", string.Join(Environment.NewLine, selectedItems)),
                Lang.Text("Setup.Details.Data.Confirm.Title"),
                Lang.Text("Common.Action.Confirm"),
                Lang.Text("Common.Action.Cancel"),
                isWarn: true);
            if (confirmResult != 1)
                return;

            Common.Log($"[Setup] 开始清除数据（缓存：{clearCache}，日志：{clearLog}，设置：{clearConfig}）");

            var allSucceeded = true;
            if (clearCache)
                foreach (var directory in CacheDirectories)
                    allSucceeded &= TryClearDirectory(directory);
            if (clearLog)
                foreach (var directory in LogDirectories)
                    allSucceeded &= TryClearDirectory(directory);
            if (clearConfig)
            {
                allSucceeded &= TryDeleteConfigFiles();
                // 标记文件：即使本次运行被强杀或崩溃，下次启动也会按标记真正清理一遍
                WriteConfigResetMarker();
                // 配置在本次运行期间仍可能被内存中的副本写回磁盘，故在进程退出时再删除一次
                ScheduleConfigResetOnExit();
            }

            Common.Log($"[Setup] 清除数据完成，全部成功：{allSucceeded}");

            // 不在此处退出程序，仅提示重启后生效
            // 清理失败绝大多数是因为文件正被占用，因此提示「已跳过、可重启后重试」而非直接报错
            var result = allSucceeded
                ? Lang.Text("Setup.Details.Data.Done")
                : Lang.Text("Setup.Details.Data.PartialSkipped");
            if (clearConfig)
                result += "，" + Lang.Text("Setup.Details.Data.RestartHint");
            LabClearResult.Text = result;
        }
        catch (Exception ex)
        {
            LabClearResult.Text = Lang.Text("Setup.Details.Data.Failed");
            Common.Log(ex, "清除数据失败", Common.LogLevel.Feedback,
                userSummary: Lang.Text("Setup.Details.Data.Failed"));
        }
    }

    /// <summary>
    /// 清空指定目录。目录不存在视为已完成；删除失败（通常是被占用）不会中断流程。
    /// </summary>
    /// <returns>是否全部删除成功。</returns>
    private static bool TryClearDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || !Directory.Exists(path))
            return true;

        try
        {
            Directory.Delete(path, true);
            Common.Log("[Setup] 已清空目录：" + path);
            return true;
        }
        catch (Exception ex)
        {
            // 目录整体删除失败时退化为逐个文件删除，被占用的文件静默跳过
            Common.Log(ex, "整体删除目录失败，改为逐个删除文件：" + path, Common.LogLevel.Debug);
        }

        var allSucceeded = true;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex)
                {
                    allSucceeded = false;
                    Common.Log(ex, "删除文件失败：" + file, Common.LogLevel.Debug);
                }
            }
        }
        catch (Exception ex)
        {
            allSucceeded = false;
            Common.Log(ex, "枚举目录失败：" + path, Common.LogLevel.Debug);
        }

        return allSucceeded;
    }

    /// <summary>
    /// 在数据目录写入「待重置本机设置」标记文件，供下次启动时兜底清理。
    /// <para>写入失败只记录日志，不影响本次已经完成的清理动作。</para>
    /// </summary>
    private static void WriteConfigResetMarker()
    {
        try
        {
            var marker = Path.Combine(Paths.Data, ConfigResetMarkerName);
            Directory.CreateDirectory(Paths.Data);
            File.WriteAllText(marker, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            Common.Log("[Setup] 已写入本机设置重置标记：" + marker);
        }
        catch (Exception ex)
        {
            Common.Log(ex, "写入本机设置重置标记失败", Common.LogLevel.Debug);
        }
    }

    /// <summary>
    /// 注册一次性回调，在进程退出时再次删除配置文件（仅重置本机设置，共享配置不动）。
    /// <para>
    /// 配置服务会把内存中的完整配置文档写回磁盘，因此仅在本页面删除配置文件时，
    /// 后续任意一次配置写入都会让旧设置重新出现。这里在进程退出时兜底删除，
    /// 保证「重启启动器后生效」的承诺成立。
    /// </para>
    /// </summary>
    private static void ScheduleConfigResetOnExit()
    {
        if (_configResetScheduled)
            return;
        _configResetScheduled = true;
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            // 退出阶段不要再写日志（日志组件可能已关闭），失败也无需反馈
            TryDeleteConfigFiles(false);
        };
    }

    /// <summary>
    /// 删除启动器设置文件，使设置恢复默认。
    /// <para>仅重置本机设置，共享配置不动：共享数据可能承载多实例共用内容，误删风险高于收益。</para>
    /// </summary>
    /// <param name="writeLog">是否记录日志。</param>
    /// <returns>是否全部删除成功。</returns>
    private static bool TryDeleteConfigFiles(bool writeLog = true)
    {
        string[] candidates =
        [
            Path.Combine(Paths.Data, "config.v1.yml"),
            Path.Combine(Paths.Data, "config.v1.yml.bak"),
            Path.Combine(Paths.Data, "config.v1.json")
        ];

        var allSucceeded = true;
        foreach (var file in candidates)
        {
            try
            {
                if (!Path.IsPathRooted(file) || !File.Exists(file))
                    continue;
                File.Delete(file);
                if (writeLog)
                    Common.Log("[Setup] 已删除配置文件：" + file);
            }
            catch (Exception ex)
            {
                allSucceeded = false;
                if (writeLog)
                    Common.Log(ex, "删除配置文件失败：" + file, Common.LogLevel.Debug);
            }
        }

        return allSucceeded;
    }
}
