package com.cloudpan.android.ui

import android.os.Environment
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.ExperimentalMaterialApi
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.*
import androidx.compose.material.pullrefresh.PullRefreshIndicator
import androidx.compose.material.pullrefresh.pullRefresh
import androidx.compose.material.pullrefresh.rememberPullRefreshState
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.cloudpan.android.data.AppDatabase
import com.cloudpan.android.data.FileEntryDto
import com.cloudpan.android.data.FileRepository
import com.cloudpan.android.data.OfflineCacheEntity
import kotlinx.coroutines.flow.*
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch
import java.io.File
import java.text.SimpleDateFormat
import java.util.*

// ---- 工具函数 ----

private fun formatSize(bytes: Long): String = when {
    bytes >= 1_073_741_824 -> "%.2f GB".format(bytes / 1_073_741_824.0)
    bytes >= 1_048_576 -> "%.1f MB".format(bytes / 1_048_576.0)
    bytes >= 1024 -> "${bytes / 1024} KB"
    else -> "$bytes B"
}

private fun sortLabel(sortBy: String): String = when (sortBy) {
    "size" -> "按大小"
    "date" -> "按日期"
    else -> "按名称"
}

private fun getFileIcon(file: FileEntryDto): ImageVector {
    if (file.type == 1) return Icons.Default.Folder
    val lower = file.path.lowercase(Locale.ROOT)
    return when {
        lower.matches(Regex(".*\\.(jpg|jpeg|png|gif|bmp|webp|heic|heif)$")) -> Icons.Default.Image
        lower.matches(Regex(".*\\.(mp4|avi|mkv|mov|wmv|flv)$")) -> Icons.Default.Videocam
        lower.matches(Regex(".*\\.(mp3|wav|flac|aac|ogg|wma|m4a)$")) -> Icons.Default.Audiotrack
        lower.matches(Regex(".*\\.(doc|docx|xls|xlsx|ppt|pptx|pdf|csv)$")) -> Icons.Default.Description
        else -> Icons.Default.InsertDriveFile
    }
}

private fun formatTimestamp(iso: String): String {
    return try {
        val sdf = SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss", Locale.getDefault())
        val date = sdf.parse(iso) ?: return iso
        SimpleDateFormat("yyyy-MM-dd HH:mm", Locale.getDefault()).format(date)
    } catch (_: Exception) {
        iso.take(16)
    }
}

private fun fileName(path: String): String =
    path.trimEnd('/').substringAfterLast('/').ifEmpty { "/" }

/** 文件排序。 */
private fun sortFiles(list: List<FileEntryDto>, sortBy: String): List<FileEntryDto> = when (sortBy) {
    "size" -> list.sortedByDescending { it.size }
    "date" -> list.sortedByDescending { it.lastModified }
    else -> list.sortedWith(compareBy({ it.type != 1 }, { it.path }))
}

/** 判断 filePath 是否为 folderPath 的直接子级。 */
private fun isDirectChild(folderPath: String, filePath: String): Boolean {
    val relative = if (filePath.startsWith(folderPath)) {
        filePath.removePrefix(folderPath)
    } else {
        return false
    }
    return !relative.trimEnd('/').contains("/")
}

// ---- 主界面 Composable ----

