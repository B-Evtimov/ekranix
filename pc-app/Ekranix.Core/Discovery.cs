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
        var payload = Encoding.UTF8.GetBytes(Protocol.DiscoverMagic);

        // Пращаме 2 пъти, защото UDP може да изгуби пакет
        for (int i = 0; i < 2; i++)
            foreach (var addr in BroadcastAddresses())
                await udp.SendAsync(payload, new IPEndPoint(addr, Protocol.DiscoveryPort), ct);

        var found = new Dictionary<string, TvDevice>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            while (true)
            {
                var res = await udp.ReceiveAsync(cts.Token);
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
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException)
                {
                    // чужд или повреден пакет: игнорираме
                }
            }
        }
        catch (OperationCanceledException) { }

        return found.Values.ToList();
    }

    // Broadcast адрес за всяка активна мрежова карта.
    // Само 255.255.255.255 не стига, защото Windows понякога го праща само през една карта.
    private static IEnumerable<IPAddress> BroadcastAddresses()
    {
        yield return IPAddress.Broadcast;

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up ||
                ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                var ip = ua.Address.GetAddressBytes();
                var mask = ua.IPv4Mask.GetAddressBytes();
                var b = new byte[4];
                for (int i = 0; i < 4; i++) b[i] = (byte)(ip[i] | ~mask[i]);
                yield return new IPAddress(b);
            }
        }
    }
}