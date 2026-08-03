package com.cloudpan.android.ui

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalFocusManager
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.cloudpan.android.BuildConfig
import com.cloudpan.android.data.DeviceItem
import com.cloudpan.android.data.FileRepository
import com.cloudpan.android.data.SettingsStore
import com.cloudpan.android.data.toUserMessage
import com.cloudpan.android.worker.PhotoBackupWorker
import kotlinx.coroutines.launch
import java.text.SimpleDateFormat
import java.util.*

@Composable
fun SettingsScreen(
    settings: SettingsStore,
    onConnected: () -> Unit
) {
    var serverUrl by remember { mutableStateOf(settings.serverUrl) }
    var token by remember { mutableStateOf(settings.token) }
    var statusMessage by remember { mutableStateOf("") }
    var devices by remember { mutableStateOf<List<DeviceItem>>(emptyList()) }
    var backupInfo by remember { mutableStateOf("") }
    var wifiOnly by remember { mutableStateOf(settings.wifiOnly) }
    var chargingOnly by remember { mutableStateOf(settings.chargingOnly) }
    val scope = rememberCoroutineScope()
    var connected by remember { mutableStateOf(false) }
    var isLoading by remember { mutableStateOf(false) }
    var showToken by remember { mutableStateOf(true) }
    var urlError by remember { mutableStateOf<String?>(null) }
    val context = androidx.compose.ui.platform.LocalContext.current
    val snackbarHostState = remember { SnackbarHostState() }
    val focusManager = LocalFocusManager.current

    /** URL 格式预校验 */
    fun isValidUrl(url: String): Boolean {
        if (url.isBlank()) return false
        val trimmed = url.trim()
        // 必须 http:// 或 https:// 开头，后跟合法主机名（含端口可选）
        return Regex("^https?://[\\w\\-.]+(:\\d+)?(/.*)?$").matches(trimmed)
    }

    // 自动重连（已保存配置时自动尝试连接，避免每次打开页面需重新输入）
    LaunchedEffect(Unit) {
        if (serverUrl.isNotBlank() && token.isNotBlank() && isValidUrl(serverUrl)) {
            isLoading = true
            statusMessage = "正在连接..."
            settings.serverUrl = serverUrl
            settings.token = token
            val repo = FileRepository(settings)
            repo.invalidateClient()
            val healthResult = repo.healthCheck()
            if (healthResult.isSuccess) {
                statusMessage = "已连接"
                connected = true
                val deviceResult = repo.getDevices()
                if (deviceResult.isSuccess) {
                    devices = deviceResult.getOrDefault(emptyList())
                }
                onConnected()
            } else {
                // 自动重连失败不清空用户已填写的字段，仅重置状态
                statusMessage = ""
                connected = false
            }
            isLoading = false
        }
    }

    Scaffold(
        snackbarHost = { SnackbarHost(hostState = snackbarHostState) }
    ) { paddingValues ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(paddingValues)
                .padding(horizontal = 24.dp, vertical = 16.dp),
            horizontalAlignment = Alignment.CenterHorizontally
        ) {
            // ---- 标题区域 ----
            Text(
                "CloudPan",
                style = MaterialTheme.typography.headlineMedium,
                fontWeight = FontWeight.Bold,
                color = MaterialTheme.colorScheme.primary
            )
            Spacer(Modifier.height(4.dp))
            Text(
                "家庭文件同步系统",
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
            Spacer(Modifier.height(20.dp))

            // 已连接状态提示
            if (connected) {
                AssistChip(
                    onClick = {},
                    label = { Text("已连接到服务器") },
                    leadingIcon = {
                        Icon(
                            Icons.Default.CheckCircle,
                            contentDescription = null,
                            tint = MaterialTheme.colorScheme.primary
                        )
                    },
                    modifier = Modifier.fillMaxWidth()
                )
                Spacer(Modifier.height(16.dp))
            }

            // ---- 服务端地址 ----
            OutlinedTextField(
                value = serverUrl,
                onValueChange = { serverUrl = it; urlError = null },
                label = { Text("服务端地址") },
                placeholder = { Text("http://192.168.1.100:8443") },
                supportingText = urlError?.let { err ->
                    { Text(err, color = MaterialTheme.colorScheme.error) }
                },
                modifier = Modifier.fillMaxWidth(),
                singleLine = true,
                enabled = !connected && !isLoading,
                keyboardOptions = KeyboardOptions(
                    keyboardType = KeyboardType.Uri,
                    imeAction = ImeAction.Next
                ),
                leadingIcon = {
                    Icon(Icons.Default.Computer, contentDescription = null)
                },
                isError = urlError != null
            )
            Spacer(Modifier.height(8.dp))

            // ---- 访问令牌（密码掩码 + 可见性切换） ----
            OutlinedTextField(
                value = token,
                onValueChange = { token = it },
                label = { Text("访问令牌") },
                placeholder = { Text("输入服务端分配的 Token") },
                modifier = Modifier.fillMaxWidth(),
                singleLine = true,
                enabled = !connected && !isLoading,
                keyboardOptions = KeyboardOptions(
                    keyboardType = KeyboardType.Password,
                    imeAction = ImeAction.Done
                ),
                keyboardActions = KeyboardActions(onDone = { focusManager.clearFocus() }),
                visualTransformation =
                    if (showToken) VisualTransformation.None else PasswordVisualTransformation(),
                trailingIcon = {
                    IconButton(onClick = { showToken = !showToken }) {
                        Icon(
                            if (showToken) Icons.Default.VisibilityOff else Icons.Default.Visibility,
                            contentDescription = if (showToken) "隐藏令牌" else "显示令牌"
                        )
                    }
                },
                leadingIcon = {
                    Icon(Icons.Default.Lock, contentDescription = null)
                }
            )
            Spacer(Modifier.height(8.dp))

            // 设备 ID 标识
            Text(
                "设备标识: ${settings.deviceId.take(8)}...",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
            Spacer(Modifier.height(16.dp))

            // ---- 连接 / 断开按钮 ----
            if (!connected) {
                Button(
                    onClick = {
                        focusManager.clearFocus()
                        var url = serverUrl.trim()

                        // URL 格式预校验 + 自动补全 http:// 前缀
                        if (!url.startsWith("http://") && !url.startsWith("https://")) {
                            url = "http://$url"
                            serverUrl = url
                        }
                        if (!isValidUrl(url)) {
                            urlError = "请输入有效的地址，如 192.168.1.100:8443"
                            return@Button
                        }
                        if (token.isBlank()) {
                            statusMessage = "请输入访问令牌"
                            return@Button
                        }

                        isLoading = true
                        statusMessage = "正在连接..."
                        urlError = null

                        scope.launch {
                            settings.serverUrl = url
                            settings.token = token
                            val repo = FileRepository(settings)
                            repo.invalidateClient()
                            val healthResult = repo.healthCheck()
                            if (healthResult.isSuccess) {
                                statusMessage = "已连接"
                                connected = true
                                val deviceResult = repo.getDevices()
                                if (deviceResult.isSuccess) {
                                    devices = deviceResult.getOrDefault(emptyList())
                                }
                                onConnected()
                                snackbarHostState.showSnackbar("已成功连接到服务器")
                            } else {
                                statusMessage = "连接失败：${healthResult.exceptionOrNull().toUserMessage()}"
                            }
                            isLoading = false
                        }
                    },
                    enabled = !isLoading,
                    modifier = Modifier
                        .fillMaxWidth()
                        .height(48.dp)
                ) {
                    if (isLoading) {
                        CircularProgressIndicator(
                            modifier = Modifier.size(20.dp),
                            strokeWidth = 2.dp,
                            color = MaterialTheme.colorScheme.onPrimary
                        )
                        Spacer(Modifier.width(8.dp))
                        Text("正在连接...")
                    } else {
                        Icon(
                            Icons.Default.Cloud,
                            contentDescription = null,
                            modifier = Modifier.size(20.dp)
                        )
                        Spacer(Modifier.width(8.dp))
                        Text("连接服务器")
                    }
                }
            } else {
                OutlinedButton(
                    onClick = {
                        connected = false
                        statusMessage = ""
                        devices = emptyList()
                        backupInfo = ""
                        scope.launch {
                            // 真正断开网络连接，清理 OkHttp 连接池
                            val tmpRepo = FileRepository(settings)
                            tmpRepo.invalidateClient()
                            snackbarHostState.showSnackbar("已断开连接")
                        }
                    },
                    modifier = Modifier
                        .fillMaxWidth()
                        .height(48.dp)
                ) {
                    Icon(
                        Icons.Default.CloudOff,
                        contentDescription = null,
                        modifier = Modifier.size(20.dp)
                    )
                    Spacer(Modifier.width(8.dp))
                    Text("断开连接")
                }
            }

            // ---- 状态提示 ----
            if (statusMessage.isNotEmpty()) {
                Spacer(Modifier.height(8.dp))
                val statusColor = when {
                    connected -> MaterialTheme.colorScheme.primary
                    statusMessage.contains("失败") || statusMessage.contains("请") ->
                        MaterialTheme.colorScheme.error
                    else -> MaterialTheme.colorScheme.onSurfaceVariant
                }
                Text(
                    statusMessage,
                    style = MaterialTheme.typography.bodyMedium,
                    color = statusColor,
                    textAlign = TextAlign.Center,
                    modifier = Modifier.fillMaxWidth()
                )
            }

            // ---- 未连接时引导说明（消除页面空洞感） ----
            if (!connected && !isLoading) {
                Spacer(Modifier.height(24.dp))
                Card(
                    modifier = Modifier.fillMaxWidth(),
                    colors = CardDefaults.cardColors(
                        containerColor = MaterialTheme.colorScheme.surfaceVariant
                    )
                ) {
                    Column(modifier = Modifier.padding(16.dp)) {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Icon(
                                Icons.Default.Info,
                                contentDescription = null,
                                modifier = Modifier.size(20.dp),
                                tint = MaterialTheme.colorScheme.onSurfaceVariant
                            )
                            Spacer(Modifier.width(8.dp))
                            Text(
                                "使用说明",
                                style = MaterialTheme.typography.titleSmall,
                                fontWeight = FontWeight.SemiBold
                            )
                        }
                        Spacer(Modifier.height(8.dp))
                        Text(
                            "1. 在服务端电脑上运行 CloudPan 服务，确保手机和服务器在同一网络\n" +
                            "2. 在服务端设置中获取服务端地址和访问令牌\n" +
                            "3. 将地址和令牌填入上方输入框，点击「连接服务器」",
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                            lineHeight = 22.sp
                        )
                    }
                }
            }

            // ---- 照片备份设置（仅已连接时显示） ----
            if (connected) {
                Spacer(Modifier.height(20.dp))
                Divider()
                Spacer(Modifier.height(12.dp))

                Row(verticalAlignment = Alignment.CenterVertically) {
                    Icon(
                        Icons.Default.PhotoLibrary,
                        contentDescription = null,
                        modifier = Modifier.size(20.dp),
                        tint = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                    Spacer(Modifier.width(8.dp))
                    Text(
                        "照片备份",
                        style = MaterialTheme.typography.titleSmall,
                        fontWeight = FontWeight.Bold
                    )
                }
                Spacer(Modifier.height(8.dp))

                // 备份开关卡片
                Card(
                    modifier = Modifier.fillMaxWidth(),
                    colors = CardDefaults.cardColors(
                        containerColor = MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.5f)
                    )
                ) {
                    Column(modifier = Modifier.padding(horizontal = 16.dp, vertical = 8.dp)) {
                        Row(
                            modifier = Modifier.fillMaxWidth(),
                            horizontalArrangement = Arrangement.SpaceBetween,
                            verticalAlignment = Alignment.CenterVertically
                        ) {
                            Row(verticalAlignment = Alignment.CenterVertically) {
                                Icon(
                                    Icons.Default.Wifi,
                                    contentDescription = null,
                                    modifier = Modifier.size(20.dp),
                                    tint = MaterialTheme.colorScheme.onSurfaceVariant
                                )
                                Spacer(Modifier.width(12.dp))
                                Text("仅 Wi-Fi")
                            }
                            Switch(
                                checked = wifiOnly,
                                onCheckedChange = {
                                    wifiOnly = it
                                    settings.wifiOnly = it
                                    scope.launch {
                                        snackbarHostState.showSnackbar(
                                            if (it) "已开启：仅 Wi-Fi 下进行备份"
                                            else "已关闭：可使用移动网络备份"
                                        )
                                    }
                                }
                            )
                        }
                        Row(
                            modifier = Modifier.fillMaxWidth(),
                            horizontalArrangement = Arrangement.SpaceBetween,
                            verticalAlignment = Alignment.CenterVertically
                        ) {
                            Row(verticalAlignment = Alignment.CenterVertically) {
                                Icon(
                                    Icons.Default.BatteryFull,
                                    contentDescription = null,
                                    modifier = Modifier.size(20.dp),
                                    tint = MaterialTheme.colorScheme.onSurfaceVariant
                                )
                                Spacer(Modifier.width(12.dp))
                                Text("仅充电时")
                            }
                            Switch(
                                checked = chargingOnly,
                                onCheckedChange = {
                                    chargingOnly = it
                                    settings.chargingOnly = it
                                    scope.launch {
                                        snackbarHostState.showSnackbar(
                                            if (it) "已开启：仅充电时进行备份"
                                            else "已关闭：不限制充电状态"
                                        )
                                    }
                                }
                            )
                        }
                    }
                }
                Spacer(Modifier.height(12.dp))

                // 备份操作按钮
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(12.dp)
                ) {
                    OutlinedButton(
                        onClick = {
                            PhotoBackupWorker.schedule(context)
                            backupInfo = "照片备份已启用"
                            scope.launch {
                                snackbarHostState.showSnackbar("照片备份已启用")
                            }
                        },
                        modifier = Modifier.weight(1f)
                    ) {
                        Icon(
                            Icons.Default.CheckCircle,
                            contentDescription = null,
                            modifier = Modifier.size(18.dp)
                        )
                        Spacer(Modifier.width(6.dp))
                        Text("启用备份")
                    }
                    OutlinedButton(
                        onClick = {
                            PhotoBackupWorker.cancel(context)
                            backupInfo = "照片备份已暂停"
                            scope.launch {
                                snackbarHostState.showSnackbar("照片备份已暂停")
                            }
                        },
                        modifier = Modifier.weight(1f)
                    ) {
                        Icon(
                            Icons.Default.Pause,
                            contentDescription = null,
                            modifier = Modifier.size(18.dp)
                        )
                        Spacer(Modifier.width(6.dp))
                        Text("暂停备份")
                    }
                }
                if (backupInfo.isNotEmpty()) {
                    Spacer(Modifier.height(4.dp))
                    Text(
                        backupInfo,
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        modifier = Modifier.fillMaxWidth()
                    )
                }
            }

            // ---- 设备列表（仅已连接且有设备时显示） ----
            if (devices.isNotEmpty()) {
                Spacer(Modifier.height(20.dp))
                Divider()
                Spacer(Modifier.height(12.dp))

                Text(
                    "已连接设备 (${devices.size})",
                    style = MaterialTheme.typography.titleSmall,
                    fontWeight = FontWeight.Bold
                )
                Spacer(Modifier.height(4.dp))

                // 使用 heightIn(max) 替代 weight(1f)，避免软键盘弹出时布局异常
                LazyColumn(
                    modifier = Modifier
                        .fillMaxWidth()
                        .heightIn(max = 240.dp)
                ) {
                    items(devices) { d ->
                        val online = d.online == 1
                        ListItem(
                            leadingContent = {
                                Icon(
                                    if (online) Icons.Default.CheckCircle else Icons.Default.Circle,
                                    contentDescription = if (online) "在线" else "离线",
                                    tint = if (online) MaterialTheme.colorScheme.primary
                                    else MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.5f)
                                )
                            },
                            headlineContent = {
                                Text(
                                    d.name,
                                    fontWeight = if (online) FontWeight.Medium else FontWeight.Normal
                                )
                            },
                            supportingContent = {
                                Text(
                                    if (online) "在线 · ${formatTimestamp(d.lastSeen)}"
                                    else "离线 · ${formatTimestamp(d.lastSeen)}",
                                    style = MaterialTheme.typography.bodySmall
                                )
                            }
                        )
                    }
                }
            }

            // 底部弹性占位
            Spacer(Modifier.weight(1f))

            // ---- 版本信息（动态获取，非硬编码） ----
            Text(
                "家庭文件同步 v${BuildConfig.VERSION_NAME}",
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.6f)
            )
        }
    }
}

private fun formatTimestamp(iso: String): String {
    return try {
        val sdf = SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss", Locale.getDefault())
        val date = sdf.parse(iso) ?: return iso
        SimpleDateFormat("MM-dd HH:mm", Locale.getDefault()).format(date)
    } catch (_: Exception) {
        iso.take(16)
    }
}
