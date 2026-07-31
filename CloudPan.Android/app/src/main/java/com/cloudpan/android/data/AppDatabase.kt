package com.cloudpan.android.data

import android.content.Context
import androidx.room.Database
import androidx.room.Room
import androidx.room.RoomDatabase

/**
 * CloudPan Android 本地 Room 数据库。
 */
@Database(entities = [OfflineCacheEntity::class], version = 1, exportSchema = false)
abstract class AppDatabase : RoomDatabase() {
    abstract fun offlineCacheDao(): OfflineCacheDao

    companion object {
        @Volatile
        private var INSTANCE: AppDatabase? = null

        fun getInstance(context: Context): AppDatabase {
            return INSTANCE ?: synchronized(this) {
                INSTANCE ?: Room.databaseBuilder(
                    context.applicationContext,
                    AppDatabase::class.java,
                    "cloudpan_cache.db"
                ).build().also { INSTANCE = it }
            }
        }
    }
}
