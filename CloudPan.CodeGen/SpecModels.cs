using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudPan.CodeGen;

// ============================================================
// shared-spec.json 反序列化模型
// 字段名与 JSON 键一一对应，使用 camelCase 自动映射
// ============================================================

public record SpecDocument(
    string Title,
    string Version,
    string Date,
    Dictionary<string, EnumDef> Enums,
    Dictionary<string, EntityDef> Entities,
    ApiDef Api,
    Dictionary<string, JsonElement> Config,
    SettingsDef? Settings = null   // v1.1.0: 服务端设置目录
);

// ---- 枚举 ----

public record EnumDef(
    string Description,
    JsonElement Values
)
{
    /// <summary>
    /// 枚举值列表。兼容三种格式：
    ///   字符串数组：["auth", "auth_ok", ...]  → 字符串常量
    ///   对象数组：[{ "name": "Synced", "value": 0 }, ...] → 显式数值枚举
    ///   HttpErrorCode：[{ "http": 400, "code": "BAD_REQUEST", "retry": false }, ...]
    /// </summary>
    public List<EnumValue> GetValues()
    {
        if (Values.ValueKind == JsonValueKind.Array)
        {
            if (Values.GetArrayLength() == 0)
            {
                return new List<EnumValue>();
            }

            var first = Values[0];
            if (first.ValueKind == JsonValueKind.String)
            {
                return Values.EnumerateArray()
                    .Select((e, i) => new EnumValue(e.GetString()!, null, null, null))
                    .ToList();
            }
            // 对象数组 — 判断是 EnumValue 还是 HttpErrorCodeValue
            if (first.TryGetProperty("http", out _))
            {
                // HttpErrorCode 格式：不通过 EnumValue 解析，返回空列表标识需特殊处理
                return new List<EnumValue>();
            }
            return JsonSerializer.Deserialize<List<EnumValue>>(Values.GetRawText(), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            })!;
        }
        throw new InvalidOperationException($"枚举值格式不支持: {Values.ValueKind}");
    }

    /// <summary>
    /// 获取 HttpErrorCode 专用值列表。仅当 Values 为 HttpErrorCode 格式时有效。
    /// </summary>
    public List<HttpErrorCodeValue> GetHttpErrorCodeValues()
    {
        if (Values.ValueKind == JsonValueKind.Array
            && Values.GetArrayLength() > 0
            && Values[0].ValueKind == JsonValueKind.Object
            && Values[0].TryGetProperty("http", out _))
        {
            return JsonSerializer.Deserialize<List<HttpErrorCodeValue>>(Values.GetRawText(), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            })!;
        }
        return new List<HttpErrorCodeValue>();
    }
}

public record EnumValue(
    string Name,
    int? Value,
    string? Scope,
    string? Note
);

/// <summary>
/// HttpErrorCode 专用值格式（含 http、code、retry 字段）。
/// </summary>
public record HttpErrorCodeValue(
    int Http,
    string Code,
    bool Retry,
    string? Note
);

// ---- 实体 ----

public record EntityDef(
    string Description,
    string Table,
    List<FieldDef> Fields,
    List<string>? Indexes,
    Dictionary<string, string>? ApiMapping,
    Dictionary<string, string>? PredefinedKeys
);

public record FieldDef(
    string Name,
    string Type,
    bool Nullable,
    string Role,
    string? Description,
    string? CsharpType = null,  // 可选：覆盖 C# 类型映射（如 "long" 替代 "int"）
    string? Default = null       // 可选：C# 属性默认值表达式（如 SyncQueue.CreatedAt = DateTime.UtcNow.ToString("O")）
);

// ---- API ----

public record ApiDef(
    string BaseUrl,
    string AuthHeader,
    string DeviceHeader,
    string? Description,
    List<EndpointDef> Endpoints,
    WebSocketDef Websocket,
    RateLimitDef? RateLimit,
    ErrorResponseDef? ErrorResponse,
    Dictionary<string, ApiResponseDef>? Responses
);

public record EndpointDef(
    string Method,
    string Path,
    string Auth,    // v1.4.0: "token" | "public" | "localhost" | "message"（替代旧 bool）
    string Description
);

public record WebSocketDef(
    string Endpoint,
    string Auth,
    string? AuthMode,    // v1.4.0: "message" 表示认证在首条 JSON 消息中
    HeartbeatDef Heartbeat,
    string ReconnectBackoff,
    int? ReconnectMaxBackoffSeconds,
    [property: JsonPropertyName("_note")] string? Note
);

public record HeartbeatDef(
    int PingIntervalSeconds,
    int PongTimeoutSeconds
);

public record RateLimitDef(
    [property: JsonPropertyName("_ref")] string Ref,
    string Default,
    string RetryAfterHeader
);

public record ErrorResponseDef(
    string Description,
    Dictionary<string, Dictionary<string, string>> Shape
);

public record ApiResponseDef(
    string Description,
    List<string> Fields
);

// ---- 设置（v1.1.0 新增） ----

public record SettingsDef(
    string Description,
    List<SettingsGroupDef> Groups,
    List<SettingsItemDef> Items
);

public record SettingsGroupDef(
    string Id,
    string Label
);

/// <summary>
/// 服务端设置项定义。字段与 shared-spec.json → settings.items 一一对应。
/// defaultRef: 指向 config 中默认值来源（如 "config.httpPort"），Default 由生成器解析。
/// </summary>
public record SettingsItemDef(
    string Key,
    string Label,
    string Description,
    string Type,              // "int" | "string" | "secret"
    string? DefaultRef,       // 如 "config.httpPort"
    string Persistence,       // "startup" | "appconfig"
    bool RestartRequired,
    string Group,             // group id，如 "network"
    int? Min,
    int? Max,
    bool? IsPath,
    string? Action            // "rotate" 等特殊动作
);
