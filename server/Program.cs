﻿﻿﻿using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace WceRoomServer;

internal static class Log
{
    public static event Action<string>? OnLog;

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg) => Write("ERROR", msg);

    private static void Write(string level, string msg)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] [{level}] {msg}";
        try
        {
            OnLog?.Invoke(line);
        }
        catch
        {
        }
    }
}

internal sealed class HttpRequest
{
    public string Method = "GET";
    public string Path = "/";
    public string Query = "";
    public Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);
    public byte[] Body = Array.Empty<byte>();

    public string Text => Encoding.UTF8.GetString(Body);

    public string GetQuery(string key)
    {
        foreach (string pair in Query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int idx = pair.IndexOf('=');
            string k = idx < 0 ? pair : pair[..idx];
            if (Uri.UnescapeDataString(k) == key)
            {
                return idx < 0 ? "" : Uri.UnescapeDataString(pair[(idx + 1)..]);
            }
        }
        return "";
    }

    public string GetHeader(string key) => Headers.TryGetValue(key, out string v) ? v : "";

    public T? Json<T>() where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(Text, Store.JsonOpts);
        }
        catch
        {
            return null;
        }
    }
}

internal sealed class HttpResponse
{
    public int Status = 200;
    public string ContentType = "application/json; charset=utf-8";
    public string Body = "";

    public static HttpResponse Json(object value, int status = 200) => new()
    {
        Status = status,
        Body = JsonSerializer.Serialize(value, Store.JsonOpts)
    };

    public static HttpResponse Error(string message, int status = 400) => Json(new { ok = false, error = message }, status);
}

internal sealed class Server
{
    private readonly ServerConfig _cfg;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public Server(ServerConfig cfg)
    {
        _cfg = cfg;
    }

