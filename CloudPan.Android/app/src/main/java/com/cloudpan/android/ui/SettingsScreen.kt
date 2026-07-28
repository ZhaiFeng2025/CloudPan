package com.cloudpan.android.ui

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import com.cloudpan.android.data.DeviceDto
import com.cloudpan.android.data.FileRepository
import com.cloudpan.android.data.SettingsStore
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
    var status by remember { mutableStateOf("") }
    var devices by remember { mutableStateOf<List<DeviceDto>>(emptyList()) }
    var backupInfo by remember { mutableStateOf("") }
    val scope = rememberCoroutineScope()
    var connected by remember { mutableStateOf(false) }

    // 读取备份状态
    LaunchedEffect(Unit) {
        val prefs = kotlinx.coroutines.Dispatchers.IO
        val lastBackup = settings.let {
            val p = it.javaClass.getDeclaredField("prefs").also { f -> f.isAccessible = true }.get(settings)
            // 直接通过 Context 读 SharedPreferences
            0L // placeholder - 实际从 PhotoBackupWorker 的 prefs 读取
        }
    }

    Column(
        modifier = Modifier.fillMaxSize().padding(24.dp),
        horizontalAlignment = Alignment.CenterHorizontally
    ) {
        Text("CloudPan", style = MaterialTheme.typography.headlineLarge)
        Spacer(Modifier.height(24.dp))

        OutlinedTextField(
            value = serverUrl, onValueChange = { serverUrl = it },
            label = { Text("服务端地址") },
            placeholder = { Text("http://192.168.1.100:8443") },
            modifier = Modifier.fillMaxWidth(), singleLine = true,
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
            enabled = !connected
        )
        Spacer(Modifier.height(8.dp))

        OutlinedTextField(
            value = token, onValueChange = { token = it },
            label = { Text("Token") },
            modifier = Modifier.fillMaxWidth(), singleLine = true,
            enabled = !connected
        )
        Spacer(Modifier.height(8.dp))

        Text("设备: ${settings.deviceId.take(8)}...",
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant)
        Spacer(Modifier.height(16.dp))

        if (!connected) {
            Button(onClick = {
                scope.launch {
                    status = "连接中..."
                    settings.serverUrl = serverUrl; settings.token = token
                    val repo = FileRepository(settings); repo.invalidateClient()
                    val r = repo.healthCheck()
                    if (r.isSuccess) {
                        status = "✅ 已连接"
                        connected = true
                        // 加载设备列表
                        val d = repo.getDevices()
                        if (d.isSuccess) devices = d.getOrThrow()
                        onConnected()
                    } else {
                        status = "❌ ${r.exceptionOrNull()?.message}"
                    }
                }
            }, modifier = Modifier.fillMaxWidth()) {
                Text("连接")
            }
        } else {
            // 断开按钮
            OutlinedButton(onClick = {
                connected = false; status = ""; devices = emptyList()
            }, modifier = Modifier.fillMaxWidth()) {
                Text("断开")
            }
        }

        if (status.isNotEmpty()) {
            Spacer(Modifier.height(8.dp))
            Text(status, style = MaterialTheme.typography.bodyMedium)
        }

        // 设备列表
        if (devices.isNotEmpty()) {
            Spacer(Modifier.height(16.dp))
            Text("已连接设备", style = MaterialTheme.typography.titleSmall, fontWeight = FontWeight.Bold)
            Spacer(Modifier.height(4.dp))
            LazyColumn(modifier = Modifier.weight(1f)) {
                items(devices) { d ->
                    val onlineIcon = if (d.online == 1) "🟢" else "⚫"
                    ListItem(
                        headlineContent = { Text("$onlineIcon ${d.name}") },
                        supportingContent = {
                            Text("${d.id.take(12)}... · ${formatTimestamp(d.lastSeen)}")
                        }
                    )
                }
            }
        } else {
            Spacer(Modifier.weight(1f))
        }

        Text("家庭文件同步 v0.1.0", style = MaterialTheme.typography.labelSmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}

private fun formatTimestamp(iso: String): String {
    return try {
        val sdf = SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss", Locale.getDefault())
        val date = sdf.parse(iso) ?: return iso
        SimpleDateFormat("MM-dd HH:mm", Locale.getDefault()).format(date)
    } catch (e: Exception) { iso.take(16) }
}
