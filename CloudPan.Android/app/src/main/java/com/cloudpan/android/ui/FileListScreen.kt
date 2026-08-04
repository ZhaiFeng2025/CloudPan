package com.cloudpan.android.ui

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
import android.os.Environment
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.GridItemSpan
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.pager.HorizontalPager
import androidx.compose.foundation.pager.rememberPagerState
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
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import com.cloudpan.android.UploadUiState
import com.cloudpan.android.data.AppDatabase
import com.cloudpan.android.data.ThumbnailLoader
import com.cloudpan.android.data.FileConflictException
import com.cloudpan.android.data.FileEntryDto
import com.cloudpan.android.data.FileRepository
import com.cloudpan.android.data.OfflineCacheEntity
import com.cloudpan.android.data.TrashItem
import com.cloudpan.android.data.toUserMessage
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

// ---- T-113：照片墙辅助（缩略图/网格/预览） ----

/** 判断是否为图片文件——扩展名与服务端 ThumbnailService.SupportedExts 对齐（含 heic/heif）。 */
private fun isImagePath(path: String): Boolean {
    val lower = path.lowercase(Locale.ROOT)
    return lower.matches(Regex(".*\\.(jpg|jpeg|png|gif|bmp|webp|heic|heif)$"))
}

/** 时间分组键（yyyy-MM）。lastModified 为 ISO 字符串，取前 7 位即月分组；解析失败归入「未知时间」。 */
private fun monthKey(lastModified: String): String = lastModified.take(7).ifEmpty { "未知时间" }

/** 月份标签展示：2026-08 → 「2026 年 8 月」；非 yyyy-MM 键原样返回。 */
private fun monthLabel(key: String): String {
    val parts = key.split("-")
    return if (parts.size == 2) "${parts[0]} 年 ${parts[1].toIntOrNull() ?: parts[1]} 月" else key
}

/**
 * 缩略图渲染（T-113）：内存→磁盘→网络三级加载（ThumbnailLoader），
 * 加载中/失败统一降级为类型图标（Image），不崩溃；滚动离开时协程取消即中止加载。
 */
@Composable
private fun ThumbnailImage(
    loader: ThumbnailLoader,
    path: String,
    widthPx: Int,
    modifier: Modifier = Modifier,
    contentScale: ContentScale = ContentScale.Crop,
    contentDescription: String? = null
) {
    val bitmap by produceState<Bitmap?>(initialValue = null, key1 = path, key2 = widthPx) {
        value = loader.load(path, widthPx)
    }
    val bmp = bitmap
    if (bmp != null) {
        Image(
            bitmap = bmp.asImageBitmap(),
            contentDescription = contentDescription,
            contentScale = contentScale,
            modifier = modifier
        )
    } else {
        Icon(
            Icons.Default.Image, contentDescription,
            modifier = modifier,
            tint = MaterialTheme.colorScheme.onSurfaceVariant
        )
    }
}

// ---- T-107：文件同步状态（FileEntryDto.state，与 shared-spec.json → CloudPan.Contract.FileState 一致） ----

private const val FILE_STATE_SYNCED = 0
private const val FILE_STATE_MODIFIED = 1
private const val FILE_STATE_DELETING = 2
private const val FILE_STATE_CLOUD_ONLY = 3
private const val FILE_STATE_DOWNLOADING = 4 // 客户端瞬态，服务端不落库
private const val FILE_STATE_UPLOADING = 5   // 客户端瞬态，服务端不落库
private const val FILE_STATE_CONFLICT = 7

/** 同步状态 →（图标, 颜色）双通道。与 Windows ResolveBrowseState 同语义（visual-kb §5：不只靠颜色，WCAG 1.4.1）。 */
private fun syncStateChannel(state: Int): Pair<ImageVector, Color> = when (state) {
    FILE_STATE_MODIFIED, FILE_STATE_UPLOADING, FILE_STATE_DOWNLOADING, FILE_STATE_DELETING ->
        Icons.Default.Sync to Color(0xFF2196F3)     // 进行中（对齐 AccentBlue）
    FILE_STATE_CLOUD_ONLY -> Icons.Default.Cloud to Color(0xFF757575) // 仅云端（对齐 TextMuted）
    FILE_STATE_CONFLICT -> Icons.Default.Warning to Color(0xFFFF9800) // 冲突（对齐 WarningOrange）
    else -> Icons.Default.Check to Color(0xFF4CAF50) // 已同步（对齐 SuccessGreen）
}

/** 同步状态无障碍文案。 */
private fun syncStateLabel(state: Int): String = when (state) {
    FILE_STATE_MODIFIED -> "已修改"
    FILE_STATE_DELETING -> "删除中"
    FILE_STATE_CLOUD_ONLY -> "仅云端"
    FILE_STATE_DOWNLOADING -> "下载中"
    FILE_STATE_UPLOADING -> "上传中"
    FILE_STATE_CONFLICT -> "冲突"
    else -> "已同步"
}

/**
 * T-107：当前列表同步汇总（仅统计文件，不含文件夹）。返回（图标, 颜色, 文案）；
 * 无文件时返回 null 不渲染。文案给家人『备份完成』感知，如「已备份 3 项 · 备份完成」。
 */
