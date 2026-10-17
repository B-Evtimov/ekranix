package com.evtimov.ekranix



import android.content.Context
import android.os.Build
import android.provider.Settings
import java.util.UUID

object DeviceIdentity {
    fun id(ctx: Context): String {
        val prefs = ctx.getSharedPreferences("ekranix", Context.MODE_PRIVATE)
        return prefs.getString("id", null) ?: UUID.randomUUID().toString().also {
            prefs.edit().putString("id", it).apply()
        }
    }

    fun name(ctx: Context): String =
        Settings.Global.getString(ctx.contentResolver, Settings.Global.DEVICE_NAME)
            ?: Build.MODEL
}