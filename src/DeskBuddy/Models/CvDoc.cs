using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DeskBuddy.Models;

/// <summary>连连看：ComfyUI 式节点（通用卡片，带输入/输出端口）。</summary>
public sealed class CvNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>节点标题。</summary>
    public string Title { get; set; } = "节点";
    /// <summary>画布坐标。</summary>
    public double X { get; set; }
    public double Y { get; set; }
    public string Color { get; set; } = "#224A90FF";
    /// <summary>节点标题字号。</summary>
    public double FontSize { get; set; } = 19;
    /// <summary>节点形状：box | diamond | circle | star | parallelogram</summary>
    public string Shape { get; set; } = "box";
    /// <summary>节点宽度。</summary>
    public double W { get; set; } = 180;
    /// <summary>输入端口。</summary>
    public List<CvPort> Inputs { get; set; } = new();
    /// <summary>输出端口。</summary>
    public List<CvPort> Outputs { get; set; } = new();
}

public sealed class CvPort
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "端口";
}

/// <summary>连线：从一个输出端口到另一个输入端口。</summary>
public sealed class CvLink
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FromPort { get; set; } = ""; // 输出端口
    public string ToPort { get; set; } = "";   // 输入端口
    /// <summary>线宽。</summary>
    public double W { get; set; } = 4;
}

/// <summary>连连看文档（.llk）。</summary>
public sealed class CvDoc
{
    public string Version { get; set; } = "1.0";
    public List<CvNode> Nodes { get; set; } = new();
    public List<CvLink> Links { get; set; } = new();

    public static CvDoc Load(string path)
    {
        try { return JsonSerializer.Deserialize<CvDoc>(File.ReadAllText(path)) ?? new CvDoc(); }
        catch { return new CvDoc(); }
    }
    public void Save(string path)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}