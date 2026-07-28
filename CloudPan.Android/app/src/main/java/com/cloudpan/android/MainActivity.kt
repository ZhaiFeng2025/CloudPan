package com.cloudpan.android

import android.net.Uri
import android.os.Bundle
import android.provider.OpenableColumns
import androidx.activity.ComponentActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.runtime.*
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
                        withContext(Dispatchers.IO) {
                            val tmpFile = File(cacheDir, "upload_${System.currentTimeMillis()}_$fileName")
                            contentResolver.openInputStream(uri)?.use { input ->
                                FileOutputStream(tmpFile).use { output ->
                                    input.copyTo(output)
                                }
                            }
                            repository.uploadFile(tmpFile, "/Uploads/$fileName")
                            refreshTrigger++
                        }
                    }
                }
            }

            if (showFileList) {
                FileListScreen(
                    repository = repository,
                    onBackToSettings = { showFileList = false },
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
