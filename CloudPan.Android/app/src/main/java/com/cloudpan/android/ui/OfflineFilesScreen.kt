package com.cloudpan.android.ui

import android.content.Context
import android.content.Intent
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.core.content.FileProvider
import com.cloudpan.android.data.AppDatabase
import com.cloudpan.android.data.OfflineCacheEntity
import kotlinx.coroutines.launch
import android.webkit.MimeTypeMap
import java.io.File

private fun formatSize(bytes: Long): String = when {
    bytes >= 1_048_576 -> "${"%.1f".format(bytes / 1_048_576.0f)} MB"
    bytes >= 1024 -> "${bytes / 1024} KB"
    else -> "$bytes B"
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun OfflineFilesScreen(
    onBack: () -> Unit
) {
    val context = LocalContext.current
    val db = remember { AppDatabase.getInstance(context) }
    var files by remember { mutableStateOf<List<OfflineCacheEntity>>(emptyList()) }
    val scope = rememberCoroutineScope()
    val snackbarHostState = remember { SnackbarHostState() }

    fun loadFiles() {
        scope.launch {
            files = db.offlineCacheDao().getAll()
        }
    }

    LaunchedEffect(Unit) { loadFiles() }

    Scaffold(
        snackbarHost = { SnackbarHost(snackbarHostState) },
        topBar = {
            TopAppBar(
                title = { Text("离线收藏") },
                navigationIcon = {
                    TextButton(onClick = onBack) { Text("← 返回") }
                }
            )
        }
    ) { padding ->
        if (files.isEmpty()) {
            Box(
                Modifier.fillMaxSize().padding(padding),
                contentAlignment = androidx.compose.ui.Alignment.Center
            ) {
                Column(horizontalAlignment = androidx.compose.ui.Alignment.CenterHorizontally) {
                    Icon(
                        Icons.Default.CloudDownload, "无离线文件",
                        modifier = Modifier.size(64.dp),
                        tint = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.4f)
                    )
                    Spacer(Modifier.height(16.dp))
                    Text(
                        "暂无离线文件",
                        style = MaterialTheme.typography.titleMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                    Spacer(Modifier.height(8.dp))
                    Text(
                        "在文件列表中点击文件旁的下载按钮\n即可下载到本地，离线时也可查看",
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.7f),
                        textAlign = TextAlign.Center
                    )
                }
            }
        } else {
            LazyColumn(Modifier.padding(padding)) {
                items(files) { item ->
                    ListItem(
                        headlineContent = { Text(File(item.path).name) },
                        supportingContent = { Text("${formatSize(item.fileSize)} · ${item.cachedAt.take(19)}") },
                        modifier = Modifier.clickable {
                            val file = File(item.localPath)
                            if (file.exists()) {
                                try {
                                    val uri = FileProvider.getUriForFile(
                                        context, "${context.packageName}.fileprovider", file
                                    )
                                    val ext = file.name.substringAfterLast('.', "").lowercase()
                                    val mimeType = if (ext.isNotEmpty()) {
                                        MimeTypeMap.getSingleton().getMimeTypeFromExtension(ext)
                                            ?: "application/octet-stream"
                                    } else {
                                        "application/octet-stream"
                                    }
                                    val intent = Intent(Intent.ACTION_VIEW).apply {
                                        setDataAndType(uri, mimeType)
                                        addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
                                    }
                                    context.startActivity(intent)
                                } catch (e: Exception) {
                                    scope.launch {
                                        snackbarHostState.showSnackbar("无法打开此文件")
                                    }
                                }
                            } else {
                                scope.launch {
                                    snackbarHostState.showSnackbar("文件已不存在")
                                }
                            }
                        },
                        trailingContent = {
                            IconButton(onClick = {
                                scope.launch {
                                    db.offlineCacheDao().deleteByPath(item.path)
                                    try { File(item.localPath).delete() } catch (_: Exception) {}
                                    loadFiles()
                                }
                            }) {
                                Icon(Icons.Default.Delete, "删除")
                            }
                        }
                    )
                }
            }
        }
    }
}
