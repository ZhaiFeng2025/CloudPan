; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
CP100 | Contract | Error | Endpoint not registered in shared-spec.json
CP101 | Contract | Error | EndpointAuth attribute does not match shared-spec.json auth field
CP102 | Security | Warning | Direct loopback/connection check in controller — use [EndpointAuth(AuthMode.Localhost)]
CP300 | Lifecycle | Warning | Services 命名空间内事件订阅但 Dispose() 中未取消订阅
CP304 | Lifecycle | Error | Services 命名空间内事件订阅但类型未实现 IDisposable，无法退订
CP301 | Lifecycle | Warning | 事件订阅使用匿名 lambda，无法退订 — 建议改为具名方法
CP302 | Lifecycle | Error | System.Threading.Timer 回调使用 async lambda（async void 异常会崩溃进程）
CP303 | Lifecycle | Warning | 可释放资源局部变量未持有（未 using / 未赋给字段 / 未传递）
CP200 | Security | Warning | Sensitive data written directly to disk — use SecretStore
CP201 | Contract | Warning | Hard-coded port literal — reference SpecPorts.HttpPort/UdpDiscoveryPort
