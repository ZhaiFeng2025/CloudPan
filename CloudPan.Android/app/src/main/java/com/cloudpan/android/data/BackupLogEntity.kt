package com.cloudpan.android.data

import androidx.room.Entity
import androidx.room.PrimaryKey

/**
 * 照片备份记录——与 shared-spec.json → entities.BackupLog 对齐。
 * localUri: 本地 MediaStore URI；fileHash: SHA-256（去重依据）；status: BackupStatus 枚举值。
 */
@Entity(tableName = "BackupLog")
data class BackupLogEntity(
    @PrimaryKey(autoGenerate = true) val id: Long = 0,
    val localUri: String,
    val remotePath: String,
    val fileHash: String,
    val fileSize: Long,
    val status: Int,
    val createdAt: String
)
