using System.Net;
using System.Net.Sockets;

namespace YoutubeDownloadHelper.Engine.Hosting;

public static class PortProbe
{
    /// <summary>
    /// <paramref name="port"/> if it can be bound on <c>127.0.0.1</c> right now, else <c>0</c> (a dynamic port). Another
    /// process could still take it before the server binds; that start then fails and the supervisor retries.
    /// </summary>
    public static int AvailableOrDynamic(int port)
    {
        if (port == 0)
        {
            return 0;
        }

        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true };
            probe.Bind(new IPEndPoint(IPAddress.Loopback, port));
            return port;
        }
        catch (SocketException)
        {
            return 0;
        }
    }
}
