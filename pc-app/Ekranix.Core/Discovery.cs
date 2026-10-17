using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Ekranix.Core;

public record TvDevice(string Id, string Name, IPAddress Address, int ControlPort)
{
    public override string ToString() => $"{Name}   ({Address})";
}

public static class Discovery
{
    public static async Task<List<TvDevice>> ScanAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        using var udp = new UdpClient(0) { EnableBroadcast = true };

        // Windows: без това едно ICMP "port unreachable" (от VPN/виртуален адаптер)
        // кара следващото четене да хвърли SocketException 10054 и всичко пада.
        if (OperatingSystem.IsWindows())
        {
            const int SIO_UDP_CONNRESET = -1744830452;
            udp.Client.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null);
        }

        var payload = Encoding.UTF8.GetBytes(Protocol.DiscoverMagic);
        var targets = BroadcastAddresses().Distinct().ToList();

        // Пращаме 2 пъти, защото UDP може да изгуби пакет.
        // Някой адаптер да откаже не е проблем, продължаваме с останалите.
        for (int i = 0; i < 2; i++)
        {
            foreach (var addr in targets)
            {
                try
                {
                    await udp.SendAsync(payload, new IPEndPoint(addr, Protocol.DiscoveryPort), ct);
                }
                catch (SocketException) { }
            }
        }

        var found = new Dictionary<string, TvDevice>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        while (!cts.IsCancellationRequested)
        {
            UdpReceiveResult res;
            try
            {
                res = await udp.ReceiveAsync(cts.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { continue; } // шум от мрежата: игнорираме

            try
            {
                var j = JsonDocument.Parse(res.Buffer).RootElement;
                if (j.GetProperty("type").GetString() != "here") continue;

                var id = j.GetProperty("id").GetString()!;
                found[id] = new TvDevice(
                    id,
                    j.GetProperty("name").GetString()!,
                    res.RemoteEndPoint.Address,
                    j.GetProperty("controlPort").GetInt32());
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                // чужд или повреден пакет: игнорираме
            }
        }

        return found.Values.ToList();
    }

    // Broadcast адрес за всяка активна мрежова карта.
    private static IEnumerable<IPAddress> BroadcastAddresses()
    {
        yield return IPAddress.Broadcast;

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up ||
                ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                var ip = ua.Address.GetAddressBytes();
                var mask = ua.IPv4Mask.GetAddressBytes();

                // Маска 255.255.255.255 (VPN и подобни): broadcast-ът е самото IP, пропускаме
                if (mask.All(b => b == 255)) continue;

                var b = new byte[4];
                for (int i = 0; i < 4; i++) b[i] = (byte)(ip[i] | ~mask[i]);
                yield return new IPAddress(b);
            }
        }
    }
}