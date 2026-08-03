package com.cloudpan.android.data

import androidx.room.Entity
import androidx.room.PrimaryKey

/**
 * 照片备份记录——与 shared-spec.json → entities.BackupLog 对齐。
 * localUri: 本地 MediaStore URI；fileHash: SHA-256（去重依据）；status: BackupStatus 枚举值。
 *
 * retryCount: 失败重试计数（Android 本地私有状态，不跨进程/不对服务端暴露，
 * 属契约驱动例外「纯内部类型」，见 CLAUDE.md 规则 0，故不进 shared-spec.json）。
 * T-060：连续失败达到上限（PhotoBackupWorker.MAX_RETRY）即标记 Blocked（Failed + retryCount 达上限），
 * 游标越过不再阻塞后续照片。
 */
@Entity(tableName = "BackupLog")
data class BackupLogEntity(
    @PrimaryKey(autoGenerate = true) val id: Long = 0,
    val localUri: String,
    val remotePath: String,
    val fileHash: String,
    val fileSize: Long,
    val status: Int,
    val retryCount: Int = 0,
    val createdAt: String
)
