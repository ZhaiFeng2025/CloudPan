package com.cloudpan.android

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.runtime.*
import com.cloudpan.android.data.FileRepository
import com.cloudpan.android.data.SettingsStore
import com.cloudpan.android.ui.FileListScreen
import com.cloudpan.android.ui.SettingsScreen

class MainActivity : ComponentActivity() {
    private lateinit var settings: SettingsStore
    private lateinit var repository: FileRepository

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        settings = SettingsStore(applicationContext)
        repository = FileRepository(settings)

        setContent {
            var showFileList by remember { mutableStateOf(false) }

            if (showFileList) {
                FileListScreen(
                    repository = repository,
                    onBackToSettings = { showFileList = false }
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
