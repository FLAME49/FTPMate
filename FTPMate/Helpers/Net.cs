using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace FTPMate.Helpers;

public static class Net
{
    public static string LocalIPv4()
    {
        try
        {
            var addresses = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                            n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                .ToList();

            var preferred = addresses.FirstOrDefault(a => !a.ToString().StartsWith("169.254."))
                            ?? addresses.FirstOrDefault();

            return preferred?.ToString() ?? "127.0.0.1";
        }
        catch
        {
            return "127.0.0.1";
        }
    }
}
