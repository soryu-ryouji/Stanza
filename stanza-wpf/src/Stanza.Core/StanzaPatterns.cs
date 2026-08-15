using System.Text.RegularExpressions;

namespace Stanza.Core;

/// <summary>解析器与写出器共用的正则模式。共用常量保证两侧字面一致（§7.2）。</summary>
public static class StanzaPatterns
{
    // 项目：+ 前必须是行首或空白字符（§7.2.4），否则 C++ 会被误解析
    public const string Project = @"(?<!\S)\+[\p{L}\p{N}][\p{L}\p{N}_-]*";

    // 标签：# 前必须是行首或空白字符，首字符必须是字母（§7.2.5），否则 #1 / C# 会被误解析
    public const string Tag = @"(?<!\S)#\p{L}[\p{L}\p{N}_-]*";

    private static readonly Regex ProjectNameRegex = new(
        @"^[\p{L}\p{N}][\p{L}\p{N}_-]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TagNameRegex = new(
        @"^\p{L}[\p{L}\p{N}_-]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>项目名合法性（§7.2.4：字母或数字开头，可含字母、数字、_、-）。供输入校验使用。</summary>
    public static bool IsValidProjectName(string name) => ProjectNameRegex.IsMatch(name);

    /// <summary>标签名合法性（§7.2.5：首字符必须是字母，可含字母、数字、_、-）。供输入校验使用。</summary>
    public static bool IsValidTagName(string name) => TagNameRegex.IsMatch(name);
}
