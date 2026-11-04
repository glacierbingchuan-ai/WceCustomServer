using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WceRoomServer;

public sealed class ServerConfig
{
    public int Port { get; set; } = 8080;
    public string Bind { get; set; } = "0.0.0.0";
    public int RoomTtlSeconds { get; set; } = 30;
    public int MaxRooms { get; set; } = 200;
    public int MaxPlayersPerRoom { get; set; } = 6;
    public string ServerName { get; set; } = "我带过的最差一届 自定义房间服务器";
    public string DataDir { get; set; } = "data";

    /// <summary>是否启用 UDP 中继（免去房主端口映射）。</summary>
    public bool RelayEnabled { get; set; } = true;
    /// <summary>中继 UDP 端口起始值，每个房间占用一个。</summary>
    public int RelayPortStart { get; set; } = 7100;
    public int RelayPortCount { get; set; } = 200;
    /// <summary>中继对外地址（公网 IP/域名），留空自动取本机内网 IPv4。</summary>
    public string RelayHost { get; set; } = "";

    public static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "server-config.json");

    public void Save()
    {
        try
        {
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, Store.JsonOpts), Encoding.UTF8);
            Log.Info("配置已保存到 server-config.json");
        }
        catch (Exception ex)
        {
            Log.Warn("保存 server-config.json 失败：" + ex.Message);
        }
    }

    public static ServerConfig LoadOrCreate()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                string text = File.ReadAllText(ConfigPath, Encoding.UTF8);
                ServerConfig? cfg = JsonSerializer.Deserialize<ServerConfig>(text, Store.JsonOpts);
                if (cfg != null)
                {
                    return cfg;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("读取 server-config.json 失败，使用默认配置：" + ex.Message);
        }

        ServerConfig def = new ServerConfig();
        try
        {
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(def, Store.JsonOpts), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Log.Warn("写入默认 server-config.json 失败：" + ex.Message);
        }
        return def;
    }

    public string ResolveDataDir()
    {
        string dir = DataDir;
        if (!Path.IsPathRooted(dir))
        {
            dir = Path.Combine(AppContext.BaseDirectory, dir);
        }
        Directory.CreateDirectory(dir);
        return dir;
    }
}

public sealed class Player
{
    public string PlayerId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Token { get; set; } = "";
    public long CreatedAt { get; set; }
    public long LastSeenAt { get; set; }
    public int VisitCount { get; set; }
}

public sealed class Room
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string HostName { get; set; } = "";
    public string HostPlayerId { get; set; } = "";
    public string Address { get; set; } = "";
    public int Port { get; set; }
    public string JoinCode { get; set; } = "";
    public int PlayerCount { get; set; } = 1;
    public int MaxPlayers { get; set; } = 6;
    public string Status { get; set; } = "Ready";
    public bool Hidden { get; set; }
    /// <summary>是否通过服务器 UDP 中继连接。</summary>
    public bool Relayed { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }

    [JsonIgnore]
    public string Token { get; set; } = "";
}

public static class Store
{
    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly ConcurrentDictionary<string, Room> s_rooms = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Player> s_players = new(StringComparer.OrdinalIgnoreCase);
    private static ServerConfig s_config = new();

    public static ServerConfig Config => s_config;
    public static IEnumerable<Room> Rooms => s_rooms.Values;
    public static IEnumerable<Player> Players => s_players.Values;

    public static void Init(ServerConfig config)
    {
        s_config = config;
    }

    public static Player? FindPlayer(string playerId)
    {
        return s_players.TryGetValue(playerId, out Player? p) ? p : null;
    }