private fun syncSummary(files: List<FileEntryDto>): Triple<ImageVector, Color, String>? {
    val states = files.mapNotNull { if (it.type == 0) it.state else null }
    if (states.isEmpty()) return null
    val total = states.size
    val synced = states.count { it == FILE_STATE_SYNCED }
    val conflicts = states.count { it == FILE_STATE_CONFLICT }
    val inFlight = states.count {
        it == FILE_STATE_MODIFIED || it == FILE_STATE_UPLOADING ||
            it == FILE_STATE_DOWNLOADING || it == FILE_STATE_DELETING
    }
    val parts = mutableListOf("已备份 $synced 项")
    if (conflicts > 0) parts.add("$conflicts 项冲突")
    if (synced == total) parts.add("备份完成") else if (inFlight > 0) parts.add("$inFlight 项同步中")
    val (icon, color) = when {
        conflicts > 0 -> Icons.Default.Warning to Color(0xFFFF9800)
        inFlight > 0 -> Icons.Default.Sync to Color(0xFF2196F3)
        else -> Icons.Default.Check to Color(0xFF4CAF50)
    }
    return Triple(icon, color, parts.joinToString(" · "))
}

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
    // T-114：上传目标目录选择——目录选择器确认后回调目标目录，由宿主启动系统文件选择器
    onPickUploadTarget: ((String) -> Unit)? = null,
    refreshTrigger: Int = 0,
    // T-091：手动上传流程状态（上传中/成功/失败），上传中 FAB 显示进度指示，结束后 Snackbar 反馈
    uploadState: UploadUiState? = null,
    onUploadStateHandled: () -> Unit = {}
) {
    // ---- 状态 ----
    var files by remember { mutableStateOf<List<FileEntryDto>>(emptyList()) }
    var status by remember { mutableStateOf("加载中……") }
    var currentPath by remember { mutableStateOf("/") }
    var searchQuery by remember { mutableStateOf("") }
    var isSearching by remember { mutableStateOf(false) }
    var sortBy by remember { mutableStateOf("name") }
    var selectedFile by remember { mutableStateOf<FileEntryDto?>(null) }
    // T-112：分享目标文件（打开分享对话框：生成链接 + 复制/发送 + 撤销）
    var shareTarget by remember { mutableStateOf<FileEntryDto?>(null) }
    var isDownloading by remember { mutableStateOf(false) }
    var downloadProgress by remember { mutableStateOf(0f) }
    var downloadBytes by remember { mutableStateOf(0L) }
    var downloadTotal by remember { mutableStateOf(0L) }
    var deleteTarget by remember { mutableStateOf<FileEntryDto?>(null) }
    // T-089：删除 409 冲突待决（文件已被其他设备修改）——单个删除目标 / 批量删除冲突路径列表
    var deleteConflictTarget by remember { mutableStateOf<FileEntryDto?>(null) }
    var bulkDeleteConflicts by remember { mutableStateOf<List<String>?>(null) }
    var showNewFolderDialog by remember { mutableStateOf(false) }
    var newFolderName by remember { mutableStateOf("") }
    var isRefreshing by remember { mutableStateOf(false) }
    var selectedPaths by remember { mutableStateOf<Set<String>>(emptySet()) }
    var isSelectionMode by remember { mutableStateOf(false) }
    var showSortMenu by remember { mutableStateOf(false) }
    // ---- T-059：分页状态（nextCursor 增量加载） ----
    var nextCursor by remember { mutableStateOf<String?>(null) }
    var hasMore by remember { mutableStateOf(false) }
    var isLoadingMore by remember { mutableStateOf(false) }
    // 已自动发起加载的游标（防止失败后无限重试；翻页成功后游标变化自然推进）
    var lastAutoLoadCursor by remember { mutableStateOf<String?>(null) }
    var showBulkDeleteDialog by remember { mutableStateOf(false) }
    var showTrashDialog by remember { mutableStateOf(false) } // T-050：回收站入口
    // T-113：照片墙——列表/网格视图切换 + 全屏预览索引（photos 顺序下标，null=未预览）
    var viewMode by remember { mutableStateOf("list") }
    var previewIndex by remember { mutableStateOf<Int?>(null) }
    // T-114：长按菜单目标 / 移动目标 / 重命名目标 / 上传目录选择器
    var contextMenuTarget by remember { mutableStateOf<FileEntryDto?>(null) }
    var moveTarget by remember { mutableStateOf<FileEntryDto?>(null) }
    var renameTarget by remember { mutableStateOf<FileEntryDto?>(null) }
    var renameName by remember { mutableStateOf("") }
    var showUploadDirPicker by remember { mutableStateOf(false) }

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
    val density = LocalDensity.current
    // FIX 4: 数据库实例提到 Composable 顶层，不在 LazyColumn items 中重复创建
    val db = remember { AppDatabase.getInstance(context) }
    // T-113：缩略图加载器（内存/磁盘缓存 + 并发上限），FileListScreen 生命周期内复用，不进 cacheDir 外再落盘
    val thumbnailLoader = remember { ThumbnailLoader(repository, File(context.cacheDir, "thumbnails")) }

    // ---- 加载逻辑 ----

    /** 分页状态提示：空结果与 hasMore 分别表达。 */
    fun updateStatus() {
        status = when {
            files.isEmpty() && hasMore -> "正在加载更多……"
            files.isEmpty() -> ""
            hasMore -> "已加载 ${files.size} 项 · 滚动加载更多"
            else -> "${files.size} 个项目"
        }
    }

    fun loadFiles() {
        scope.launch {
            isRefreshing = true
            status = "加载中……"
            // T-059：重置分页状态（搜索/切目录/刷新都从头开始）
            nextCursor = null
            hasMore = false
            isLoadingMore = false
            lastAutoLoadCursor = null
            if (searchQuery.length >= 2) {
                val result = repository.searchFiles(searchQuery)
                result.onSuccess { items ->
                    files = sortFiles(items, sortBy)
                    status = if (items.isEmpty()) "未找到匹配的文件"
                    else "共 ${items.size} 个结果"
                    isSearching = true
                }.onFailure { e ->
                    snackbarHostState.showSnackbar("搜索失败：${e.toUserMessage()}")
                }
            } else {
                isSearching = false
                val result = if (currentPath == "/") repository.getFileTree()
                else repository.getFileTreeInFolder(currentPath)
                result.onSuccess { response ->
                    // 服务端按 Path 返回整个子树，UI 只取当前目录的直系子项
                    files = sortFiles(
                        response.data
                            .filter { it.path != currentPath }
                            .filter { isDirectChild(currentPath, it.path) },
                        sortBy
                    )
                    nextCursor = response.nextCursor
                    hasMore = response.hasMore
                    updateStatus()
                }.onFailure { e ->
                    snackbarHostState.showSnackbar("加载失败：${e.toUserMessage()}")
                }
            }
            isRefreshing = false
        }
    }

    /** T-059：滚动到底自动加载下一页（nextCursor 增量），去重后拼接直系子项。 */
    fun loadMore() {
        if (!hasMore || nextCursor == null || isLoadingMore) return
        scope.launch {
            isLoadingMore = true
            val path = currentPath
            val result = if (path == "/") repository.getFileTree(cursor = nextCursor)
            else repository.getFileTreeInFolder(path, cursor = nextCursor)
            result.onSuccess { response ->
                // 请求期间已切换目录/刷新，丢弃过期页，避免串目录拼接（无重复无丢项）
                if (currentPath != path) {
                    nextCursor = null
                    hasMore = false
                    return@onSuccess
                }
                val direct = response.data
                    .filter { it.path != path }
                    .filter { isDirectChild(path, it.path) }
                // 去重拼接：服务端游标保证不与已加载页重叠，此处按 path 兜底防边界重复
                val existing = files.map { it.path }.toSet()
                files = sortFiles(files + direct.filter { it.path !in existing }, sortBy)
                nextCursor = response.nextCursor
                hasMore = response.hasMore
                updateStatus()
            }.onFailure { e ->
                snackbarHostState.showSnackbar("加载更多失败：${e.toUserMessage()}")
            }
            isLoadingMore = false
        }
    }

    val pullRefreshState = rememberPullRefreshState(
        refreshing = isRefreshing,
        onRefresh = {
            searchQuery = ""
            loadFiles()
        }
    )

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

    // T-091：手动上传结果 Snackbar 反馈（成功/失败白话提示），展示后回调清除状态；
    // 上传中分支不清除状态，保证 FAB 进度指示与防重复点击在上传期间持续生效
    LaunchedEffect(uploadState) {
        val state = uploadState ?: return@LaunchedEffect
        when (state) {
            is UploadUiState.Uploading -> Unit
            is UploadUiState.Success -> {
                snackbarHostState.showSnackbar("已上传「${state.fileName}」")
                onUploadStateHandled()
            }
            is UploadUiState.Failed -> {
                snackbarHostState.showSnackbar("上传失败：「${state.fileName}」${state.message}")
                onUploadStateHandled()
            }
        }
    }

    // ---- 对话框 ----

    // 删除确认（T-050：软删进回收站可恢复，确认文案白话化）
    if (deleteTarget != null) {
        AlertDialog(
            onDismissRequest = { deleteTarget = null },
            icon = { Icon(Icons.Default.Delete, null, tint = MaterialTheme.colorScheme.error) },
            title = { Text("删除") },
            text = { Text("将「${fileName(deleteTarget!!.path)}」移入回收站，可在回收站恢复") },
            confirmButton = {
                TextButton(onClick = {
                    val f = deleteTarget!!; deleteTarget = null
                    scope.launch {
                        // T-089：携带文件列表中的当前版本（baseVersion），不再恒传 0
                        val r = repository.deleteFile(f.path, f.version)
                        loadFiles() // 先刷新列表，Snackbar 挂起等待不阻塞
                        if (r.isSuccess) {
                            val trashItem = r.getOrNull()
                            if (trashItem != null) {
                                val result = snackbarHostState.showSnackbar(
                                    message = "已删除，可撤销",
                                    actionLabel = "撤销",
                                    withDismissAction = true,
                                    duration = SnackbarDuration.Short
                                )
                                if (result == SnackbarResult.ActionPerformed) {
                                    val ok = repository.restoreTrash(trashItem.trashFileName).isSuccess
                                    snackbarHostState.showSnackbar(
                                        if (ok) "已恢复" else "恢复失败，请到回收站查看"
                                    )
                                    if (ok) loadFiles()
                                }
                            } else {
                                snackbarHostState.showSnackbar("已删除")
                            }
                        } else if (r.exceptionOrNull() is FileConflictException) {
                            // T-089：文件已被其他设备修改，弹窗让用户决定强制删除或跳过，不静默
                            deleteConflictTarget = f
                        } else {
                            snackbarHostState.showSnackbar("删除失败：${r.exceptionOrNull().toUserMessage()}")
                        }
                    }
                }) { Text("删除", color = MaterialTheme.colorScheme.error) }
            },
            dismissButton = { TextButton(onClick = { deleteTarget = null }) { Text("取消") } }
        )
    }

    // T-089：删除冲突（409）——文件已被其他设备修改，让用户决定强制删除（覆盖）或跳过，不静默
    if (deleteConflictTarget != null) {
        AlertDialog(
            onDismissRequest = { deleteConflictTarget = null },
            icon = { Icon(Icons.Default.Warning, null, tint = MaterialTheme.colorScheme.error) },
            title = { Text("删除冲突") },
            text = { Text("「${fileName(deleteConflictTarget!!.path)}」已被其他设备修改，仍要删除吗？") },
            confirmButton = {
                TextButton(onClick = {
                    val f = deleteConflictTarget!!; deleteConflictTarget = null
                    scope.launch {
                        // 覆盖：baseVersion=0 强制删除（不校验版本，服务端版本递增）
                        val r = repository.deleteFile(f.path, 0)
                        loadFiles()
                        snackbarHostState.showSnackbar(
                            if (r.isSuccess) "已删除" else "删除失败：${r.exceptionOrNull().toUserMessage()}"
                        )
                    }
                }) { Text("仍删除", color = MaterialTheme.colorScheme.error) }
            },
            dismissButton = { TextButton(onClick = { deleteConflictTarget = null }) { Text("跳过") } }
        )
    }

    // FIX 7: 批量删除（T-050：软删进回收站可恢复，确认文案白话化）
    if (showBulkDeleteDialog) {
        AlertDialog(
            onDismissRequest = { showBulkDeleteDialog = false },
            icon = { Icon(Icons.Default.DeleteSweep, null, tint = MaterialTheme.colorScheme.error) },
            title = { Text("批量删除") },
            text = { Text("将已选择的 ${selectedPaths.size} 项移入回收站，可在回收站恢复") },
            confirmButton = {
                TextButton(onClick = {
                    showBulkDeleteDialog = false
                    scope.launch {
                        var success = 0; var failed = 0
                        val conflictPaths = mutableListOf<String>()
                        val deletedItems = mutableListOf<TrashItem>()
                        for (path in selectedPaths) {
                            // T-089：携带文件列表中的当前版本（baseVersion），不再恒传 0
                            val version = files.firstOrNull { it.path == path }?.version ?: 0
                            val r = repository.deleteFile(path, version)
                            if (r.isSuccess) {
                                success++
                                r.getOrNull()?.let { deletedItems.add(it) }
                            } else if (r.exceptionOrNull() is FileConflictException) {
                                conflictPaths.add(path)
                            } else failed++
                        }
                        selectedPaths = emptySet()
                        isSelectionMode = false
                        loadFiles() // 先刷新列表，Snackbar 挂起等待不阻塞
                        if (conflictPaths.isNotEmpty()) {
                            // T-089：冲突项弹窗让用户决定强制删除或跳过，不静默
                            bulkDeleteConflicts = conflictPaths
                            return@launch
                        }
                        if (success > 0 && deletedItems.isNotEmpty()) {
                            val result = snackbarHostState.showSnackbar(
                                message = "已删除 ${success} 项，可撤销" +
                                    if (failed > 0) "，${failed} 项失败" else "",
                                actionLabel = "撤销",
                                withDismissAction = true,
                                duration = SnackbarDuration.Short
                            )
                            if (result == SnackbarResult.ActionPerformed) {
                                var restored = 0
                                for (item in deletedItems) {
                                    if (repository.restoreTrash(item.trashFileName).isSuccess) restored++
                                }
                                snackbarHostState.showSnackbar(
                                    if (restored > 0) "已恢复 ${restored} 项" else "恢复失败，请到回收站查看"
                                )
                                if (restored > 0) loadFiles()
                            }
                        } else {
                            snackbarHostState.showSnackbar(
                                "已删除 ${success} 项" + if (failed > 0) "，${failed} 项失败" else ""
                            )
                        }
                    }
                }) { Text("删除", color = MaterialTheme.colorScheme.error) }
            },
            dismissButton = { TextButton(onClick = { showBulkDeleteDialog = false }) { Text("取消") } }
        )
    }

    // T-089：批量删除冲突（409）——冲突项未被删除，让用户决定强制删除（覆盖）或跳过，不静默
    bulkDeleteConflicts?.let { conflicts ->
        AlertDialog(
            onDismissRequest = { bulkDeleteConflicts = null },
            icon = { Icon(Icons.Default.Warning, null, tint = MaterialTheme.colorScheme.error) },
            title = { Text("删除冲突") },
            text = { Text("${conflicts.size} 项已被其他设备修改，未删除。是否强制删除这些项？") },
            confirmButton = {
                TextButton(onClick = {
                    bulkDeleteConflicts = null
                    scope.launch {
                        // 覆盖：baseVersion=0 强制删除（不校验版本，服务端版本递增）
                        var forceDeleted = 0; var forceFailed = 0
                        for (path in conflicts) {
                            val r = repository.deleteFile(path, 0)
                            if (r.isSuccess) forceDeleted++ else forceFailed++
                        }
                        loadFiles()
                        snackbarHostState.showSnackbar(
                            "已强制删除 ${forceDeleted} 项" +
                                if (forceFailed > 0) "，${forceFailed} 项失败" else ""
                        )
                    }
                }) { Text("强制删除", color = MaterialTheme.colorScheme.error) }
            },
            dismissButton = { TextButton(onClick = { bulkDeleteConflicts = null }) { Text("跳过") } }
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
                    // T-069：新建目录不再拼接尾斜杠——与服务端「目录无尾斜杠」约定、Windows 客户端
                    // mkdir 一致；currentPath 可能带或不带尾斜杠（进入文件夹时来自服务端路径），兜底补分隔符
                    val fullPath = (if (currentPath.endsWith("/")) currentPath else "$currentPath/") + name
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

    // T-114：重命名对话框（POST /api/files/move，oldPath→parent/新名称；服务端 409=目标已存在）
    if (renameTarget != null) {
        AlertDialog(
            onDismissRequest = { renameTarget = null },
            icon = { Icon(Icons.Default.Edit, null) },
            title = { Text("重命名") },
            text = {
                OutlinedTextField(
                    value = renameName,
                    onValueChange = { renameName = it },
                    label = { Text("新名称") },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth()
                )
            },
            confirmButton = {
                TextButton(onClick = {
                    val f = renameTarget!!; renameTarget = null
                    val name = renameName.trim()
                    if (name.isEmpty() || name == fileName(f.path)) {
                        // 名称为空或未变更，无需操作
                    } else {
                        scope.launch {
                            // 目标为原路径所在目录 + 新名称（目录无尾斜杠，对齐 T-069 服务端约定）
                            val parent = f.path.substringBeforeLast("/", "/").ifEmpty { "/" }
                            val newPath = if (parent == "/") "/$name" else "$parent/$name"
                            val r = repository.moveFile(f.path, newPath, f.version)
                            loadFiles()
                            val msg = when {
                                r.isSuccess -> "已重命名为「$name」"
                                r.exceptionOrNull() is FileConflictException ->
                                    "重命名失败：目标位置已有同名文件"
                                else -> "重命名失败：${r.exceptionOrNull().toUserMessage()}"
                            }
                            snackbarHostState.showSnackbar(msg)
                        }
                    }
                }) { Text("确定") }
            },
            dismissButton = { TextButton(onClick = { renameTarget = null }) { Text("取消") } }
        )
    }

    // T-114：移动目标目录选择（长按菜单「移动」进入，移动到所选目录）
    if (moveTarget != null) {
        val f = moveTarget!!
        DirectoryPickerDialog(
            repository = repository,
            title = "移动「${fileName(f.path)}」到",
            confirmLabel = "移动到此处",
            startDir = f.path.substringBeforeLast("/", "/").ifEmpty { "/" },
            onConfirm = { targetDir ->
                moveTarget = null
                val newPath = FileRepository.joinRemotePath(targetDir, fileName(f.path))
                // 目标即文件所在目录（无需移动），或目录移动到自身/子目录（服务端将 500 回滚，前端先行拦截）
                val invalidTarget = newPath == f.path ||
                    (f.type == 1 && newPath.startsWith(f.path.trimEnd('/') + "/"))
                if (invalidTarget) {
                    snackbarHostState.showSnackbar("不能移动到当前目录或其子目录")
                } else {
                    scope.launch {
                        val r = repository.moveFile(f.path, newPath, f.version)
                        loadFiles()
                        val msg = when {
                            r.isSuccess -> "已移动到「$targetDir」"
                            r.exceptionOrNull() is FileConflictException ->
                                "移动失败：目标位置已有同名文件"
                            else -> "移动失败：${r.exceptionOrNull().toUserMessage()}"
                        }
                        snackbarHostState.showSnackbar(msg)
                    }
                }
            },
            onDismiss = { moveTarget = null }
        )
    }

    // T-114：上传目标目录选择（FAB 上传前先选目录，替代硬编码 /Uploads/）
    if (showUploadDirPicker) {
        DirectoryPickerDialog(
            repository = repository,
            title = "选择上传目录",
            confirmLabel = "选择此目录",
            startDir = currentPath,
            onConfirm = { dir ->
                showUploadDirPicker = false
                onPickUploadTarget?.invoke(dir)
            },
            onDismiss = { showUploadDirPicker = false }
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
                            // Material3 1.1.x 进度参数为 Float（BOM 2023.10.01）
                            progress = downloadProgress,
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
                    // T-112：分享入口（仅非下载中可点），复用当前选中文件生成分享链接；
                    // 先关闭下载对话框，避免两个 AlertDialog 叠放
                    if (!isDownloading) {
                        Spacer(Modifier.height(12.dp))
                        OutlinedButton(
                            onClick = {
                                selectedFile = null
                                shareTarget = file
                            },
                            modifier = Modifier.fillMaxWidth()
                        ) { Text("生成分享链接") }
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
                            if (r.isSuccess) "已下载" else "下载失败：${r.exceptionOrNull().toUserMessage()}"
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
                            progress = offlineDownloadProgress,
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
                    offlineDownloadError = r.exceptionOrNull().toUserMessage()
                }
            } catch (_: kotlinx.coroutines.CancellationException) {
                // 用户取消，不做处理
            } catch (e: Exception) {
                offlineDownloadFailed = true
                offlineDownloadError = e.toUserMessage()
            } finally {
                offlineDownloadJob = null
            }
        }
    }

    // T-050：回收站对话框（最近删除列表 + 恢复/清空）
    if (showTrashDialog) {
        TrashDialog(
            repository = repository,
            snackbarHostState = snackbarHostState,
            onDismiss = { showTrashDialog = false }
        )
    }

    // T-112：分享对话框（生成分享链接 + 复制/系统发送 + 撤销）
    shareTarget?.let { file ->
        ShareDialog(
            file = file,
            repository = repository,
            snackbarHostState = snackbarHostState,
            onDismiss = { shareTarget = null }
        )
    }

    // ---- T-113：照片墙数据——当前目录图片文件按时间倒序，yyyy-MM 分组；网格与全屏预览共用同一顺序 ----
    val photos = remember(files) {
        files.filter { it.type == 0 && isImagePath(it.path) }
            .sortedByDescending { it.lastModified }
    }
    val groupedPhotos = remember(photos) {
        photos.groupBy { monthKey(it.lastModified) }
            .toList()
            .sortedByDescending { it.first } // 新月份在前
    }
    val photoIndexByPath = remember(photos) {
        photos.mapIndexed { index, p -> p.path to index }.toMap()
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

                            // T-113：照片墙视图切换（列表/网格）
                            IconButton(onClick = {
                                viewMode = if (viewMode == "list") "grid" else "list"
                            }) {
                                Icon(
                                    if (viewMode == "list") Icons.Default.GridView else Icons.Default.ViewList,
                                    if (viewMode == "list") "切换到照片墙" else "切换到列表"
                                )
                            }
                            IconButton(onClick = { showNewFolderDialog = true }) {
                                Icon(Icons.Default.CreateNewFolder, "新建文件夹")
                            }
                            // T-050：回收站入口（最近删除，可恢复/清空）
                            IconButton(onClick = { showTrashDialog = true }) {
                                Icon(Icons.Default.DeleteSweep, "回收站")
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
            if (onPickUploadTarget != null && !isSelectionMode) {
                // T-091：上传中 FAB 显示进度指示，且禁止重复点击触发二次上传
                val isUploading = uploadState is UploadUiState.Uploading
                FloatingActionButton(
                    // T-114：先弹「选择上传目录」对话框，确认后再由宿主启动系统文件选择器
                    onClick = { if (!isUploading) showUploadDirPicker = true }
                ) {
                    if (isUploading) {
                        // T-105：大文件分块上传显示确定进度百分比；小文件直传保持不确定进度
                        val uploadProgress = (uploadState as? UploadUiState.Uploading)?.progress
                        if (uploadProgress != null) {
                            CircularProgressIndicator(
                                progress = uploadProgress,
                                modifier = Modifier.size(24.dp),
                                strokeWidth = 2.dp,
                                color = MaterialTheme.colorScheme.onPrimaryContainer
                            )
                        } else {
                            CircularProgressIndicator(
                                modifier = Modifier.size(24.dp),
                                strokeWidth = 2.dp,
                                color = MaterialTheme.colorScheme.onPrimaryContainer
                            )
                        }
                    } else {
                        Icon(Icons.Default.Add, "上传文件")
                    }
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

                // T-107：同步汇总（已备份 N 项/冲突 M 项），图标+颜色双通道给家人『备份完成』感知
                val summary = syncSummary(files)
                if (summary != null) {
                    Row(
                        modifier = Modifier.padding(horizontal = 16.dp, vertical = 4.dp),
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        Icon(
                            summary.first, null,
                            modifier = Modifier.size(16.dp),
                            tint = summary.second
                        )
                        Spacer(Modifier.width(6.dp))
                        Text(
                            summary.third,
                            style = MaterialTheme.typography.bodySmall,
                            color = summary.second
                        )
                    }
                }

                // T-059：仅当分页已翻完（!hasMore）且无子项时才显示空状态；
                // 若首屏直系子项为空但 hasMore 仍为真，则继续走 LazyColumn 触发增量加载
                if (files.isEmpty() && !isRefreshing && !hasMore) {
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
                } else if (viewMode == "grid") {
                    // T-113：照片墙网格视图（yyyy-MM 分组，点击进入全屏预览）
                    // T-113：网格照片墙自动翻页（T-059 分页）——hasMore 时连续增量加载，
                    // 目录照片 >200 项时网格也能滚动浏览全部（仅文件元数据翻页，缩略图仍按需加载）
                    LaunchedEffect(viewMode, hasMore, isLoadingMore) {
                        if (viewMode == "grid" && hasMore && !isLoadingMore) {
                            val cur = nextCursor
                            if (cur != null && cur != lastAutoLoadCursor) {
                                lastAutoLoadCursor = cur
                                loadMore()
                            }
                        }
                    }
                    if (photos.isEmpty()) {
                        Box(
                            modifier = Modifier.fillMaxSize(),
                            contentAlignment = Alignment.Center
                        ) {
                            Column(horizontalAlignment = Alignment.CenterHorizontally) {
                                Icon(
                                    Icons.Default.Photo, null,
                                    modifier = Modifier.size(72.dp),
                                    tint = MaterialTheme.colorScheme.onSurfaceVariant
                                        .copy(alpha = 0.5f)
                                )
                                Spacer(Modifier.height(16.dp))
                                Text(
                                    if (hasMore) "正在加载照片……" else "此文件夹暂无照片",
                                    style = MaterialTheme.typography.titleMedium,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant
                                )
                            }
                        }
                    } else {
                        PhotoGrid(
                            groupedPhotos = groupedPhotos,
                            photoIndexByPath = photoIndexByPath,
                            thumbnailLoader = thumbnailLoader,
                            onPhotoClick = { previewIndex = it }
                        )
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

                            Box(modifier = Modifier.fillMaxWidth()) {
                            // FIX 1: 📁🖼️📄⭐☆ → Material Icons
                            ListItem(
                                leadingContent = {
                                    if (file.type == 0 && isImagePath(file.path)) {
                                        // T-113：图片行接入缩略图（getThumbnail），失败降级类型图标
                                        val thumbWidthPx = with(density) { 40.dp.roundToPx() }
                                        ThumbnailImage(
                                            loader = thumbnailLoader,
                                            path = file.path,
                                            widthPx = thumbWidthPx,
                                            modifier = Modifier
                                                .size(40.dp)
                                                .clip(RoundedCornerShape(6.dp)),
                                            contentDescription = shortName
                                        )
                                    } else {
                                        Icon(
                                            getFileIcon(file), null,
                                            modifier = Modifier.size(24.dp),
                                            tint = if (file.type == 1)
                                                MaterialTheme.colorScheme.primary
                                            else MaterialTheme.colorScheme.onSurfaceVariant
                                        )
                                    }
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
                                    Row(verticalAlignment = Alignment.CenterVertically) {
                                        Text(
                                            formatSize(file.size) +
                                            if (file.type == 0)
                                                " · ${formatTimestamp(file.lastModified)}" else "",
                                            style = MaterialTheme.typography.bodySmall
                                        )
                                        if (file.type == 0) {
                                            // T-107：同步状态图标（图标+颜色双通道，对齐 Windows ResolveBrowseState）
                                            val (stateIcon, stateColor) = syncStateChannel(file.state)
                                            Spacer(Modifier.width(6.dp))
                                            Icon(
                                                stateIcon,
                                                syncStateLabel(file.state),
                                                modifier = Modifier.size(14.dp),
                                                tint = stateColor
                                            )
                                        }
                                    }
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
                                        // T-114：长按弹出上下文菜单（移动/重命名/多选），原批量选择改由「多选」进入
                                        if (!isSelectionMode) contextMenuTarget = file
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
                            // T-114：长按上下文菜单（移动/重命名/多选——多选保留原批量选择能力）
                            DropdownMenu(
                                expanded = contextMenuTarget?.path == file.path,
                                onDismissRequest = { contextMenuTarget = null }
                            ) {
                                DropdownMenuItem(
                                    text = { Text("移动") },
                                    leadingIcon = { Icon(Icons.Default.DriveFileMove, null) },
                                    onClick = {
                                        contextMenuTarget = null
                                        moveTarget = file
                                    }
                                )
                                DropdownMenuItem(
                                    text = { Text("重命名") },
                                    leadingIcon = { Icon(Icons.Default.Edit, null) },
                                    onClick = {
                                        contextMenuTarget = null
                                        renameTarget = file
                                        renameName = fileName(file.path)
                                    }
                                )
                                DropdownMenuItem(
                                    text = { Text("多选") },
                                    leadingIcon = { Icon(Icons.Default.CheckBox, null) },
                                    onClick = {
                                        contextMenuTarget = null
                                        isSelectionMode = true
                                        selectedPaths = setOf(file.path)
                                    }
                                )
                            }
                            }
                        }
                        // T-059：hasMore 时滚动到底自动加载下一页（nextCursor 增量）
                        if (hasMore) {
                            item(key = "load_more") {
                                LaunchedEffect(nextCursor, isLoadingMore) {
                                    val cur = nextCursor
                                    if (!isLoadingMore && cur != null && cur != lastAutoLoadCursor) {
                                        lastAutoLoadCursor = cur
                                        loadMore()
                                    }
                                }
                                Box(
                                    modifier = Modifier.fillMaxWidth().padding(vertical = 16.dp),
                                    contentAlignment = Alignment.Center
                                ) {
                                    Row(verticalAlignment = Alignment.CenterVertically) {
                                        if (isLoadingMore) {
                                            CircularProgressIndicator(
                                                modifier = Modifier.size(20.dp),
                                                strokeWidth = 2.dp
                                            )
                                            Spacer(Modifier.width(8.dp))
                                            Text("加载中……", style = MaterialTheme.typography.bodySmall)
                                        } else {
                                            Text(
                                                "滚动加载更多",
                                                style = MaterialTheme.typography.bodySmall,
                                                color = MaterialTheme.colorScheme.onSurfaceVariant
                                            )
                                        }
                                    }
                                }
                            }
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

    // T-113：全屏预览（photos 顺序下标进入，HorizontalPager 左右翻页，返回键关闭）
    previewIndex?.let { index ->
        if (photos.isNotEmpty()) {
            PhotoPreview(
                photos = photos,
                initialIndex = index.coerceIn(0, photos.lastIndex),
                loader = thumbnailLoader,
                onDismiss = { previewIndex = null }
            )
        }
    }
}

// ---- T-112：分享对话框（生成链接 + 复制/系统发送 + 撤销，对齐 Windows ShareDialog 生成后即撤销语义的扩展） ----

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun ShareDialog(
    file: FileEntryDto,
    repository: FileRepository,
    snackbarHostState: SnackbarHostState,
    onDismiss: () -> Unit
) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var isCreating by remember { mutableStateOf(false) }
    var isRevoking by remember { mutableStateOf(false) }
    var shareId by remember { mutableStateOf<String?>(null) }
    var shareUrl by remember { mutableStateOf<String?>(null) }

    AlertDialog(
        onDismissRequest = { if (!isCreating && !isRevoking) onDismiss() },
        icon = { Icon(Icons.Default.Share, null) },
        title = { Text("分享文件") },
        text = {
            Column(Modifier.fillMaxWidth()) {
                Text(fileName(file.path), fontWeight = FontWeight.Medium)
                Spacer(Modifier.height(4.dp))
                if (shareUrl == null) {
                    Text(
                        "生成分享链接后，家人可在任意设备的浏览器中打开下载",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                    Spacer(Modifier.height(16.dp))
                    Button(
                        onClick = {
                            scope.launch {
                                isCreating = true
                                val r = repository.createShare(file.path)
                                if (r.isSuccess) {
                                    val data = r.getOrNull()?.data
                                    shareId = data?.shareId
                                    shareUrl = data?.url
                                } else {
                                    snackbarHostState.showSnackbar(
                                        "创建分享失败：${r.exceptionOrNull().toUserMessage()}"
                                    )
                                    onDismiss()
                                }
                                isCreating = false
                            }
                        },
                        enabled = !isCreating,
                        modifier = Modifier.fillMaxWidth()
                    ) {
                        Text(if (isCreating) "生成中……" else "生成分享链接")
                    }
                } else {
                    OutlinedTextField(
                        value = shareUrl ?: "",
                        onValueChange = {},
                        readOnly = true,
                        label = { Text("分享链接") },
                        modifier = Modifier.fillMaxWidth()
                    )
                    Spacer(Modifier.height(8.dp))
                    Text(
                        "点击「复制链接」或「发送」分享给家人",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                    Spacer(Modifier.height(8.dp))
                    Row {
                        TextButton(onClick = {
                            val clipboard = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
                            clipboard.setPrimaryClip(ClipData.newPlainText("分享链接", shareUrl))
                            snackbarHostState.showSnackbar("链接已复制")
                        }) { Text("复制链接") }
                        TextButton(onClick = {
                            val send = Intent(Intent.ACTION_SEND).apply {
                                type = "text/plain"
                                putExtra(Intent.EXTRA_TEXT, shareUrl)
                            }
                            context.startActivity(Intent.createChooser(send, "分享链接"))
                        }) { Text("发送") }
                        TextButton(
                            onClick = {
                                val id = shareId
                                if (id != null) {
                                    scope.launch {
                                        isRevoking = true
                                        val r = repository.revokeShare(id)
                                        val ok = r.isSuccess && r.getOrNull() == true
                                        snackbarHostState.showSnackbar(
                                            if (ok) "已撤销分享" else "撤销失败（分享可能已失效）"
                                        )
                                        if (ok) onDismiss()
                                        isRevoking = false
                                    }
                                }
                            },
                            enabled = !isRevoking,
                            colors = ButtonDefaults.textButtonColors(
                                contentColor = MaterialTheme.colorScheme.error
                            )
                        ) { Text("撤销分享") }
                    }
                }
            }
        },
        confirmButton = {},
        dismissButton = { TextButton(onClick = onDismiss) { Text("关闭") } }
    )
}

// ---- T-050：回收站对话框（最近删除列表 + 恢复/清空，对齐 Windows T-014） ----

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun TrashDialog(
    repository: FileRepository,
    snackbarHostState: SnackbarHostState,
    onDismiss: () -> Unit
) {
    val scope = rememberCoroutineScope()
    var items by remember { mutableStateOf<List<TrashItem>>(emptyList()) }
    var loaded by remember { mutableStateOf(false) }
    var selectedMeta by remember { mutableStateOf<String?>(null) }
    var showEmptyConfirm by remember { mutableStateOf(false) }

    suspend fun refresh() {
        val r = repository.getTrash()
        items = r.getOrNull() ?: emptyList()
        loaded = true
    }

    LaunchedEffect(Unit) { refresh() }

    // 清空回收站确认（不可逆操作，需二次确认）
    if (showEmptyConfirm) {
        AlertDialog(
            onDismissRequest = { showEmptyConfirm = false },
            icon = { Icon(Icons.Default.DeleteForever, null, tint = MaterialTheme.colorScheme.error) },
            title = { Text("清空回收站") },
            text = { Text("确定要清空回收站吗？清空后无法恢复。") },
            confirmButton = {
                TextButton(onClick = {
                    showEmptyConfirm = false
                    scope.launch {
                        val ok = repository.emptyTrash().isSuccess
                        snackbarHostState.showSnackbar(if (ok) "已清空回收站" else "清空失败")
                        refresh()
                    }
                }) { Text("清空", color = MaterialTheme.colorScheme.error) }
            },
            dismissButton = { TextButton(onClick = { showEmptyConfirm = false }) { Text("取消") } }
        )
    }

    AlertDialog(
        onDismissRequest = onDismiss,
        icon = { Icon(Icons.Default.DeleteSweep, null, tint = MaterialTheme.colorScheme.error) },
        title = { Text("回收站（最近删除）") },
        text = {
            Column(Modifier.fillMaxWidth()) {
                if (!loaded) {
                    Box(Modifier.fillMaxWidth().padding(24.dp), contentAlignment = Alignment.Center) {
                        Text("加载中……")
                    }
                } else if (items.isEmpty()) {
                    Box(Modifier.fillMaxWidth().padding(24.dp), contentAlignment = Alignment.Center) {
                        Text("回收站是空的")
                    }
                } else {
                    Column(Modifier.heightIn(max = 300.dp)) {
                        items.forEach { item ->
                            val displayName = fileName(item.originalPath)
                            val isSelected = item.trashFileName == selectedMeta
                            ListItem(
                                headlineContent = {
                                    Text(displayName, maxLines = 1, overflow = TextOverflow.Ellipsis)
                                },
                                supportingContent = {
                                    Text(
                                        (if (item.isDirectory) "文件夹" else formatSize(item.fileSize)) +
                                            " · " + (if (item.ageDays > 0) "${item.ageDays} 天前" else "刚刚")
                                    )
                                },
                                colors = ListItemDefaults.colors(
                                    containerColor = if (isSelected)
                                        MaterialTheme.colorScheme.primaryContainer.copy(alpha = 0.3f)
                                    else Color.Transparent
                                ),
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .clickable { selectedMeta = if (isSelected) null else item.trashFileName }
                            )
                        }
                    }
                }
            }
        },
        confirmButton = {
            TextButton(onClick = {
                val meta = selectedMeta
                if (meta != null) {
                    scope.launch {
                        val result = repository.restoreTrash(meta)
                        val msg = when {
                            result.isSuccess -> "已恢复"
                            // T-094/F-136：恢复失败给具体原因与下一步，不再『恢复失败，请稍后重试』死端
                            result.exceptionOrNull() is FileConflictException ->
                                "恢复失败：目标位置已有同名文件，请先删除或改名原位置的同名文件后，再重试恢复"
                            else -> "恢复失败：${result.exceptionOrNull().toUserMessage()}"
                        }
                        snackbarHostState.showSnackbar(msg)
                        if (result.isSuccess) refresh()
                    }
                }
            }) { Text("恢复选中") }
        },
        dismissButton = {
            Row(verticalAlignment = Alignment.CenterVertically) {
                TextButton(onClick = { showEmptyConfirm = true }) {
                    Text("清空回收站", color = MaterialTheme.colorScheme.error)
                }
                TextButton(onClick = onDismiss) { Text("关闭") }
            }
        }
    )
}

// ---- T-113：相册网格视图（yyyy-MM 分组，3 列缩略图，点击进入全屏预览） ----

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun PhotoGrid(
    groupedPhotos: List<Pair<String, List<FileEntryDto>>>,
    photoIndexByPath: Map<String, Int>,
    thumbnailLoader: ThumbnailLoader,
    onPhotoClick: (Int) -> Unit
) {
    val density = LocalDensity.current
    val screenWidthPx = with(density) { LocalConfiguration.current.screenWidthDp.dp.roundToPx() }
    val cellWidthPx = (screenWidthPx / 3).coerceAtLeast(1)
    LazyVerticalGrid(
        columns = GridCells.Fixed(3),
        modifier = Modifier.fillMaxSize(),
        contentPadding = PaddingValues(horizontal = 2.dp, vertical = 4.dp),
        horizontalArrangement = Arrangement.spacedBy(2.dp),
        verticalArrangement = Arrangement.spacedBy(2.dp)
    ) {
        groupedPhotos.forEach { (key, monthPhotos) ->
            item(key = "header_$key", span = { GridItemSpan(maxLineSpan) }) {
                Text(
                    monthLabel(key),
                    modifier = Modifier.padding(horizontal = 6.dp, vertical = 6.dp),
                    style = MaterialTheme.typography.labelMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
            items(monthPhotos, key = { it.path }) { photo ->
                ThumbnailImage(
                    loader = thumbnailLoader,
                    path = photo.path,
                    widthPx = cellWidthPx,
                    modifier = Modifier
                        .aspectRatio(1f)
                        .clip(RoundedCornerShape(4.dp))
                        .clickable { onPhotoClick(photoIndexByPath[photo.path] ?: 0) },
                    contentDescription = fileName(photo.path)
                )
            }
        }
    }
}

// ---- T-113：全屏预览（黑色背景 + 左右滑动翻页 + 页号/文件名指示） ----

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun PhotoPreview(
    photos: List<FileEntryDto>,
    initialIndex: Int,
    loader: ThumbnailLoader,
    onDismiss: () -> Unit
) {
    val density = LocalDensity.current
    val screenWidthPx = with(density) { LocalConfiguration.current.screenWidthDp.dp.roundToPx() }
    val pagerState = rememberPagerState(
        initialPage = initialIndex.coerceIn(0, photos.lastIndex)
    ) { photos.size }
    BackHandler(onBack = onDismiss)

    Dialog(
        onDismissRequest = onDismiss,
        properties = DialogProperties(usePlatformDefaultWidth = false, decorFitsSystemWindows = false)
    ) {
        Box(
            modifier = Modifier
                .fillMaxSize()
                .background(Color.Black)
        ) {
            HorizontalPager(state = pagerState) { page ->
                Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                    ThumbnailImage(
                        loader = loader,
                        path = photos[page].path,
                        widthPx = screenWidthPx,
                        modifier = Modifier.fillMaxSize(),
                        contentScale = ContentScale.Fit,
                        contentDescription = fileName(photos[page].path)
                    )
                }
            }
            // 页号指示 + 关闭按钮 + 文件名
            Text(
                "${pagerState.currentPage + 1} / ${photos.size}",
                modifier = Modifier.align(Alignment.TopCenter).padding(top = 20.dp),
                color = Color.White,
                style = MaterialTheme.typography.titleSmall
            )
            IconButton(
                onClick = onDismiss,
                modifier = Modifier.align(Alignment.TopEnd).padding(8.dp)
            ) {
                Icon(Icons.Default.Close, "关闭预览", tint = Color.White)
            }
            Text(
                fileName(photos[pagerState.currentPage].path),
                modifier = Modifier
                    .align(Alignment.BottomCenter)
                    .padding(horizontal = 24.dp)
                    .padding(bottom = 28.dp),
                color = Color.White,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis
            )
        }
    }
}

// ---- T-114：目录选择对话框（移动目标目录 / 上传目标目录共用） ----

/**
 * T-114：目录选择对话框——移动目标目录、上传目标目录共用。
 * 只展示子目录（type==1），点击进入，左上角返回上级，确认按钮以当前目录为选中目标。
 * 数据来自 getFileTreeInFolder（子树），UI 取 currentDir 的直系子目录。
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun DirectoryPickerDialog(
    repository: FileRepository,
    title: String,
    confirmLabel: String,
    startDir: String,
    onConfirm: (String) -> Unit,
    onDismiss: () -> Unit
) {
    var currentDir by remember { mutableStateOf(startDir) }
    var dirs by remember { mutableStateOf<List<String>>(emptyList()) }
    var loading by remember { mutableStateOf(true) }

    suspend fun loadDirs() {
        loading = true
        val r = repository.getFileTreeInFolder(currentDir)
        dirs = r.getOrNull()?.data
            ?.filter { it.path != currentDir }
            ?.filter { it.type == 1 && isDirectChild(currentDir, it.path) }
            ?.map { it.path }
            ?.sorted() ?: emptyList()
        loading = false
    }

    LaunchedEffect(currentDir) { loadDirs() }

    AlertDialog(
        onDismissRequest = onDismiss,
        icon = { Icon(Icons.Default.Folder, null) },
        title = { Text(title) },
        text = {
            Column(Modifier.fillMaxWidth()) {
                // 当前目录 + 返回上级按钮（根目录时隐藏返回）
                Row(verticalAlignment = Alignment.CenterVertically) {
                    if (currentDir != "/") {
                        IconButton(onClick = {
                            currentDir = currentDir.substringBeforeLast("/", "/").ifEmpty { "/" }
                        }) { Icon(Icons.Default.ArrowUpward, "返回上级目录") }
                    }
                    Text(
                        currentDir.ifEmpty { "/" },
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        style = MaterialTheme.typography.bodyMedium,
                        modifier = Modifier.weight(1f)
                    )
                }
                Spacer(Modifier.height(4.dp))
                Box(Modifier.heightIn(max = 280.dp)) {
                    when {
                        loading -> Text("加载中……", modifier = Modifier.padding(16.dp))
                        dirs.isEmpty() -> Text(
                            "没有子文件夹",
                            modifier = Modifier.padding(16.dp),
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                        else -> LazyColumn {
                            items(dirs) { dir ->
                                ListItem(
                                    headlineContent = {
                                        Text(fileName(dir), maxLines = 1, overflow = TextOverflow.Ellipsis)
                                    },
                                    leadingContent = {
                                        Icon(
                                            Icons.Default.Folder, null,
                                            tint = MaterialTheme.colorScheme.primary
                                        )
                                    },
                                    modifier = Modifier
                                        .fillMaxWidth()
                                        .clickable { currentDir = dir }
                                )
                            }
                        }
                    }
                }
            }
        },
        confirmButton = {
            TextButton(onClick = { onConfirm(currentDir.ifEmpty { "/" }) }) { Text(confirmLabel) }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text("取消") } }
    )
}
