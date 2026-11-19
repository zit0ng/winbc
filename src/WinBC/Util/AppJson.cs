using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinBC.Models;

namespace WinBC.Util;

/// <summary>
/// JSON 序列化用的 Source Generator 上下文。
/// 走生成代码而不是反射 —— <b>NativeAOT 下反射式序列化是被禁用</b>的
/// （运行时直接抛 "Reflection-based serialization has been disabled"）。
/// 新增需要存盘的类型时，记得在这里补一行 [JsonSerializable]。
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(Snapshot))]
[JsonSerializable(typeof(Settings))]
[JsonSerializable(typeof(List<string>))]
internal partial class AppJsonContext : JsonSerializerContext
{
}

/// <summary>所有 JSON 读写的统一入口 —— 保证普通构建和 AOT 构建走同一条路径。</summary>
internal static class AppJson
{
    /// <summary>缩进 + 中文不转义（从生成的 Options 派生，所以自带 TypeInfoResolver）。</summary>
    public static readonly JsonSerializerOptions Options = new(AppJsonContext.Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, typeof(T), Options);

    public static T? Deserialize<T>(string json) => (T?)JsonSerializer.Deserialize(json, typeof(T), Options);
}
