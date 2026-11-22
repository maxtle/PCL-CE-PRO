using PCL.Core.Utils;

namespace PCL;

/// <summary>设置页面中「详情」页的辅助方法。</summary>
public static class PageSetupUpdateViewModel
{
    /// <summary>
    /// 将形如 <c>v2.14.0-beta.1</c> 的版本名格式化为更易读的形式，
    /// 例如 <c>2.14.0 Beta 1</c>。
    /// </summary>
    /// <param name="str">原始版本名。</param>
    /// <returns>格式化后的版本名；无法解析时原样返回。</returns>
    public static string VersionNameFormat(string str)
    {
        if (string.IsNullOrWhiteSpace(str))
            return str;
        str = str.Replace("v", "");
        if (!str.Contains("-"))
            return str;
        var add = str.AfterLast("-");
        str = str.BeforeLast("-");
        return $"{str} {add.Replace(".", " ").Replace("beta", "Beta").Replace("rc", "RC")}";
    }
}
