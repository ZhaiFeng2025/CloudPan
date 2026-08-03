using System.Net;

namespace CloudPan.Client.Core.Services;

/// <summary>
/// 同步错误的白话归因——把底层异常（HttpRequestException/AggregateException 等）映射为
/// 家庭用户可读的『原因 + 下一步』，避免错误弹窗透出英文异常栈（F-31）。
/// </summary>
public sealed class ErrorAttribution
{
    /// <summary>白话原因描述。</summary>
    public string Message { get; }

    /// <summary>建议的下一步动作；无特定建议时为空字符串。</summary>
    public string NextStep { get; }

    public ErrorAttribution(string message, string nextStep = "")
    {
        Message = message;
        NextStep = nextStep;
    }

    /// <summary>
    /// 从异常生成白话归因：递归解包 AggregateException 的全部内层异常（CLAUDE.md 7.3，
    /// 不只处理第一个），逐条归类后取最具体的一条（优先级最高者）。
    /// </summary>
    public static ErrorAttribution FromException(Exception exception)
    {
        ErrorAttribution best = new("同步失败（未知错误）", "请重试；若持续失败，可在日志中查看详细原因");
        int bestPriority = -1;

        foreach (Exception leaf in Flatten(exception))
        {
            (int priority, ErrorAttribution attribution) = Classify(leaf);
            if (priority > bestPriority)
            {
                bestPriority = priority;
                best = attribution;
            }
        }

        return best;
    }

    /// <summary>递归解包 AggregateException 全部内层异常（Flatten 展开嵌套），其余类型原样返回。</summary>
    private static IEnumerable<Exception> Flatten(Exception exception)
    {
        if (exception is AggregateException aggregate)
        {
            foreach (Exception inner in aggregate.Flatten().InnerExceptions)
            {
                foreach (Exception leaf in Flatten(inner))
                {
                    yield return leaf;
                }
            }
        }
        else
        {
            yield return exception;
        }
    }

    /// <summary>
    /// 按异常类型归类。返回（优先级, 归因）；优先级越高表示归因越具体，
    /// 多内层异常并存时取最具体的一条。
    /// </summary>
    private static (int Priority, ErrorAttribution Attribution) Classify(Exception exception)
    {
        switch (exception)
        {
            case HttpRequestException http when http.StatusCode == HttpStatusCode.Unauthorized:
                return (100, new ErrorAttribution("登录凭证已失效，无法连接云盘服务", "请打开设置，重新配置云盘地址与 Token"));
            case UnauthorizedAccessException:
                return (90, new ErrorAttribution("没有访问权限，无法读写文件", "请检查同步文件夹的访问权限设置"));
            case IOException io when IsDiskFull(io):
                return (80, new ErrorAttribution("磁盘空间不足，无法完成同步", "请清理磁盘空间后重试"));
            case HttpRequestException http when http.StatusCode == HttpStatusCode.NotFound:
                return (75, new ErrorAttribution("找不到该文件或文件夹", "文件可能已在其他设备上被删除，请刷新后再试"));
            case HttpRequestException http when http.StatusCode is null:
                return (70, new ErrorAttribution("无法连接到云盘服务", "请检查台式机是否已开机、云盘服务是否正在运行"));
            case HttpRequestException http:
                return (60, new ErrorAttribution($"云盘服务返回错误（HTTP {(int)http.StatusCode.GetValueOrDefault()}）", "请稍后重试"));
            case TaskCanceledException:
                return (50, new ErrorAttribution("同步请求超时", "请稍后重试，或检查网络连接"));
            default:
                return (0, new ErrorAttribution("同步失败（未知错误）", "请重试；若持续失败，可在日志中查看详细原因"));
        }
    }

    /// <summary>判断 IOException 是否由磁盘空间不足引起（按 HRESULT 与常见文案）。</summary>
    private static bool IsDiskFull(IOException exception)
    {
        // ERROR_DISK_FULL = 0x80070070
        if (exception.HResult == unchecked((int)0x80070070))
        {
            return true;
        }
        return exception.Message.Contains("enough space", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("磁盘空间", StringComparison.Ordinal)
            || exception.Message.Contains("磁盘已满", StringComparison.Ordinal);
    }
}
