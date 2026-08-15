namespace Stanza.Core;

/// <summary>
/// 优先级（RFC §7.2.1）：艾森豪威尔四象限字母（A–D）+ 可选的象限内序号（0–9，单位数）。
/// 字母顺序即象限的执行顺序：A 重要且紧急、B 重要不紧急、C 紧急不重要、D 不重要不紧急。
/// </summary>
public readonly record struct StanzaPriority(char Quadrant, int? Order)
{
    /// <summary>规范文本形式：<c>A</c> 或 <c>A3</c>（不含括号）。</summary>
    public override string ToString() => Order is { } o ? $"{Quadrant}{o}" : Quadrant.ToString();
}
