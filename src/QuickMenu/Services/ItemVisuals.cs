using System.Windows.Media;

namespace QuickMenu.Services;

/// <summary>条目类型的公共视觉定义（图标字形 / 颜色 / 中文标签）。</summary>
public static class ItemVisuals
{
    public static string GlyphFor(string type) => type switch
    {
        "url" => "\uE774",      // Globe
        "folder" => "\uE8B7",   // Folder
        "file" => "\uE8A5",     // Page
        "command" => "\uE756",  // CommandPrompt
        _ => "\uE71D"           // Apps
    };

    public static Color ColorFor(string type) => type switch
    {
        "url" => Color.FromRgb(0x34, 0xC7, 0x59),      // 绿
        "folder" => Color.FromRgb(0x0A, 0x84, 0xFF),   // 蓝
        "file" => Color.FromRgb(0xFF, 0x9F, 0x0A),     // 橙
        "command" => Color.FromRgb(0x48, 0x48, 0x4A),  // 灰
        _ => Color.FromRgb(0x58, 0x56, 0xD6)           // 靛蓝
    };

    public static string TypeLabel(string type) => type switch
    {
        "url" => "网页",
        "folder" => "文件夹",
        "file" => "文件",
        "command" => "命令",
        _ => "程序"
    };

    public static string PathLabel(string type) => type switch
    {
        "url" => "网址",
        "folder" => "文件夹路径",
        "file" => "文件路径",
        _ => "程序名或完整路径"
    };
}