    public void Start()
    {
        IPAddress bind = _cfg.Bind == "0.0.0.0"
            ? IPAddress.Any
            : IPAddress.Parse(_cfg.Bind);
        _listener = new TcpListener(bind, _cfg.Port);
        _listener.Start();
        _cts = new CancellationTokenSource();

        Log.Info($"HTTP 服务已启动：http://{_cfg.Bind}:{_cfg.Port}");
        foreach (string ip in GetLocalIPv4())
        {
            Log.Info($"局域网可访问：http://{ip}:{_cfg.Port}/api/health");
        }

        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _ = Task.Run(() => CleanupLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }
                Log.Warn("接受连接失败：" + ex.Message);
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(client), token);
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        try
        {
            client.NoDelay = true;
            using NetworkStream stream = client.GetStream();
            HttpRequest? request = await ReadRequestAsync(stream);
            if (request == null)
            {
                return;
            }

            HttpResponse response;
            try
            {
                response = Router.Handle(request, _cfg, client.Client.RemoteEndPoint as IPEndPoint);
            }
            catch (Exception ex)
            {
                Log.Error($"处理 {request.Method} {request.Path} 异常：{ex}");
                response = HttpResponse.Error("服务器内部错误", 500);
            }
            await WriteResponseAsync(stream, response);
        }
        catch (Exception ex)
        {
            Log.Warn("连接处理异常：" + ex.Message);
        }
        finally
        {
            client.Close();
        }
    }

    private static async Task<HttpRequest?> ReadRequestAsync(NetworkStream stream)
    {
        const int maxHeader = 64 * 1024;
        List<byte> all = new(1024);
        byte[] buf = new byte[8192];
        int headerEnd = -1;

        while (all.Count < maxHeader)
        {
            int n = await stream.ReadAsync(buf);
            if (n <= 0)
            {
                return null;
            }
            for (int i = 0; i < n; i++)
            {
                all.Add(buf[i]);
            }
            headerEnd = FindHeaderEnd(all);
            if (headerEnd >= 0)
            {
                break;
            }
        }
        if (headerEnd < 0)
        {
            return null;
        }

        string head = Encoding.UTF8.GetString(all.GetRange(0, headerEnd).ToArray());
        string[] lines = head.Split("\r\n");
        string[] parts = lines[0].Split(' ');
        if (parts.Length < 2)
        {
            return null;
        }

        HttpRequest req = new()
        {
            Method = parts[0].ToUpperInvariant(),
            Path = parts[1]
        };

        int q = req.Path.IndexOf('?');
        if (q >= 0)
        {
            req.Query = req.Path[(q + 1)..];
            req.Path = req.Path[..q];
        }
        req.Path = Uri.UnescapeDataString(req.Path);

        for (int i = 1; i < lines.Length; i++)
        {
            int c = lines[i].IndexOf(':');
            if (c > 0)
            {
                req.Headers[lines[i][..c].Trim()] = lines[i][(c + 1)..].Trim();
            }
        }

        int contentLength = 0;
        string cl = req.GetHeader("Content-Length");
        if (!string.IsNullOrEmpty(cl))
        {
            int.TryParse(cl, out contentLength);
        }
        if (contentLength < 0 || contentLength > 1024 * 1024)
        {
            return null;
        }

        int bodyStart = headerEnd + 4;
        int have = all.Count - bodyStart;
        while (have < contentLength)
        {
            int n = await stream.ReadAsync(buf);
            if (n <= 0)
            {
                break;
            }
            for (int i = 0; i < n; i++)
            {
                all.Add(buf[i]);
            }
            have = all.Count - bodyStart;
        }
        if (contentLength > 0 && have >= contentLength)
        {
            req.Body = all.GetRange(bodyStart, contentLength).ToArray();
        }
        return req;
    }

    private static int FindHeaderEnd(List<byte> data)
    {
        for (int i = 0; i + 3 < data.Count; i++)
        {
            if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
            {
                return i;
            }
        }
        return -1;
    }

    private static async Task WriteResponseAsync(NetworkStream stream, HttpResponse response)
    {
        byte[] body = Encoding.UTF8.GetBytes(response.Body);
        string reason = response.Status switch
        {
            200 => "OK",
            201 => "Created",
            204 => "No Content",
            400 => "Bad Request",
            401 => "Unauthorized",
            403 => "Forbidden",
            404 => "Not Found",
            405 => "Method Not Allowed",
            409 => "Conflict",
            413 => "Payload Too Large",
            429 => "Too Many Requests",
            500 => "Internal Server Error",
            _ => "OK"
        };
        StringBuilder sb = new();
        sb.Append($"HTTP/1.1 {response.Status} {reason}\r\n");
        sb.Append($"Content-Type: {response.ContentType}\r\n");
        sb.Append($"Content-Length: {body.Length}\r\n");
        sb.Append("Access-Control-Allow-Origin: *\r\n");
        sb.Append("Access-Control-Allow-Headers: *\r\n");
        sb.Append("Access-Control-Allow-Methods: GET,POST,DELETE,OPTIONS\r\n");
        sb.Append("Cache-Control: no-store\r\n");
        sb.Append("Connection: close\r\n\r\n");
        byte[] head = Encoding.UTF8.GetBytes(sb.ToString());
        await stream.WriteAsync(head);
        if (body.Length > 0)
        {
            await stream.WriteAsync(body);
        }
        await stream.FlushAsync();
    }

    private async Task CleanupLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(5000, token);
                Store.CleanupExpired();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warn("清理过期房间失败：" + ex.Message);
            }
        }
    }

    public static IEnumerable<string> GetLocalIPv4()
    {
        HashSet<string> list = new();
        try
        {
            foreach (IPAddress ip in Dns.GetHostEntry(Dns.GetHostName()).AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                {
                    list.Add(ip.ToString());
                }
            }
        }
        catch
        {
        }
        if (list.Count == 0)
        {
            list.Add("127.0.0.1");
        }
        return list;
    }
}

