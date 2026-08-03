package com.cloudpan.android

import android.net.Uri
import android.os.Bundle
import android.provider.OpenableColumns
import androidx.activity.ComponentActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.*
import com.cloudpan.android.data.FileConflictException
import com.cloudpan.android.data.FileRepository
import com.cloudpan.android.data.SettingsStore
import com.cloudpan.android.ui.FileListScreen
import com.cloudpan.android.ui.SettingsScreen
import com.cloudpan.android.worker.PhotoBackupWorker
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File
import java.io.FileOutputStream

/** T-089：上传冲突待决信息——保留本地缓存文件，用户选择「覆盖」时强制重传（baseVersion=0）。 */
private data class UploadConflict(val localFile: File, val remotePath: String)

class MainActivity : ComponentActivity() {
    private lateinit var settings: SettingsStore
    private lateinit var repository: FileRepository

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        settings = SettingsStore(applicationContext)
        repository = FileRepository(settings)

        // 启动照片备份 Worker（仅在 Token 已设置时）
        if (settings.token.isNotEmpty()) {
            PhotoBackupWorker.schedule(applicationContext)
        }

        setContent {
            var showFileList by remember { mutableStateOf(false) }
            var refreshTrigger by remember { mutableIntStateOf(0) }
            // T-089：上传 409 冲突待决（文件已被其他设备修改）
            var pendingUploadConflict by remember { mutableStateOf<UploadConflict?>(null) }
            val scope = rememberCoroutineScope()

            // 文件选择器 launcher
            val filePickerLauncher = rememberLauncherForActivityResult(
                contract = ActivityResultContracts.GetContent()
            ) { uri: Uri? ->
                if (uri != null) {
                    val fileName = try {
                        val cursor = contentResolver.query(uri, null, null, null, null)
                        cursor?.use {
                            it.moveToFirst()
                            it.getString(it.getColumnIndexOrThrow(OpenableColumns.DISPLAY_NAME))
                        }
                    } catch (e: Exception) { null } ?: "uploaded_file"

                    scope.launch {
                        val remotePath = "/Uploads/$fileName"
                        val conflict: UploadConflict? = withContext(Dispatchers.IO) {
                            val tmpFile = File(cacheDir, "upload_${System.currentTimeMillis()}_$fileName")
                            contentResolver.openInputStream(uri)?.use { input ->
                                FileOutputStream(tmpFile).use { output ->
                                    input.copyTo(output)
                                }
                            }
                            // T-089：先查目标文件当前版本作为 baseVersion，触发服务端 409 并发保护
                            val baseVersion = repository.resolveBaseVersion(remotePath)
                            val result = repository.uploadFile(tmpFile, remotePath, baseVersion)
                            if (result.exceptionOrNull() is FileConflictException) {
                                UploadConflict(tmpFile, remotePath)
                            } else null
                        }
                        if (conflict != null) {
                            pendingUploadConflict = conflict
                        } else {
                            refreshTrigger++
                        }
                    }
                }
            }

            // T-089：上传冲突弹窗（不静默覆盖其他设备改动）
            pendingUploadConflict?.let { conflict ->
                AlertDialog(
                    onDismissRequest = { pendingUploadConflict = null },
                    title = { Text("上传冲突") },
                    text = { Text("「${conflict.remotePath}」已被其他设备修改，是否覆盖？") },
                    confirmButton = {
                        TextButton(onClick = {
                            pendingUploadConflict = null
                            scope.launch {
                                // 覆盖：baseVersion=0 强制覆盖服务端版本
                                withContext(Dispatchers.IO) {
                                    repository.uploadFile(conflict.localFile, conflict.remotePath, 0)
                                }
                                refreshTrigger++
                            }
                        }) { Text("覆盖") }
                    },
                    dismissButton = {
                        TextButton(onClick = { pendingUploadConflict = null }) { Text("跳过") }
                    }
                )
            }

            if (showFileList) {
                FileListScreen(
                    repository = repository,
                    onBackToSettings = {
                        showFileList = false
                        repository.invalidateClient()  // 断开HTTP连接池
                    },
                    onPickFileForUpload = { filePickerLauncher.launch("application/*,image/*,video/*") },
                    refreshTrigger = refreshTrigger
                )
            } else {
                SettingsScreen(
                    settings = settings,
                    onConnected = { showFileList = true }
                )
            }
        }
    }
}