@OptIn(
    ExperimentalMaterial3Api::class,
    ExperimentalFoundationApi::class,
    ExperimentalMaterialApi::class
)
@Composable
fun FileListScreen(
    repository: FileRepository,
    onBackToSettings: () -> Unit,
    onPickFileForUpload: (() -> Unit)? = null,
    refreshTrigger: Int = 0
) {
    // ---- 状态 ----
    var files by remember { mutableStateOf<List<FileEntryDto>>(emptyList()) }
    var status by remember { mutableStateOf("加载中……") }
    var currentPath by remember { mutableStateOf("/") }
    var searchQuery by remember { mutableStateOf("") }
    var isSearching by remember { mutableStateOf(false) }
    var sortBy by remember { mutableStateOf("name") }
    var selectedFile by remember { mutableStateOf<FileEntryDto?>(null) }
    var isDownloading by remember { mutableStateOf(false) }
    var downloadProgress by remember { mutableStateOf(0f) }
    var downloadBytes by remember { mutableStateOf(0L) }
    var downloadTotal by remember { mutableStateOf(0L) }
    var deleteTarget by remember { mutableStateOf<FileEntryDto?>(null) }
    var showNewFolderDialog by remember { mutableStateOf(false) }
    var newFolderName by remember { mutableStateOf("") }
    var isRefreshing by remember { mutableStateOf(false) }
    var selectedPaths by remember { mutableStateOf<Set<String>>(emptySet()) }
    var isSelectionMode by remember { mutableStateOf(false) }
    var showSortMenu by remember { mutableStateOf(false) }
    var showBulkDeleteDialog by remember { mutableStateOf(false) }

    // 离线缓存下载状态
    var offlineDownloadFile by remember { mutableStateOf<FileEntryDto?>(null) }
    var offlineDownloadProgress by remember { mutableStateOf(0f) }
    var offlineDownloadBytes by remember { mutableStateOf(0L) }
    var offlineDownloadTotal by remember { mutableStateOf(0L) }
    var offlineDownloadFailed by remember { mutableStateOf(false) }
    var offlineDownloadError by remember { mutableStateOf("") }
    var offlineDownloadJob by remember { mutableStateOf<Job?>(null) }
    var offlineCacheRefreshFlag by remember { mutableStateOf(0) }

    val scope = rememberCoroutineScope()
    val snackbarHostState = remember { SnackbarHostState() }
    val context = LocalContext.current
    // FIX 4: 数据库实例提到 Composable 顶层，不在 LazyColumn items 中重复创建
    val db = remember { AppDatabase.getInstance(context) }

    val pullRefreshState = rememberPullRefreshState(
        refreshing = isRefreshing,
        onRefresh = {
            searchQuery = ""
            loadFiles()
        }
    )

    // ---- 加载逻辑 ----

    fun loadFiles() {
        scope.launch {
            isRefreshing = true
            status = "加载中……"
            if (searchQuery.length >= 2) {
                val result = repository.searchFiles(searchQuery)
                result.onSuccess { items ->
                    files = sortFiles(items, sortBy)
                    status = if (items.isEmpty()) "未找到匹配的文件"
                    else "共 ${items.size} 个结果"
                    isSearching = true
                }.onFailure { e ->
                    snackbarHostState.showSnackbar("搜索失败：${e.message}")
                }
            } else {
                isSearching = false
                val result = if (currentPath == "/") repository.getFileTree()
                else repository.getFileTreeInFolder(currentPath)
                result.onSuccess { response ->
                    files = sortFiles(
                        response.data
                            .filter { it.path != currentPath }
                            .filter { isDirectChild(currentPath, it.path) },
                        sortBy
                    )
                    status = if (files.isEmpty()) "" else "${files.size} 个项目"
                }.onFailure { e ->
                    snackbarHostState.showSnackbar("加载失败：${e.message}")
                }
            }
            isRefreshing = false
        }
    }

    // 路径或排序变更时立即重新加载
    LaunchedEffect(currentPath, sortBy) { loadFiles() }

    // FIX 2: 搜索加 300ms debounce
    LaunchedEffect(Unit) {
        snapshotFlow { searchQuery }
            .debounce(300)
            .distinctUntilChanged()
            .collect { loadFiles() }
    }

    // 外部刷新触发
    LaunchedEffect(refreshTrigger) { if (refreshTrigger > 0) loadFiles() }

    // ---- 对话框 ----

    // 删除确认
    if (deleteTarget != null) {
        AlertDialog(
            onDismissRequest = { deleteTarget = null },
            icon = { Icon(Icons.Default.Delete, null, tint = MaterialTheme.colorScheme.error) },
            title = { Text("删除") },
            text = { Text("确定删除 ${deleteTarget!!.path.removePrefix("/")}？") },
            confirmButton = {
                TextButton(onClick = {
                    val f = deleteTarget!!; deleteTarget = null
                    scope.launch {
                        val r = repository.deleteFile(f.path)
                        snackbarHostState.showSnackbar(
                            if (r.isSuccess) "已删除" else "删除失败：${r.exceptionOrNull()?.message}"
                        )
                        loadFiles()
                    }
                }) { Text("删除", color = MaterialTheme.colorScheme.error) }
            },
            dismissButton = { TextButton(onClick = { deleteTarget = null }) { Text("取消") } }
        )
    }

    // FIX 7: 批量删除
    if (showBulkDeleteDialog) {
        AlertDialog(
            onDismissRequest = { showBulkDeleteDialog = false },
            icon = { Icon(Icons.Default.DeleteSweep, null, tint = MaterialTheme.colorScheme.error) },
            title = { Text("批量删除") },
            text = { Text("确定删除已选择的 ${selectedPaths.size} 项？") },
            confirmButton = {
                TextButton(onClick = {
                    showBulkDeleteDialog = false
                    scope.launch {
                        var success = 0; var failed = 0
                        for (path in selectedPaths) {
                            if (repository.deleteFile(path).isSuccess) success++ else failed++
                        }
                        snackbarHostState.showSnackbar(
                            "已删除 ${success} 项" + if (failed > 0) "，${failed} 项失败" else ""
                        )
                        selectedPaths = emptySet()
                        isSelectionMode = false
                        loadFiles()
                    }
                }) { Text("删除", color = MaterialTheme.colorScheme.error) }
            },
            dismissButton = { TextButton(onClick = { showBulkDeleteDialog = false }) { Text("取消") } }
        )
    }

    // 新建文件夹
    if (showNewFolderDialog) {
        AlertDialog(
            onDismissRequest = { showNewFolderDialog = false },
            icon = { Icon(Icons.Default.CreateNewFolder, null) },
            title = { Text("新建文件夹") },
            text = {
                OutlinedTextField(
                    value = newFolderName,
                    onValueChange = { newFolderName = it },
                    label = { Text("名称") },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth()
                )
            },
            confirmButton = {
                TextButton(onClick = {
                    val name = newFolderName; showNewFolderDialog = false; newFolderName = ""
                    val fullPath = currentPath + name + "/"
                    scope.launch {
                        val r = repository.createFolder(fullPath)
                        snackbarHostState.showSnackbar(if (r.isSuccess) "已创建" else "创建失败")
                        loadFiles()
                    }
                }) { Text("创建") }
            },
            dismissButton = { TextButton(onClick = { showNewFolderDialog = false }) { Text("取消") } }
        )
    }

    // FIX 5: 下载对话框——带百分比进度的确定模式 ProgressIndicator
    if (selectedFile != null) {
        val file = selectedFile!!
        AlertDialog(
            onDismissRequest = { if (!isDownloading) selectedFile = null },
            icon = { Icon(Icons.Default.Download, null) },
            title = { Text(if (isDownloading) "下载中" else "下载文件") },
            text = {
                Column {
                    Text(fileName(file.path), fontWeight = FontWeight.Medium)
                    Spacer(Modifier.height(4.dp))
                    Text(formatSize(file.size), style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant)
                    if (isDownloading) {
                        Spacer(Modifier.height(16.dp))
                        LinearProgressIndicator(
                            progress = { downloadProgress },
                            modifier = Modifier
                                .fillMaxWidth()
                                .height(8.dp)
                                .clip(RoundedCornerShape(4.dp))
                        )
                        Spacer(Modifier.height(8.dp))
                        Text(
                            "${formatSize(downloadBytes)} / ${formatSize(downloadTotal)}（${(downloadProgress * 100).toInt()}%）",
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                    }
                }
            },
            confirmButton = {
                if (!isDownloading) TextButton(onClick = {
                    isDownloading = true
                    downloadProgress = 0f
                    downloadBytes = 0L
                    downloadTotal = file.size
                    scope.launch {
                        val dir = Environment.getExternalStoragePublicDirectory(
                            Environment.DIRECTORY_DOWNLOADS
                        )
                        val r = repository.downloadFileWithProgress(
                            file.path, dir
                        ) { downloaded, total ->
                            downloadBytes = downloaded
                            downloadTotal = total
                            downloadProgress = if (total > 0) downloaded.toFloat() / total else 0f
                        }
                        isDownloading = false
                        selectedFile = null
                        snackbarHostState.showSnackbar(
                            if (r.isSuccess) "已下载" else "下载失败：${r.exceptionOrNull()?.message}"
                        )
                    }
                }) { Text("下载到设备") }
            },
            dismissButton = {
                if (!isDownloading) TextButton(onClick = { selectedFile = null }) { Text("取消") }
            }
        )
    }

    // 离线缓存下载对话框（带取消/重试）
    if (offlineDownloadFile != null) {
        val file = offlineDownloadFile!!
        AlertDialog(
            onDismissRequest = {
                if (offlineDownloadJob == null) {
                    offlineDownloadFile = null
                    offlineDownloadFailed = false
                    offlineDownloadError = ""
                }
            },
            icon = { Icon(Icons.Default.CloudDownload, null) },
            title = {
                Text(
                    when {
                        offlineDownloadJob != null -> "下载中……"
                        offlineDownloadFailed -> "下载失败"
                        else -> "离线缓存"
                    }
                )
            },
            text = {
                Column {
                    Text(fileName(file.path), fontWeight = FontWeight.Medium)
                    Spacer(Modifier.height(4.dp))
                    Text(formatSize(file.size), style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant)
                    if (offlineDownloadJob != null) {
                        Spacer(Modifier.height(16.dp))
                        LinearProgressIndicator(
                            progress = { offlineDownloadProgress },
                            modifier = Modifier
                                .fillMaxWidth()
                                .height(8.dp)
                                .clip(RoundedCornerShape(4.dp))
                        )
                        Spacer(Modifier.height(8.dp))
                        Text(
                            "${formatSize(offlineDownloadBytes)} / ${formatSize(offlineDownloadTotal)}（${(offlineDownloadProgress * 100).toInt()}%）",
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                    } else if (offlineDownloadFailed) {
                        Spacer(Modifier.height(12.dp))
                        Text(
                            offlineDownloadError.ifEmpty { "下载失败" },
                            color = MaterialTheme.colorScheme.error,
                            style = MaterialTheme.typography.bodySmall
                        )
                    }
                }
            },
            confirmButton = {
                when {
                    offlineDownloadJob != null -> TextButton(onClick = {
                        offlineDownloadJob?.cancel()
                        offlineDownloadJob = null
                        offlineDownloadFile = null
                        offlineDownloadFailed = false
                        offlineDownloadError = ""
                        // 删除可能的半截 .tmp 文件
                        val cacheDir = File(context.filesDir, "offline_cache")
                        val tmpFile = File(cacheDir, ".${fileName(file.path)}.tmp")
                        try { tmpFile.delete() } catch (_: Exception) {}
                    }) {
                        Text("取消", color = MaterialTheme.colorScheme.error)
                    }
                    offlineDownloadFailed -> TextButton(onClick = {
                        offlineDownloadFile = file
                        offlineDownloadFailed = false
                        offlineDownloadError = ""
                    }) { Text("重试") }
                }
            },
            dismissButton = {
                if (offlineDownloadFailed) {
                    TextButton(onClick = {
                        offlineDownloadFile = null
                        offlineDownloadFailed = false
                        offlineDownloadError = ""
                    }) { Text("关闭") }
                }
            }
        )
    }

    // 离线缓存：对话框打开时自动开始下载
    LaunchedEffect(offlineDownloadFile) {
        val file = offlineDownloadFile ?: return@LaunchedEffect
        if (offlineDownloadFailed) return@LaunchedEffect
        offlineDownloadProgress = 0f
        offlineDownloadBytes = 0L
        offlineDownloadTotal = file.size
        offlineDownloadJob = scope.launch {
            try {
                val cacheDir = File(context.filesDir, "offline_cache")
                cacheDir.mkdirs()
                val r = repository.downloadFileWithProgress(
                    file.path, cacheDir
                ) { downloaded, total ->
                    offlineDownloadBytes = downloaded
                    offlineDownloadTotal = total
                    offlineDownloadProgress = if (total > 0) downloaded.toFloat() / total else 0f
                }
                if (r.isSuccess) {
                    val localFile = r.getOrNull()!!
                    db.offlineCacheDao().insert(
                        OfflineCacheEntity(
                            path = file.path,
                            localPath = localFile.absolutePath,
                            fileHash = file.hash ?: "",
                            fileSize = file.size,
                            cachedAt = java.time.Instant.now().toString()
                        )
                    )
                    offlineDownloadFile = null
                    offlineCacheRefreshFlag++
                    snackbarHostState.showSnackbar("已下载到设备，离线可用")
                } else {
                    offlineDownloadFailed = true
                    offlineDownloadError = r.exceptionOrNull()?.message ?: "下载失败"
                }
            } catch (_: kotlinx.coroutines.CancellationException) {
                // 用户取消，不做处理
            } catch (e: Exception) {
                offlineDownloadFailed = true
                offlineDownloadError = e.message ?: "下载失败"
            } finally {
                offlineDownloadJob = null
            }
        }
    }

    // ---- 主布局 ----

    Scaffold(
        topBar = {
            Column {
                // ---- 搜索栏 ----
                // FIX 1: 🔍 → Icons.Default.Search, ✕ → Icons.Default.Clear
                OutlinedTextField(
                    value = searchQuery,
                    onValueChange = { searchQuery = it },
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(start = 8.dp, end = 8.dp, top = 4.dp, bottom = 0.dp),
                    placeholder = { Text("搜索文件……") },
                    singleLine = true,
                    leadingIcon = { Icon(Icons.Default.Search, "搜索") },
                    trailingIcon = {
                        if (searchQuery.isNotEmpty()) {
                            IconButton(onClick = { searchQuery = "" }) {
                                Icon(Icons.Default.Clear, "清除")
                            }
                        }
                    }
                )

                if (isSelectionMode) {
                    // FIX 7: 选择模式工具栏
                    TopAppBar(
                        title = { Text("已选择 ${selectedPaths.size} 项") },
                        navigationIcon = {
                            IconButton(onClick = {
                                isSelectionMode = false
                                selectedPaths = emptySet()
                            }) { Icon(Icons.Default.Close, "取消选择") }
                        },
                        actions = {
                            TextButton(onClick = {
                                selectedPaths = if (selectedPaths.size == files.size) emptySet()
                                else files.map { it.path }.toSet()
                            }) {
                                Text(if (selectedPaths.size == files.size) "取消全选" else "全选")
                            }
                            if (selectedPaths.isNotEmpty()) {
                                IconButton(onClick = { showBulkDeleteDialog = true }) {
                                    Icon(Icons.Default.Delete, "删除所选",
                                        tint = MaterialTheme.colorScheme.error)
                                }
                            }
                        },
                        colors = TopAppBarDefaults.topAppBarColors(
                            containerColor = MaterialTheme.colorScheme.errorContainer.copy(alpha = 0.3f)
                        )
                    )
                } else if (!isSearching) {
                    // ---- 面包屑 + 操作按钮 ----
                    // FIX 3: 面包屑改用 Row+clickable Text，弃用 ClickableText+offset
                    // FIX 9: 显示短文件名，不会拥挤
                    TopAppBar(
                        title = {
                            Row(
                                modifier = Modifier.horizontalScroll(rememberScrollState()),
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                TextButton(
                                    onClick = { currentPath = "/" },
                                    contentPadding = PaddingValues(horizontal = 4.dp, vertical = 0.dp)
                                ) {
                                    Icon(Icons.Default.Home, "根目录",
                                        modifier = Modifier.size(20.dp))
                                }
                                val parts = currentPath.trim('/').split('/')
                                    .filter { it.isNotEmpty() }
                                parts.forEachIndexed { index, part ->
                                    Text("›",
                                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                                        style = MaterialTheme.typography.titleSmall)
                                    TextButton(
                                        onClick = {
                                            currentPath = "/" + parts.take(index + 1)
                                                .joinToString("/") + "/"
                                        },
                                        contentPadding = PaddingValues(
                                            horizontal = 4.dp, vertical = 0.dp)
                                    ) {
                                        Text(
                                            part,
                                            maxLines = 1,
                                            overflow = TextOverflow.Ellipsis,
                                            fontWeight = if (index == parts.lastIndex)
                                                FontWeight.Bold else FontWeight.Normal
                                        )
                                    }
                                }
                            }
                        },
                        navigationIcon = {
                            if (currentPath != "/") {
                                IconButton(onClick = {
                                    currentPath = currentPath.substringBeforeLast("/", "/")
                                    if (!currentPath.endsWith("/")) currentPath += "/"
                                    if (currentPath == "" || currentPath.isEmpty())
                                        currentPath = "/"
                                }) { Icon(Icons.Default.ArrowBack, "返回上一级") }
                            }
                        },
                        actions = {
                            // FIX 11: 排序按钮改为下拉菜单+文字标签
                            Box {
                                TextButton(onClick = { showSortMenu = true }) {
                                    Text(sortLabel(sortBy),
                                        style = MaterialTheme.typography.labelMedium)
                                    Icon(Icons.Default.ArrowDropDown, "选择排序方式")
                                }
                                DropdownMenu(
                                    expanded = showSortMenu,
                                    onDismissRequest = { showSortMenu = false }
                                ) {
                                    listOf("name" to "按名称排序",
                                        "size" to "按大小排序",
                                        "date" to "按日期排序").forEach { (key, label) ->
                                        DropdownMenuItem(
                                            text = { Text(label) },
                                            onClick = { sortBy = key; showSortMenu = false },
                                            leadingIcon = if (sortBy == key) {
                                                { Icon(Icons.Default.Check, null,
                                                    tint = MaterialTheme.colorScheme.primary) }
                                            } else null
                                        )
                                    }
                                }
                            }

                            IconButton(onClick = { showNewFolderDialog = true }) {
                                Icon(Icons.Default.CreateNewFolder, "新建文件夹")
                            }
                            IconButton(onClick = {
                                searchQuery = ""
                                loadFiles()
                            }) { Icon(Icons.Default.Refresh, "刷新") }
                            IconButton(onClick = onBackToSettings) {
                                Icon(Icons.Default.Settings, "设置")
                            }
                            IconButton(onClick = {
                                scope.launch {
                                    onBackToSettings()
                                    // 重置连接状态，允许修改配置
                                }
                            }) {
                                Icon(Icons.Default.LinkOff, "断开连接")
                            }
                        }
                    )
                } else {
                    TopAppBar(
                        title = { Text("搜索结果") },
                        navigationIcon = {
                            IconButton(onClick = { searchQuery = "" }) {
                                Icon(Icons.Default.ArrowBack, "返回")
                            }
                        }
                    )
                }
            }
        },
        floatingActionButton = {
            if (onPickFileForUpload != null && !isSelectionMode) {
                FloatingActionButton(onClick = onPickFileForUpload) {
                    Icon(Icons.Default.Add, "上传文件")
                }
            }
        },
        snackbarHost = { SnackbarHost(snackbarHostState) }
    ) { padding ->
        // FIX 8: 下拉刷新 (pullRefresh)
        Box(
            modifier = Modifier
                .padding(padding)
                .fillMaxSize()
                .pullRefresh(pullRefreshState)
        ) {
            Column(modifier = Modifier.fillMaxSize()) {
                // FIX 6: 状态栏文案全中文
                if (status.isNotEmpty()) {
                    Text(
                        status,
                        modifier = Modifier.padding(horizontal = 16.dp, vertical = 8.dp),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }

                if (files.isEmpty() && !isRefreshing) {
                    // FIX 10: 空状态视图（插画 + 引导文案）
                    Box(
                        modifier = Modifier.fillMaxSize(),
                        contentAlignment = Alignment.Center
                    ) {
                        Column(
                            horizontalAlignment = Alignment.CenterHorizontally,
                            modifier = Modifier.padding(32.dp)
                        ) {
                            val (icon, title, subtitle) = if (isSearching) {
                                Triple(
                                    Icons.Default.SearchOff,
                                    "未找到匹配的文件",
                                    "尝试其他关键词或清除搜索条件"
                                )
                            } else {
                                Triple(
                                    Icons.Default.FolderOpen,
                                    "此文件夹为空",
                                    "点击右下角 + 上传文件，或点击新建文件夹按钮"
                                )
                            }
                            Icon(
                                icon, null,
                                modifier = Modifier.size(72.dp),
                                tint = MaterialTheme.colorScheme.onSurfaceVariant
                                    .copy(alpha = 0.5f)
                            )
                            Spacer(Modifier.height(16.dp))
                            Text(
                                title,
                                style = MaterialTheme.typography.titleMedium,
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                            )
                            Spacer(Modifier.height(8.dp))
                            Text(
                                subtitle,
                                style = MaterialTheme.typography.bodyMedium,
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                                    .copy(alpha = 0.7f),
                                textAlign = TextAlign.Center
                            )
                        }
                    }
                } else {
                    LazyColumn {
                        items(files, key = { it.path }) { file ->
                            val shortName = fileName(file.path)
                            var isFavorited by remember { mutableStateOf(false) }

                            LaunchedEffect(file.path, offlineCacheRefreshFlag) {
                                isFavorited =
                                    db.offlineCacheDao().getByPath(file.path) != null
                            }

                            val isSelected = file.path in selectedPaths
                            // 强制在离线缓存下载完成后重新查询星标状态
                            val _refresh = offlineCacheRefreshFlag

                            // FIX 1: 📁🖼️📄⭐☆ → Material Icons
                            ListItem(
                                leadingContent = {
                                    Icon(
                                        getFileIcon(file), null,
                                        modifier = Modifier.size(24.dp),
                                        tint = if (file.type == 1)
                                            MaterialTheme.colorScheme.primary
                                        else MaterialTheme.colorScheme.onSurfaceVariant
                                    )
                                },
                                headlineContent = {
                                    Row(verticalAlignment = Alignment.CenterVertically) {
                                        Text(
                                            shortName,
                                            maxLines = 1,
                                            overflow = TextOverflow.Ellipsis,
                                            style = MaterialTheme.typography.bodyLarge,
                                            color = if (isSelected)
                                                MaterialTheme.colorScheme.primary
                                            else MaterialTheme.colorScheme.onSurface
                                        )
                                        if (isFavorited) {
                                            Spacer(Modifier.width(4.dp))
                                            Icon(
                                                Icons.Default.Star, "已收藏",
                                                modifier = Modifier.size(16.dp),
                                                tint = MaterialTheme.colorScheme.tertiary
                                            )
                                        }
                                    }
                                },
                                supportingContent = {
                                    Text(
                                        formatSize(file.size) +
                                        if (file.type == 0)
                                            " · ${formatTimestamp(file.lastModified)}" else "",
                                        style = MaterialTheme.typography.bodySmall
                                    )
                                },
                                colors = ListItemDefaults.colors(
                                    containerColor = if (isSelected)
                                        MaterialTheme.colorScheme.primaryContainer
                                            .copy(alpha = 0.3f)
                                    else Color.Transparent
                                ),
                                modifier = Modifier.combinedClickable(
                                    onClick = {
                                        if (isSelectionMode) {
                                            selectedPaths = if (isSelected) {
                                                val newSet = selectedPaths - file.path
                                                if (newSet.isEmpty()) isSelectionMode = false
                                                newSet
                                            } else selectedPaths + file.path
                                        } else {
                                            if (file.type == 1) currentPath = file.path
                                            else selectedFile = file
                                        }
                                    },
                                    onLongClick = {
                                        if (!isSelectionMode) {
                                            isSelectionMode = true
                                            selectedPaths = setOf(file.path)
                                        }
                                    }
                                ),
                                trailingContent = {
                                    if (file.type == 0 && !isSelectionMode) {
                                        IconButton(onClick = {
                                            scope.launch {
                                                if (isFavorited) {
                                                    // 先查数据库获取 localPath 再删除本地文件
                                                    val cached = db.offlineCacheDao()
                                                        .getByPath(file.path)
                                                    if (cached != null) {
                                                        try { File(cached.localPath).delete() }
                                                        catch (_: Exception) {}
                                                    }
                                                    db.offlineCacheDao()
                                                        .deleteByPath(file.path)
                                                    isFavorited = false
                                                    snackbarHostState
                                                        .showSnackbar("已从设备移除，下次需要时重新下载")
                                                } else {
                                                    offlineDownloadFile = file
                                                    offlineDownloadFailed = false
                                                    offlineDownloadError = ""
                                                }
                                            }
                                        }) {
                                            Icon(
                                                if (isFavorited) Icons.Default.CloudDownload
                                                else Icons.Default.Download,
                                                if (isFavorited) "移除离线缓存" else "下载到设备（离线可用）",
                                                tint = if (isFavorited)
                                                    MaterialTheme.colorScheme.primary
                                                else MaterialTheme.colorScheme.onSurfaceVariant
                                            )
                                        }
                                    }
                                }
                            )
                        }
                    }
                }
            }

            // FIX 8: 下拉刷新指示器
            PullRefreshIndicator(
                refreshing = isRefreshing,
                state = pullRefreshState,
                modifier = Modifier.align(Alignment.TopCenter)
            )
        }
    }
}
