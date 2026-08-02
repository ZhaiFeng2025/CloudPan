; Unshipped analyzer release

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
CP400 | Correctness | Error | Middleware ordering: UseRateLimit must follow UseTokenAuth
CP401 | Lifecycle | Error | Fire-and-forget async discard in Timer or void method
CP402 | Correctness | Error | Stale DbContext reused in catch(DbUpdateException) block
CP404 | Security | Error | Command injection via Process.Start cmd.exe with user input
