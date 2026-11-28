using PCL.Core.App.Localization;
using PCL.Core.UI.Hints;
using PCL.Core.App.Region;

namespace PCL;

internal static class RestrictedFeaturePolicy
{
    // PCL-CE PRO 定制说明：
    // 上游 PCL CE 依据「系统时区 + 系统国家/地区」对 Minecraft 下载、安装、
    //
    // 补全与在线整合包安装做了限制（见 PCL.Core/App/Region/RegionPolicy.cs）。
    // 本项目面向个人自用场景，已按维护者决定解除该限制，故此处直接放行。
    // 注意：RegionPolicy 本身的判定逻辑保持不变，相关单元测试仍然有效。

    internal static RegionAssessment Assess() => RegionPolicy.Evaluate(RegionPolicy.ReadWindowsSignals());

    internal static bool IsRestricted => false;

    internal static bool Check(string feature, bool showHint = true) => true;

    internal static void ThrowIfRestricted(string feature)
    {
        // 已解除限制，不再抛出取消异常
    }
}
