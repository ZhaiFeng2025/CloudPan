package com.cloudpan.android.data

import androidx.room.Entity
import androidx.room.PrimaryKey

/**
 * 离线缓存实体——与 shared-spec.json OfflineCache 对齐。
 */
@Entity(tableName = "OfflineCache")
data class OfflineCacheEntity(
    @PrimaryKey val path: String,
    val localPath: String,
    val fileHash: String,
    val fileSize: Long,
    val cachedAt: String
)
