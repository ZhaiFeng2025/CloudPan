package com.cloudpan.android.data

import android.util.Log
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.ResponseBody
import java.io.File
import java.io.FileOutputStream

/**
 * 文件操作仓库——封装 API 调用和错误处理。
 */
class FileRepository(private val settings: SettingsStore) {
    private var _api: CloudPanApi? = null

    private fun api(): CloudPanApi {
        if (_api == null || needsRefresh) {
            _api = ApiClientFactory.create(
                settings.serverUrl,
                settings.token,
                settings.deviceId
            )
        }
        return _api!!
    }

    private var needsRefresh = true

    fun invalidateClient() {
        needsRefresh = true
        _api = null
    }

    suspend fun getFileTree(sinceVersion: Int = 0, cursor: String? = null): Result<FileTreeResponse> {
        return safeCall { api().getFileTree(sinceVersion, 100, cursor) }
    }

    suspend fun downloadFile(remotePath: String, localDir: File): Result<File> {
        return withContext(Dispatchers.IO) {
            try {
                val response = api().downloadFile(remotePath)
                if (!response.isSuccessful) {
                    return@withContext Result.failure(
                        Exception("下载失败: ${response.code()} ${response.message()}")
                    )
                }
                val body: ResponseBody = response.body()
                    ?: return@withContext Result.failure(Exception("空响应体"))

                val fileName = remotePath.substringAfterLast('/')
                val localFile = File(localDir, fileName)
                FileOutputStream(localFile).use { out ->
                    body.byteStream().use { input -> input.copyTo(out) }
                }
                Result.success(localFile)
            } catch (e: Exception) {
                Result.failure(e)
            }
        }
    }

    suspend fun createFolder(path: String): Result<Unit> {
        return safeCall {
            api().createFolder(mapOf("path" to path))
            Unit
        }
    }

    suspend fun createShare(filePath: String, password: String? = null): Result<ShareResponse> {
        val body = mutableMapOf<String, Any>("filePath" to filePath)
        if (password != null) body["password"] = password
        return safeCall { api().createShare(body) }
    }

    suspend fun healthCheck(): Result<Boolean> {
        return safeCall {
            val r = api().healthCheck()
            if (r.isSuccessful) Unit else throw Exception("健康检查失败: ${r.code()}")
            true
        }
    }

    private suspend fun <T> safeCall(block: suspend () -> T): Result<T> {
        return try {
            Result.success(block())
        } catch (e: Exception) {
            Log.e("CloudPan", "API 错误", e)
            Result.failure(e)
        }
    }
}
