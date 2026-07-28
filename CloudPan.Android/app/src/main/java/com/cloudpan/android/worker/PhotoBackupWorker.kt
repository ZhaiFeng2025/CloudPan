package com.cloudpan.android.worker

import android.content.Context
import android.content.SharedPreferences
import android.content.pm.ServiceInfo
import android.os.Build
import android.provider.MediaStore
import android.util.Log
import androidx.work.*
import com.cloudpan.android.data.ApiClientFactory
import com.cloudpan.android.data.CloudPanApi
import com.cloudpan.android.data.SettingsStore
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.MediaType.Companion.toMediaTypeOrNull
import okhttp3.MultipartBody
import okhttp3.RequestBody.Companion.asRequestBody
import java.io.File
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.concurrent.TimeUnit

/**
 * WorkManager Worker——定期扫描新增照片并上传到服务端 /Photos/ 目录。
 * 间隔: 15 分钟（shared-spec.json config.androidPollIntervalMinutes）。
 */
class PhotoBackupWorker(
    context: Context,
    params: WorkerParameters
) : CoroutineWorker(context, params) {

    private val settings = SettingsStore(context)
    private val prefs: SharedPreferences =
        context.getSharedPreferences("cloudpan_backup", Context.MODE_PRIVATE)

    override suspend fun doWork(): Result = withContext(Dispatchers.IO) {
        try {
            val token = settings.token
            if (token.isEmpty()) {
                Log.w(TAG, "Token 未设置，跳过照片备份")
                return@withContext Result.success() // 不算失败——等待用户设置
            }

            val api = ApiClientFactory.create(settings.serverUrl, token, settings.deviceId)

            // 1. 获取上次备份时间
            val lastBackupEpoch = prefs.getLong(KEY_LAST_BACKUP, 0L)
            val lastBackupSeconds = lastBackupEpoch / 1000

            // 2. 查询 MediaStore 中新增的图片
            val projection = arrayOf(
                MediaStore.Images.Media._ID,
                MediaStore.Images.Media.DATA,
                MediaStore.Images.Media.DISPLAY_NAME,
                MediaStore.Images.Media.DATE_ADDED,
                MediaStore.Images.Media.SIZE,
                MediaStore.Images.Media.MIME_TYPE
            )
            val selection = "${MediaStore.Images.Media.DATE_ADDED} > ?"
            val selectionArgs = arrayOf(lastBackupSeconds.toString())

            val cursor = applicationContext.contentResolver.query(
                MediaStore.Images.Media.EXTERNAL_CONTENT_URI,
                projection, selection, selectionArgs,
                "${MediaStore.Images.Media.DATE_ADDED} ASC"
            )

            var uploaded = 0
            var maxDate = lastBackupEpoch

            cursor?.use { c ->
                val idCol = c.getColumnIndexOrThrow(MediaStore.Images.Media._ID)
                val pathCol = c.getColumnIndexOrThrow(MediaStore.Images.Media.DATA)
                val nameCol = c.getColumnIndexOrThrow(MediaStore.Images.Media.DISPLAY_NAME)
                val dateCol = c.getColumnIndexOrThrow(MediaStore.Images.Media.DATE_ADDED)
                val mimeCol = c.getColumnIndexOrThrow(MediaStore.Images.Media.MIME_TYPE)

                while (c.moveToNext()) {
                    val filePath = c.getString(pathCol) ?: continue
                    val fileName = c.getString(nameCol) ?: "photo.jpg"
                    val dateAdded = c.getLong(dateCol)
                    val mimeType = c.getString(mimeCol) ?: "image/jpeg"

                    val file = File(filePath)
                    if (!file.exists()) continue

                    // 构建远程路径: /Photos/2026-07/IMG_xxx.jpg
                    val dateStr = Instant.ofEpochSecond(dateAdded)
                        .atZone(ZoneId.systemDefault())
                        .toLocalDate()
                    val monthDir = dateStr.format(DateTimeFormatter.ofPattern("yyyy-MM"))
                    val remotePath = "/Photos/$monthDir/$fileName"

                    try {
                        uploadPhoto(api, file, remotePath, mimeType)
                        uploaded++
                        if (dateAdded * 1000L > maxDate) {
                            maxDate = dateAdded * 1000L
                        }
                        Log.i(TAG, "已备份: $remotePath")
                    } catch (e: Exception) {
                        Log.e(TAG, "备份失败: $remotePath", e)
                        // 单个失败不阻塞其他文件
                    }
                }
            }

            // 3. 更新最后备份时间
            if (maxDate > lastBackupEpoch) {
                prefs.edit().putLong(KEY_LAST_BACKUP, maxDate).apply()
            }

            Log.i(TAG, "照片备份完成: 上传 $uploaded 张")
            Result.success()
        } catch (e: Exception) {
            Log.e(TAG, "照片备份异常", e)
            Result.retry()
        }
    }

    private suspend fun uploadPhoto(
        api: CloudPanApi,
        file: File,
        remotePath: String,
        mimeType: String
    ) {
        val mediaType = mimeType.toMediaTypeOrNull() ?: "image/jpeg".toMediaTypeOrNull()!!
        val requestBody = file.asRequestBody(mediaType)
        val filePart = MultipartBody.Part.createFormData("file", file.name, requestBody)
        val pathPart = remotePath.toRequestBody(MultipartBody.FORM)
        val versionPart = "0".toRequestBody(MultipartBody.FORM)
        val modifiedPart = Instant.ofEpochMilli(file.lastModified())
            .toString().toRequestBody(MultipartBody.FORM)

        val response = api.uploadFile(filePart, pathPart, versionPart, modifiedPart)
        if (!response.isSuccessful) {
            throw Exception("上传失败: ${response.code()} ${response.message()}")
        }
    }

    companion object {
        private const val TAG = "PhotoBackup"
        private const val KEY_LAST_BACKUP = "last_backup_epoch_ms"
        private const val WORK_NAME = "photo_backup_periodic"

        /** 注册定期备份任务（每 15 分钟）。 */
        fun schedule(context: Context) {
            val constraints = Constraints.Builder()
                .setRequiredNetworkType(NetworkType.CONNECTED)
                .setRequiresBatteryNotLow(true)
                .build()

            val request = PeriodicWorkRequestBuilder<PhotoBackupWorker>(
                15, TimeUnit.MINUTES
            )
                .setConstraints(constraints)
                .setBackoffCriteria(
                    BackoffPolicy.EXPONENTIAL,
                    WorkRequest.MIN_BACKOFF_MILLIS,
                    TimeUnit.MILLISECONDS
                )
                .build()

            WorkManager.getInstance(context).enqueueUniquePeriodicWork(
                WORK_NAME,
                ExistingPeriodicWorkPolicy.KEEP,
                request
            )
        }

        /** 取消定期备份。 */
        fun cancel(context: Context) {
            WorkManager.getInstance(context).cancelUniqueWork(WORK_NAME)
        }
    }
}

/** 工具方法：String → RequestBody（multipart 表单字段）。 */
private fun String.toRequestBody(mediaType: okhttp3.MediaType?): okhttp3.RequestBody {
    return okhttp3.RequestBody.create(mediaType, this)
}
