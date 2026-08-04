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
import com.cloudpan.android.data.ErrorAttribution
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

/**
 * T-091：手动上传 UI 状态（上传中 / 成功 / 失败白话归因）。
 * 由 MainActivity 上传流程驱动，FileListScreen 据此显示 FAB 进度指示与结果 Snackbar。
 */
sealed interface UploadUiState {
    /** T-105：progress 为分块上传进度百分比（0f-1f），null=不确定进度（小文件直传无块级进度回调）。 */
    data class Uploading(val fileName: String, val progress: Float? = null) : UploadUiState
    data class Success(val fileName: String) : UploadUiState
    data class Failed(val fileName: String, val message: String) : UploadUiState
}

/** T-091：手动上传 IO 结果——冲突保留临时文件待弹窗，成功/失败清理临时文件。 */
private sealed interface UploadOutcome {
    data class Conflict(val localFile: File, val remotePath: String) : UploadOutcome
    data class Failed(val exception: Throwable?) : UploadOutcome
    object Success : UploadOutcome
}

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
            // T-091：手动上传流程状态（上传中/成功/失败），驱动 FileListScreen 进度指示与结果 Snackbar
            var uploadState by remember { mutableStateOf<UploadUiState?>(null) }
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
                        // T-091：进入上传中状态（FileListScreen 显示 FAB 进度指示）
                        uploadState = UploadUiState.Uploading(fileName)
                        val outcome = withContext(Dispatchers.IO) {
                            // T-091：整个上传流程包 try/catch，禁止未捕获异常冒泡崩溃
                            val tmpFile = File(cacheDir, "upload_${System.currentTimeMillis()}_$fileName")
                            try {
                                val input = contentResolver.openInputStream(uri) ?: run {
                                    tmpFile.delete()
                                    return@withContext UploadOutcome.Failed(
                                        Exception("无法读取所选文件，请重新选择")
                                    )
                                }
                                input.use { stream ->
                                    FileOutputStream(tmpFile).use { output ->
                                        stream.copyTo(output)
                                    }
                                }
                                // T-089：先查目标文件当前版本作为 baseVersion，触发服务端 409 并发保护
                                val baseVersion = repository.resolveBaseVersion(remotePath)
                                // T-105：大文件分块上传进度反馈（块级回调）；scope.launch 切回主线程更新 Compose 状态
                                val result = repository.uploadFile(tmpFile, remotePath, baseVersion) { uploaded, total ->
                                    if (total > 0) {
                                        val p = uploaded.toFloat() / total.toFloat()
                                        scope.launch { uploadState = UploadUiState.Uploading(fileName, p) }
                                    }
                                }
                                when {
                                    // T-089：文件已被其他设备修改，弹窗让用户决定覆盖或跳过，不静默
                                    result.exceptionOrNull() is FileConflictException ->
                                        UploadOutcome.Conflict(tmpFile, remotePath)
                                    result.isFailure -> {
                                        tmpFile.delete() // 上传失败，清理临时文件
                                        UploadOutcome.Failed(result.exceptionOrNull())
                                    }
                                    else -> {
                                        tmpFile.delete() // 上传完成，清理临时文件
                                        UploadOutcome.Success
                                    }
                                }
                            } catch (e: kotlinx.coroutines.CancellationException) {
                                tmpFile.delete() // 协程取消（如页面销毁），清理临时文件
                                throw e
                            } catch (e: Exception) {
                                tmpFile.delete() // 读/写/上传异常，清理半成品临时文件
                                UploadOutcome.Failed(e)
                            }
                        }
                        when (outcome) {
                            is UploadOutcome.Conflict ->
                                pendingUploadConflict = UploadConflict(outcome.localFile, outcome.remotePath)
                            is UploadOutcome.Failed ->
                                uploadState = UploadUiState.Failed(
                                    fileName,
                                    ErrorAttribution.from(outcome.exception).displayText()
                                )
                            UploadOutcome.Success -> {
                                uploadState = UploadUiState.Success(fileName)
                                refreshTrigger++
                            }
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
                    refreshTrigger = refreshTrigger,
                    // T-091：手动上传进度/结果状态，Snackbar 展示后由回调清除
                    uploadState = uploadState,
                    onUploadStateHandled = { uploadState = null }
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
