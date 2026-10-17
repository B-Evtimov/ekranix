package com.evtimov.ekranix

import android.os.Bundle
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity
import androidx.lifecycle.lifecycleScope

class MainActivity : AppCompatActivity() {
    private lateinit var discovery: DiscoveryResponder

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)
        findViewById<TextView>(R.id.deviceNameText).text = DeviceIdentity.name(this)
        discovery = DiscoveryResponder(this)
    }

    override fun onStart() {
        super.onStart()
        discovery.start(lifecycleScope)
    }

    override fun onStop() {
        discovery.stop()
        super.onStop()
    }
}