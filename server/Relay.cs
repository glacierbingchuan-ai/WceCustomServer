using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WceRoomServer;

/// <summary>
/// UDP 中继：房主和客户端都主动连到中继端口，服务器在中间转发 KCP 报文，
/// 从而免去房主做端口映射（房主 NAT 的映射由它自己向外发包建立）。
/// </summary>
public static class Relay
{
    private const string RegisterMagic = "WCR1";
    private const string RegisterAck = "WCOK";
    private const int MaxClientsPerRoom = 16;
    private const long HostTimeoutMs = 60000;

    private sealed class Session
    {
        public string RoomId = "";
        public int Port;
        public UdpClient Socket = null!;
        public Thread? Worker;
        public volatile bool Running = true;
        public IPEndPoint? HostEndpoint;
        public long LastHostSeenAt;
        public readonly Dictionary<uint, IPEndPoint> ClientsByToken = new();
        public readonly Dictionary<string, uint> TokensByEndpoint = new();
        public readonly Dictionary<uint, long> ClientLastSeen = new();
    }

    private static readonly ConcurrentDictionary<string, Session> s_sessions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<int> s_usedPorts = new();
    private static readonly object s_lock = new();

    private static bool s_enabled;
    private static int s_portStart = 7100;
    private static int s_portCount = 200;
    private static string s_host = "";
    private static int s_maxPlayers = 6;

    public static bool Enabled => s_enabled;
    public static string Host => s_host;

    public static void Init(ServerConfig cfg)
    {
        s_enabled = cfg.RelayEnabled;
        s_portStart = Math.Max(1024, cfg.RelayPortStart);
        s_portCount = Math.Max(1, cfg.RelayPortCount);
        s_maxPlayers = Math.Max(1, cfg.MaxPlayersPerRoom);
        s_host = (cfg.RelayHost ?? "").Trim();
        if (s_host.Length == 0)
        {
            s_host = DetectLocalIPv4();
        }
        if (s_enabled)
        {
            Log.Info($"UDP 中继已启用：{s_host}，端口范围 {s_portStart}-{s_portStart + s_portCount - 1}");
        }
    }

    private static string DetectLocalIPv4()
    {
        try
        {
            foreach (System.Net.NetworkInformation.NetworkInterface ni in
                     System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                {
                    continue;
                }
                foreach (System.Net.NetworkInformation.UnicastIPAddressInformation ip in
                         ni.GetIPProperties().UnicastAddresses)
                {
                    if (ip.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !System.Net.IPAddress.IsLoopback(ip.Address))
                    {
                        return ip.Address.ToString();
                    }
                }
            }
        }
        catch
        {
            // ignored
        }
        return "127.0.0.1";
    }

    /// <summary>为房间分配一个中继端口并启动监听；失败返回 0。</summary>
    public static int Allocate(string roomId)
    {
        if (!s_enabled)
        {
            return 0;
        }
        int port = 0;
        lock (s_lock)
        {
            for (int i = 0; i < s_portCount; i++)
            {
                int candidate = s_portStart + i;
                if (!s_usedPorts.Contains(candidate))
                {
                    port = candidate;
                    s_usedPorts.Add(port);
                    break;
                }
            }
        }
        if (port == 0)
        {
            Log.Warn("中继端口已耗尽，无法为房间分配端口");
            return 0;
        }

        Session session = new() { RoomId = roomId, Port = port };
        try
        {
            session.Socket = new UdpClient(new IPEndPoint(IPAddress.Any, port));
            session.Socket.Client.ReceiveTimeout = 1000;
        }
        catch (Exception ex)
        {
            Log.Warn($"中继端口 {port} 监听失败：{ex.Message}");
            lock (s_lock)
            {
                s_usedPorts.Remove(port);
            }
            return 0;
        }

        s_sessions[roomId] = session;
        session.Worker = new Thread(() => Loop(session));
        session.Worker.IsBackground = true;
        session.Worker.Name = "relay-" + port;
        session.Worker.Start();
        Log.Info($"房间 {roomId} 中继端口：{port}");
        return port;
    }

