using CloudPan.Shared;

namespace CloudPan.Server;

/// 标记 API 端点的认证模式。由 shared-spec.json → endpoints[].auth 驱动。
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class EndpointAuthAttribute : Attribute
{
    public AuthMode Mode { get; }
    public EndpointAuthAttribute(AuthMode mode) => Mode = mode;
}
