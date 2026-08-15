using System.Windows;
using System.Windows.Controls;

namespace DeskBuddy;

/// <summary>
/// 宫格布局面板（「填充整行」模式）：
/// 每行条目均匀分布、占满整行宽度，最后一行也不再留空；
/// 「文件」分区头（Kind=file-section）自动跨整行作为分隔。
/// </summary>
public sealed class FillWrapPanel : Panel
{
    public const double TileW = 96;
    public const double TileH = 100;
    private const double DividerH = 30;

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (UIElement c in Children)
        {
            c.Measure(IsDivider(c) ? new Size(Math.Max(0, availableSize.Width), DividerH) : new Size(TileW, TileH));
        }
        var height = 0.0;
        foreach (UIElement c in Children)
        {
            height += IsDivider(c) ? DividerH : TileH;
        }
        return new Size(availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var cols = Math.Max(1, (int)Math.Floor(finalSize.Width / TileW));
        if (cols == 0) cols = 1;
        var y = 0.0;
        var rowBuffer = new List<UIElement>();
        void FlushRow()
        {
            if (rowBuffer.Count == 0) return;
            var rowItems = rowBuffer.Count;
            var gap = (finalSize.Width - rowItems * TileW) / (rowItems + 1);
            for (var i = 0; i < rowBuffer.Count; i++)
            {
                var x = gap + i * (TileW + gap);
                rowBuffer[i].Arrange(new Rect(x, y, TileW, TileH));
            }
            rowBuffer.Clear();
            y += TileH;
        }
        foreach (UIElement c in Children)
        {
            if (IsDivider(c))
            {
                FlushRow();
                c.Arrange(new Rect(0, y, finalSize.Width, DividerH));
                y += DividerH;
                continue;
            }
            rowBuffer.Add(c);
            if (rowBuffer.Count >= cols) FlushRow();
        }
        FlushRow();
        return finalSize;
    }

    private static bool IsDivider(UIElement c) =>
        c is FrameworkElement { DataContext: ItemVm { Kind: "file-section" } };
}
