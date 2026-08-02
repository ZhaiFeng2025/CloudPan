package com.cloudpan.android.worker

import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.SharedPreferences
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.os.BatteryManager
import android.os.Build
import android.provider.MediaStore
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.util.Log
import androidx.core.app.NotificationCompat
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
        // 创建通知渠道（在 try 之外，确保 catch 也能访问）
        createNotificationChannel()
        val notificationManager = applicationContext.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        val notificationId = NOTIFICATION_ID_BACKUP

        try {
            // 检查备份条件（快速检查，暂不设置前台通知）
            val token = settings.token
            if (token.isEmpty()) {
                Log.d(TAG, "Token 未设置，跳过")
                return@withContext Result.success()
            }

            // Wi-Fi 检测
            if (settings.wifiOnly && !isWifiConnected()) {
                Log.d(TAG, "非 Wi-Fi 网络，跳过备份（wifiOnly=true）")
                return@withContext Result.success()
            }

            // 充电检测
            if (settings.chargingOnly && !isCharging()) {
                Log.d(TAG, "未在充电，跳过备份（chargingOnly=true）")
                return@withContext Result.success()
            }

            // 通过基本检查后，设置前台通知，防止系统 kill
            var notification = buildProgressNotification("准备照片备份...", 0, 0)
            setForeground(ForegroundInfo(notificationId, notification))

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
                        // 更新通知中的已上传计数
                        notification = buildProgressNotification("正在备份照片...", uploaded, -1)
                        notificationManager.notify(notificationId, notification)
                        Log.i(TAG, "已备份: $remotePath")
                    } catch (e: Exception) {
                        Log.e(TAG, "备份失败: $remotePath", e)
                        // 发送单张失败通知
                        notification = buildFailureNotification("备份失败", "照片「$fileName」上传失败：${e.message}")
                        notificationManager.notify(notificationId, notification)
                        // 单个失败不阻塞其他文件
                    }
                }
            }

            // 3. 更新最后备份时间
            if (maxDate > lastBackupEpoch) {
                prefs.edit().putLong(KEY_LAST_BACKUP, maxDate).apply()
            }

            // 完成通知
            notification = buildProgressNotification(
                if (uploaded > 0) "照片备份完成，已上传 $uploaded 张"
                else "没有新照片需要备份",
                uploaded, uploaded.coerceAtLeast(1)
            )
            notificationManager.notify(notificationId, notification)

            Log.i(TAG, "照片备份完成: 上传 $uploaded 张")
            Result.success()
        } catch (e: Exception) {
            Log.e(TAG, "照片备份异常", e)
            try {
                val errorNotification = buildFailureNotification("备份异常", e.message ?: "发生未知错误")
                notificationManager.notify(notificationId, errorNotification)
            } catch (_: Exception) {
                // 通知发送失败不阻止重试
            }
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

    // ============================================================
    // 前台通知
    // ============================================================

    private fun createNotificationChannel() {
        val channel = NotificationChannel(
            CHANNEL_ID,
            "照片备份",
            NotificationManager.IMPORTANCE_LOW
        ).apply {
            description = "显示照片备份进度和上传结果"
        }
        val manager = applicationContext.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        manager.createNotificationChannel(channel)
    }

    private fun buildProgressNotification(text: String, uploaded: Int, total: Int): Notification {
        val builder = NotificationCompat.Builder(applicationContext, CHANNEL_ID)
            .setContentTitle("照片备份")
            .setContentText(text)
            .setSmallIcon(android.R.drawable.ic_menu_upload)
            .setOngoing(true)
            .setPriority(NotificationCompat.PRIORITY_LOW)
            .setSilent(true)

        if (total > 0) {
            builder.setProgress(total, uploaded, false)
        } else {
            builder.setProgress(0, 0, true) // 不确定进度
        }

        return builder.build()
    }

    private fun buildFailureNotification(title: String, text: String): Notification {
        return NotificationCompat.Builder(applicationContext, CHANNEL_ID)
            .setContentTitle(title)
            .setContentText(text)
            .setSmallIcon(android.R.drawable.ic_menu_upload)
            .setOngoing(false)
            .setPriority(NotificationCompat.PRIORITY_DEFAULT)
            .setAutoCancel(true)
            .build()
    }

    // ============================================================
    // 网络和电源检测
    // ============================================================

    private fun isWifiConnected(): Boolean {
        val cm = applicationContext.getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager
            ?: return false
        val network = cm.activeNetwork ?: return false
        val caps = cm.getNetworkCapabilities(network) ?: return false
        return caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)
    }

    private fun isCharging(): Boolean {
        val intent = applicationContext.registerReceiver(
            null, IntentFilter(Intent.ACTION_BATTERY_CHANGED)
        ) ?: return false
        val status = intent.getIntExtra(BatteryManager.EXTRA_STATUS, -1)
        return status == BatteryManager.BATTERY_STATUS_CHARGING
                || status == BatteryManager.BATTERY_STATUS_FULL
    }

    companion object {
        private const val TAG = "PhotoBackup"
        private const val KEY_LAST_BACKUP = "last_backup_epoch_ms"
        private const val WORK_NAME = "photo_backup_periodic"
        private const val CHANNEL_ID = "photo_backup_channel"
        private const val NOTIFICATION_ID_BACKUP = 1001

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
