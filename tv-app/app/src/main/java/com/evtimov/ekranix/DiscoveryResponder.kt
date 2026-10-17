package com.evtimov.ekranix

import android.content.Context
import android.net.wifi.WifiManager
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import org.json.JSONObject
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetSocketAddress
import java.net.SocketException

class DiscoveryResponder(private val ctx: Context) {
    private var socket: DatagramSocket? = null
    private var job: Job? = null
    private var lock: WifiManager.MulticastLock? = null

    fun start(scope: CoroutineScope) {
        // Без този lock много телевизори изхвърлят broadcast пакетите, за да пестят ток
        val wifi = ctx.applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
        lock = wifi.createMulticastLock("ekranix").apply {
            setReferenceCounted(false)
            acquire()
        }

        val reply = JSONObject()
            .put("type", "here")
            .put("id", DeviceIdentity.id(ctx))
            .put("name", DeviceIdentity.name(ctx))
            .put("controlPort", Protocol.CONTROL_PORT)
            .toString()
            .toByteArray()

        job = scope.launch(Dispatchers.IO) {
            val s = DatagramSocket(null).apply {
                reuseAddress = true
                broadcast = true
                bind(InetSocketAddress(Protocol.DISCOVERY_PORT))
            }
            socket = s
            val buf = ByteArray(512)
            while (isActive) {
                val packet = DatagramPacket(buf, buf.size)
                try {
                    s.receive(packet)
                } catch (e: SocketException) {
                    break // сокетът е затворен от stop()
                }
                val msg = String(packet.data, 0, packet.length).trim()
                if (msg == Protocol.DISCOVER_MAGIC) {
                    s.send(DatagramPacket(reply, reply.size, packet.address, packet.port))
                }
            }
        }
    }

    fun stop() {
        socket?.close()
        job?.cancel()
        lock?.release()
        socket = null
        job = null
        lock = null
    }
}