namespace CloudPan.CodeGen.Generators;

/// <summary>
/// 生成器共享的命名辅助方法（从 EntityGenerator/EnumGenerator 各自的私有实现抽取）。
/// </summary>
public static class Naming
{
    /// <summary>
    /// 将 snake_case 转为 PascalCase："auth_ok" → "AuthOk"。
    /// 小写化每段其余部分——对已是小写的输入（predefinedKeys、枚举名）输出不变，
    /// 对混合大小写输入（如 JSON 键）更稳健。
    /// </summary>
    public static string ToPascalCase(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }
        return string.Concat(input.Split('_')
            .Select(part => part.Length > 0 ? char.ToUpper(part[0]) + part[1..].ToLower() : ""));
    }
}
