package com.cloudpan.android.data

/**
 * Android 照片备份状态。显式数值对齐 shared-spec.json → enums.BackupStatus：
 * Pending=0 / Uploading=1 / Done=2 / Failed=3。
 * 服务端持久化与传输使用数值，禁止依赖 Kotlin 序数。
 */
enum class BackupStatus(val value: Int) {
    Pending(0),
    Uploading(1),
    Done(2),
    Failed(3)
}
