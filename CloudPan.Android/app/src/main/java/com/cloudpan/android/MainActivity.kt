package com.cloudpan.android

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.cloudpan.android.data.ApiClientFactory
import com.cloudpan.android.data.CloudPanApi
import kotlinx.coroutines.*

class MainActivity : ComponentActivity() {
    private lateinit var api: CloudPanApi

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        // TODO: 从 SharedPreferences 或启动参数读取
        val serverUrl = "http://10.0.2.2:8443"
        val token = ""
        val deviceId = java.util.UUID.randomUUID().toString().replace("-", "")

        api = ApiClientFactory.create(serverUrl, token, deviceId)

        setContent {
            CloudPanApp(api)
        }
    }
}

@Composable
fun CloudPanApp(api: CloudPanApi) {
    var status by remember { mutableStateOf("连接中...") }
    val scope = rememberCoroutineScope()

    MaterialTheme {
        Scaffold { padding ->
            Column(
                modifier = Modifier
                    .fillMaxSize()
                    .padding(padding)
                    .padding(16.dp)
            ) {
                Text(
                    text = "CloudPan",
                    style = MaterialTheme.typography.headlineMedium
                )
                Spacer(modifier = Modifier.height(8.dp))
                Text(text = status)

                Spacer(modifier = Modifier.height(16.dp))

                Button(onClick = {
                    scope.launch(Dispatchers.IO) {
                        try {
                            val response = api.healthCheck()
                            if (response.isSuccessful) {
                                status = "✅ 服务端已连接\n${response.body()}"
                            } else {
                                status = "❌ 连接失败: ${response.code()}"
                            }
                        } catch (e: Exception) {
                            status = "❌ 错误: ${e.message}"
                        }
                    }
                }) {
                    Text("测试连接")
                }
            }
        }
    }
}