internal static class Router
{
    public static HttpResponse Handle(HttpRequest req, ServerConfig cfg, IPEndPoint? remote)
    {
        if (req.Method == "OPTIONS")
        {
            return new HttpResponse { Status = 204, Body = "" };
        }

        string[] seg = req.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (seg.Length == 0)
        {
            return HttpResponse.Json(new
            {
                ok = true,
                service = cfg.ServerName,
                hint = "管理面板请直接使用服务器窗口（WinForms）"
            });
        }

        try
        {
            if (seg[0] != "api")
            {
                return HttpResponse.Error("接口不存在", 404);
            }

            if (seg.Length == 2 && seg[1] == "health" && req.Method == "GET")
            {
                return HttpResponse.Json(new
                {
                    ok = true,
                    service = cfg.ServerName,
                    version = "1.0",
                    time = Store.Now(),
                    rooms = Store.Rooms.Count(),
                    players = Store.Players.Count(),
                    relay = Relay.Enabled,
                    relayHost = Relay.Host
                });
            }

            if (seg.Length == 2 && seg[1] == "info" && req.Method == "GET")
            {
                return HttpResponse.Json(new
                {
                    ok = true,
                    serverName = cfg.ServerName,
                    maxRooms = cfg.MaxRooms,
                    maxPlayersPerRoom = cfg.MaxPlayersPerRoom,
                    roomTtlSeconds = cfg.RoomTtlSeconds
                });
            }

            if (seg.Length == 2 && seg[1] == "stats" && req.Method == "GET")
            {
                return HttpResponse.Json(new
                {
                    ok = true,
                    roomCount = Store.Rooms.Count(),
                    readyRooms = Store.Rooms.Count(r => r.Status == "Ready" && !r.Hidden),
                    runningRooms = Store.Rooms.Count(r => r.Status == "Running"),
                    onlinePlayers = Store.Rooms.Sum(r => r.PlayerCount),
                    accountCount = Store.Players.Count()
                });
            }

            if (seg.Length == 2 && seg[1] == "rooms")
            {
                if (req.Method == "GET")
                {
                    return ListRooms(req);
                }
                if (req.Method == "POST")
                {
                    return CreateRoom(req, remote);
                }
                return HttpResponse.Error("方法不支持", 405);
            }

            if (seg.Length == 4 && seg[1] == "rooms" && seg[2] == "by-code" && req.Method == "GET")
            {
                Room? byCode = Store.FindRoomByCode(seg[3]);
                if (byCode == null)
                {
                    return HttpResponse.Error("房间码无效或已过期", 404);
                }
                return HttpResponse.Json(new { ok = true, room = byCode });
            }

            if (seg.Length >= 3 && seg[1] == "rooms")
            {
                string id = seg[2];
                Room? room = Store.FindRoom(id);
                if (room == null)
                {
                    return HttpResponse.Error("房间不存在或已关闭", 404);
                }
                if (seg.Length == 3 && req.Method == "GET")
                {
                    return HttpResponse.Json(new { ok = true, room });
                }
                if (seg.Length == 4 && seg[3] == "heartbeat" && req.Method == "POST")
                {
                    return Heartbeat(req, room);
                }
                if (seg.Length == 4 && seg[3] == "status" && req.Method == "POST")
                {
                    return UpdateStatus(req, room);
                }
                if (seg.Length == 3 && req.Method == "DELETE")
                {
                    return DeleteRoom(req, room);
                }
                return HttpResponse.Error("接口不存在", 404);
            }

            if (seg.Length >= 2 && seg[1] == "players")
            {
                if (seg.Length == 3 && seg[2] == "register" && req.Method == "POST")
                {
                    return RegisterPlayer(req);
                }
                if (seg.Length == 3 && seg[2] == "rename" && req.Method == "POST")
                {
                    return RenamePlayer(req);
                }
                if (seg.Length == 3 && req.Method == "GET")
                {
                    Player? p = Store.FindPlayer(seg[2]);
                    if (p == null)
                    {
                        return HttpResponse.Error("账号不存在", 404);
                    }
                    return HttpResponse.Json(new
                    {
                        ok = true,
                        player = new
                        {
                            playerId = p.PlayerId,
                            name = p.Name,
                            createdAt = p.CreatedAt,
                            lastSeenAt = p.LastSeenAt,
                            visitCount = p.VisitCount
                        }
                    });
                }
            }

            return HttpResponse.Error("接口不存在", 404);
        }
        catch (Exception ex)
        {
            Log.Error("路由异常：" + ex);
            return HttpResponse.Error("服务器内部错误", 500);
        }
    }

    private static HttpResponse ListRooms(HttpRequest req)
    {
        bool includeHidden = req.GetQuery("includeHidden") == "1";
        List<Room> rooms = Store.Rooms
            .Where(r => includeHidden || !r.Hidden)
            .Where(r => r.Status == "Ready" || r.Status == "Running")
            .OrderByDescending(r => r.PlayerCount)
            .ThenByDescending(r => r.UpdatedAt)
            .ToList();
        return HttpResponse.Json(new { ok = true, rooms, serverTime = Store.Now() });
    }

