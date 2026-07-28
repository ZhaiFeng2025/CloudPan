namespace CloudPan.CodeGen.Generators;

/// <summary>
/// 共享工具：SQLite 类型 → C# 类型映射。
/// </summary>
public static class TypeMapper
{
    public static string MapToCSharp(FieldDef field)
    {
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