    public static Player RegisterPlayer(string playerId, string name, out string? error)
    {
        error = null;
        playerId = (playerId ?? "").Trim();
        name = (name ?? "").Trim();
        if (playerId.Length < 4 || playerId.Length > 64 || !playerId.All(Uri.IsHexDigit))
        {
            error = "playerId 必须是 4-64 位十六进制字符串";
            return null!;
        }
        if (!ValidateName(name, out error))
        {
            return null!;
        }

        Player? other = s_players.Values.FirstOrDefault(p =>
            !string.Equals(p.PlayerId, playerId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (other != null)
        {
            error = "该名字已被其他玩家使用";
            return null!;
        }

        long now = Now();
        Player player = s_players.GetOrAdd(playerId, _ => new Player
        {
            PlayerId = playerId,
            Token = Guid.NewGuid().ToString("N"),
            CreatedAt = now
        });
        player.Name = name;
        player.LastSeenAt = now;
        player.VisitCount++;
        return player;
    }

    public static bool ValidateToken(Player player, string token)
    {
        return player != null && !string.IsNullOrEmpty(token) &&
               CryptographicEquals(player.Token, token);
    }

    private static bool CryptographicEquals(string a, string b)
    {
        return string.Equals(a, b, StringComparison.Ordinal);
    }

    public static bool ValidateName(string name, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "名字不能为空";
            return false;
        }
        if (name.Length > 16)
        {
            error = "名字最多 16 个字符";
            return false;
        }
        if (name.Any(char.IsControl))
        {
            error = "名字包含非法字符";
            return false;
        }
        return true;
    }

    public static Room? FindRoom(string id)
    {
        return s_rooms.TryGetValue(id, out Room? r) ? r : null;
    }

    public static Room? FindRoomByCode(string joinCode)
    {
        if (string.IsNullOrWhiteSpace(joinCode))
        {
            return null;
        }
        return s_rooms.Values.FirstOrDefault(r => string.Equals(r.JoinCode, joinCode, StringComparison.OrdinalIgnoreCase));
    }

    private const string JoinCodeChars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private static string NewJoinCode()
    {
        for (int attempt = 0; attempt < 30; attempt++)
        {
            char[] chars = new char[6];
            for (int i = 0; i < chars.Length; i++)
            {
                chars[i] = JoinCodeChars[Random.Shared.Next(JoinCodeChars.Length)];
            }
            string code = "LC_" + new string(chars);
            if (s_rooms.Values.All(r => !string.Equals(r.JoinCode, code, StringComparison.OrdinalIgnoreCase)))
            {
                return code;
            }
        }
        return "LC_" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
    }

    public static Room? FindRoomByPlayer(string playerId)
    {
        return s_rooms.Values.FirstOrDefault(r => string.Equals(r.HostPlayerId, playerId, StringComparison.OrdinalIgnoreCase));
    }

    public static Room CreateRoom(Room room, out string? error)
    {
        error = null;
        if (s_rooms.Count >= s_config.MaxRooms)
        {
            error = "服务器房间数量已达上限";
            return null!;
        }
        if (string.IsNullOrWhiteSpace(room.Address))
        {
            error = "缺少房间主机地址";
            return null!;
        }
        room.Name = string.IsNullOrWhiteSpace(room.Name) ? "未命名房间" : room.Name.Trim();
        if (room.Name.Length > 32)
        {
            room.Name = room.Name[..32];
        }
        room.Id = Guid.NewGuid().ToString("N");
        room.JoinCode = NewJoinCode();
        room.MaxPlayers = Math.Clamp(room.MaxPlayers, 1, s_config.MaxPlayersPerRoom);
        room.PlayerCount = Math.Clamp(room.PlayerCount, 0, room.MaxPlayers);
        room.Status = "Ready";
        room.CreatedAt = Now();
        room.UpdatedAt = room.CreatedAt;
        s_rooms[room.Id] = room;
        return room;
    }

    public static bool RemoveRoom(string id)
    {
        bool removed = s_rooms.TryRemove(id, out _);
        if (removed)
        {
            Relay.FreeRoom(id);
        }
        return removed;
    }

    public static long Now()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    public static void CleanupExpired()
    {
        long ttl = s_config.RoomTtlSeconds * 1000L;
        long now = Now();
        foreach (Room room in s_rooms.Values)
        {
            if (now - room.UpdatedAt > ttl)
            {
                RemoveRoom(room.Id);
                Log.Info($"房间超时下线：{room.Name} ({room.Id})");
            }
        }
    }
}
