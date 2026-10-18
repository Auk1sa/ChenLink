using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ChenLink.Core;

public static class LocalNetwork
{
    /// <summary>本机局域网 IPv4（排除回环与虚拟网卡），用于提示好友该连哪个地址。</summary>
    public static string[] IPv4()
    {
        var list = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(addr.Address))
                        list.Add(addr.Address.ToString());
                }
            }
        }
        catch
        {
            // 取不到就返回空，界面自行降级
        }
        return list.Distinct().ToArray();
    }
}
