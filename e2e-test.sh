#!/bin/bash
# ============================================================
# CloudPan 端到端审查套件（结构化版，对应 docs/e2e-review-plan.md 场景矩阵）
# 覆盖场景:
#   阶段 0 安装与配置: IN-01/02/03, CF-01/02
#   阶段 1 服务端 API: FS-01..09, CO-01, SE-01..04
#   阶段 2 客户端同步: FS-10..13
#   阶段 3 可靠性:     RE-01..02
# 用法: bash e2e-test.sh   （前置: dotnet build CloudPan.sln -c Release）
# ============================================================

# 禁用 MSYS 路径转换：curl 的 -F "path=/xxx" 中 /xxx 不被转成 Windows 绝对路径（否则服务端报"路径越界"）
export MSYS_NO_PATHCONV=1

ROOT="/e/XiaoFeng/云盘"
E2E_DIR="$ROOT/.e2e-test"
# bash 路径（git bash 工具用：dd/sha256sum/test -f）与 Windows 路径（.NET exe/curl 用）分开
E2E_WIN="$(cygpath -w "$E2E_DIR")"
SERVER_ROOT="$(cygpath -w "$E2E_DIR/server-root")"     # 主服务端 SyncRoot
CLIENT_ROOT="$(cygpath -w "$E2E_DIR/client-root")"     # 主客户端同步根
SERVER_ROOT_BASH="$E2E_DIR/server-root"
CLIENT_ROOT_BASH="$E2E_DIR/client-root"

TOKEN="e2e_test_token_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
PORT=8443
BASE="http://localhost:$PORT"
SERVER_EXE="$ROOT/CloudPan.Server.Host/bin/Release/net8.0-windows/CloudPan.Server.exe"
CLIENT_EXE="$ROOT/CloudPan.Client.UI/bin/Release/net8.0-windows/CloudPan.Client.exe"
AUTH="Authorization: Bearer $TOKEN"
DEV="X-Device-Id: e2e-tester"
LOCALAPP="$(cygpath -u "$LOCALAPPDATA")/CloudPan/client-config.json"

# 全局共享状态（跨场景）
BIG_HASH=""

# ---------- 结果统计 ----------
PASS=0; FAIL=0; SKIP=0
FAILED_LIST=()
SKIPPED_LIST=()

check() { # $1=0/1  $2=场景ID  $3=描述
  local rc=$1 id=$2 desc=$3
  if [ "$rc" = "0" ]; then
    PASS=$((PASS+1)); echo "  ✅ $id — $desc"
  else
    FAIL=$((FAIL+1)); FAILED_LIST+=("$id — $desc"); echo "  ❌ $id — $desc"
  fi
}

check_skip() { # $1=场景ID  $2=描述
  SKIP=$((SKIP+1)); SKIPPED_LIST+=("$1 — $2"); echo "  ⏭️  $1 — $2"
}

# ---------- 服务端进程管理 ----------
start_server() { # 默认：主 SyncRoot + 预置 Token，headless
  (cd "$ROOT" && CloudPan__Token="$TOKEN" SyncRoot="$SERVER_ROOT" "$SERVER_EXE" --service > "$E2E_WIN\\server.log" 2>&1) &
  SERVER_PID=$!
}
# 参数: $1=SyncRoot(Windows)  $2=Token（为空则不预设环境变量）
start_server_custom() {
  local root="$1" token="$2"
  if [ -n "$token" ]; then
    (cd "$ROOT" && CloudPan__Token="$token" SyncRoot="$root" "$SERVER_EXE" --service > "$E2E_WIN\\phase0-server.log" 2>&1) &
  else
    (cd "$ROOT" && SyncRoot="$root" "$SERVER_EXE" --service > "$E2E_WIN\\phase0-server.log" 2>&1) &
  fi
  SERVER_PID=$!
}
stop_server() {
  taskkill //F //IM CloudPan.Server.exe 2>/dev/null || true
  sleep 2
}
# 强制释放 8443/8450 端口：按镜像名 + netstat 定位 PID 双管齐下，并验证端口真正释放
# （MSYS_NO_PATHCONV=1 下单斜杠开关不触发路径转换；cmd.exe 兜底解决服务进程无法用 taskkill 直接终止的问题）
ensure_port_free() {
  cmd.exe /c "taskkill /F /IM CloudPan.Server.exe" >/dev/null 2>&1 || true
  taskkill /F /IM CloudPan.Server.exe >/dev/null 2>&1 || true
  local pids
  pids=$(netstat -ano 2>/dev/null | grep -E ":(8443|8450)\s" | grep -i listening | awk '{print $NF}' | sort -u)
  for pid in $pids; do
    taskkill /F /PID "$pid" >/dev/null 2>&1 || true
  done
  sleep 3
  for i in $(seq 1 15); do
    if ! netstat -ano 2>/dev/null | grep -E ":8443\s" | grep -qi listening; then
      return 0
    fi
    sleep 1
  done
  return 1
}
# 就绪返回 0，60 秒超时返回 1
wait_health() {
  for i in $(seq 1 60); do
    CODE=$(curl -s -o /dev/null -w "%{http_code}" "$BASE/api/health" 2>/dev/null)
    if echo "$CODE" | grep -q '^200'; then return 0; fi
    sleep 1
  done
  return 1
}

