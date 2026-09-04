using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DeskBuddy.Models;

/// <summary>连连看：思维导图中的一个节点（自由摆放，无层级）。</summary>
public sealed class MindNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>节点文字。</summary>
    public string Text { get; set; } = "新节点";
    /// <summary>画布坐标（未缩放）。</summary>
    public double X { get; set; }
    public double Y { get; set; }
    /// <summary>主题色（ARGB 十六进制 #AARRGGBB）。</summary>
    public string Color { get; set; } = "#224A90FF";
    /// <summary>节点宽度（自适应文字大小）。</summary>
    public double W { get; set; } = 110;
}

/// <summary>连连看：节点之间的连线（纯视觉关联，无层级）。</summary>
public sealed class MindLink
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string From { get; set; } = "";
    public string To { get; set; } = "";
}

/// <summary>连连看文档（对应一个 .llk 文件）。</summary>
public sealed class MindmapDoc
{
    public string Version { get; set; } = "1.0";
    public List<MindNode> Nodes { get; set; } = new();
    public List<MindLink> Links { get; set; } = new();

    public static MindmapDoc Load(string path)
    {
        try { return JsonSerializer.Deserialize<MindmapDoc>(File.ReadAllText(path)) ?? new MindmapDoc(); }
        catch { return new MindmapDoc(); }
    }

    public void Save(string path)
    {
        var opts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(path, JsonSerializer.Serialize(this, opts));
    }
}