namespace CloudPan.CodeGen.Generators;

/// <summary>
/// 共享工具：SQLite 类型 → C# 类型映射。
/// </summary>
public static class TypeMapper
{
    public static string MapToCSharp(FieldDef field)
    {
        // csharpType 覆盖优先（如 long 替代 int）
        if (!string.IsNullOrEmpty(field.CsharpType))
            return field.CsharpType;

        return field.Type switch
        {
            "TEXT" => "string",
            "INTEGER" => "int",
            "REAL" => "double",
            "BLOB" => "byte[]",
            _ => "string"
        };
    }
}
