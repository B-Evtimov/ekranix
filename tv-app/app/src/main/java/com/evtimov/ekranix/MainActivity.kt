package com.evtimov.ekranix

import android.os.Bundle
import android.view.Surface
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.View
import android.view.WindowManager
import android.widget.TextView
import androidx.activity.OnBackPressedCallback
import androidx.appcompat.app.AppCompatActivity
import androidx.lifecycle.lifecycleScope

class MainActivity : AppCompatActivity(),
    ControlServer.Listener, VideoReceiver.Listener, SurfaceHolder.Callback {

    private lateinit var discovery: DiscoveryResponder
    private var control: ControlServer? = null
    private var video: VideoReceiver? = null

    private lateinit var overlay: View
    private lateinit var codeText: TextView
    private lateinit var statusText: TextView

    private var paired = false
    private var surface: Surface? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)

        overlay = findViewById(R.id.overlay)
        codeText = findViewById(R.id.codeText)
        statusText = findViewById(R.id.statusText)
        findViewById<TextView>(R.id.deviceNameText).text = DeviceIdentity.name(this)
        findViewById<SurfaceView>(R.id.videoSurface).holder.addCallback(this)

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

        val c = ControlServer(lifecycleScope, DeviceIdentity.name(this), this)
        control = c
        c.start()

        video = VideoReceiver(lifecycleScope, { addr -> c.pairedAddress == addr }, this).also {
            it.surface = surface
            it.start()
        }
    }

    override fun onStop() {
        video?.stop()
        video = null
        control?.stop()
        control = null
        discovery.stop()
        paired = false
        overlay.visibility = View.VISIBLE
        super.onStop()
    }

    // --- Surface ---
    override fun surfaceCreated(holder: SurfaceHolder) {
        surface = holder.surface
        video?.surface = holder.surface
    }

    override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) {}

    override fun surfaceDestroyed(holder: SurfaceHolder) {
        surface = null
        video?.surface = null
        video?.closeStream()
    }

    // --- Управление ---
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
        video?.closeStream()
        codeText.visibility = View.VISIBLE
        statusText.text = "$reason\nВъведи новия код на компютъра"
        overlay.visibility = View.VISIBLE
    }

    // --- Видео ---
    override fun onStreamStarted() {
        overlay.visibility = View.GONE
    }

    override fun onStreamEnded() {
        overlay.visibility = View.VISIBLE
    }
}