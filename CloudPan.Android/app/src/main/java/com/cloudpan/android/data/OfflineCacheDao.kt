package com.cloudpan.android.data

import androidx.room.*

/**
 * 离线缓存 DAO。
 */
@Dao
interface OfflineCacheDao {
    @Query("SELECT * FROM OfflineCache ORDER BY cachedAt DESC")
    suspend fun getAll(): List<OfflineCacheEntity>

    @Query("SELECT * FROM OfflineCache WHERE path = :path")
    suspend fun getByPath(path: String): OfflineCacheEntity?

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun insert(entity: OfflineCacheEntity)

    @Delete
    suspend fun delete(entity: OfflineCacheEntity)

    @Query("DELETE FROM OfflineCache WHERE path = :path")
    suspend fun deleteByPath(path: String)
}
