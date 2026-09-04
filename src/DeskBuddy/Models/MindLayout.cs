using System;
using System.Collections.Generic;
using System.Linq;

namespace DeskBuddy.Models;

/// <summary>Xmind 式树状自动布局（从左到右展开）。
/// 规则：同级同 X；每个父节点垂直居中于其子树总高度；叶节点自上而下依次排列。</summary>
public static class MindLayout
{
    public const double LevelGap = 260;   // 相邻层级水平间距
    public const double NodeGap = 14;     // 同级纵向间距
    public const double NodeH = 40;       // 节点近似高度

    public static void Layout(List<MindNode> nodes)
    {
        if (nodes.Count == 0) return;
        var children = nodes.ToLookup(n => n.ParentId ?? "");
        var roots = nodes.Where(n => string.IsNullOrEmpty(n.ParentId)).ToList();
        if (roots.Count == 0) roots = new List<MindNode> { nodes[0] }; // 容错：无根则取首个为根

        double cursorY = 0;
        foreach (var root in roots)
        {
            // 清掉根的父引用
            root.ParentId = "";
            cursorY += PlaceTree(root, 0, children, ref cursorY) + NodeGap;
        }
    }

    /// <summary>放置节点及其子树，返回该子树总高度（含自身）。cursorY 为当前可用的 y 顶。</summary>
    private static double PlaceTree(MindNode node, int depth, ILookup<string, MindNode> children, ref double cursorY)
    {
        node.X = depth * LevelGap;
        var kids = children[node.Id].Where(c => c.Id != node.Id).OrderBy(c => c.Id).ToList();
        if (kids.Count == 0)
        {
            node.Y = cursorY;
            cursorY += NodeH + NodeGap;
            return NodeH;
        }
        double totalH = 0;
        foreach (var k in kids) totalH += PlaceTree(k, depth + 1, children, ref cursorY);
        // 父节点垂直居中于子树
        node.Y = kids[0].Y + (kids[^1].Y - kids[0].Y) / 2;
        return totalH;
    }
}