# ============================================================
# 阶段 0：安装与配置
# ============================================================

# IN-01 发布产物可运行（自包含单文件，真实分发件） + IN-03 UDP 端口可达
scenario_in01() {
  local PUB_WIN="$E2E_WIN\\publish-e2e"
  local ROOT_WIN="$(cygpath -w "$ROOT")"
  echo "  [IN-01] 发布自包含单文件产物 (win-x64)..."
  # 注意：dotnet/MSBuild 必须传 Windows 路径，git-bash 的 Unix 路径会导致 MSB1001 Unknown switch
  dotnet publish "$ROOT_WIN\\CloudPan.Server.Host\\CloudPan.Server.Host.csproj" \
    -c Release -r win-x64 --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -o "$PUB_WIN" > "$E2E_WIN\\publish.log" 2>&1
  if [ ! -f "$E2E_DIR/publish-e2e/CloudPan.Server.exe" ]; then
    check 1 "IN-01" "发布产物生成 (publish 日志见 .e2e-test/publish.log)"; return
  fi
  check 0 "IN-01" "发布产物生成 (CloudPan.Server.exe 单文件)"

  # 运行发布产物（独立根，预置 token）
  ensure_port_free
  local PUB_ROOT_WIN="$(cygpath -w "$E2E_DIR/pub-root")"
  mkdir -p "$E2E_DIR/pub-root"
  (cd "$ROOT" && CloudPan__Token="$TOKEN" SyncRoot="$PUB_ROOT_WIN" "$E2E_DIR/publish-e2e/CloudPan.Server.exe" --service > "$E2E_WIN\\publish-server.log" 2>&1) &
  if wait_health; then
    check 0 "IN-01" "发布产物服务端启动 (health 200)"
  else
    check 1 "IN-01" "发布产物服务端启动失败 (日志见 .e2e-test/publish-server.log)"
    stop_server; return
  fi

  # IN-03 UDP 局域网发现端口 8450 可达
  scenario_in03

  stop_server
}

# IN-03 UDP 发现广播响应（需 IN-01 的发布实例在运行）
scenario_in03() {
  printf '\xEF\xBB\xBF' > "$E2E_DIR/udp-probe.ps1"
  cat >> "$E2E_DIR/udp-probe.ps1" <<'EOF'
$ErrorActionPreference = "Stop"
$client = New-Object System.Net.Sockets.UdpClient
try {
    $client.Client.ReceiveTimeout = 3000
    $bytes = [System.Text.Encoding]::UTF8.GetBytes("CLOUDPAN_DISCOVER")
    $remote = New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Parse("127.0.0.1"), 8450)
    $client.Send($bytes, $bytes.Length, $remote) | Out-Null
    $ep = New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any, 0)
    $resp = $client.Receive([ref]$ep)
    [System.Text.Encoding]::UTF8.GetString($resp)
} catch {
    Write-Output "UDP_PROBE_ERROR: $($_.Exception.Message)"
} finally {
    $client.Close()
}
EOF
  UDP_OUT=$(powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$E2E_WIN\\udp-probe.ps1" 2>&1 | tr -d '\r')
  echo "$UDP_OUT" | grep -q '"server"'
  check $? "IN-03" "UDP 局域网发现响应 (端口 8450)"
}

