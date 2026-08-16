using System.IO;
using System.Text.Json;

namespace Stanza.App.Services;

/// <summary>
/// %APPDATA%/Stanza 下 JSON 配置文件的通用读写。
/// 读取失败（不存在/损坏）回退默认值，写入失败静默——配置问题不影响主流程。
/// </summary>
internal static class JsonFileStore
{
    /// <summary>配置文件路径：%APPDATA%/Stanza/&lt;fileName&gt;。</summary>
    public static string PathFor(string fileName)
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Stanza", fileName);

    public static T Load<T>(string path) where T : new()
    {
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? new T();
            }
        }
        catch
        {
            // 配置损坏时回退默认值，不影响主流程
        }
        return new T();
    }

    public static void Save<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(value));
        }
        catch
        {
            // 配置写不进不影响主流程
        }
    }
}
