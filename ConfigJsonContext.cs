using System.Text.Json.Serialization;

namespace WinNetFix;

/// <summary>源生成 JSON 上下文，配合 PublishTrimmed 避免反射警告。</summary>
[JsonSerializable(typeof(Config))]
internal partial class ConfigJsonContext : JsonSerializerContext
{
}