# IN-02 Windows 服务安装（需管理员权限；非管理员转手册验收）
scenario_in02() {
  local PUB_DIR="$E2E_DIR/publish-e2e"
  local IS_ADMIN
  IS_ADMIN=$(powershell.exe -NoProfile -Command \
    "(New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)" 2>/dev/null | tr -d '\r')
  if [ "$IS_ADMIN" != "True" ]; then
    check_skip "IN-02" "Windows 服务安装（需管理员权限，转手册验收清单）"
    return
  fi
  if [ ! -f "$PUB_DIR/CloudPan.Server.exe" ]; then
    check_skip "IN-02" "Windows 服务安装（发布产物缺失，转手册验收清单）"
    return
  fi

  local SRV_ROOT_WIN="$(cygpath -w "$E2E_DIR/service-root")"
  mkdir -p "$E2E_DIR/service-root"
  { printf '\xEF\xBB\xBF'; cat; } > "$E2E_DIR/svc-install.ps1" <<EOF
\$svc = "CloudPanE2ETest"
sc.exe stop \$svc 2>&1 | Out-Null
sc.exe delete \$svc 2>&1 | Out-Null
\$bin = '"$PUB_DIR\\CloudPan.Server.exe" --SyncRoot "$SRV_ROOT_WIN"'
sc.exe create \$svc binPath= \$bin start= demand | Out-Null
sc.exe start \$svc 2>&1 | Out-Null
EOF
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$E2E_WIN\\svc-install.ps1" 2>&1 | tr -d '\r' > /dev/null
  sleep 4
  if wait_health; then
    check 0 "IN-02" "Windows 服务安装并启动 (health 200)"
  else
    STATE=$(powershell.exe -NoProfile -Command "sc.exe query CloudPanE2ETest" 2>&1 | grep -i "STATE" | tr -d '\r')
    check 1 "IN-02" "Windows 服务启动后 health 未就绪 ($STATE)"
  fi
  # 清理服务
  { printf '\xEF\xBB\xBF'; cat; } > "$E2E_DIR/svc-cleanup.ps1" <<'EOF'
$svc = "CloudPanE2ETest"
sc.exe stop $svc 2>&1 | Out-Null
Start-Sleep -Seconds 2
sc.exe delete $svc 2>&1 | Out-Null
EOF
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$E2E_WIN\\svc-cleanup.ps1" 2>&1 | tr -d '\r' > /dev/null
  stop_server
}

# CF-01 服务端首次启动（无预置 Token）自动生成 Token 并可认证
scenario_cf01() {
  local CF01_ROOT_WIN="$(cygpath -w "$E2E_DIR/cf01-root")"
  mkdir -p "$E2E_DIR/cf01-root"
  ensure_port_free
  start_server_custom "$CF01_ROOT_WIN" ""   # 不预设 token → 应自动生成
  if ! wait_health; then
    check 1 "CF-01" "无预置 Token 服务端启动"; stop_server; return
  fi
  # 轮询 token.txt（DatabaseInitializer 启动时写入）
  local GEN_TOKEN=""
  for i in $(seq 1 30); do
    if [ -f "$E2E_DIR/cf01-root/.cloudpan/token.txt" ]; then
      GEN_TOKEN=$(cat "$E2E_DIR/cf01-root/.cloudpan/token.txt" | tr -d '\r\n ' | tr -d '\r')
      [ -n "$GEN_TOKEN" ] && break
    fi
    sleep 1
  done
  if [ -z "$GEN_TOKEN" ]; then
    check 1 "CF-01" "服务端自动生成 Token (token.txt 未生成)"; stop_server; return
  fi
  # 用自动生成的 Token 访问受保护 API → 200
  CODE=$(curl -s -o /dev/null -w "%{http_code}" "$BASE/api/files/tree" -H "Authorization: Bearer $GEN_TOKEN")
  [ "$CODE" = "200" ]
  check $? "CF-01" "无预置 Token 自动生成且可认证 (HTTP $CODE)"
  stop_server
}

