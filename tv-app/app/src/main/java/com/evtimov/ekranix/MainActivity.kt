package com.evtimov.ekranix

import android.os.Bundle
import android.view.View
import android.view.WindowManager
import android.widget.TextView
import androidx.activity.OnBackPressedCallback
import androidx.appcompat.app.AppCompatActivity
import androidx.lifecycle.lifecycleScope

class MainActivity : AppCompatActivity(), ControlServer.Listener {
    private lateinit var discovery: DiscoveryResponder
    private var control: ControlServer? = null
    private lateinit var codeText: TextView
    private lateinit var statusText: TextView
    private var paired = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)

        codeText = findViewById(R.id.codeText)
        statusText = findViewById(R.id.statusText)
        findViewById<TextView>(R.id.deviceNameText).text = DeviceIdentity.name(this)

        discovery = DiscoveryResponder(this)

        // НАЗАД: ако сме свързани, прекъсва връзката, иначе затваря приложението
        onBackPressedDispatcher.addCallback(this, object : OnBackPressedCallback(true) {
            override fun handleOnBackPressed() {
                if (paired) control?.stopSession() else finish()
            }
        })
    }

    override fun onStart() {
        super.onStart()
        discovery.start(lifecycleScope)
        control = ControlServer(lifecycleScope, DeviceIdentity.name(this), this).also { it.start() }
    }

    override fun onStop() {
        control?.stop()
        control = null
        discovery.stop()
        paired = false
        super.onStop()
    }

    override fun onCodeChanged(code: String) {
        codeText.text = "${code.substring(0, 3)} ${code.substring(3)}"
    }

    override fun onPaired(pcName: String) {
        paired = true
        codeText.visibility = View.INVISIBLE
        statusText.text = "Свързан с $pcName\nНАЗАД за прекъсване"
    }

    override fun onDisconnected(reason: String) {
        paired = false
        codeText.visibility = View.VISIBLE
        statusText.text = "$reason\nВъведи новия код на компютъра"
    }
}