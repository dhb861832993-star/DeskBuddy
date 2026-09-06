using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DeskBuddy.Models;

/// <summary>端口数据类型（用于连线校验）。</summary>
public static class PortType
{
    public const string Any = "any";
    public const string Text = "text";
    public const string Number = "number";
    public const string Image = "image";

    /// <summary>两个端口类型是否兼容（源输出 → 目标输入）。Any 表示通配。</summary>
    public static bool Compatible(string fromType, string toType)
        => fromType == Any || toType == Any || fromType == toType;
}

/// <summary>端口（Handle）。输入端口在节点左侧，输出端口在右侧。</summary>
public sealed class Port
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>端口显示名。</summary>
    public string Name { get; set; } = "";
    /// <summary>端口数据类型（PortType 常量）。</summary>
    public string Type { get; set; } = PortType.Any;
    /// <summary>true=输入端口，false=输出端口。</summary>
    public bool IsInput { get; set; }
}

/// <summary>节点类型。</summary>
public static class NodeKind
{
    public const string Input = "input";       // 输入节点（文本/数值源）
    public const string Output = "output";     // 输出节点（展示结果）
    public const string Parameter = "parameter"; // 参数节点（可调参数）
    public const string Process = "process";   // 处理节点（通用模拟处理）
    public const string Group = "group";       // 分组节点（容器）
}

/// <summary>执行状态。</summary>
public static class ExecStatus
{
    public const string Idle = "idle";
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Success = "success";
    public const string Failed = "failed";
}

/// <summary>工作流节点（Monet 式：类型 + 端口 + 参数 + 执行状态）。</summary>
public sealed class WfNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>节点标题。</summary>
    public string Title { get; set; } = "节点";
    /// <summary>节点类型（NodeKind 常量）。</summary>
    public string Kind { get; set; } = NodeKind.Process;
    /// <summary>画布坐标。</summary>
    public double X { get; set; }
    public double Y { get; set; }
    /// <summary>节点颜色（ARGB 十六进制）。</summary>
    public string Color { get; set; } = "#FF3A3A3C";
    /// <summary>标题字号（全局默认 24）。</summary>
    public double FontSize { get; set; } = 24;
    public double W { get; set; } = 180;
    /// <summary>输入端口。</summary>
    public List<Port> Inputs { get; set; } = new();
    /// <summary>输出端口。</summary>
    public List<Port> Outputs { get; set; } = new();
    /// <summary>节点参数（键值）。</summary>
    public Dictionary<string, string> Params { get; set; } = new();
    /// <summary>执行状态。</summary>
    public string Status { get; set; } = ExecStatus.Idle;
}

/// <summary>连线（源输出端口 → 目标输入端口）。</summary>
public sealed class WfEdge
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FromPort { get; set; } = "";
    public string ToPort { get; set; } = "";
    public double W { get; set; } = 4;
}

/// <summary>工作流文档（.llk）。</summary>
public sealed class WorkflowDoc
{
    public string Version { get; set; } = "2.0";
    public List<WfNode> Nodes { get; set; } = new();
    public List<WfEdge> Edges { get; set; } = new();

    public static WorkflowDoc Load(string path)
    {
        try { return JsonSerializer.Deserialize<WorkflowDoc>(File.ReadAllText(path)) ?? new WorkflowDoc(); }
        catch { return new WorkflowDoc(); }
    }
    public void Save(string path)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>新建一份示例文档（输入→处理→输出 链路）。</summary>
    public static WorkflowDoc NewSample()
    {
        var d = new WorkflowDoc();
        var input = new WfNode
        {
            Title = "输入", Kind = NodeKind.Input, X = 0, Y = 0, Color = "#FF3A3A3C",
            Outputs = { new Port { Name = "文本", Type = PortType.Text } }
        };
        var process = new WfNode
        {
            Title = "处理", Kind = NodeKind.Process, X = 340, Y = 60, Color = "#FF3A3A3C",
            Inputs = { new Port { Name = "文本", Type = PortType.Text, IsInput = true } },
            Outputs = { new Port { Name = "结果", Type = PortType.Text } }
        };
        var output = new WfNode
        {
            Title = "输出", Kind = NodeKind.Output, X = 680, Y = 0, Color = "#FF3A3A3C",
            Inputs = { new Port { Name = "文本", Type = PortType.Text, IsInput = true } }
        };
        d.Nodes.Add(input);
        d.Nodes.Add(process);
        d.Nodes.Add(output);
        d.Edges.Add(new WfEdge { FromPort = input.Outputs[0].Id, ToPort = process.Inputs[0].Id });
        d.Edges.Add(new WfEdge { FromPort = process.Outputs[0].Id, ToPort = output.Inputs[0].Id });
        return d;
    }
}