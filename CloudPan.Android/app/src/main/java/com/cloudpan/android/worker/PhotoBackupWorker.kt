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
import com.cloudpan.android.data.AppDatabase
import com.cloudpan.android.data.BackupLogDao
import com.cloudpan.android.data.BackupLogEntity
import com.cloudpan.android.data.BackupStatus
import com.cloudpan.android.data.CloudPanApi
import com.cloudpan.android.data.SettingsStore
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.MediaType.Companion.toMediaTypeOrNull
import okhttp3.MultipartBody
import okhttp3.RequestBody.Companion.asRequestBody
import java.io.File
import java.security.MessageDigest
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.concurrent.TimeUnit

/**
 * WorkManager Worker——定期扫描新增照片并上传到服务端 /Photos/ 目录。
 * 间隔: 15 分钟（shared-spec.json config.androidPollIntervalMinutes）。
 *
 * 游标语义（T-044）：KEY_LAST_BACKUP 只推进到『连续成功段』末尾——照片按
 * DATE_ADDED 升序处理，遇失败即停，失败照片与后续照片保留在待传集合，
 * 下次运行重试；不再出现「较早照片失败、较晚照片成功导致失败照片被跳过」。
 * 每张照片经 BackupLog（Uri+FileHash 去重 / BackupStatus 状态机）记录，远程路径
 * 含哈希短码避免同名照片相互覆盖。
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
            val backupLogDao = AppDatabase.getInstance(applicationContext).backupLogDao()

            // 1. 获取上次备份游标（只推进到连续成功段末尾）
            val lastBackupEpoch = prefs.getLong(KEY_LAST_BACKUP, 0L)
            val lastBackupSeconds = lastBackupEpoch / 1000

            // 2. 查询 MediaStore 中新增的图片（按 DATE_ADDED 升序）
            val projection = arrayOf(
                MediaStore.Images.Media._ID,
                MediaStore.Images.Media.DATA,
                MediaStore.Images.Media.DISPLAY_NAME,
                MediaStore.Images.Media.DATE_ADDED,
                MediaStore.Images.Media.SIZE,
                MediaStore.Images.Media.MIME_TYPE
            )
            // 用 >=（而非 >）：DATE_ADDED 精度为秒，若游标所在秒内既有成功又有失败，
            // 失败照片与游标同秒，必须再次纳入扫描（由 BackupLog 去重避免重复上传），
            // 否则同秒失败照片会被 `>` 永久排除 → 静默丢失。
            val selection = "${MediaStore.Images.Media.DATE_ADDED} >= ?"
            val selectionArgs = arrayOf(lastBackupSeconds.toString())

            val cursor = applicationContext.contentResolver.query(
                MediaStore.Images.Media.EXTERNAL_CONTENT_URI,
                projection, selection, selectionArgs,
                "${MediaStore.Images.Media.DATE_ADDED} ASC"
            )

            var uploaded = 0
            var hadFailure = false
            // 连续成功段末尾（毫秒）——遇失败即停，不越过失败照片
            var segmentEnd = lastBackupEpoch
            var hasSegmentProgress = false

            cursor?.use { c ->
                val idCol = c.getColumnIndexOrThrow(MediaStore.Images.Media._ID)
                val pathCol = c.getColumnIndexOrThrow(MediaStore.Images.Media.DATA)
                val nameCol = c.getColumnIndexOrThrow(MediaStore.Images.Media.DISPLAY_NAME)
                val dateCol = c.getColumnIndexOrThrow(MediaStore.Images.Media.DATE_ADDED)
                val mimeCol = c.getColumnIndexOrThrow(MediaStore.Images.Media.MIME_TYPE)

                photo@ while (c.moveToNext()) {
                    val mediaId = c.getLong(idCol)
                    val filePath = c.getString(pathCol) ?: continue@photo
                    val fileName = c.getString(nameCol) ?: "photo.jpg"
                    val dateAdded = c.getLong(dateCol)
                    val mimeType = c.getString(mimeCol) ?: "image/jpeg"

                    val file = File(filePath)
                    if (!file.exists()) {
                        // 文件已不存在（被移动/删除）：视为已解决，允许连续段越过
                        if (dateAdded * 1000L > segmentEnd) {
                            segmentEnd = dateAdded * 1000L
                            hasSegmentProgress = true
                        }
                        continue@photo
                    }

                    // 本地 MediaStore URI（BackupLog.localUri 去重依据）
                    val contentUri = MediaStore.Images.Media.EXTERNAL_CONTENT_URI.buildUpon()
                        .appendPath(mediaId.toString()).build().toString()

                    try {
                        val newlyUploaded = backupPhoto(
                            api = api,
                            dao = backupLogDao,
                            file = file,
                            contentUri = contentUri,
                            fileName = fileName,
                            dateAdded = dateAdded,
                            mimeType = mimeType
                        )
                        if (newlyUploaded) uploaded++
                        // 推进连续段（含去重跳过/已备份照片）
                        if (dateAdded * 1000L > segmentEnd) {
                            segmentEnd = dateAdded * 1000L
                            hasSegmentProgress = true
                        }
                        notification = buildProgressNotification("正在备份照片...", uploaded, -1)
                        notificationManager.notify(notificationId, notification)
                        Log.i(TAG, "已备份: $fileName")
                    } catch (e: Exception) {
                        Log.e(TAG, "备份失败: $fileName", e)
                        // 发送单张失败通知
                        notification = buildFailureNotification("备份失败", "照片「$fileName」上传失败：${e.message}")
                        notificationManager.notify(notificationId, notification)
                        // 连续段语义：遇失败即停，游标停在失败照片之前，该照片及后续下次运行重试
                        hadFailure = true
                        break@photo
                    }
                }
            }

            // 3. 游标只推进到连续成功段末尾
            if (hasSegmentProgress && segmentEnd > lastBackupEpoch) {
                prefs.edit().putLong(KEY_LAST_BACKUP, segmentEnd).apply()
            }

            // 完成通知
            notification = buildProgressNotification(
                when {
                    hadFailure -> "备份暂停：有照片失败，将在下次自动重试（已上传 $uploaded 张）"
                    uploaded > 0 -> "照片备份完成，已上传 $uploaded 张"
                    else -> "没有新照片需要备份"
                },
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

    /**
     * 备份单张照片并写 BackupLog（状态机 Pending→Uploading→Done/Failed）。
     * 去重：同 Uri 且同 FileHash 已 Done，或任一同 FileHash 已 Done → 跳过（返回 false）。
     * 返回 true 表示本次新上传；抛异常表示失败（由调用方停止连续段，下次运行重试）。
     */
    private suspend fun backupPhoto(
        api: CloudPanApi,
        dao: BackupLogDao,
        file: File,
        contentUri: String,
        fileName: String,
        dateAdded: Long,
        mimeType: String
    ): Boolean {
        val fileHash = file.sha256Hex()

        // 按 Uri+FileHash 去重：同 Uri 且同哈希已备份 → 跳过
        val existing = dao.findByLocalUri(contentUri)
        if (existing != null && existing.status == BackupStatus.Done.value && existing.fileHash == fileHash) {
            return false
        }
        // 内容级去重：同 FileHash 已 Done → 跳过
        if (dao.findDoneByFileHash(fileHash) != null) {
            return false
        }

        // 远程路径含哈希短码，避免同名照片相互覆盖
        val dateStr = Instant.ofEpochSecond(dateAdded)
            .atZone(ZoneId.systemDefault())
            .toLocalDate()
        val monthDir = dateStr.format(DateTimeFormatter.ofPattern("yyyy-MM"))
        val remotePath = "/Photos/$monthDir/${buildRemoteName(fileName, fileHash.take(8))}"
        val fileSize = file.length()

        // 首次记录：插入 Pending（@Upsert id=0 会 INSERT 并自动生成主键）
        if (existing == null) {
            dao.upsert(
                BackupLogEntity(
                    localUri = contentUri,
                    remotePath = remotePath,
                    fileHash = fileHash,
                    fileSize = fileSize,
                    status = BackupStatus.Pending.value,
                    createdAt = Instant.now().toString()
                )
            )
        }
        // 重查以取得真实主键（新插入记录的 id 由数据库生成，不能沿用 0）
        // 读不回则抛异常：让连续段停止、下次重试，避免被当作『已解析』而静默跳过
        val record = dao.findByLocalUri(contentUri)
            ?: throw IllegalStateException("BackupLog 写入后无法读回记录: $contentUri")

        try {
            // Pending/Failed → Uploading
            dao.upsert(record.copy(
                status = BackupStatus.Uploading.value,
                remotePath = remotePath,
                fileHash = fileHash,
                fileSize = fileSize
            ))
            uploadPhoto(api, file, remotePath, mimeType)
            // → Done
            dao.upsert(record.copy(
                status = BackupStatus.Done.value,
                remotePath = remotePath,
                fileHash = fileHash,
                fileSize = fileSize
            ))
            return true
        } catch (e: Exception) {
            // → Failed（保留记录，下次运行重试）
            dao.upsert(record.copy(
                status = BackupStatus.Failed.value,
                remotePath = remotePath,
                fileHash = fileHash,
                fileSize = fileSize
            ))
            throw e
        }
    }

    /** 远程文件名含哈希短码：`IMG_001.jpg` + `a1b2c3d4` → `IMG_001_a1b2c3d4.jpg`。 */
    private fun buildRemoteName(fileName: String, shortHash: String): String {
        val dot = fileName.lastIndexOf('.')
        return if (dot > 0) {
            "${fileName.substring(0, dot)}_$shortHash${fileName.substring(dot)}"
        } else {
            "${fileName}_$shortHash"
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

/** 工具方法：计算文件 SHA-256（十六进制小写）。 */
private fun File.sha256Hex(): String {
    val md = MessageDigest.getInstance("SHA-256")
    inputStream().use { input ->
        val buffer = ByteArray(8192)
        while (true) {
            val n = input.read(buffer)
            if (n < 0) break
            md.update(buffer, 0, n)
        }
    }
    return md.digest().joinToString("") { byte ->
        Integer.toHexString(byte.toInt() and 0xff).padStart(2, '0')
    }
}
