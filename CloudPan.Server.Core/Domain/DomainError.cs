using CloudPan.Shared;

namespace CloudPan.Server.Services;

/// <summary>
/// 领域层操作错误。携带 HttpErrorCode 使领域服务与 ASP.NET 解耦，
/// Controller 直接映射为 HTTP 错误响应（ApiErrors/Error 扩展）。
/// </summary>
public sealed record DomainError(ErrorCode Code, string Message, string UserMessage, string? Detail = null);
