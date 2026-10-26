using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using QRCoder;

namespace Scimilir;

public static class Network
{
    /// <summary>
    /// The addresses other devices on your home network (like your phone) can
    /// use to reach this PC, such as http://192.168.1.20:5080.
    /// </summary>
    public static List<string> LanUrls(int port)
    {
        var addresses = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;
                foreach (var a in nic.GetIPProperties().UnicastAddresses)
                    if (a.Address.AddressFamily == AddressFamily.InterNetwork && IsPrivate(a.Address))
                        addresses.Add(a.Address);
            }
        }
        catch (NetworkInformationException) { }

        // Home routers almost always use 192.168.x.x, so list those first.
        return [.. addresses.Distinct()
            .OrderBy(a => a.GetAddressBytes()[0] == 192 ? 0 : 1)
            .Select(a => $"http://{a}:{port}")];
    }

    static bool IsPrivate(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }

    /// <summary>A QR code picture (SVG) of the address, for a phone camera to scan.</summary>
    public static string QrSvg(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        return new SvgQRCode(data).GetGraphic(8);
    }
}
