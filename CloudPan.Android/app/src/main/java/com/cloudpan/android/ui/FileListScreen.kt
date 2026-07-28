package com.cloudpan.android.ui

import android.os.Environment
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import com.cloudpan.android.data.FileEntryDto
import com.cloudpan.android.data.FileRepository
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun FileListScreen(
    repository: FileRepository,
    onBackToSettings: () -> Unit
) {
    var files by remember { mutableStateOf<List<FileEntryDto>>(emptyList()) }
    var status by remember { mutableStateOf("加载中...") }
    var selectedFile by remember { mutableStateOf<FileEntryDto?>(null) }
    val scope = rememberCoroutineScope()

    fun loadFiles() {
        scope.launch {
            status = "加载中..."
            val result = repository.getFileTree()
            result.onSuccess { response ->
                files = response.data.sortedBy { it.path }
                status = "${files.size} 个文件"
            }.onFailure { e ->
                status = "❌ ${e.message}"
            }
        }
    }

    LaunchedEffect(Unit) { loadFiles() }

    // 下载对话框
    if (selectedFile != null) {
        AlertDialog(
            onDismissRequest = { selectedFile = null },
            title = { Text("下载文件") },
            text = { Text("下载 ${selectedFile!!.path} 到 Downloads 目录？") },
            confirmButton = {
                TextButton(onClick = {
                    val file = selectedFile!!
                    selectedFile = null
                    scope.launch {
                        status = "下载中: ${file.path}"
                        val downloadsDir = Environment.getExternalStoragePublicDirectory(
                            Environment.DIRECTORY_DOWNLOADS
                        )
                        val result = repository.downloadFile(file.path, downloadsDir)
                        status = if (result.isSuccess) {
                            "✅ 已下载: ${result.getOrNull()?.name}"
                        } else {
                            "❌ ${result.exceptionOrNull()?.message}"
                        }
                    }
                }) { Text("确认下载") }
            },
            dismissButton = {
                TextButton(onClick = { selectedFile = null }) { Text("取消") }
            }
        )
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("CloudPan 文件") },
                actions = {
                    IconButton(onClick = { loadFiles() }) {
                        Text("🔄")
                    }
                    IconButton(onClick = onBackToSettings) {
                        Text("⚙️")
                    }
                }
            )
        }
    ) { padding ->
        Column(modifier = Modifier.padding(padding)) {
            // 状态栏
            Text(
                text = status,
                modifier = Modifier.padding(horizontal = 16.dp, vertical = 4.dp),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )

            // 文件列表
            LazyColumn {
                items(files) { file ->
                    val icon = if (file.type == 1) "📁" else "📄"
                    val sizeStr = when {
                        file.size >= 1_048_576 -> "${file.size / 1_048_576.0f} MB"
                        file.size >= 1024 -> "${file.size / 1024.0f} KB"
                        else -> "${file.size} B"
                    }

                    ListItem(
                        headlineContent = {
                            Text(
                                "$icon ${file.path}",
                                fontWeight = FontWeight.Normal
                            )
                        },
                        supportingContent = {
                            Text("v${file.version} · $sizeStr")
                        },
                        modifier = Modifier.clickable {
                            if (file.type == 0) { // 文件类型
                                selectedFile = file
                            }
                        }
                    )
                }
            }
        }
    }
}