    private sealed class CreateRoomRequest
    {
        public string? Name { get; set; }
        public string? HostName { get; set; }
        public string? HostPlayerId { get; set; }
        public string? Token { get; set; }
        public string? Address { get; set; }
        public int Port { get; set; }
        public int MaxPlayers { get; set; }
        public bool Hidden { get; set; }
    }

    private static HttpResponse CreateRoom(HttpRequest req, IPEndPoint? remote)
    {
        CreateRoomRequest? body = req.Json<CreateRoomRequest>();
        if (body == null)
        {
            return HttpResponse.Error("请求内容不是合法 JSON");
        }

        string hostName = (body.HostName ?? "").Trim();
        string? error = null;
        if (!Store.ValidateName(hostName, out error))
        {
            return HttpResponse.Error(error ?? "主机名字不合法");
        }

        string playerId = (body.HostPlayerId ?? "").Trim();
        Player? player = null;
        if (playerId.Length > 0)
        {
            player = Store.FindPlayer(playerId);
            if (player != null && !Store.ValidateToken(player, body.Token ?? ""))
            {
                return HttpResponse.Error("玩家令牌无效，请重新注册名字", 401);
            }
        }

        int port = body.Port <= 0 ? 6565 : body.Port;
        string address = (body.Address ?? "").Trim();
        if (string.IsNullOrEmpty(address))
        {
            address = remote?.Address.ToString() ?? "";
            if (address.StartsWith("::ffff:"))
            {
                address = address[7..];
            }
        }
        if (!Relay.Enabled && string.IsNullOrEmpty(address))
        {
            return HttpResponse.Error("无法识别主机地址，请在 CustomServer.cfg 中设置 PublicHost");
        }

        Room room = new()
        {
            Name = body.Name ?? "",
            HostName = hostName,
            HostPlayerId = player?.PlayerId ?? playerId,
            Address = address,
            Port = port,
            MaxPlayers = body.MaxPlayers <= 0 ? 6 : body.MaxPlayers,
            Hidden = body.Hidden,
            Token = player?.Token ?? ""
        };

        // 同一玩家重复建房时，先关闭旧房间
        Room? old = Store.Rooms.FirstOrDefault(r => r.HostPlayerId == room.HostPlayerId && room.HostPlayerId.Length > 0);
        if (old != null)
        {
            Store.RemoveRoom(old.Id);
        }

        Room? created = Store.CreateRoom(room, out error);
        if (created == null)
        {
            return HttpResponse.Error(error ?? "创建房间失败");
        }

        // 启用中继时，房间对外地址统一指向服务器中继端口，客户端无需关心房主地址
        if (Relay.Enabled)
        {
            int relayPort = Relay.Allocate(created.Id);
            if (relayPort > 0)
            {
                created.Address = Relay.Host;
                created.Port = relayPort;
                created.Relayed = true;
            }
            else
            {
                Log.Warn($"房间 {created.JoinCode} 中继端口分配失败，退回直连地址 {created.Address}:{created.Port}");
            }
        }

        Log.Info($"房间上线：{created.Name} / 主机 {created.HostName} / {created.JoinCode} / " +
                 $"连接 {created.Address}:{created.Port} / id={created.Id}");
        return HttpResponse.Json(new { ok = true, room = created }, 201);
    }

    private sealed class HeartbeatRequest
    {
        public string? Token { get; set; }
        public int PlayerCount { get; set; }
        public string? Status { get; set; }
    }

    private static HttpResponse Heartbeat(HttpRequest req, Room room)
    {
        HeartbeatRequest? body = req.Json<HeartbeatRequest>();
        if (body == null)
        {
            return HttpResponse.Error("请求内容不是合法 JSON");
        }
        if (!RoomTokenOk(room, body.Token, req))
        {
            return HttpResponse.Error("房间令牌无效", 401);
        }

        room.PlayerCount = Math.Clamp(body.PlayerCount, 0, room.MaxPlayers);
        if (!string.IsNullOrEmpty(body.Status) &&
            (body.Status == "Ready" || body.Status == "Running") &&
            room.Status != "Running")
        {
            room.Status = body.Status;
        }
        room.UpdatedAt = Store.Now();
        return HttpResponse.Json(new { ok = true, room });
    }

    private sealed class StatusRequest
    {
        public string? Token { get; set; }
        public string? Status { get; set; }
    }

