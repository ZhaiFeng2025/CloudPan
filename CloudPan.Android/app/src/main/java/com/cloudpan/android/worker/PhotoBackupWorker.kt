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
import com.cloudpan.android.data.FileConflictException
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

/** 单张照片备份结果（T-060：失败重试上限 + 游标越过）。 */
private enum class BackupOutcome {
    /** 本次新上传成功。 */
    Uploaded,

    /** 去重跳过（已备份/内容级去重/已 Blocked），视为已解析，游标可越过。 */
    Skipped,

    /** 本次触发失败重试上限，标记 Blocked，游标越过继续备份后续照片。 */
    Blocked
}

/**
 * WorkManager Worker——定期扫描新增照片并上传到服务端 /Photos/ 目录。
 * 间隔: 15 分钟（shared-spec.json config.androidPollIntervalMinutes）。
 *
 * 游标语义（T-044）：KEY_LAST_BACKUP 只推进到『连续成功段』末尾——照片按
 * DATE_ADDED 升序处理，遇失败即停，失败照片与后续照片保留在待传集合，
 * 下次运行重试；不再出现「较早照片失败、较晚照片成功导致失败照片被跳过」。
 * 每张照片经 BackupLog（Uri+FileHash 去重 / BackupStatus 状态机）记录，远程路径
 * 含哈希短码避免同名照片相互覆盖。
 * 队头阻塞防护（T-060）：单张照片连续失败达到 MAX_RETRY 次即标记 Blocked，游标越过
 * 继续备份后续照片，坏照片不再阻塞整个相册；未达上限仍按 T-044 语义停止、下次运行重试。
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
            // T-060：本次运行中标记 Blocked（重试超限）的照片数，用于完成通知提示
            var blockedPhotos = 0
            // 连续成功段末尾（毫秒）——遇失败即停，不越过失败照片
            var segmentEnd = lastBackupEpoch
            var hasSegmentProgress = false
            // T-089：月目录 → 目录内「路径→版本」表缓存，避免每张照片都全量拉取该月文件树
            val folderVersionCache = HashMap<String, Map<String, Int>>()

            // 推进连续段末尾：某照片被成功上传/去重跳过/标记 Blocked 处理后，游标即可越过该照片
            fun advanceSegment(dateAdded: Long) {
                if (dateAdded * 1000L > segmentEnd) {
                    segmentEnd = dateAdded * 1000L
                    hasSegmentProgress = true
                }
            }

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
                        advanceSegment(dateAdded)
                        continue@photo
                    }

                    // 本地 MediaStore URI（BackupLog.localUri 去重依据）
                    val contentUri = MediaStore.Images.Media.EXTERNAL_CONTENT_URI.buildUpon()
                        .appendPath(mediaId.toString()).build().toString()

                    try {
                        when (backupPhoto(
                            api = api,
                            dao = backupLogDao,
                            file = file,
                            contentUri = contentUri,
                            fileName = fileName,
                            dateAdded = dateAdded,
                            mimeType = mimeType,
                            folderVersionCache = folderVersionCache
                        )) {
                            BackupOutcome.Uploaded -> {
                                uploaded++
                                advanceSegment(dateAdded)
                                notification = buildProgressNotification("正在备份照片...", uploaded, -1)
                                notificationManager.notify(notificationId, notification)
                                Log.i(TAG, "已备份: $fileName")
                            }
                            BackupOutcome.Skipped -> {
                                // 去重跳过（已备份/已 Blocked 安全网）：视为已解决，允许连续段越过
                                advanceSegment(dateAdded)
                                Log.d(TAG, "跳过（去重/已处理）: $fileName")
                            }
                            BackupOutcome.Blocked -> {
                                // 失败重试已达上限：标记 Blocked，游标越过继续，不再阻塞后续照片
                                blockedPhotos++
                                advanceSegment(dateAdded)
                                notification = buildFailureNotification(
                                    "照片已 Blocked",
                                    "照片「$fileName」重试 $MAX_RETRY 次仍失败，已跳过备份，请手动处理"
                                )
                                notificationManager.notify(notificationId, notification)
                                Log.e(TAG, "照片已 Blocked（重试 $MAX_RETRY 次失败），跳过: $fileName")
                            }
                        }
                    } catch (e: Exception) {
                        Log.e(TAG, "备份失败: $fileName", e)
                        // 发送单张失败通知
                        notification = buildFailureNotification("备份失败", "照片「$fileName」上传失败：${e.message}")
                        notificationManager.notify(notificationId, notification)
                        // 连续段语义（未达重试上限）：遇失败即停，游标停在失败照片之前，该照片及后续下次运行重试
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
                    blockedPhotos > 0 && hadFailure ->
                        "备份暂停：$blockedPhotos 张已 Blocked（重试超限，请手动处理），另有照片失败将在下次重试（已上传 $uploaded 张）"
                    blockedPhotos > 0 ->
                        "照片备份完成，已上传 $uploaded 张；$blockedPhotos 张已 Blocked（重试超限，请手动处理）"
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
     * 去重：同 Uri 且同 FileHash 已 Done，或任一同 FileHash 已 Done → 跳过。
     *
     * 失败重试上限（T-060）：单张照片连续失败记录 retryCount，达到 [MAX_RETRY] 次即标记 Blocked
     * （Failed + retryCount 达上限），返回 [BackupOutcome.Blocked] 由调用方越过游标继续备份后续照片，
     * 坏照片不再阻塞整个相册；未达上限抛异常，由调用方按连续段语义停止、下次运行重试。
     *
     * @return Uploaded=本次新上传；Skipped=去重跳过或已 Blocked；Blocked=本次触发失败重试上限。
     * @throws Exception 未达失败重试上限的上传失败（调用方停止连续段）。
     */
    private suspend fun backupPhoto(
        api: CloudPanApi,
        dao: BackupLogDao,
        file: File,
        contentUri: String,
        fileName: String,
        dateAdded: Long,
        mimeType: String,
        folderVersionCache: MutableMap<String, Map<String, Int>>
    ): BackupOutcome {
        val fileHash = file.sha256Hex()

        // 按 Uri+FileHash 去重：同 Uri 且同哈希已备份 → 跳过
        val existing = dao.findByLocalUri(contentUri)
        if (existing != null && existing.status == BackupStatus.Done.value && existing.fileHash == fileHash) {
            return BackupOutcome.Skipped
        }
        // 已 Blocked（Failed 且 retryCount 达上限）：不再重试，静默跳过（避免每次运行重复通知），游标越过
        if (existing != null && existing.status == BackupStatus.Failed.value && existing.retryCount >= MAX_RETRY) {
            return BackupOutcome.Skipped
        }
        // 内容级去重：同 FileHash 已 Done → 跳过
        if (dao.findDoneByFileHash(fileHash) != null) {
            return BackupOutcome.Skipped
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
            uploadPhoto(api, file, remotePath, mimeType, folderVersionCache)
            // → Done（成功即清零重试计数，避免照片内容变更后再次上传被旧计数误判 Blocked）
            dao.upsert(record.copy(
                status = BackupStatus.Done.value,
                remotePath = remotePath,
                fileHash = fileHash,
                fileSize = fileSize,
                retryCount = 0
            ))
            return BackupOutcome.Uploaded
        } catch (e: Exception) {
            // → Failed（保留记录，下次运行重试）并累加重试计数
            val nextRetry = record.retryCount + 1
            dao.upsert(record.copy(
                status = BackupStatus.Failed.value,
                remotePath = remotePath,
                fileHash = fileHash,
                fileSize = fileSize,
                retryCount = nextRetry
            ))
            if (nextRetry >= MAX_RETRY) {
                // 已达失败重试上限：标记 Blocked（Failed + retryCount 达上限），调用方越过游标继续
                Log.e(TAG, "照片已达失败重试上限（$MAX_RETRY 次），标记 Blocked: $fileName", e)
                return BackupOutcome.Blocked
            }
            // 未达上限：抛异常让连续段停止，下次运行重试
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
        mimeType: String,
        folderVersionCache: MutableMap<String, Map<String, Int>>
    ) {
        // T-089：上传携带目标远程路径当前版本（先查询），不再恒传 0，触发服务端 409 并发保护
        val baseVersion = resolveRemoteVersion(api, remotePath, folderVersionCache)
        val mediaType = mimeType.toMediaTypeOrNull() ?: "image/jpeg".toMediaTypeOrNull()!!
        val requestBody = file.asRequestBody(mediaType)
        val filePart = MultipartBody.Part.createFormData("file", file.name, requestBody)
        val pathPart = remotePath.toRequestBody(MultipartBody.FORM)
        val versionPart = baseVersion.toString().toRequestBody(MultipartBody.FORM)
        val modifiedPart = Instant.ofEpochMilli(file.lastModified())
            .toString().toRequestBody(MultipartBody.FORM)

        val response = api.uploadFile(filePart, pathPart, versionPart, modifiedPart)
        if (response.code() == 409) {
            // 文件已被其他设备修改：不静默覆盖，抛异常进入失败重试/Blocked（通知用户手动处理）
            throw FileConflictException("照片已被其他设备修改，本次未覆盖")
        }
        if (!response.isSuccessful) {
            throw Exception("上传失败: ${response.code()} ${response.message()}")
        }
    }

    /**
     * 解析目标远程路径当前版本（上传 baseVersion 用，T-089）。
     * 按「月目录 → 路径→版本表」缓存，避免每张照片都全量拉取该月文件树；
     * 文件不存在或查询失败返回 0（baseVersion=0 表示不校验）。
     */
    private suspend fun resolveRemoteVersion(
        api: CloudPanApi,
        remotePath: String,
        cache: MutableMap<String, Map<String, Int>>
    ): Int {
        val folder = remotePath.substringBeforeLast('/').ifEmpty { "/" }
        return try {
            val versions = cache.getOrPut(folder) {
                val r = api.getFileTreeInFolder(folder, 10000, null)
                if (r.isSuccessful) {
                    r.body()?.data?.associate { it.path to it.version } ?: emptyMap()
                } else emptyMap()
            }
            versions[remotePath] ?: 0
        } catch (_: Exception) {
            0
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

        /** 单张照片失败重试上限（T-060）：连续失败达到该次数即标记 Blocked，越过游标继续备份后续照片。 */
        private const val MAX_RETRY = 3

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
