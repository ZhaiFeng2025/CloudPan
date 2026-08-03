using System.Text;

namespace CloudPan.CodeGen.Generators;

/// <summary>
/// 从 shared-spec.json 生成 Android Kotlin DTO 文件（Dtos.g.kt）。
/// 覆盖四类产物，与 C# 侧（Dtos.g.cs / ApiResponses.g.cs / Enums.g.cs）同源：
///   - 枚举（enums）→ Kotlin enum class（显式数值，防序数漂移）+ 字符串常量 object + HttpErrorCode
///   - 实体 DTO（entities → apiMapping）→ data class，@SerializedName 对齐 apiMapping value
///   - 响应 DTO（api.responses）→ data class，字段名即 JSON 属性名
///   - 错误响应体（api.errorResponse.shape）→ ErrorBody / ErrorInfo
/// 输出到 CloudPan.Android/.../data/Generated/，纳入 --verify。
/// 渐进项：Retrofit interface 方法签名（参数绑定）因 spec 无结构化参数段，本次保留手工定义，
/// 仅路由常量由 KotlinApiGenerator 生成（见 SpecRoutes.g.kt）。
/// </summary>
public static class KotlinDtoGenerator
{
    public static string Generate(SpecDocument spec)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("// AUTO-GENERATED from shared-spec.json")
          .AppendLine($"// 版本: {spec.Version}  日期: {spec.Date}")
          .AppendLine("// 源: shared-spec.json → enums + entities.apiMapping + api.responses + api.errorResponse")
          .AppendLine("// 请勿手工编辑 — 重新生成: dotnet run --project CloudPan.CodeGen")
          .AppendLine();
        sb.AppendLine("package com.cloudpan.android.data");
        sb.AppendLine();
        sb.AppendLine("import com.google.gson.annotations.SerializedName");
        sb.AppendLine();

        GenerateEnums(sb, spec);
        GenerateEntityDtos(sb, spec);
        GenerateResponseDtos(sb, spec);
        GenerateErrorBody(sb, spec);

