package com.cloudpan.android.ui

import android.os.Environment
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.text.ClickableText
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import com.cloudpan.android.data.FileEntryDto
import com.cloudpan.android.data.FileRepository
import kotlinx.coroutines.launch

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
    var currentPath by remember { mutableStateOf("/") }
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
            val result = if (currentPath == "/") {
                repository.getFileTree()
            } else {
                repository.getFileTreeInFolder(currentPath)
            }
            result.onSuccess { response ->
                files = response.data.filter { it.path != currentPath }.sortedBy { it.path }
                status = "${files.size} 个项目"
            }.onFailure { e ->
                status = "❌ ${e.message}"
                snackbarHostState.showSnackbar("加载失败: ${e.message}")
            }
        }
    }

    LaunchedEffect(currentPath) { loadFiles() }
    LaunchedEffect(refreshTrigger) { if (refreshTrigger > 0) loadFiles() }

    // ---- 对话框 ----

    if (deleteTarget != null) {
        AlertDialog(
            onDismissRequest = { deleteTarget = null },
            title = { Text("删除") },
            text = { Text("确定删除 ${deleteTarget!!.path}？") },
            confirmButton = {
                TextButton(onClick = {
                    val f = deleteTarget!!; deleteTarget = null
                    scope.launch {
                        val r = repository.deleteFile(f.path)
                        snackbarHostState.showSnackbar(
                            if (r.isSuccess) "已删除" else "失败: ${r.exceptionOrNull()?.message}"
                        )
                        loadFiles()
                    }
                }) { Text("删除", color = MaterialTheme.colorScheme.error) }
            },
            dismissButton = { TextButton(onClick = { deleteTarget = null }) { Text("取消") } }
        )
    }

    if (showNewFolderDialog) {
        AlertDialog(
            onDismissRequest = { showNewFolderDialog = false },
            title = { Text("新建文件夹") },
            text = {
                OutlinedTextField(
                    value = newFolderName,
                    onValueChange = { newFolderName = it },
                    label = { Text("名称") },
                    singleLine = true
                )
            },
            confirmButton = {
                TextButton(onClick = {
                    val name = newFolderName; showNewFolderDialog = false; newFolderName = ""
                    val fullPath = currentPath + name + "/"
                    scope.launch {
                        val r = repository.createFolder(fullPath)
                        snackbarHostState.showSnackbar(if (r.isSuccess) "已创建" else "失败")
                        loadFiles()
                    }
                }) { Text("创建") }
            },
            dismissButton = { TextButton(onClick = { showNewFolderDialog = false }) { Text("取消") } }
        )
    }

    if (selectedFile != null) {
        val file = selectedFile!!
        AlertDialog(
            onDismissRequest = { if (!isDownloading) selectedFile = null },
            title = { Text(if (isDownloading) "下载中..." else "下载文件") },
            text = {
                Column {
                    Text(file.path); Text(formatSize(file.size), style = MaterialTheme.typography.bodySmall)
                    if (isDownloading) { Spacer(Modifier.height(12.dp)); LinearProgressIndicator(Modifier.fillMaxWidth()) }
                }
            },
            confirmButton = {
                if (!isDownloading) TextButton(onClick = {
                    isDownloading = true
                    scope.launch {
                        val dir = Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS)
                        val r = repository.downloadFile(file.path, dir)
                        isDownloading = false; selectedFile = null
                        snackbarHostState.showSnackbar(if (r.isSuccess) "已下载" else "失败: ${r.exceptionOrNull()?.message}")
                    }
                }) { Text("确认下载") }
            },
            dismissButton = {
                if (!isDownloading) TextButton(onClick = { selectedFile = null }) { Text("取消") }
            }
        )
    }

    // ---- 主界面 ----

    Scaffold(
        topBar = {
            TopAppBar(
                title = {
                    // 面包屑导航
                    ClickableText(
                        text = buildBreadcrumb(currentPath),
                        style = MaterialTheme.typography.titleSmall,
                        onClick = { offset ->
                            val clicked = getPathAtOffset(currentPath, offset)
                            if (clicked != null) currentPath = clicked
                        }
                    )
                },
                navigationIcon = {
                    if (currentPath != "/") {
                        IconButton(onClick = {
                            currentPath = currentPath.substringBeforeLast("/", "/")
                            if (!currentPath.endsWith("/")) currentPath += "/"
                            if (currentPath.isEmpty() || currentPath == "") currentPath = "/"
                        }) { Text("⬅") }
                    }
                },
                actions = {
                    IconButton(onClick = { showNewFolderDialog = true }) { Text("📁+") }
                    IconButton(onClick = { loadFiles() }) { Text("🔄") }
                    IconButton(onClick = onBackToSettings) { Text("⚙️") }
                }
            )
        },
        floatingActionButton = {
            if (onPickFileForUpload != null) {
                FloatingActionButton(onClick = onPickFileForUpload) { Text("＋") }
            }
        },
        snackbarHost = { SnackbarHost(snackbarHostState) }
    ) { padding ->
        Column(modifier = Modifier.padding(padding)) {
            Text(status, Modifier.padding(horizontal = 16.dp, vertical = 4.dp),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant)

            LazyColumn {
                items(files) { file ->
                    val icon = if (file.type == 1) "📁" else "📄"
                    ListItem(
                        headlineContent = { Text("$icon ${file.path}") },
                        supportingContent = { Text("v${file.version} · ${formatSize(file.size)}") },
                        modifier = Modifier.combinedClickable(
                            onClick = {
                                if (file.type == 1) currentPath = file.path  // 进入文件夹
                                else selectedFile = file                       // 下载文件
                            },
                            onLongClick = {
                                if (file.type == 0) deleteTarget = file         // 长按删除
                            }
                        )
                    )
                }
            }
        }
    }
}

/** 构建面包屑 AnnotatedString，每段可点击。 */
private fun buildBreadcrumb(path: String): AnnotatedString {
    val builder = AnnotatedString.Builder()
    builder.pushStringAnnotation("path", "/")
    builder.append("🏠 ")
    builder.pop()
    if (path == "/") return builder.toAnnotatedString()

    val parts = path.trim('/').split('/')
    var accumulated = "/"
    for ((i, part) in parts.withIndex()) {
        builder.append(" › ")
        accumulated += "$part/"
        builder.pushStringAnnotation("path", accumulated)
        builder.append(part)
        builder.pop()
    }
    return builder.toAnnotatedString()
}

/** 根据点击偏移量找到对应的路径。 */
private fun getPathAtOffset(path: String, offset: Int): String? {
    val str = buildBreadcrumb(path)
    return str.getStringAnnotations("path", offset, offset)
        .firstOrNull()?.item
}