    private static HttpResponse UpdateStatus(HttpRequest req, Room room)
    {
        StatusRequest? body = req.Json<StatusRequest>();
        if (body == null)
        {
            return HttpResponse.Error("请求内容不是合法 JSON");
        }
        if (!RoomTokenOk(room, body.Token, req))
        {
            return HttpResponse.Error("房间令牌无效", 401);
        }
        if (body.Status != "Ready" && body.Status != "Running")
        {
            return HttpResponse.Error("状态只能是 Ready 或 Running");
        }
        room.Status = body.Status;
        room.UpdatedAt = Store.Now();
        Log.Info($"房间状态更新：{room.Name} -> {room.Status}");
        return HttpResponse.Json(new { ok = true, room });
    }

    private static bool RoomTokenOk(Room room, string? bodyToken, HttpRequest req)
    {
        string token = bodyToken ?? "";
        if (string.IsNullOrEmpty(token))
        {
            token = req.GetHeader("X-Room-Token");
        }
        if (string.IsNullOrEmpty(token))
        {
            token = req.GetQuery("token");
        }
        if (string.IsNullOrEmpty(room.Token))
        {
            return true;
        }
        return string.Equals(token, room.Token, StringComparison.Ordinal);
    }

    private static HttpResponse DeleteRoom(HttpRequest req, Room room)
    {
        if (!RoomTokenOk(room, null, req))
        {
            return HttpResponse.Error("房间令牌无效", 401);
        }
        Store.RemoveRoom(room.Id);
        Log.Info($"房间下线：{room.Name} / 主机 {room.HostName} / id={room.Id}");
        return HttpResponse.Json(new { ok = true });
    }

    private sealed class RegisterPlayerRequest
    {
        public string? PlayerId { get; set; }
        public string? Name { get; set; }
        public string? Version { get; set; }
    }

    private static HttpResponse RegisterPlayer(HttpRequest req)
    {
        RegisterPlayerRequest? body = req.Json<RegisterPlayerRequest>();
        if (body == null)
        {
            return HttpResponse.Error("请求内容不是合法 JSON");
        }
        string? error = null;
        Player? player = Store.RegisterPlayer(body.PlayerId ?? "", body.Name ?? "", out error);
        if (player == null)
        {
            return HttpResponse.Error(error ?? "注册失败");
        }
        Log.Info($"玩家注册/更新：{player.Name} ({player.PlayerId}) 第 {player.VisitCount} 次登录");
        return HttpResponse.Json(new
        {
            ok = true,
            relay = Relay.Enabled,
            relayHost = Relay.Host,
            player = new
            {
                playerId = player.PlayerId,
                name = player.Name,
                token = player.Token,
                createdAt = player.CreatedAt,
                lastSeenAt = player.LastSeenAt,
                visitCount = player.VisitCount
            }
        });
    }

    private sealed class RenamePlayerRequest
    {
        public string? PlayerId { get; set; }
        public string? Token { get; set; }
        public string? Name { get; set; }
    }

    private static HttpResponse RenamePlayer(HttpRequest req)
    {
        RenamePlayerRequest? body = req.Json<RenamePlayerRequest>();
        if (body == null)
        {
            return HttpResponse.Error("请求内容不是合法 JSON");
        }
        Player? player = Store.FindPlayer(body.PlayerId ?? "");
        if (player == null)
        {
            return HttpResponse.Error("账号不存在", 404);
        }
        if (!Store.ValidateToken(player, body.Token ?? ""))
        {
            return HttpResponse.Error("玩家令牌无效", 401);
        }
        string? error = null;
        string name = (body.Name ?? "").Trim();
        if (!Store.ValidateName(name, out error))
        {
            return HttpResponse.Error(error ?? "名字不合法");
        }
        if (Store.Players.Any(p => !string.Equals(p.PlayerId, player.PlayerId, StringComparison.OrdinalIgnoreCase) &&
                                   string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return HttpResponse.Error("该名字已被其他玩家使用", 409);
        }
        player.Name = name;
        player.LastSeenAt = Store.Now();
        return HttpResponse.Json(new { ok = true, player = new { playerId = player.PlayerId, name = player.Name } });
    }
}

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        ServerConfig cfg = ServerConfig.LoadOrCreate();
        Store.Init(cfg);
        Relay.Init(cfg);
        Application.Run(new MainForm(cfg));
    }
}
