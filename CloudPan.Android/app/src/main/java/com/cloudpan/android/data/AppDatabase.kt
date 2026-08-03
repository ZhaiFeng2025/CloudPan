package com.cloudpan.android.data

import android.content.Context
import androidx.room.Database
import androidx.room.Room
import androidx.room.RoomDatabase
import androidx.room.migration.Migration
import androidx.sqlite.db.SupportSQLiteDatabase

/**
 * CloudPan Android 本地 Room 数据库。
 */
@Database(
    entities = [OfflineCacheEntity::class, BackupLogEntity::class],
    version = 3,
    exportSchema = false
)
abstract class AppDatabase : RoomDatabase() {
    abstract fun offlineCacheDao(): OfflineCacheDao
    abstract fun backupLogDao(): BackupLogDao

    companion object {
        @Volatile
        private var INSTANCE: AppDatabase? = null

        /** v1→v2：新增 BackupLog 表（与 shared-spec.json → entities.BackupLog 列对齐）。 */
        private val MIGRATION_1_2 = object : Migration(1, 2) {
            override fun migrate(db: SupportSQLiteDatabase) {
                db.execSQL(
                    "CREATE TABLE IF NOT EXISTS `BackupLog` (" +
                        "`id` INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, " +
                        "`localUri` TEXT NOT NULL, " +
                        "`remotePath` TEXT NOT NULL, " +
                        "`fileHash` TEXT NOT NULL, " +
                        "`fileSize` INTEGER NOT NULL, " +
                        "`status` INTEGER NOT NULL, " +
                        "`createdAt` TEXT NOT NULL)"
                )
            }
        }

        /** v2→v3：BackupLog 新增 retryCount（失败重试计数，默认 0，T-060）。 */
        private val MIGRATION_2_3 = object : Migration(2, 3) {
            override fun migrate(db: SupportSQLiteDatabase) {
                db.execSQL("ALTER TABLE `BackupLog` ADD COLUMN `retryCount` INTEGER NOT NULL DEFAULT 0")
            }
        }

        fun getInstance(context: Context): AppDatabase {
            return INSTANCE ?: synchronized(this) {
                INSTANCE ?: Room.databaseBuilder(
                    context.applicationContext,
                    AppDatabase::class.java,
                    "cloudpan_cache.db"
                )
                    .addMigrations(MIGRATION_1_2, MIGRATION_2_3)
                    .build()
                    .also { INSTANCE = it }
            }
        }
    }
}
