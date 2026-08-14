namespace QuickMenu.Models;

/// <summary>快速菜单中的一个条目。</summary>
public class QuickMenuItem
{
    /// <summary>显示名称。</summary>
    public string Name { get; set; } = "";

    /// <summary>类型：app | url | folder | file | command</summary>
    public string Type { get; set; } = "app";

    /// <summary>程序路径 / 网址 / 文件夹路径 / 文件路径 / 命令。</summary>
    public string Path { get; set; } = "";

    /// <summary>附加参数（app / command 类型使用）。</summary>
    public string Args { get; set; } = "";

    /// <summary>额外搜索关键词，用空格分隔。</summary>
    public string Keywords { get; set; } = "";

    /// <summary>可选：自定义图标文件路径（.ico/.png/.exe 均可）。留空自动提取。</summary>
    public string Icon { get; set; } = "";

    /// <summary>设为 true 时不显示在菜单中（配置保留）。</summary>
    public bool Hidden { get; set; }
}