        return sb.ToString();
    }

    // ============================================================
    // 枚举
    // ============================================================

    private static void GenerateEnums(StringBuilder sb, SpecDocument spec)
    {
        sb.AppendLine("// ===================== 枚举（显式数值对齐 shared-spec.json enums） =====================");
        sb.AppendLine();

        foreach (var (enumName, enumDef) in spec.Enums)
        {
            // HttpErrorCode → 元数据 data class + 常量 object
            var httpCodes = enumDef.GetHttpErrorCodeValues();
            if (httpCodes.Count > 0)
            {
                GenerateHttpErrorCodes(sb, enumName, httpCodes);
                continue;
            }

            var values = enumDef.GetValues();
            if (values.Count == 0)
            {
                continue;
            }

            // 字符串数组（无显式数值）→ object + const val
            if (values.All(v => v.Value == null))
            {
                GenerateStringConstants(sb, enumName, values);
                continue;
            }

            GenerateNumericEnum(sb, enumName, enumDef.Description, values);
        }
    }

    private static void GenerateNumericEnum(StringBuilder sb, string name, string description, List<EnumValue> values)
    {
        sb.AppendLine($"// {description}");
        sb.AppendLine($"enum class {name}(val value: Int)");
        sb.AppendLine("{");

        for (int i = 0; i < values.Count; i++)
        {
            var v = values[i];
            int val = v.Value ?? i;
            string comma = i < values.Count - 1 ? "," : "";

            if (!string.IsNullOrEmpty(v.Note))
            {
                sb.AppendLine($"    // {v.Note}");
            }

            string scopeSuffix = v.Scope == "client-only" ? " // 仅客户端本地瞬态" : "";
            sb.AppendLine($"    {v.Name}({val}){comma}{scopeSuffix}");
        }

        sb.AppendLine("}");
        sb.AppendLine();
    }

    private static void GenerateStringConstants(StringBuilder sb, string name, List<EnumValue> values)
    {
        sb.AppendLine($"// 字符串常量：{name} 各事件名。");
        sb.AppendLine($"object {name}");
        sb.AppendLine("{");

        foreach (var v in values)
        {
            sb.AppendLine($"    const val {Naming.ToPascalCase(v.Name!)} = \"{v.Name}\"");
        }

        sb.AppendLine("}");
        sb.AppendLine();
    }

    private static void GenerateHttpErrorCodes(StringBuilder sb, string name, List<HttpErrorCodeValue> codes)
    {
        sb.AppendLine("// HTTP 错误码元数据。");
        sb.AppendLine("data class ErrorCode(");
        sb.AppendLine("    val httpStatus: Int,");
        sb.AppendLine("    val code: String,");
        sb.AppendLine("    val retry: Boolean");
        sb.AppendLine(")");
        sb.AppendLine();
        sb.AppendLine($"// {name}——所有错误响应必须引用此枚举，禁止手写错误码字符串。");
        sb.AppendLine($"object {name}");
        sb.AppendLine("{");

        foreach (var c in codes)
        {
            sb.AppendLine($"    // HTTP {c.Http} — {c.Code}{(c.Retry ? "（可重试）" : "")}");
            sb.AppendLine($"    val {c.Code} = ErrorCode({c.Http}, \"{c.Code}\", {c.Retry.ToString().ToLowerInvariant()})");
        }

        sb.AppendLine("}");
        sb.AppendLine();
    }

    // ============================================================
    // 实体 DTO（entities → apiMapping）
    // ============================================================

    private static void GenerateEntityDtos(StringBuilder sb, SpecDocument spec)
    {
        sb.AppendLine("// ===================== 实体 DTO（entities → apiMapping） =====================");
        sb.AppendLine();

        foreach (var (entityName, entity) in spec.Entities)
        {
            if (entity.ApiMapping == null || entity.ApiMapping.Count == 0)
            {
                continue;
            }

            // 只收集 apiMapping 覆盖的字段，属性名 = apiMapping value（JSON 字段名）
            var mappedFields = new List<(FieldDef Field, string JsonName)>();
            foreach (var field in entity.Fields)
            {
                if (entity.ApiMapping.TryGetValue(field.Name, out string? jsonName))
                {
                    mappedFields.Add((field, jsonName));
                }
            }

            if (mappedFields.Count == 0)
            {
                continue;
            }

            string dtoName = $"{entityName}Dto";

            sb.AppendLine($"// {entity.Description}");
            sb.AppendLine($"data class {dtoName}(");

            for (int i = 0; i < mappedFields.Count; i++)
            {
                var (field, jsonName) = mappedFields[i];
                string comma = i < mappedFields.Count - 1 ? "," : "";
                sb.AppendLine($"    @SerializedName(\"{jsonName}\") val {jsonName}: {MapFieldType(field)}{comma}");
            }

            sb.AppendLine(")");
            sb.AppendLine();
        }
    }

    /// <summary>SQLite 字段类型 → Kotlin 类型（csharpType 覆盖优先，如 long → Long）。</summary>
    private static string MapFieldType(FieldDef field)
    {
        string baseType;
        if (!string.IsNullOrEmpty(field.CsharpType))
        {
            baseType = field.CsharpType switch
            {
                "long" => "Long",
                "int" => "Int",
                "string" => "String",
                "bool" => "Boolean",
                _ => "String"
            };
        }
        else
        {
            baseType = field.Type switch
            {
                "TEXT" => "String",
                "INTEGER" => "Int",
                "REAL" => "Double",
                "BLOB" => "ByteArray",
                _ => "String"
            };
        }

        return field.Nullable ? baseType + "?" : baseType;
    }

    // ============================================================
    // API 响应 DTO（api.responses）
    // ============================================================

    private static void GenerateResponseDtos(StringBuilder sb, SpecDocument spec)
    {
        sb.AppendLine("// ===================== API 响应 DTO（api.responses） =====================");
        sb.AppendLine();

        var responses = spec.Api.Responses;
        if (responses == null || responses.Count == 0)
        {
            return;
        }

        foreach (var (name, def) in responses)
        {
            sb.AppendLine($"// {def.Description}");
            sb.AppendLine($"data class {name}(");

            var fields = ParseResponseFields(def.Fields);
            for (int i = 0; i < fields.Count; i++)
            {
                var (jsonName, kotlinType) = fields[i];
                string comma = i < fields.Count - 1 ? "," : "";
                sb.AppendLine($"    @SerializedName(\"{jsonName}\") val {jsonName}: {kotlinType}{comma}");
            }

            sb.AppendLine(")");
            sb.AppendLine();
        }
    }

    /// <summary>
    /// 解析响应字段描述 "propName: typeExpr"，属性名 = JSON 字段名（camelCase）。
    /// 类型支持基础映射、可空后缀 "?"、数组后缀 "[]"；其余视为 DTO/嵌套响应引用类型。
    /// </summary>
    private static List<(string JsonName, string KotlinType)> ParseResponseFields(List<string> fields)
    {
        var result = new List<(string, string)>();
        foreach (string field in fields)
        {
            int colon = field.IndexOf(':');
            if (colon < 0)
            {
                continue;
            }

            string fieldName = field[..colon].Trim();
            string typeExpr = field[(colon + 1)..].Trim();
            result.Add((fieldName, MapToKotlinType(typeExpr)));
        }

        return result;
    }

    private static string MapToKotlinType(string typeExpr)
    {
        string t = typeExpr;

        bool nullable = t.EndsWith("?");
        if (nullable)
        {
            t = t[..^1];
        }

        bool array = t.EndsWith("[]");
        if (array)
        {
            t = t[..^2];
        }

        string baseType = t switch
        {
            "string" => "String",
            "int" => "Int",
            "long" => "Long",
            "bool" => "Boolean",
            _ => t // 引用类型（FileEntryDto / UploadData / TrashItem 等）
        };

        string kotlinType = array ? $"List<{baseType}>" : baseType;
        if (nullable)
        {
            kotlinType += "?";
        }
        return kotlinType;
    }

    // ============================================================
    // 错误响应体（api.errorResponse.shape）
    // ============================================================

    private static void GenerateErrorBody(StringBuilder sb, SpecDocument spec)
    {
        var errorResp = spec.Api.ErrorResponse;
        if (errorResp == null || errorResp.Shape == null || errorResp.Shape.Count == 0)
        {
            return;
        }

        sb.AppendLine("// ===================== 统一错误响应体（api.errorResponse.shape） =====================");
        sb.AppendLine();

        foreach (var (wrapperName, fields) in errorResp.Shape)
        {
            string infoName = Naming.ToPascalCase(wrapperName) + "Info";
            string bodyName = Naming.ToPascalCase(wrapperName) + "Body";

            sb.AppendLine($"// 统一 API 错误响应体——所有错误响应使用此格式。");
            sb.AppendLine($"data class {bodyName}(");
            sb.AppendLine($"    @SerializedName(\"{wrapperName}\") val {wrapperName}: {infoName}");
            sb.AppendLine(")");
            sb.AppendLine();
            sb.AppendLine($"data class {infoName}(");

            var fieldList = fields.ToList();
            for (int i = 0; i < fieldList.Count; i++)
            {
                string key = fieldList[i].Key;
                string comma = i < fieldList.Count - 1 ? "," : "";
                // detail 为可选调试详情，其余为必填字符串
                string nullable = key == "detail" ? "?" : "";
                sb.AppendLine($"    @SerializedName(\"{key}\") val {key}: String{nullable}{comma}");
            }

            sb.AppendLine(")");
            sb.AppendLine();
        }
    }
}
