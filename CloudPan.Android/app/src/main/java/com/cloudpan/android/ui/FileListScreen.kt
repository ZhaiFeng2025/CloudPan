package com.cloudpan.android.ui

import android.os.Environment
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import com.cloudpan.android.data.FileEntryDto
import com.cloudpan.android.data.FileRepository
import kotlinx.coroutines.launch

/** 文件大小格式化。 */
private fun formatSize(bytes: Long): String = when {
    bytes >= 1_048_576 -> "${"%.1f".format(bytes / 1_048_576.0f)} MB"
    bytes >= 1024 -> "${bytes / 1024} KB"
    else -> "$bytes B"
}

@OptIn(ExperimentalMaterial3Api::class, ExperimentalFoundationApi::class)
@Composable
fun FileListScreen(
    repository: FileRepository,
    onBackToSettings: () -> Unit,
    onPickFileForUpload: (() -> Unit)? = null,
    refreshTrigger: Int = 0
) {
    var files by remember { mutableStateOf<List<FileEntryDto>>(emptyList()) }
    var status by remember { mutableStateOf("加载中...") }
    var selectedFile by remember { mutableStateOf<FileEntryDto?>(null) }
    var isDownloading by remember { mutableStateOf(false) }
    var deleteTarget by remember { mutableStateOf<FileEntryDto?>(null) }
    var showNewFolderDialog by remember { mutableStateOf(false) }
    var newFolderName by remember { mutableStateOf("") }
    val scope = rememberCoroutineScope()
    val snackbarHostState = remember { SnackbarHostState() }

    fun loadFiles() {
        scope.launch {
            status = "加载中..."
            val result = repository.getFileTree()
            result.onSuccess { response ->
                files = response.data.sortedBy { it.path }
                status = "${files.size} 个文件"
            }.onFailure { e ->
                status = "❌ ${e.message}"
                snackbarHostState.showSnackbar("加载失败: ${e.message}")
            }
        }
    }

    LaunchedEffect(Unit) { loadFiles() }
    LaunchedEffect(refreshTrigger) { if (refreshTrigger > 0) loadFiles() }

    // 删除确认对话框
    if (deleteTarget != null) {
        AlertDialog(
            onDismissRequest = { deleteTarget = null },
            title = { Text("删除文件") },
            text = { Text("确定要删除 ${deleteTarget!!.path} 吗？") },
            confirmButton = {
                TextButton(onClick = {
                    val file = deleteTarget!!
                    deleteTarget = null
                    scope.launch {
                        val result = repository.deleteFile(file.path)
                        if (result.isSuccess) {
                            snackbarHostState.showSnackbar("已删除: ${file.path}")
                            loadFiles()
                        } else {
                            snackbarHostState.showSnackbar("删除失败: ${result.exceptionOrNull()?.message}")
                        }
                    }
                }) { Text("删除", color = MaterialTheme.colorScheme.error) }
            },
            dismissButton = {
                TextButton(onClick = { deleteTarget = null }) { Text("取消") }
            }
        )
    }

    // 新建文件夹对话框
    if (showNewFolderDialog) {
        AlertDialog(
            onDismissRequest = { showNewFolderDialog = false },
            title = { Text("新建文件夹") },
            text = {
                OutlinedTextField(
                    value = newFolderName,
                    onValueChange = { newFolderName = it },
                    label = { Text("文件夹名称") },
                    singleLine = true
                )
            },
            confirmButton = {
                TextButton(onClick = {
                    val name = newFolderName
                    showNewFolderDialog = false
                    newFolderName = ""
                    scope.launch {
                        val path = if (name.endsWith("/")) "/$name" else "/$name/"
                        val result = repository.createFolder(path)
                        if (result.isSuccess) {
                            snackbarHostState.showSnackbar("已创建: $path")
                            loadFiles()
                        } else {
                            snackbarHostState.showSnackbar("创建失败: ${result.exceptionOrNull()?.message}")
                        }
                    }
                }) { Text("创建") }
            },
            dismissButton = {
                TextButton(onClick = { showNewFolderDialog = false }) { Text("取消") }
            }
        )
    }

    // 下载对话框（含进度指示器）
    if (selectedFile != null) {
        val file = selectedFile!!
        AlertDialog(
            onDismissRequest = { if (!isDownloading) selectedFile = null },
            title = { Text(if (isDownloading) "下载中..." else "下载文件") },
            text = {
                Column {
                    Text("${file.path}")
                    Text(formatSize(file.size), style = MaterialTheme.typography.bodySmall)
                    if (isDownloading) {
                        Spacer(modifier = Modifier.height(12.dp))
                        LinearProgressIndicator(modifier = Modifier.fillMaxWidth())
                        Spacer(modifier = Modifier.height(4.dp))
                        Text(status, style = MaterialTheme.typography.bodySmall)
                    }
                }
            },
            confirmButton = {
                if (!isDownloading) {
                    TextButton(onClick = {
                        isDownloading = true
                        scope.launch {
                            val downloadsDir = Environment.getExternalStoragePublicDirectory(
                                Environment.DIRECTORY_DOWNLOADS
                            )
                            status = "下载中..."
                            val result = repository.downloadFile(file.path, downloadsDir)
                            isDownloading = false
                            selectedFile = null
                            if (result.isSuccess) {
                                snackbarHostState.showSnackbar("已下载: ${result.getOrNull()?.name}")
                            } else {
                                snackbarHostState.showSnackbar("下载失败: ${result.exceptionOrNull()?.message}")
                            }
                        }
                    }) { Text("确认下载") }
                }
            },
            dismissButton = {
                if (!isDownloading) {
                    TextButton(onClick = { selectedFile = null }) { Text("取消") }
                }
            }
        )
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("CloudPan 文件") },
                actions = {
                    IconButton(onClick = { showNewFolderDialog = true }) {
                        Text("📁+")
                    }
                    IconButton(onClick = { loadFiles() }) {
                        Text("🔄")
                    }
                    IconButton(onClick = onBackToSettings) {
                        Text("⚙️")
                    }
                }
            )
        },
        floatingActionButton = {
            if (onPickFileForUpload != null) {
                FloatingActionButton(onClick = onPickFileForUpload) {
                    Text("＋")
                }
            }
        },
        snackbarHost = { SnackbarHost(snackbarHostState) }
    ) { padding ->
        Column(modifier = Modifier.padding(padding)) {
            Text(
                text = status,
                modifier = Modifier.padding(horizontal = 16.dp, vertical = 4.dp),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )

            LazyColumn {
                items(files) { file ->
                    val icon = if (file.type == 1) "📁" else "📄"

                    ListItem(
                        headlineContent = {
                            Text("$icon ${file.path}", fontWeight = FontWeight.Normal)
                        },
                        supportingContent = {
                            Text("v${file.version} · ${formatSize(file.size)}")
                        },
                        modifier = Modifier.combinedClickable(
                            onClick = {
                                if (file.type == 0) selectedFile = file
                            },
                            onLongClick = {
                                if (file.type == 0) deleteTarget = file
                            }
                        )
                    )
                }
            }
        }
    }
}
