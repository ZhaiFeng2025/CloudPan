package com.cloudpan.android.data

import android.content.Context
import android.content.SharedPreferences

/**
 * 本地设置持久化（SharedPreferences）。
 * 存储服务端地址、Token、设备 ID。
 */
class SettingsStore(context: Context) {
    private val prefs: SharedPreferences =
        context.getSharedPreferences("cloudpan_settings", Context.MODE_PRIVATE)

    var serverUrl: String
        get() = prefs.getString("server_url", "http://10.0.2.2:8443") ?: "http://10.0.2.2:8443"
        set(value) = prefs.edit().putString("server_url", value).apply()

    var token: String
        get() = prefs.getString("token", "") ?: ""
        set(value) = prefs.edit().putString("token", value).apply()

    val deviceId: String
        get() {
            var id = prefs.getString("device_id", null)
            if (id == null) {
                id = java.util.UUID.randomUUID().toString().replace("-", "")
                prefs.edit().putString("device_id", id).apply()
            }
            return id
        }
}