    public static void FreeRoom(string roomId)
    {
        Session? session;
        if (!s_sessions.TryRemove(roomId, out session) || session == null)
        {
            return;
        }
        session.Running = false;
        try
        {
            session.Socket.Dispose();
        }
        catch
        {
            // ignored
        }
        lock (s_lock)
        {
            s_usedPorts.Remove(session.Port);
        }
        Log.Info($"房间 {roomId} 中继端口释放：{session.Port}");
    }

    private static void Loop(Session session)
    {
        UdpClient socket = session.Socket;
        byte[] buffer = new byte[8192];
        while (session.Running)
        {
            IPEndPoint? remote = null;
            try
            {
                byte[] data = socket.Receive(ref remote);
                Handle(session, remote, data);
            }
            catch (SocketException ex)
            {
                if (!session.Running)
                {
                    break;
                }
                if (ex.SocketErrorCode == SocketError.TimedOut || ex.SocketErrorCode == SocketError.WouldBlock)
                {
                    if (session.HostEndpoint != null &&
                        Store.Now() - session.LastHostSeenAt > HostTimeoutMs)
                    {
                        session.HostEndpoint = null;
                    }
                    continue;
                }
                if (ex.SocketErrorCode == SocketError.ConnectionReset)
                {
                    continue;
                }
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warn("中继异常：" + ex.Message);
            }
        }
    }

    private static readonly byte[] s_magic = Encoding.ASCII.GetBytes(RegisterMagic);

    private static void Handle(Session session, IPEndPoint remote, byte[] data)
    {
        if (remote == null || data.Length < 2)
        {
            return;
        }

        // 房主注册（每次注册都会刷新 NAT 映射）
        if (data.Length >= 4 &&
            data[0] == s_magic[0] && data[1] == s_magic[1] && data[2] == s_magic[2] && data[3] == s_magic[3])
        {
            session.HostEndpoint = new IPEndPoint(remote.Address, remote.Port);
            session.LastHostSeenAt = Store.Now();
            SendTo(session, remote, Encoding.ASCII.GetBytes(RegisterAck));
            return;
        }

        if (session.HostEndpoint == null)
        {
            return;
        }

        bool fromHost = remote.Address.Equals(session.HostEndpoint.Address) &&
                        remote.Port == session.HostEndpoint.Port;

        if (fromHost)
        {
            // 房主回包：前 4 字节是客户端令牌
            if (data.Length < 5)
            {
                return;
            }
            uint token = BitConverter.ToUInt32(data, 0);
            IPEndPoint? client;
            lock (session.ClientsByToken)
            {
                client = session.ClientsByToken.TryGetValue(token, out IPEndPoint? ep) ? ep : null;
            }
            if (client == null)
            {
                return;
            }
            byte[] payload = new byte[data.Length - 4];
            Array.Copy(data, 4, payload, 0, payload.Length);
            SendTo(session, client, payload);
            return;
        }

        // 客户端上行：按来源地址分配令牌，加上 4 字节头后转发给房主
        string key = remote.Address + ":" + remote.Port;
        uint clientToken;
        lock (session.ClientsByToken)
        {
            uint? existing = session.TokensByEndpoint.TryGetValue(key, out uint t) ? t : (uint?)null;
            if (existing.HasValue)
            {
                clientToken = existing.Value;
            }
            else
            {
                if (session.ClientsByToken.Count >= Math.Min(MaxClientsPerRoom, s_maxPlayers + 4))
                {
                    return;
                }
                do
                {
                    clientToken = (uint)Random.Shared.Next(1, int.MaxValue);
                } while (session.ClientsByToken.ContainsKey(clientToken));

                session.ClientsByToken[clientToken] = remote;
                session.TokensByEndpoint[key] = clientToken;
            }
            session.ClientLastSeen[clientToken] = Store.Now();
        }

        byte[] outData = new byte[data.Length + 4];
        BitConverter.GetBytes(clientToken).CopyTo(outData, 0);
        Array.Copy(data, 0, outData, 4, data.Length);
        SendTo(session, session.HostEndpoint, outData);
    }

    private static void SendTo(Session session, IPEndPoint endpoint, byte[] data)
    {
        try
        {
            lock (session.Socket)
            {
                session.Socket.Send(data, data.Length, endpoint);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("中继发送失败：" + ex.Message);
        }
    }
}
