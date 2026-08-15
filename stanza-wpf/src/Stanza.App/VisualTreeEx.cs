using System.Windows;
using System.Windows.Media;

namespace Stanza.App;

/// <summary>视觉树遍历工具。</summary>
public static class VisualTreeEx
{
    public static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }

    public static T? FindVisualAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node != null)
        {
            if (node is T match) return match;
            node = node switch
            {
                Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(node),
                FrameworkContentElement fce => fce.Parent,   // 文本内容元素（Run 等）
                _ => null,
            };
        }
        return null;
    }
}