# CF-02 客户端配置持久化：预写 DPAPI 加密 config → 客户端无参数启动 → 自动重连并同步
# 拓扑注意：服务端镜像根与客户端同步根必须分离（真实部署中两端在不同机器）。
# 若共用同一根，客户端 FileWatcher 会锁定服务端写入的文件 → 原子重命名 500（非产品缺陷，是不受支持的拓扑）。
scenario_cf02() {
  local CFG_SRV_WIN="$(cygpath -w "$E2E_DIR/cfg-server-root")"   # 服务端镜像根
  local CFG_CLI_WIN="$(cygpath -w "$E2E_DIR/cfg-client-root")"   # 客户端同步根（写入配置）
  local CFG_CLI_BASH="$E2E_DIR/cfg-client-root"
  mkdir -p "$E2E_DIR/cfg-server-root" "$E2E_DIR/cfg-client-root"
  # 备份现有客户端配置（测试后还原，避免污染后续主客户端）
  local CFG_BAK="$E2E_DIR/client-config.json.bak"
  [ -f "$LOCALAPP" ] && cp "$LOCALAPP" "$CFG_BAK"
  # 预写 DPAPI 加密的配置（模拟 SetupForm 保存后的状态）
  { printf '\xEF\xBB\xBF'; cat; } > "$E2E_DIR/write-config.ps1" <<EOF
Add-Type -AssemblyName System.Security
\$token = "$TOKEN"
\$syncRoot = "$CFG_CLI_WIN"
\$serverUrl = "http://localhost:$PORT"
\$bytes = [System.Text.Encoding]::UTF8.GetBytes(\$token)
\$encrypted = [System.Security.Cryptography.ProtectedData]::Protect(
    \$bytes, \$null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
\$cfg = @{ ServerUrl = \$serverUrl; SyncRoot = \$syncRoot; TokenEncrypted = [Convert]::ToBase64String(\$encrypted) }
\$dir = Join-Path \$env:LOCALAPPDATA "CloudPan"
New-Item -ItemType Directory -Path \$dir -Force | Out-Null
\$cfg | ConvertTo-Json | Set-Content -Path (Join-Path \$dir "client-config.json") -Encoding UTF8
EOF
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$E2E_WIN\\write-config.ps1" 2>&1 | tr -d '\r' > /dev/null
  if [ ! -f "$LOCALAPP" ]; then
    check 1 "CF-02" "预写客户端配置文件失败"; stop_server; return
  fi

  # 启动服务端（独立镜像根，预置 token）
  ensure_port_free
  start_server_custom "$CFG_SRV_WIN" "$TOKEN"
  if ! wait_health; then
    check 1 "CF-02" "服务端就绪"; stop_server; return
  fi
  # 启动客户端（无参数 → 读持久化配置自动连接，不弹配置窗）
  "$CLIENT_EXE" > "$E2E_WIN\\cf02-client.log" 2>&1 &
  local CFG_CLIENT_PID=$!
  sleep 15
  # 验证同步：在客户端同步根创建文件 → 服务端文件树（镜像根）出现
  echo "persisted config works" > "$CFG_CLI_BASH/from-persisted-config.txt"
  local FOUND=0
  for i in $(seq 1 12); do
    TREE=$(curl -s "$BASE/api/files/tree" -H "$AUTH")
    if echo "$TREE" | grep -q "from-persisted-config.txt"; then FOUND=1; break; fi
    sleep 5
  done
  [ "$FOUND" = "1" ]
  check $? "CF-02" "客户端凭持久化配置自动重连并同步"
  kill $CFG_CLIENT_PID 2>/dev/null
  taskkill //F //IM CloudPan.Client.exe 2>/dev/null || true
  stop_server
  # 还原配置
  if [ -f "$CFG_BAK" ]; then cp "$CFG_BAK" "$LOCALAPP"; else rm -f "$LOCALAPP"; fi
}

# ============================================================
# 阶段 1：服务端 API
# ============================================================

# FS-01 服务端健康检查就绪
scenario_fs01() {
  wait_health
  check $? "FS-01" "服务端健康检查就绪 (端口 $PORT)"
}

# FS-02 小文件上传/下载内容一致
scenario_fs02() {
  echo "hello cloudpan e2e upload" > "$E2E_DIR/upload.txt"
  UP=$(MSYS_NO_PATHCONV=1 curl -s -X POST "$BASE/api/files/upload" -H "$AUTH" -H "$DEV" \
    -F "file=@$E2E_WIN\\upload.txt" -F "path=/docs/upload.txt")
  if ! echo "$UP" | grep -q '"path":"/docs/upload.txt"'; then
    echo "    响应: $UP"; check 1 "FS-02" "上传 /docs/upload.txt"; return
  fi
  curl -s "$BASE/api/files/download?path=/docs/upload.txt" -H "$AUTH" -o "$E2E_WIN\\dl.txt"
  cmp -s "$E2E_DIR/upload.txt" "$E2E_DIR/dl.txt"
  check $? "FS-02" "上传/下载内容一致"
}

# FS-03 文件树列出
scenario_fs03() {
  sleep 1
  TREE=$(curl -s "$BASE/api/files/tree" -H "$AUTH")
  echo "$TREE" | grep -q "upload.txt"
  check $? "FS-03" "文件树包含 upload.txt"
}

# FS-04 分块上传 15MB 大文件 → 下载哈希一致
scenario_fs04() {
  dd if=/dev/urandom of="$E2E_DIR/big.bin" bs=1M count=15 2>/dev/null
  BIG_HASH=$(sha256sum "$E2E_DIR/big.bin" | awk '{print $1}')
  TOTAL=4
  for i in 0 1 2 3; do
    dd if="$E2E_DIR/big.bin" of="$E2E_DIR/chunk$i.bin" bs=4M skip=$i count=1 2>/dev/null
    RESP=$(MSYS_NO_PATHCONV=1 curl -s -X POST "$BASE/api/files/upload/chunk" -H "$AUTH" -H "$DEV" \
      -F "chunk=@$E2E_WIN\\chunk$i.bin" -F "path=/big.bin" -F "chunkIndex=$i" -F "totalChunks=$TOTAL" -F "fileHash=$BIG_HASH")
    if ! echo "$RESP" | grep -q '"ok":true\|"isComplete":true\|"status":"complete"'; then
      echo "    块 $i 响应: $RESP"
    fi
  done
  curl -s "$BASE/api/files/download?path=/big.bin" -H "$AUTH" -o "$E2E_WIN\\big-dl.bin"
  if [ -f "$E2E_DIR/big-dl.bin" ]; then
    DL_BIG_HASH=$(sha256sum "$E2E_DIR/big-dl.bin" | awk '{print $1}')
    [ "$BIG_HASH" = "$DL_BIG_HASH" ]
    check $? "FS-04" "分块上传 15MB 哈希一致"
  else
    check 1 "FS-04" "分块上传下载文件未生成"
  fi
}

# FS-05 版本历史（上传新版本 → 产生版本记录）
scenario_fs05() {
  echo "version 2 content" > "$E2E_DIR/upload.txt"
  MSYS_NO_PATHCONV=1 curl -s -X POST "$BASE/api/files/upload" -H "$AUTH" -H "$DEV" \
    -F "file=@$E2E_WIN\\upload.txt" -F "path=/docs/upload.txt" -F "baseVersion=1" > /dev/null
  VERS=$(curl -s "$BASE/api/versions?path=/docs/upload.txt" -H "$AUTH")
  echo "$VERS" | grep -q '"version"'
  check $? "FS-05" "版本历史记录存在"
}

# FS-06 删除进回收站
scenario_fs06() {
  curl -s -X POST "$BASE/api/files/delete" -H "$AUTH" -H "$DEV" -H "Content-Type: application/json" \
    -d '{"path":"/docs/upload.txt"}' > /dev/null
  TRASH=$(curl -s "$BASE/api/trash" -H "$AUTH")
  echo "$TRASH" | grep -qi "upload.txt"
  check $? "FS-06" "删除文件进入回收站"
}

# FS-07 分享链接创建 + 公开下载哈希一致
scenario_fs07() {
  SHARE=$(curl -s -X POST "$BASE/api/shares" -H "$AUTH" -H "$DEV" -H "Content-Type: application/json" \
    -d '{"filePath":"/big.bin"}')
  SHARE_ID=$(echo "$SHARE" | sed -n 's/.*"shareId":"\([^"]*\)".*/\1/p')
  if [ -z "$SHARE_ID" ]; then
    echo "    响应: $SHARE"; check 1 "FS-07" "创建分享链接"; return
  fi
  curl -s "$BASE/share/$SHARE_ID/download" -o "$E2E_WIN\\share-dl.bin"
  SHARE_HASH=$(sha256sum "$E2E_DIR/share-dl.bin" | awk '{print $1}')
  [ "$SHARE_HASH" = "$BIG_HASH" ]
  check $? "FS-07" "分享下载哈希一致"
}

# FS-08 缩略图生成（标准 PNG → JPEG）
# 注意：用 System.Drawing 生成 100x100 标准 PNG；1x1 极小 PNG 属 SkiaSharp 解码边界情况，不宜作测试样本
scenario_fs08() {
  { printf '\xEF\xBB\xBF'; cat; } > "$E2E_DIR/make-png.ps1" <<EOF
Add-Type -AssemblyName System.Drawing
\$bmp = New-Object System.Drawing.Bitmap 100,100
\$g = [System.Drawing.Graphics]::FromImage(\$bmp)
\$g.Clear([System.Drawing.Color]::Red)
\$bmp.Save("$E2E_WIN\\pixel.png", [System.Drawing.Imaging.ImageFormat]::Png)
\$g.Dispose()
\$bmp.Dispose()
EOF
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$E2E_WIN\\make-png.ps1" 2>&1 | tr -d '\r' > /dev/null
  UP=$(MSYS_NO_PATHCONV=1 curl -s -X POST "$BASE/api/files/upload" -H "$AUTH" -H "$DEV" \
    -F "file=@$E2E_WIN\\pixel.png" -F "path=/pixel.png" -F "baseVersion=0")
  if ! echo "$UP" | grep -q '"path":"/pixel.png"'; then
    echo "    上传响应: $UP"; check 1 "FS-08" "上传 PNG"; return
  fi
  CODE=$(curl -s -o "$E2E_WIN\\thumb.jpg" -w "%{http_code}" "$BASE/api/thumbnails?path=/pixel.png&width=64" -H "$AUTH")
  [ "$CODE" = "200" ]
  check $? "FS-08" "缩略图生成 (HTTP $CODE)"
}

# FS-09 版本冲突检测（过时 baseVersion → 409）
scenario_fs09() {
  echo "conflict v1" > "$E2E_DIR/conf.txt"
  MSYS_NO_PATHCONV=1 curl -s -X POST "$BASE/api/files/upload" -H "$AUTH" -H "$DEV" \
    -F "file=@$E2E_WIN\\conf.txt" -F "path=/conflict.txt" -F "baseVersion=0" > /dev/null
  echo "conflict v2" > "$E2E_DIR/conf.txt"
  MSYS_NO_PATHCONV=1 curl -s -X POST "$BASE/api/files/upload" -H "$AUTH" -H "$DEV" \
    -F "file=@$E2E_WIN\\conf.txt" -F "path=/conflict.txt" -F "baseVersion=1" > /dev/null
  echo "conflict stale" > "$E2E_DIR/conf.txt"
  CODE=$(MSYS_NO_PATHCONV=1 curl -s -o /dev/null -w "%{http_code}" -X POST "$BASE/api/files/upload" \
    -H "$AUTH" -H "$DEV" -F "file=@$E2E_WIN\\conf.txt" -F "path=/conflict.txt" -F "baseVersion=1")
  [ "$CODE" = "409" ]
  check $? "FS-09" "过时 baseVersion 触发 409 冲突 (HTTP $CODE)"
}

# CO-01 Unicode 中文文件名上传/下载
# 注意：path 值必须从文件读取（-F "path=<file" 与 --data-urlencode "path@file"），
# 避免 curl.exe 在 Windows 上把非 ASCII 命令行参数按 ANSI 转换导致编码损坏（已定向验证为测试环境问题，非服务端缺陷）。
scenario_co01() {
  echo "unicode content" > "$E2E_DIR/cn.txt"
  printf '/测试文档.txt' > "$E2E_DIR/cn-path.txt"
  local CNPATH_WIN="$E2E_WIN\\cn-path.txt"
  UP=$(MSYS_NO_PATHCONV=1 curl -s -X POST "$BASE/api/files/upload" -H "$AUTH" -H "$DEV" \
    -F "file=@$E2E_WIN\\cn.txt" -F "path=<$CNPATH_WIN" -F "baseVersion=0")
  if ! echo "$UP" | grep -q "测试文档"; then
    echo "    响应: $UP"; check 1 "CO-01" "Unicode 文件名上传"; return
  fi
  curl -s -G "$BASE/api/files/download" --data-urlencode "path@$CNPATH_WIN" -H "$AUTH" -o "$E2E_WIN\\cn-dl.txt"
  cmp -s "$E2E_DIR/cn.txt" "$E2E_DIR/cn-dl.txt"
  check $? "CO-01" "Unicode 文件名下载内容一致"
}

# SE-01 无 Token → 401
scenario_se01() {
  CODE=$(curl -s -o /dev/null -w "%{http_code}" "$BASE/api/files/tree")
  [ "$CODE" = "401" ]
  check $? "SE-01" "无 Token 请求被拒 (HTTP $CODE)"
}

# SE-02 错误 Token → 401
scenario_se02() {
  CODE=$(curl -s -o /dev/null -w "%{http_code}" "$BASE/api/files/tree" -H "Authorization: Bearer wrong-token")
  [ "$CODE" = "401" ]
  check $? "SE-02" "错误 Token 请求被拒 (HTTP $CODE)"
}

# SE-03 路径穿越防护（上传 ../ 与下载 ../ 均拒绝）
# 关键安全属性 = "越界文件未泄漏"：返回 400/403/404 均视为拒绝（404 = 校验后未命中文件，同样未泄漏元数据）
scenario_se03() {
  echo "escape" > "$E2E_DIR/esc.txt"
  UP=$(MSYS_NO_PATHCONV=1 curl -s -o /dev/null -w "%{http_code}" -X POST "$BASE/api/files/upload" \
    -H "$AUTH" -H "$DEV" -F "file=@$E2E_WIN\\esc.txt" -F "path=../escape.txt")
  if [ "$UP" = "200" ]; then
    check 1 "SE-03" "上传路径穿越 ../ 未被拒绝 (HTTP $UP)"; return
  fi
  DL=$(curl -s -G -o /dev/null -w "%{http_code}" "$BASE/api/files/download" --data-urlencode "path=../.cloudpan/server.db" -H "$AUTH")
  if [ "$DL" = "200" ]; then
    check 1 "SE-03" "下载路径穿越未被拒绝 (HTTP $DL)"; return
  fi
  check 0 "SE-03" "路径穿越上传/下载均被拒绝 (upload $UP / download $DL)"
}

# SE-04 速率限制（80 次请求触发 429）
scenario_se04() {
  RL_DEV="X-Device-Id: rate-limit-probe"
  COUNT429=0
  for i in $(seq 1 80); do
    CODE=$(curl -s -o /dev/null -w "%{http_code}" "$BASE/api/files/tree" -H "$AUTH" -H "$RL_DEV")
    if [ "$CODE" = "429" ]; then COUNT429=$((COUNT429+1)); fi
  done
  [ "$COUNT429" -ge 1 ]
  check $? "SE-04" "速率限制触发 429 (80 次请求中 $COUNT429 次被限)"
}

# ============================================================
# 阶段 2：客户端同步
# ============================================================

# FS-10 客户端全量同步（服务端 → 客户端）
scenario_fs10() {
  if [ -f "$CLIENT_ROOT_BASH/big.bin" ]; then
    C_HASH=$(sha256sum "$CLIENT_ROOT_BASH/big.bin" | awk '{print $1}')
    [ "$C_HASH" = "$BIG_HASH" ]
    check $? "FS-10" "客户端已同步服务端 big.bin (哈希一致)"
  else
    check 1 "FS-10" "客户端未同步 big.bin"
  fi
}

# FS-11 客户端本地创建 → 上传服务端
scenario_fs11() {
  echo "file created on client side" > "$CLIENT_ROOT_BASH/from-client.txt"
  sleep 10
  TREE2=$(curl -s "$BASE/api/files/tree" -H "$AUTH")
  echo "$TREE2" | grep -q "from-client.txt"
  check $? "FS-11" "客户端本地文件已上传服务端"
}

# FS-12 客户端删除 → 同步删除服务端
scenario_fs12() {
  rm -f "$CLIENT_ROOT_BASH/from-client.txt"
  sleep 10
  TREE3=$(curl -s "$BASE/api/files/tree" -H "$AUTH")
  if echo "$TREE3" | grep -q "from-client.txt"; then
    check 1 "FS-12" "客户端删除未同步到服务端"
  else
    check 0 "FS-12" "客户端删除已同步服务端"
  fi
}

# FS-13 客户端修改 → 产生版本记录
scenario_fs13() {
  echo "small modify file" > "$CLIENT_ROOT_BASH/modify-me.txt"
  sleep 8
  echo "small modify file - v2" > "$CLIENT_ROOT_BASH/modify-me.txt"
  sleep 10
  MOD_VERS=$(curl -s "$BASE/api/versions?path=/modify-me.txt" -H "$AUTH")
  echo "$MOD_VERS" | grep -q '"version"'
  check $? "FS-13" "客户端修改文件产生版本记录"
}

# ============================================================
# 阶段 3：可靠性
# ============================================================

# RE-01 服务端重启后数据一致性（DB+FS 不丢文件）
scenario_re01() {
  stop_server
  sleep 3
  start_server
  if ! wait_health; then
    check 1 "RE-01" "服务端重启后健康检查就绪"; return
  fi
  TREE=$(curl -s "$BASE/api/files/tree" -H "$AUTH")
  if ! echo "$TREE" | grep -q "big.bin"; then
    check 1 "RE-01" "重启后文件树缺失 big.bin"; return
  fi
  curl -s "$BASE/api/files/download?path=/big.bin" -H "$AUTH" -o "$E2E_WIN\\reboot-dl.bin"
  DL_HASH=$(sha256sum "$E2E_DIR/reboot-dl.bin" | awk '{print $1}')
  [ "$DL_HASH" = "$BIG_HASH" ]
  check $? "RE-01" "服务端重启后数据一致性 (哈希一致)"
}

# RE-02 客户端断线重连恢复同步（服务端重启后客户端自动恢复）
scenario_re02() {
  echo "reconnect test file" > "$CLIENT_ROOT_BASH/reconnect.txt"
  FOUND=0
  for i in $(seq 1 24); do   # 最多 120 秒
    TREE=$(curl -s "$BASE/api/files/tree" -H "$AUTH")
    if echo "$TREE" | grep -q "reconnect.txt"; then FOUND=1; break; fi
    sleep 5
  done
  [ "$FOUND" = "1" ]
  check $? "RE-02" "客户端断线重连后恢复同步 (reconnect.txt 上传服务端)"
}

# ============================================================
# 主流程
# ============================================================
echo "======================================================"
echo "  CloudPan 端到端审查套件"
echo "======================================================"

# 前置检查：Release 构建产物
if [ ! -f "$SERVER_EXE" ] || [ ! -f "$CLIENT_EXE" ]; then
  echo "❌ 未找到 Release 构建产物。请先执行:"
  echo "   dotnet build CloudPan.sln -c Release"
  exit 2
fi

# 清理残留进程（避免端口占用导致误连旧实例）
ensure_port_free
taskkill //F //IM CloudPan.Client.exe 2>/dev/null || true
sleep 2

# 准备环境
echo ""
echo "[环境] 准备测试目录"
rm -rf "$E2E_DIR"
mkdir -p "$SERVER_ROOT_BASH" "$CLIENT_ROOT_BASH"
echo "  server-root: $SERVER_ROOT"
echo "  client-root: $CLIENT_ROOT"

echo ""
echo "──────────────────────────────────────────"
echo "  阶段 0: 安装与配置 (IN-01..03, CF-01, CF-02)"
echo "──────────────────────────────────────────"
scenario_in01    # 含 IN-03 UDP 探测（发布产物实例）
scenario_in02    # Windows 服务（需管理员，否则转手册）
scenario_cf01    # 服务端首次启动自动生成 Token
scenario_cf02    # 客户端配置持久化自动重连

echo ""
echo "──────────────────────────────────────────"
echo "  阶段 1: 服务端 API 场景 (FS-01..09, CO-01, SE-01..04)"
echo "──────────────────────────────────────────"

# 启动主服务端
ensure_port_free
start_server
wait_health && echo "  ✅ 服务端 /api/health 就绪" || echo "  ⚠️ health 60s 未就绪——继续，由 FS-01 判定"

scenario_fs01
scenario_fs02
scenario_fs03
scenario_fs04
scenario_fs05
scenario_fs06
scenario_fs07
scenario_fs08
scenario_fs09
scenario_co01
scenario_se01
scenario_se02
scenario_se03
scenario_se04

echo ""
echo "──────────────────────────────────────────"
echo "  阶段 2: 客户端同步场景 (FS-10..13)"
echo "──────────────────────────────────────────"
echo "[启动] 客户端 (WinForms 托盘，headless 参数)"
"$CLIENT_EXE" "$BASE" "$CLIENT_ROOT" "$TOKEN" > "$E2E_WIN\\client.log" 2>&1 &
CLIENT_PID=$!
echo "  客户端 PID: $CLIENT_PID"
sleep 15   # 等待首次全量同步

scenario_fs10
scenario_fs11
scenario_fs12
scenario_fs13

echo ""
echo "──────────────────────────────────────────"
echo "  阶段 3: 可靠性场景 (RE-01, RE-02)"
echo "──────────────────────────────────────────"
scenario_re01
scenario_re02

# ---------- 汇总 ----------
echo ""
echo "======================================================"
echo "  CloudPan 端到端审查结果: $PASS 通过 / $FAIL 失败 / $SKIP 跳过"
echo "======================================================"
if [ "$FAIL" -gt 0 ]; then
  echo "失败场景:"
  for f in "${FAILED_LIST[@]}"; do echo "  ❌ $f"; done
fi
if [ "$SKIP" -gt 0 ]; then
  echo "跳过场景（转手册验收清单，见 docs/e2e-review-plan.md 附录 A）:"
  for s in "${SKIPPED_LIST[@]}"; do echo "  ⏭️  $s"; done
fi

# ---------- 清理 ----------
kill $CLIENT_PID 2>/dev/null
ensure_port_free
sleep 1
taskkill //F //IM CloudPan.Client.exe 2>/dev/null || true
sleep 1

if [ "$FAIL" = "0" ]; then
  echo "✅ 端到端审查全部通过"
  exit 0
else
  echo "❌ 存在失败项，查看日志: $E2E_DIR/server.log / client.log / publish.log"
  exit 1
fi
