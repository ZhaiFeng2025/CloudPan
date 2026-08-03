package com.cloudpan.android.data

import androidx.room.Dao
import androidx.room.Query
import androidx.room.Upsert

/**
 * 照片备份记录 DAO。状态机由 PhotoBackupWorker 驱动（Pending→Uploading→Done/Failed）。
 */
@Dao
interface BackupLogDao {

    /** 按本地 MediaStore URI 查记录（用于去重与状态更新）。 */
    @Query("SELECT * FROM BackupLog WHERE localUri = :localUri LIMIT 1")
    suspend fun findByLocalUri(localUri: String): BackupLogEntity?

    /** 内容级去重：同 FileHash 且已 Done(2) 则跳过（哈希相同视为同一照片内容）。 */
    @Query("SELECT * FROM BackupLog WHERE fileHash = :fileHash AND status = 2 LIMIT 1")
    suspend fun findDoneByFileHash(fileHash: String): BackupLogEntity?

    @Upsert
    suspend fun upsert(entity: BackupLogEntity)
}
