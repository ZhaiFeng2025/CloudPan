package com.cloudpan.android.ui

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import com.cloudpan.android.data.SettingsStore
import kotlinx.coroutines.launch

@Composable
fun SettingsScreen(
    settings: SettingsStore,
    onConnected: () -> Unit
) {
    var serverUrl by remember { mutableStateOf(settings.serverUrl) }
    var token by remember { mutableStateOf(settings.token) }
    var status by remember { mutableStateOf("") }
    val scope = rememberCoroutineScope()

    Column(
        modifier = Modifier
            .fillMaxSize()
            .padding(24.dp),
        horizontalAlignment = Alignment.CenterHorizontally
    ) {
        Text(
            text = "CloudPan",
            style = MaterialTheme.typography.headlineLarge
        )
        Spacer(modifier = Modifier.height(32.dp))

        OutlinedTextField(
            value = serverUrl,
            onValueChange = { serverUrl = it },
            label = { Text("服务端地址") },
            placeholder = { Text("http://192.168.1.100:8443") },
            modifier = Modifier.fillMaxWidth(),
            singleLine = true,
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri)
        )
        Spacer(modifier = Modifier.height(12.dp))

        OutlinedTextField(
            value = token,
            onValueChange = { token = it },
            label = { Text("家庭共享 Token") },
            placeholder = { Text("服务端启动时显示的 64 位字符") },
            modifier = Modifier.fillMaxWidth(),
            singleLine = true
        )
        Spacer(modifier = Modifier.height(12.dp))

        // 设备 ID（只读）
        Text(
            text = "设备 ID: ${settings.deviceId.take(8)}...",
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant
        )
        Spacer(modifier = Modifier.height(24.dp))

        Button(
            onClick = {
                scope.launch {
                    status = "连接中..."
                    settings.serverUrl = serverUrl
                    settings.token = token

                    val repo = com.cloudpan.android.data.FileRepository(settings)
                    repo.invalidateClient()
                    val result = repo.healthCheck()
                    status = if (result.isSuccess) "✅ 连接成功" else "❌ ${result.exceptionOrNull()?.message}"
                    if (result.isSuccess) onConnected()
                }
            },
            modifier = Modifier.fillMaxWidth()
        ) {
            Text("连接")
        }

        if (status.isNotEmpty()) {
            Spacer(modifier = Modifier.height(12.dp))
            Text(text = status, style = MaterialTheme.typography.bodyMedium)
        }

        Spacer(modifier = Modifier.weight(1f))
        Text(
            text = "家庭文件同步 v0.1.0",
            style = MaterialTheme.typography.labelSmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant
        )
    }
}
