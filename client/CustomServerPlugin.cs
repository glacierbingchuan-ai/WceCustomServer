using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Logging;
using Cysharp.Threading.Tasks;
using HarmonyLib;
using Mirror;
using Newtonsoft.Json;
using TMPro;
using Unity.Sync.Relay.Lobby;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using kcp2k;

namespace WceCustomServer
{
    [BepInPlugin("wce.customserver", "WCE Custom Server", "1.0.0")]
    public class CustomServerPlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            CSE.Init(Logger);
            try
            {
                Harmony harmony = new Harmony("wce.customserver.patches");
                harmony.PatchAll(typeof(CustomServerPlugin).Assembly);
                Logger.LogInfo("Harmony 补丁已应用");
            }
            catch (Exception ex)
            {
                Logger.LogError("Harmony 补丁应用失败：" + ex);
            }
        }
    }

    internal sealed class PluginConfig
    {
        public string ApiUrl = "http://127.0.0.1:8080";
        public int GamePort = 6565;
        public string PublicHost = "";
        public bool AutoSelect = true;
        public string RoomName = "";
        public string PlayerName = "";

        public static string FilePath => Path.Combine(Paths.GameRootPath, "CustomServer.cfg");

        public static PluginConfig Load()
        {
            PluginConfig cfg = new PluginConfig();
            try
            {
                if (!File.Exists(FilePath))
                {
                    File.WriteAllText(FilePath,
                        "; 我带过的最差一届 自定义服务器配置\n" +
                        "; 修改后重新启动游戏生效\n" +
                        "[Server]\n" +
                        "ApiUrl=http://127.0.0.1:8080\n" +
                        "GamePort=6565\n" +
                        "; 房主对外地址（公网 IP 或域名）。留空会自动获取本机公网 IP；局域网可留空\n" +
                        "PublicHost=\n" +
                        "; 是否默认选中“自定义服务器”\n" +
                        "AutoSelect=true\n" +
                        "[Room]\n" +
                        "; 建房时显示的房间名，留空则使用 玩家名+的房间\n" +
                        "RoomName=\n" +
                        "[Player]\n" +
                        "; 默认玩家名（首次启动弹窗会预填），留空则使用上次保存的名字\n" +
                        "PlayerName=\n", Encoding.UTF8);
                }

                foreach (string raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#") || line.StartsWith("["))
                    {
                        continue;
                    }
                    int idx = line.IndexOf('=');
                    if (idx <= 0)
                    {
                        continue;
                    }
                    string key = line.Substring(0, idx).Trim();
                    string value = line.Substring(idx + 1).Trim();
                    switch (key.ToLowerInvariant())
                    {
                        case "apiurl":
                            if (value.Length > 0) cfg.ApiUrl = value;
                            break;
                        case "gameport":
                            int p;
                            if (int.TryParse(value, out p) && p > 1024 && p < 65536) cfg.GamePort = p;
                            break;
                        case "publichost":
                            cfg.PublicHost = value;
                            break;
                        case "autoselect":
                            cfg.AutoSelect = !value.Equals("false", StringComparison.OrdinalIgnoreCase) && value != "0";
                            break;
                        case "roomname":
                            cfg.RoomName = value;
                            break;
                        case "playername":
                            cfg.PlayerName = value;
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                if (CustomServerPlugin.Log != null)
                {
                    CustomServerPlugin.Log.LogWarning("读取 CustomServer.cfg 失败：" + ex.Message);
                }
            }
            cfg.ApiUrl = cfg.ApiUrl.TrimEnd('/');
            return cfg;
        }
    }

    [Serializable]
    internal sealed class ProfileData
    {
        public string apiUrl;
        public string playerId;
        public string name;
        public string token;
    }

    [Serializable]
    internal sealed class RegisterRequest
    {
        public string playerId;
        public string name;
    }

    [Serializable]
    internal sealed class CreateRoomRequest
    {
        public string name;
        public string hostName;
        public string hostPlayerId;
        public string token;
        public string address;
        public int port;
        public int maxPlayers;
        public bool hidden;
    }

    [Serializable]
    internal sealed class HeartbeatRequest
    {
        public string token;
        public int playerCount;
        public string status;
    }

    [Serializable]
    internal sealed class StatusRequest
    {
        public string token;
        public string status;
    }

    [Serializable]
    internal sealed class PlayerDto
    {
        [JsonProperty("playerId")] public string playerId;
        [JsonProperty("name")] public string name;
        [JsonProperty("token")] public string token;
    }

    [Serializable]
    internal sealed class RoomDto
    {
        [JsonProperty("id")] public string id;
        [JsonProperty("name")] public string name;
        [JsonProperty("hostName")] public string hostName;
        [JsonProperty("address")] public string address;
        [JsonProperty("port")] public int port;
        [JsonProperty("joinCode")] public string joinCode;
        [JsonProperty("playerCount")] public int playerCount;
        [JsonProperty("maxPlayers")] public int maxPlayers;
        [JsonProperty("status")] public string status;
        [JsonProperty("hidden")] public bool hidden;
        [JsonProperty("relayed")] public bool relayed;
        [JsonProperty("updatedAt")] public long updatedAt;
    }

    [Serializable]
    internal sealed class ApiResult
    {
        [JsonProperty("ok")] public bool ok;
        [JsonProperty("error")] public string error;
        [JsonProperty("relay")] public bool relay;
        [JsonProperty("relayHost")] public string relayHost;
        [JsonProperty("player")] public PlayerDto player;
        [JsonProperty("room")] public RoomDto room;
    }

    [Serializable]
    internal sealed class RoomsResult
    {
        [JsonProperty("ok")] public bool ok;
        [JsonProperty("error")] public string error;
        [JsonProperty("rooms")] public RoomDto[] rooms;
    }

    internal static class CSE
    {
        public static PluginConfig Config = new PluginConfig();
        public static bool CustomSelected;
        public static bool HealthOk;
        public static int ApiPingMs = -1;
        public static string LastError = "";
        public static string PlayerName = "";
        public static string Token = "";
        public static string RoomId = "";
        public static string RoomJoinCode = "";
        public static bool RoomRunning;
        public static bool DialogVisible;
        public static string DialogName = "";
        public static string DialogServerUrl = "";
        public static string DialogError = "";
        public static bool DialogBusy;
        public static int LastHttpCode = -1;
        public static string ToastText = "";
        public static float ToastUntil;
        // 客户端加入时使用的目标端口（中继端口），0 表示用 GamePort
        public static int PendingClientPort;
        public static bool IsHosting;
        // 服务器是否开启 UDP 中继；关闭时建房需要玩家手动填写外网地址
        public static bool ServerRelayEnabled = true;
        public static bool AddressDialogVisible;
        public static bool AddressDialogDone;
        public static bool AddressAccepted;
        public static string AddressInput = "";
        public static string AddressError = "";
        private static ManualLogSource _log;
        private static ProfileData _profile;

        public static string PlayerId
        {
            get
            {
                try
                {
                    return PlayerIdentityManager.PlayerID;
                }
                catch
                {
                    return "";
                }
            }
        }

        public static string ProfilePath => Path.Combine(Application.persistentDataPath, "WceCustomServerProfile.json");

        public static void Init(ManualLogSource log)
        {
            _log = log;
            Config = PluginConfig.Load();
            LoadProfile();
            PlayerName = Config.PlayerName.Length > 0 ? Config.PlayerName : (_profile != null ? _profile.name : "");
            DialogName = PlayerName.Length > 0 ? PlayerName : DefaultName();
            DialogServerUrl = Config.ApiUrl;
            _log.LogInfo("自定义服务器 API：" + Config.ApiUrl + "，玩家名：" + PlayerName);
        }

        public static string ApiBase => Config.ApiUrl;

        public static string DefaultName()
        {
            string id = PlayerId;
            string suffix = id.Length >= 4 ? id.Substring(id.Length - 4) : "0000";
            return "玩家" + suffix.ToUpperInvariant();
        }

        public static string RoomDisplayName
        {
            get
            {
                if (Config.RoomName.Length > 0)
                {
                    return Config.RoomName;
                }
                return DisplayName + "的房间";
            }
        }

        public static string DisplayName => PlayerName.Length > 0 ? PlayerName : DefaultName();

        public static string HostAddress
        {
            get
            {
                if (Config.PublicHost.Length > 0)
                {
                    return Config.PublicHost;
                }
                try
                {
                    return GameUtility.GetLocalIPv4();
                }
                catch
                {
                    return "127.0.0.1";
                }
            }
        }

        public static void ShowToast(string text)
        {
            ToastText = text;
            ToastUntil = Time.realtimeSinceStartup + 3f;
        }

        private static void LoadProfile()
        {
            try
            {
                if (File.Exists(ProfilePath))
                {
                    _profile = JsonUtility.FromJson<ProfileData>(File.ReadAllText(ProfilePath, Encoding.UTF8));
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning("读取玩家档案失败：" + ex.Message);
            }
            if (_profile != null && _profile.apiUrl != Config.ApiUrl)
            {
                _profile.token = "";
                _profile.playerId = "";
            }
            if (_profile != null && _profile.token != null && _profile.token.Length > 0)
            {
                Token = _profile.token;
            }
            if (_profile != null && _profile.name != null && _profile.name.Length > 0 && Config.PlayerName.Length == 0)
            {
                PlayerName = _profile.name;
            }
        }

        private static void SaveProfile()
        {
            try
            {
                _profile = new ProfileData
                {
                    apiUrl = Config.ApiUrl,
                    playerId = PlayerId,
                    name = PlayerName,
                    token = Token
                };
                File.WriteAllText(ProfilePath, JsonUtility.ToJson(_profile, true), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                _log.LogWarning("保存玩家档案失败：" + ex.Message);
            }
        }

        public static IEnumerator StartupFlowCoroutine()
        {
            _log.LogInfo("登录界面已打开，显示服务器配置弹窗");
            DialogServerUrl = Config.ApiUrl;
            if (DialogName.Length == 0)
            {
                DialogName = PlayerName.Length > 0 ? PlayerName : DefaultName();
            }
            DialogError = "";
            DialogBusy = false;
            DialogVisible = true;
            yield break;
        }

        public static void SaveConfig()
        {
            try
            {
                string text =
                    "; 我带过的最差一届 自定义服务器配置\n" +
                    "; 修改后重新启动游戏生效\n" +
                    "\n[Server]\n" +
                    "ApiUrl=" + Config.ApiUrl + "\n" +
                    "GamePort=" + Config.GamePort + "\n" +
                    "; 服务器关闭 UDP 中继时使用：房主对外地址（公网 IP 或域名）\n" +
                    "; 开启中继（默认）时由服务器自动分配，无需填写\n" +
                    "PublicHost=" + Config.PublicHost + "\n" +
                    "AutoSelect=" + (Config.AutoSelect ? "true" : "false") + "\n" +
                    "\n[Room]\n" +
                    "RoomName=" + Config.RoomName + "\n" +
                    "\n[Player]\n" +
                    "PlayerName=" + PlayerName + "\n";
                File.WriteAllText(PluginConfig.FilePath, text, Encoding.UTF8);
                _log.LogInfo("已保存服务器配置：" + Config.ApiUrl);
            }
            catch (Exception ex)
            {
                _log.LogWarning("保存 CustomServer.cfg 失败：" + ex.Message);
            }
        }

        public static void ConfirmDialog()
        {
            if (DialogBusy)
            {
                return;
            }
            string name = (DialogName ?? "").Trim();
            if (name.Length == 0)
            {
                DialogError = "名字不能为空";
                return;
            }
            if (name.Length > 16)
            {
                DialogError = "名字最多 16 个字符";
                return;
            }
            foreach (char c in name)
            {
                if (char.IsControl(c))
                {
                    DialogError = "名字包含非法字符";
                    return;
                }
            }
            string url = (DialogServerUrl ?? "").Trim();
            if (url.Length == 0)
            {
                DialogError = "请填写服务器地址";
                return;
            }
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                url = "http://" + url;
            }
            url = url.TrimEnd('/');
            DialogError = "";
            DialogBusy = true;
            ConfirmDialogAsync(name, url).Forget();
        }

        private static async UniTaskVoid ConfirmDialogAsync(string name, string url)
        {
            try
            {
                if (!string.Equals(Config.ApiUrl, url, StringComparison.Ordinal))
                {
                    Config.ApiUrl = url;
                    SaveConfig();
                }
                HealthOk = false;
                string health = await RequestAsync("GET", "/api/health", null);
                if (health == null || LastHttpCode < 200 || LastHttpCode >= 300)
                {
                    DialogError = "无法连接服务器 " + Config.ApiUrl + "（" + LastError + "）";
                    _log.LogWarning(DialogError);
                    DialogBusy = false;
                    return;
                }
                HealthOk = true;

                ApiResult result = await RegisterAsync(name);
                if (result == null || !result.ok || result.player == null)
                {
                    DialogError = result != null && result.error != null && result.error.Length > 0
                        ? result.error
                        : ("注册失败：" + LastError);
                    _log.LogWarning("注册失败：" + DialogError + "（playerId=" + PlayerId + "，LastError=" + LastError + "）");
                    DialogBusy = false;
                    return;
                }
                PlayerName = result.player.name;
                Token = result.player.token;
                ServerRelayEnabled = result.relay;
                SaveProfile();
                DialogVisible = false;
                DialogBusy = false;
                ShowToast("欢迎，" + PlayerName + "！账号已注册到服务器");
                _log.LogInfo("玩家账号注册成功：" + PlayerName + " / " + PlayerId + " / 服务器中继=" + ServerRelayEnabled);
            }
            catch (Exception ex)
            {
                DialogBusy = false;
                DialogError = "注册异常：" + ex.Message;
                _log.LogError("注册异常：" + ex);
            }
        }

        public static void CancelDialog()
        {
            DialogVisible = false;
            DialogBusy = false;
            if (PlayerName.Length == 0)
            {
                PlayerName = DefaultName();
            }
            ShowToast("已跳过名字设置，本次使用：" + PlayerName);
        }

        /// <summary>服务器未开启中继时，弹出窗口让房主填写外网地址。</summary>
        private static async UniTask<string> AskPublicAddressAsync()
        {
            CSEGuiBehaviour.Ensure();
            AddressInput = "";
            AddressError = "";
            AddressAccepted = false;
            AddressDialogDone = false;
            AddressDialogVisible = true;
            while (!AddressDialogDone)
            {
                await UniTask.Yield();
            }
            return AddressAccepted ? Config.PublicHost.Trim() : "";
        }

        public static void ConfirmAddressDialog()
        {
            string value = (AddressInput ?? "").Trim();
            if (value.Length == 0)
            {
                AddressError = "请填写外网地址";
                return;
            }
            if (value.IndexOf(' ') >= 0)
            {
                AddressError = "地址不能包含空格";
                return;
            }
            if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(7);
            }
            else if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(8);
            }
            value = value.TrimEnd('/');
            Config.PublicHost = value;
            AddressError = "";
            AddressAccepted = true;
            AddressDialogDone = true;
            AddressDialogVisible = false;
            try
            {
                SaveConfig();
            }
            catch (Exception ex)
            {
                _log.LogWarning("保存外网地址失败：" + ex.Message);
            }
        }

        public static void CancelAddressDialog()
        {
            AddressAccepted = false;
            AddressDialogDone = true;
            AddressDialogVisible = false;
        }

        private static async UniTask<ApiResult> RegisterAsync(string name)
        {
            RegisterRequest req = new RegisterRequest
            {
                playerId = PlayerId,
                name = name
            };
            string body = await RequestAsync("POST", "/api/players/register", JsonUtility.ToJson(req));
            if (body == null)
            {
                return null;
            }
            try
            {
                return JsonConvert.DeserializeObject<ApiResult>(body);
            }
            catch (Exception ex)
            {
                LastError = "解析注册响应失败：" + ex.Message;
                _log.LogWarning("注册响应解析失败：" + body);
                return null;
            }
        }

        public static async UniTask EnsureProfileForHostAsync()
        {
            if (Token.Length > 0 || !HealthOk)
            {
                return;
            }
            string name = PlayerName.Length > 0 ? PlayerName : DefaultName();
            ApiResult result = await RegisterAsync(name);
            if (result != null && result.ok && result.player != null)
            {
                PlayerName = result.player.name;
                Token = result.player.token;
                ServerRelayEnabled = result.relay;
                SaveProfile();
                _log.LogInfo("已自动注册账号：" + PlayerName + " / 服务器中继=" + ServerRelayEnabled);
            }
            else
            {
                _log.LogWarning("自动注册账号失败：" + LastError);
            }
        }

        public static async UniTask<IReadOnlyList<LobbyRoom>> RefreshRoomsUniTask(bool readyOnly)
        {
            List<LobbyRoom> list = new List<LobbyRoom>();
            string body = await RequestAsync("GET", "/api/rooms", null);
            if (body != null)
            {
                RoomsResult result = null;
                try
                {
                    result = JsonConvert.DeserializeObject<RoomsResult>(body);
                }
                catch (Exception ex)
                {
                    LastError = "解析房间列表失败：" + ex.Message;
                }
                if (result != null && result.rooms != null)
                {
                    foreach (RoomDto dto in result.rooms)
                    {
                        if (dto == null || dto.id == null || dto.id.Length == 0)
                        {
                            continue;
                        }
                        if (readyOnly && !string.Equals(dto.status, "Ready", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        if (dto.joinCode != null && dto.joinCode.Length > 0
                            && dto.address != null && dto.address.Length > 0)
                        {
                            AddressByCode[dto.joinCode] = dto.address;
                            PortByCode[dto.joinCode] = dto.port;
                        }
                        list.Add(ToLobbyRoom(dto));
                    }
                }
            }
            else
            {
                HealthOk = false;
            }

            _log.LogInfo("自定义房间列表：" + list.Count + " 个房间（readyOnly=" + readyOnly + "）");

            try
            {
                NetManager nm = MonoSingletonT<NetManager>.Instance;
                Traverse.Create(nm).Field("mRoomList").SetValue(list);
                Traverse.Create(nm).Method("NotifyRoomListUpdated").GetValue();
            }
            catch (Exception ex)
            {
                _log.LogWarning("发布自定义房间列表失败：" + ex.Message);
            }
            return list;
        }

        public static readonly Dictionary<string, string> AddressByCode =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static readonly Dictionary<string, int> PortByCode =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>解析房间码对应的目标端口；0 表示沿用 GamePort。</summary>
        public static int ResolvePort(string joinCode)
        {
            if (string.IsNullOrEmpty(joinCode))
            {
                return 0;
            }
            string code = joinCode.Trim();
            int port;
            if (PortByCode.TryGetValue(code, out port) && port > 0)
            {
                return port;
            }
            string rest = code.StartsWith("LC_", StringComparison.OrdinalIgnoreCase) ? code.Substring(3) : code;
            if (PortByCode.TryGetValue(rest, out port) && port > 0)
            {
                return port;
            }
            return 0;
        }

        public static string ResolveAddress(string joinCode)
        {
            if (string.IsNullOrEmpty(joinCode))
            {
                return "";
            }
            string code = joinCode.Trim();
            string address;
            if (AddressByCode.TryGetValue(code, out address) && address.Length > 0)
            {
                return address;
            }
            string rest = code.StartsWith("LC_", StringComparison.OrdinalIgnoreCase) ? code.Substring(3) : code;
            if (AddressByCode.TryGetValue(rest, out address) && address.Length > 0)
            {
                return address;
            }
            // 兼容旧格式：LC_1.2.3.4 直接当地址
            if (rest.Contains('.') || rest.Contains(':'))
            {
                return rest;
            }
            return "";
        }

        public static void JoinServer(string address, int port = 0)
        {
            IsHosting = false;
            PendingClientPort = port;
            GameRoot.Inst.NetworkType = ENetworkType.Local;
            GameRoot.Inst.LoadingUI.SafeSetActive(true);
            GameRoot.Inst.GameMode = EGameMode.NewGame;
            MonoSingletonT<NetManager>.Instance.StartClient(address).Forget();
            SingletonT<UIManager>.Instance.CloseUI(EUIWindowType.Setting);
        }

        public static async UniTaskVoid JoinByCodeAsync(string joinCode, UILoginView view)
        {
            ShowToast("正在解析房间码…");
            string body = await RequestAsync("GET", "/api/rooms/by-code/" + Uri.EscapeDataString(joinCode), null);
            if (body == null)
            {
                ShowToast("无法连接服务器，房间码解析失败");
                return;
            }
            RoomDto dto = null;
            try
            {
                ApiResult result = JsonConvert.DeserializeObject<ApiResult>(body);
                dto = result != null ? result.room : null;
            }
            catch (Exception ex)
            {
                LastError = "解析房间码响应失败：" + ex.Message;
            }
            if (dto == null || dto.address == null || dto.address.Length == 0)
            {
                ShowToast("房间码无效或已过期");
                return;
            }
            if (dto.joinCode != null && dto.joinCode.Length > 0)
            {
                AddressByCode[dto.joinCode] = dto.address;
                PortByCode[dto.joinCode] = dto.port;
            }
            JoinServer(dto.address, dto.port);
            try
            {
                GameObject panel = Traverse.Create(view).Field("mRoomListPanel").GetValue<GameObject>();
                if (panel != null)
                {
                    panel.SafeSetActive(false);
                }
            }
            catch
            {
            }
        }

        private static LobbyRoom ToLobbyRoom(RoomDto dto)
        {
            Dictionary<string, string> props = new Dictionary<string, string>
            {
                ["roomName"] = dto.name ?? "",
                ["hostName"] = dto.hostName ?? "",
                ["hostAddress"] = dto.address ?? "",
                ["regionId"] = "custom",
                ["hiddenRoom"] = dto.hidden ? "true" : "false"
            };
            return new LobbyRoom
            {
                RoomUuid = dto.id,
                OwnerId = dto.hostName ?? "",
                Status = string.Equals(dto.status, "Running", StringComparison.OrdinalIgnoreCase)
                    ? LobbyRoomStatus.Running
                    : LobbyRoomStatus.Ready,
                Name = dto.name ?? "",
                JoinCode = dto.joinCode != null && dto.joinCode.Length > 0
                    ? dto.joinCode
                    : "LC_" + (dto.address ?? ""),
                MaxPlayers = (uint)Mathf.Max(1, dto.maxPlayers),
                Visibility = LobbyRoomVisibility.Public,
                PlayerCount = (uint)Mathf.Max(0, dto.playerCount),
                RoomCode = "",
                CustomProperties = props
            };
        }

        public static async UniTaskVoid StartRoomRegistrationAsync()
        {
            await EnsureProfileForHostAsync();
            if (!HealthOk || Token.Length == 0)
            {
                _log.LogWarning("未注册玩家账号，房间不会出现在服务器列表");
                return;
            }
            if (RoomId.Length > 0)
            {
                return;
            }

            // 自定义服务器统一发布为公开房间，保证其他玩家能在房间列表中看到
            bool hidden = false;

            // 服务器开启中继时地址由服务器分配；关闭中继时必须让玩家手动填写外网地址
            string address = Config.PublicHost.Trim();
            if (!ServerRelayEnabled && address.Length == 0)
            {
                address = await AskPublicAddressAsync();
                if (address.Length == 0)
                {
                    ShowToast("已取消发布房间：未填写外网地址");
                    _log.LogWarning("服务器未开启中继，玩家取消填写外网地址，房间未发布");
                    return;
                }
            }

            CreateRoomRequest req = new CreateRoomRequest
            {
                name = RoomDisplayName,
                hostName = DisplayName,
                hostPlayerId = PlayerId,
                token = Token,
                address = address,
                port = Config.GamePort,
                maxPlayers = 6,
                hidden = hidden
            };
            string body = await RequestAsync("POST", "/api/rooms", JsonUtility.ToJson(req));
            if (body == null)
            {
                ShowToast("房间发布失败：无法连接服务器");
                return;
            }

            ApiResult result = null;
            try
            {
                result = JsonConvert.DeserializeObject<ApiResult>(body);
            }
            catch (Exception ex)
            {
                LastError = "解析建房响应失败：" + ex.Message;
                _log.LogWarning("建房响应解析失败：" + body);
            }
            if (result == null || !result.ok || result.room == null)
            {
                string error = result != null && result.error != null && result.error.Length > 0 ? result.error : LastError;
                _log.LogWarning("房间发布失败：" + error);
                ShowToast("房间发布失败：" + error);
                return;
            }

            RoomId = result.room.id;
            RoomJoinCode = result.room.joinCode;
            RoomRunning = false;
            _log.LogInfo("房间已发布：" + result.room.name + " / " + RoomJoinCode + " / id=" + RoomId);
            if (result.room.relayed)
            {
                RelayProxy.Start(result.room.address, result.room.port, RoomId, Config.GamePort);
                _log.LogInfo("房间走服务器中继：" + result.room.address + ":" + result.room.port +
                             "，本地 KCP 端口 " + Config.GamePort);
            }
            ShowToast("房间已发布，加入码：" + RoomJoinCode);
            HeartbeatLoopAsync(RoomId).Forget();
        }

        private static async UniTaskVoid HeartbeatLoopAsync(string roomId)
        {
            while (RoomId == roomId && roomId.Length > 0)
            {
                await UniTask.Delay(TimeSpan.FromSeconds(5), true);
                if (RoomId != roomId)
                {
                    break;
                }
                int players = 1;
                try
                {
                    Room room = MonoSingletonT<RoomManager>.Instance.CurrentRoom;
                    if (room != null)
                    {
                        players = room.PlayerCount;
                    }
                }
                catch
                {
                }

                HeartbeatRequest req = new HeartbeatRequest
                {
                    token = Token,
                    playerCount = players,
                    status = RoomRunning ? "Running" : "Ready"
                };
                string body = await RequestAsync("POST", "/api/rooms/" + roomId + "/heartbeat", JsonUtility.ToJson(req));
                int code = LastHttpCode;
                if (body == null)
                {
                    HealthOk = false;
                    continue;
                }
                if (code == 404 || code == 410)
                {
                    _log.LogWarning("服务器返回房间不存在（" + code + "），房间已被管理端关闭");
                    KickBackToLoginAsync(roomId).Forget();
                    return;
                }
                HealthOk = true;
            }
        }

        private static async UniTaskVoid KickBackToLoginAsync(string roomId)
        {
            if (RoomId != roomId)
            {
                return;
            }
            RoomId = "";
            RoomJoinCode = "";
            RoomRunning = false;
            RelayProxy.Stop();
            ShowToast("房间已被服务器关闭，已断开联机");
            _log.LogWarning("房间已被服务器关闭，断开房间内所有玩家并返回登录界面");
            try
            {
                NetManager nm = MonoSingletonT<NetManager>.Instance;
                await nm.Clear();
            }
            catch (Exception ex)
            {
                _log.LogWarning("断开联机时出错：" + ex);
            }
            try
            {
                MonoSingletonT<SceneManager>.Instance.ChangeScene(1001);
            }
            catch (Exception ex)
            {
                _log.LogWarning("返回登录界面失败：" + ex);
            }
        }

        public static async UniTaskVoid UpdateRoomStatusAsync(bool started)
        {
            RoomRunning = started;
            if (RoomId.Length == 0 || !HealthOk)
            {
                return;
            }
            StatusRequest req = new StatusRequest
            {
                token = Token,
                status = started ? "Running" : "Ready"
            };
            await RequestAsync("POST", "/api/rooms/" + RoomId + "/status", JsonUtility.ToJson(req));
        }

        public static async UniTaskVoid ShutdownRoomAsync()
        {
            string roomId = RoomId;
            RoomId = "";
            RoomJoinCode = "";
            RoomRunning = false;
            RelayProxy.Stop();
            if (roomId.Length == 0)
            {
                return;
            }
            await RequestAsync("DELETE", "/api/rooms/" + roomId + "?token=" + Uri.EscapeDataString(Token), null);
            _log.LogInfo("房间已从服务器下线：" + roomId);
        }

        private static async UniTask<string> RequestAsync(string method, string path, string json)
        {
            string url = Config.ApiUrl + path;
            Stopwatch sw = Stopwatch.StartNew();
            HttpResult result = await SendHttpAsync(method, url, json, 8000);
            ApiPingMs = (int)sw.ElapsedMilliseconds;

            if (result == null || result.code == 0)
            {
                ApiPingMs = -1;
                LastHttpCode = 0;
                LastError = "连接失败：" + (result != null ? result.error : "未知错误");
                _log.LogWarning("[HTTP] " + method + " " + path + " 失败：" + LastError);
                return null;
            }

            LastHttpCode = result.code;
            if (result.code >= 400)
            {
                LastError = "HTTP " + result.code;
                _log.LogWarning("[HTTP] " + method + " " + path + " 失败：code=" + result.code
                    + " 响应=" + (result.body != null ? result.body.Substring(0, Math.Min(300, result.body.Length)) : "(空)"));
            }
            else
            {
                _log.LogInfo("[HTTP] " + method + " " + path + " 成功：" + ApiPingMs + "ms");
            }
            return result.body;
        }

        /// <summary>
        /// 用 .NET 的 HttpWebRequest 发送请求，绕开 UnityWebRequest 只允许 HTTPS 的限制。
        /// </summary>
        private static async UniTask<HttpResult> SendHttpAsync(string method, string url, string json, int timeoutMs)
        {
            Task<HttpResult> task = Task.Run(() => SendHttpBlocking(method, url, json, timeoutMs));
            while (!task.IsCompleted)
            {
                await UniTask.Yield();
            }
            if (task.IsFaulted)
            {
                return new HttpResult { code = 0, error = task.Exception != null ? task.Exception.Message : "请求异常" };
            }
            return task.Result;
        }

        private static HttpResult SendHttpBlocking(string method, string url, string json, int timeoutMs)
        {
            HttpResult result = new HttpResult();
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = method;
                request.Timeout = timeoutMs;
                request.ReadWriteTimeout = timeoutMs;
                request.KeepAlive = false;
                request.Proxy = null;
                if (json != null)
                {
                    byte[] payload = Encoding.UTF8.GetBytes(json);
                    request.ContentType = "application/json";
                    request.ContentLength = payload.Length;
                    using (Stream stream = request.GetRequestStream())
                    {
                        stream.Write(payload, 0, payload.Length);
                    }
                }
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    result.code = (int)response.StatusCode;
                    result.body = ReadBody(response);
                }
            }
            catch (WebException ex)
            {
                if (ex.Response is HttpWebResponse errorResponse)
                {
                    using (errorResponse)
                    {
                        result.code = (int)errorResponse.StatusCode;
                        result.body = ReadBody(errorResponse);
                    }
                }
                else
                {
                    result.code = 0;
                    result.error = ex.Message;
                }
            }
            catch (Exception ex)
            {
                result.code = 0;
                result.error = ex.Message;
            }
            return result;
        }

        private static string ReadBody(WebResponse response)
        {
            using (Stream stream = response.GetResponseStream())
            {
                if (stream == null)
                {
                    return "";
                }
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
        }
    }

    internal sealed class HttpResult
    {
        public int code;
        public string body;
        public string error;
    }

    internal sealed class CSEGuiBehaviour : MonoBehaviour
    {
        private static CSEGuiBehaviour _instance;
        private static bool _flowStarted;
        private static Font _uiFont;
        private static Texture2D _dimTex;
        private static Texture2D _panelTex;
        private static Texture2D _fieldTex;
        private static Texture2D _buttonTex;
        private static Texture2D _buttonHotTex;
        private Rect _panelRect = new Rect(0, 0, 580, 340);
        private bool _focusPending = true;

        public static void Ensure()
        {
            if (_instance != null)
            {
                return;
            }
            GameObject go = new GameObject("WceCustomServerUI");
            _instance = go.AddComponent<CSEGuiBehaviour>();
            if (!_flowStarted)
            {
                _flowStarted = true;
                _instance.StartCoroutine(CSE.StartupFlowCoroutine());
            }
        }

        private void Update()
        {
            if (CSE.DialogVisible)
            {
                Cursor.visible = true;
                Cursor.lockState = CursorLockMode.None;
            }
        }

        private static Texture2D MakeTex(Color color)
        {
            Texture2D tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, color);
            tex.Apply();
            tex.hideFlags = HideFlags.HideAndDontSave;
            return tex;
        }

        private static void EnsureAssets()
        {
            if (_uiFont == null)
            {
                _uiFont = Font.CreateDynamicFontFromOSFont("Microsoft YaHei", 16);
                if (_uiFont == null)
                {
                    _uiFont = Font.CreateDynamicFontFromOSFont("Arial", 16);
                }
            }
            if (_panelTex == null)
            {
                _dimTex = MakeTex(new Color(0f, 0f, 0f, 0.45f));
                _panelTex = MakeTex(new Color(0.10f, 0.11f, 0.14f, 1f));
                _fieldTex = MakeTex(new Color(0.17f, 0.18f, 0.22f, 1f));
                _buttonTex = MakeTex(new Color(0.20f, 0.45f, 0.85f, 1f));
                _buttonHotTex = MakeTex(new Color(0.28f, 0.56f, 0.96f, 1f));
            }
        }

        private static GUIStyle LabelStyle(int size, Color color, bool bold)
        {
            GUIStyle style = new GUIStyle(GUI.skin.label);
            if (_uiFont != null)
            {
                style.font = _uiFont;
            }
            style.fontSize = size;
            style.normal.textColor = color;
            style.wordWrap = true;
            style.richText = false;
            style.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
            return style;
        }

        private static GUIStyle FieldStyle()
        {
            GUIStyle style = new GUIStyle(GUI.skin.textField);
            if (_uiFont != null)
            {
                style.font = _uiFont;
            }
            style.fontSize = 15;
            style.alignment = TextAnchor.MiddleLeft;
            style.padding = new RectOffset(10, 10, 6, 6);
            style.normal.background = _fieldTex;
            style.normal.textColor = Color.white;
            style.hover.background = _fieldTex;
            style.hover.textColor = Color.white;
            style.focused.background = _fieldTex;
            style.focused.textColor = Color.white;
            style.active.background = _fieldTex;
            style.active.textColor = Color.white;
            return style;
        }

        private static GUIStyle ButtonStyle(bool primary)
        {
            GUIStyle style = new GUIStyle(GUI.skin.button);
            if (_uiFont != null)
            {
                style.font = _uiFont;
            }
            style.fontSize = 15;
            style.normal.background = primary ? _buttonTex : _fieldTex;
            style.normal.textColor = Color.white;
            style.hover.background = primary ? _buttonHotTex : _fieldTex;
            style.hover.textColor = Color.white;
            style.active.background = primary ? _buttonHotTex : _fieldTex;
            style.active.textColor = Color.white;
            style.focused.background = primary ? _buttonTex : _fieldTex;
            style.focused.textColor = Color.white;
            return style;
        }

        private void OnGUI()
        {
            if (!CSE.DialogVisible && !CSE.AddressDialogVisible && Time.realtimeSinceStartup >= CSE.ToastUntil)
            {
                return;
            }
            GUI.depth = -100;
            GUI.color = Color.white;
            GUI.backgroundColor = Color.white;
            GUI.contentColor = Color.white;
            EnsureAssets();
            if (_uiFont != null)
            {
                GUI.skin.font = _uiFont;
            }

            if (CSE.DialogVisible)
            {
                GUI.color = Color.white;
                GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _dimTex);
                _panelRect.x = (Screen.width - _panelRect.width) / 2f;
                _panelRect.y = (Screen.height - _panelRect.height) / 2f;
                GUI.DrawTexture(_panelRect, _panelTex);

                GUILayout.BeginArea(new Rect(_panelRect.x + 26f, _panelRect.y + 20f, _panelRect.width - 52f, _panelRect.height - 40f));
                GUILayout.Label("自定义服务器", LabelStyle(20, Color.white, true));
                GUILayout.Space(4);
                GUILayout.Label("先填写房间服务器地址，再输入名字。进入游戏时会自动在服务器注册这个账号。",
                    LabelStyle(13, new Color(0.72f, 0.76f, 0.82f), false));
                GUILayout.Space(16);

                GUILayout.Label("服务器地址", LabelStyle(14, Color.white, false));
                GUI.SetNextControlName("wce_url_input");
                string urlNext = GUILayout.TextField(CSE.DialogServerUrl ?? "", 100, FieldStyle(), GUILayout.Height(32));
                if (!CSE.DialogBusy)
                {
                    CSE.DialogServerUrl = urlNext;
                }
                GUILayout.Space(12);

                GUILayout.Label("玩家名字（1-16 个字符）", LabelStyle(14, Color.white, false));
                GUI.SetNextControlName("wce_name_input");
                string nameNext = GUILayout.TextField(CSE.DialogName ?? "", 16, FieldStyle(), GUILayout.Height(32));
                if (!CSE.DialogBusy)
                {
                    CSE.DialogName = nameNext;
                }

                if (_focusPending)
                {
                    GUI.FocusControl("wce_name_input");
                    _focusPending = false;
                }

                GUILayout.Space(12);
                if (CSE.DialogError.Length > 0)
                {
                    GUILayout.Label(CSE.DialogError, LabelStyle(14, new Color(0.98f, 0.45f, 0.42f), false));
                }
                else if (CSE.DialogBusy)
                {
                    GUILayout.Label("正在连接服务器并注册账号...", LabelStyle(14, new Color(0.65f, 0.80f, 1f), false));
                }
                else
                {
                    GUILayout.Label(" ", LabelStyle(14, Color.white, false));
                }

                GUILayout.FlexibleSpace();
                GUILayout.BeginHorizontal();
                GUI.enabled = !CSE.DialogBusy;
                if (GUILayout.Button(CSE.DialogBusy ? "连接中..." : "进入游戏", ButtonStyle(true), GUILayout.Height(38)))
                {
                    CSE.ConfirmDialog();
                }
                GUILayout.Space(10);
                if (GUILayout.Button("以后再说", ButtonStyle(false), GUILayout.Height(38)))
                {
                    CSE.CancelDialog();
                }
                GUI.enabled = true;
                GUILayout.EndHorizontal();
                GUILayout.Space(4);
                GUILayout.EndArea();

                if (!CSE.DialogBusy && Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return)
                {
                    CSE.ConfirmDialog();
                    Event.current.Use();
                }
            }

            if (CSE.AddressDialogVisible)
            {
                GUI.color = Color.white;
                GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _dimTex);
                Rect addrRect = new Rect((Screen.width - 560f) / 2f, (Screen.height - 300f) / 2f, 560f, 300f);
                GUI.DrawTexture(addrRect, _panelTex);

                GUILayout.BeginArea(new Rect(addrRect.x + 26f, addrRect.y + 20f, addrRect.width - 52f, addrRect.height - 40f));
                GUILayout.Label("自定义服务器 · 外网地址", LabelStyle(20, Color.white, true));
                GUILayout.Space(4);
                GUILayout.Label("当前服务器未开启 UDP 中继，其他玩家需要直连你的主机。请填写你的公网 IP 或域名，并在路由器把游戏 UDP 端口映射到本机。",
                    LabelStyle(13, new Color(0.72f, 0.76f, 0.82f), false));
                GUILayout.Space(14);
                GUILayout.Label("外网地址", LabelStyle(14, Color.white, false));
                GUI.SetNextControlName("wce_addr_input");
                CSE.AddressInput = GUILayout.TextField(CSE.AddressInput ?? "", 64, FieldStyle(), GUILayout.Height(32));
                GUILayout.Space(10);
                if (CSE.AddressError.Length > 0)
                {
                    GUILayout.Label(CSE.AddressError, LabelStyle(14, new Color(0.98f, 0.45f, 0.42f), false));
                }
                else
                {
                    GUILayout.Label("例如 1.2.3.4 或 game.example.com", LabelStyle(13, new Color(0.62f, 0.68f, 0.76f), false));
                }

                GUILayout.FlexibleSpace();
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("确定并创建房间", ButtonStyle(true), GUILayout.Height(38)))
                {
                    CSE.ConfirmAddressDialog();
                }
                GUILayout.Space(10);
                if (GUILayout.Button("取消", ButtonStyle(false), GUILayout.Height(38)))
                {
                    CSE.CancelAddressDialog();
                }
                GUILayout.EndHorizontal();
                GUILayout.Space(4);
                GUILayout.EndArea();

                if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return)
                {
                    CSE.ConfirmAddressDialog();
                    Event.current.Use();
                }
            }

            if (Time.realtimeSinceStartup < CSE.ToastUntil && CSE.ToastText.Length > 0)
            {
                GUIStyle style = new GUIStyle(GUI.skin.box);
                if (_uiFont != null)
                {
                    style.font = _uiFont;
                }
                style.alignment = TextAnchor.MiddleCenter;
                style.fontSize = 15;
                style.normal.textColor = Color.white;
                style.padding = new RectOffset(18, 18, 10, 10);
                GUI.color = new Color(1f, 1f, 1f, 0.85f);
                GUI.Box(new Rect(Screen.width / 2f - 280f, Screen.height - 96f, 560f, 44f), CSE.ToastText, style);
                GUI.color = Color.white;
            }
        }

    }

    [HarmonyPatch(typeof(UILoginView), "OnActive")]
    internal static class Patch_UILoginViewOnActive
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            CSEGuiBehaviour.Ensure();
        }
    }

    [HarmonyPatch(typeof(UILoginView), "InitServerDropDown")]
    internal static class Patch_InitServerDropDown
    {
        [HarmonyPostfix]
        private static void Postfix(UILoginView __instance)
        {
            DropdownHelper.EnsureOption(__instance);
        }
    }

    [HarmonyPatch(typeof(UILoginView), "OnSettingChanged")]
    internal static class Patch_OnSettingChanged
    {
        [HarmonyPostfix]
        private static void Postfix(UILoginView __instance, ESettingType type)
        {
            if (type == ESettingType.Language)
            {
                DropdownHelper.EnsureOption(__instance);
            }
        }
    }

    [HarmonyPatch(typeof(UILoginView), "ApplyServerSelection")]
    internal static class Patch_ApplyServerSelection
    {
        [HarmonyPostfix]
        private static void Postfix(int index)
        {
            if (index == 2)
            {
                CSE.CustomSelected = true;
                if (GameRoot.Inst != null)
                {
                    GameRoot.Inst.NetworkType = ENetworkType.Local;
                }
            }
            else if (index == 0 || index == 1)
            {
                CSE.CustomSelected = false;
            }
        }
    }

    [HarmonyPatch(typeof(UILoginView), "GetSelectedServerNetworkType")]
    internal static class Patch_GetSelectedServerNetworkType
    {
        [HarmonyPrefix]
        private static bool Prefix(UILoginView __instance, ref ENetworkType __result)
        {
            Dropdown dd = DropdownHelper.GetDropdown(__instance);
            if (dd != null && dd.value == 2)
            {
                __result = ENetworkType.Local;
                return false;
            }
            return true;
        }
    }

    internal static class DropdownHelper
    {
        public static Dropdown GetDropdown(UILoginView view)
        {
            try
            {
                return Traverse.Create(view).Field("mServerListDorpDown").GetValue<Dropdown>();
            }
            catch
            {
                return null;
            }
        }

        public static void EnsureOption(UILoginView view)
        {
            Dropdown dd = GetDropdown(view);
            if (dd == null)
            {
                return;
            }
            if (dd.options.Count < 3)
            {
                while (dd.options.Count < 3)
                {
                    dd.options.Add(new Dropdown.OptionData("自定义服务器"));
                }
                dd.RefreshShownValue();
            }
            else if (dd.options.Count > 3)
            {
                dd.options.RemoveRange(3, dd.options.Count - 3);
                if (dd.value > 2)
                {
                    dd.SetValueWithoutNotify(0);
                }
                dd.RefreshShownValue();
            }

            if (CSE.Config.AutoSelect)
            {
                dd.SetValueWithoutNotify(2);
                CSE.CustomSelected = true;
                if (GameRoot.Inst != null)
                {
                    GameRoot.Inst.NetworkType = ENetworkType.Local;
                }
            }
            else if (dd.value == 2)
            {
                CSE.CustomSelected = true;
                if (GameRoot.Inst != null)
                {
                    GameRoot.Inst.NetworkType = ENetworkType.Local;
                }
            }
        }
    }

    [HarmonyPatch(typeof(NetManager), "RefreshRoomListAsync")]
    internal static class Patch_RefreshRoomListAsync
    {
        [HarmonyPrefix]
        private static bool Prefix(ref UniTask<IReadOnlyList<LobbyRoom>> __result, bool readyOnly)
        {
            if (!CSE.CustomSelected)
            {
                return true;
            }
            __result = CSE.RefreshRoomsUniTask(readyOnly);
            return false;
        }
    }

    [HarmonyPatch(typeof(UILoginView), "JoinSelectedRoomAsync")]
    internal static class Patch_JoinSelectedRoomAsync
    {
        [HarmonyPrefix]
        private static bool Prefix(UILoginView __instance, LobbyRoom selectedRoom)
        {
            if (!CSE.CustomSelected || selectedRoom == null)
            {
                return true;
            }
            string joinCode = selectedRoom.JoinCode ?? "";
            if (!joinCode.StartsWith("LC_", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            string address = CSE.ResolveAddress(joinCode);
            if (address.Length == 0)
            {
                CSE.ShowToast("房间码无效或已过期，请刷新房间列表");
                return false;
            }

            CSE.JoinServer(address, CSE.ResolvePort(joinCode));
            try
            {
                GameObject panel = Traverse.Create(__instance).Field("mRoomListPanel").GetValue<GameObject>();
                if (panel != null)
                {
                    panel.SafeSetActive(false);
                }
            }
            catch
            {
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(UILoginView), "OnJoinServerClick")]
    internal static class Patch_OnJoinServerClick
    {
        [HarmonyPrefix]
        private static bool Prefix(UILoginView __instance)
        {
            if (!CSE.CustomSelected)
            {
                return true;
            }
            string code = "";
            try
            {
                InputField input = Traverse.Create(__instance).Field("mJoinCodeInput").GetValue<InputField>();
                if (input != null && input.text != null)
                {
                    code = input.text.Trim();
                }
            }
            catch
            {
            }
            if (code.Length == 0 || !code.StartsWith("LC_", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            string address = CSE.ResolveAddress(code);
            if (address.Length == 0)
            {
                CSE.JoinByCodeAsync(code, __instance).Forget();
                return false;
            }
            CSE.JoinServer(address, CSE.ResolvePort(code));
            return false;
        }
    }

    [HarmonyPatch(typeof(GameUtility), "GetCustomJoinCode")]
    internal static class Patch_GetCustomJoinCode
    {
        [HarmonyPrefix]
        private static bool Prefix(ref string __result)
        {
            if (!CSE.CustomSelected)
            {
                return true;
            }
            __result = CSE.RoomJoinCode.Length > 0 ? CSE.RoomJoinCode : "";
            return false;
        }
    }

    [HarmonyPatch(typeof(NetManager), "StartHost")]
    internal static class Patch_StartHost
    {
        [HarmonyPrefix]
        private static void Prefix(NetManager __instance)
        {
            if (CSE.CustomSelected)
            {
                CSE.IsHosting = true;
                CSE.PendingClientPort = 0;
                __instance.SetRoomName(CSE.RoomDisplayName);
            }
        }
    }

    [HarmonyPatch(typeof(NetManager), "InitTransportPort")]
    internal static class Patch_InitTransportPort
    {
        [HarmonyPostfix]
        private static void Postfix(Transport __result)
        {
            if (!CSE.CustomSelected)
            {
                return;
            }
            KcpTransport kcp = __result as KcpTransport;
            if (kcp != null)
            {
                // 加入他人房间时连到服务器分配的中继端口；自己开房时仍用本地 GamePort
                int targetPort = !CSE.IsHosting && CSE.PendingClientPort > 0
                    ? CSE.PendingClientPort
                    : CSE.Config.GamePort;
                kcp.port = (ushort)targetPort;
            }
        }
    }

    [HarmonyPatch(typeof(NetManager), "StartLocalHost")]
    internal static class Patch_StartLocalHost
    {
        [HarmonyPostfix]
        private static void Postfix(bool __result)
        {
            if (__result && CSE.CustomSelected)
            {
                CSE.StartRoomRegistrationAsync().Forget();
            }
        }
    }

    [HarmonyPatch(typeof(NetManager), "SetRoomStarted")]
    internal static class Patch_SetRoomStarted
    {
        [HarmonyPostfix]
        private static void Postfix(bool started)
        {
            if (CSE.CustomSelected)
            {
                CSE.UpdateRoomStatusAsync(started).Forget();
            }
        }
    }

    [HarmonyPatch(typeof(NetManager), "Clear")]
    internal static class Patch_Clear
    {
        [HarmonyPrefix]
        private static void Prefix()
        {
            if (CSE.CustomSelected)
            {
                CSE.ShutdownRoomAsync().Forget();
            }
        }
    }

    [HarmonyPatch(typeof(NetManager), "GetRegionLatencyMs")]
    internal static class Patch_GetRegionLatencyMs
    {
        [HarmonyPrefix]
        private static bool Prefix(string regionNameOrId, ref int __result)
        {
            if (CSE.CustomSelected && string.Equals(regionNameOrId, "custom", StringComparison.OrdinalIgnoreCase))
            {
                __result = CSE.ApiPingMs;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(NetManager), "GetRegionDisplayName")]
    internal static class Patch_GetRegionDisplayName
    {
        [HarmonyPrefix]
        private static bool Prefix(string regionId, ref string __result)
        {
            if (CSE.CustomSelected && string.Equals(regionId, "custom", StringComparison.OrdinalIgnoreCase))
            {
                __result = "自定义";
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(NameLabelComponent), "SetName")]
    internal static class Patch_SetName
    {
        [HarmonyPrefix]
        private static bool Prefix(NameLabelComponent __instance, string name, bool sync)
        {
            if (SteamManager.Initialized || !CSE.CustomSelected)
            {
                return true;
            }
            try
            {
                TextMeshPro text = Traverse.Create(__instance).Field("mNameText").GetValue<TextMeshPro>();
                if (text == null || text.text == null)
                {
                    return false;
                }
                if (string.IsNullOrEmpty(name) || (__instance.owner != null && __instance.owner.isLocal))
                {
                    text.text = string.Empty;
                }
                else
                {
                    text.text = name;
                }
                if (!string.IsNullOrEmpty(name))
                {
                    __instance.playerName = name;
                }
                if (sync && __instance.owner != null)
                {
                    if (MonoSingletonT<NetManager>.Instance.IsHost())
                    {
                        SetNameResponse_S2C message = default(SetNameResponse_S2C);
                        message.UID = __instance.owner.UID;
                        message.Name = name;
                        MonoSingletonT<RoomManager>.Instance.BroadcastToAllExceptHost(message);
                    }
                    else if (MonoSingletonT<NetManager>.Instance.IsClientOnly())
                    {
                        SetNameRequest_C2S request = default(SetNameRequest_C2S);
                        request.UID = __instance.owner.UID;
                        request.Name = name;
                        MonoSingletonT<RoomManager>.Instance.SendToServer(request);
                    }
                }
            }
            catch (Exception ex)
            {
                CustomServerPlugin.Log.LogWarning("设置自定义玩家名失败：" + ex.Message);
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(NameLabelComponent), "OnBindObj")]
    internal static class Patch_NameLabelBind
    {
        [HarmonyPostfix]
        private static void Postfix(NameLabelComponent __instance)
        {
            if (SteamManager.Initialized || !CSE.CustomSelected || __instance.owner == null)
            {
                return;
            }
            try
            {
                RoleData data = __instance.owner.GetData<RoleData>();
                if (data == null)
                {
                    return;
                }
                if (__instance.owner.isLocal)
                {
                    data.NetData.Name = CSE.DisplayName;
                    __instance.SetName(CSE.DisplayName, true);
                }
                else if (!string.IsNullOrEmpty(data.NetData.Name))
                {
                    __instance.SetName(data.NetData.Name, false);
                }
            }
            catch (Exception ex)
            {
                CustomServerPlugin.Log.LogWarning("绑定自定义玩家名失败：" + ex.Message);
            }
        }
    }

    /// <summary>
    /// 房主侧的 UDP 中继代理：向服务器中继端口登记本机地址，并把中继过来的客户端报文
    /// 转发给本地 KCP 服务器（127.0.0.1:GamePort），从而不需要在路由器上做端口映射。
    /// </summary>
    internal static class RelayProxy
    {
        private const string RegisterMagic = "WCR1";
        private const int MaxClients = 20;

        private static volatile bool s_running;
        private static Thread s_mainThread;
        private static UdpClient s_udp;
        private static IPEndPoint s_relay;
        private static string s_roomId = "";
        private static int s_gamePort = 6565;
        private static readonly object s_sendLock = new object();
        private static readonly Dictionary<uint, UdpClient> s_locals = new Dictionary<uint, UdpClient>();

        public static bool Running => s_running;

        public static void Start(string relayHost, int relayPort, string roomId, int gamePort)
        {
            Stop();
            if (string.IsNullOrEmpty(relayHost) || relayPort <= 0 || string.IsNullOrEmpty(roomId))
            {
                return;
            }
            IPAddress address = ResolveHost(relayHost);
            if (address == null)
            {
                CustomServerPlugin.Log?.LogWarning("无法解析中继地址：" + relayHost);
                return;
            }
            s_relay = new IPEndPoint(address, relayPort);
            s_roomId = roomId;
            s_gamePort = gamePort;
            s_running = true;
            s_mainThread = new Thread(MainLoop);
            s_mainThread.IsBackground = true;
            s_mainThread.Name = "wce-relay-proxy";
            s_mainThread.Start();
            CustomServerPlugin.Log?.LogInfo("中继代理已启动：" + relayHost + ":" + relayPort);
        }

        public static void Stop()
        {
            if (!s_running && s_udp == null)
            {
                return;
            }
            s_running = false;
            UdpClient udp = s_udp;
            s_udp = null;
            try
            {
                udp?.Close();
            }
            catch
            {
            }
            List<UdpClient> locals;
            lock (s_sendLock)
            {
                locals = new List<UdpClient>(s_locals.Values);
                s_locals.Clear();
            }
            foreach (UdpClient local in locals)
            {
                try
                {
                    local.Close();
                }
                catch
                {
                }
            }
            s_relay = null;
            s_roomId = "";
            s_locals.Clear();
        }

        private static IPAddress ResolveHost(string host)
        {
            try
            {
                IPAddress ip;
                if (IPAddress.TryParse(host, out ip))
                {
                    return ip;
                }
                IPHostEntry entry = Dns.GetHostEntry(host);
                foreach (IPAddress candidate in entry.AddressList)
                {
                    if (candidate.AddressFamily == AddressFamily.InterNetwork)
                    {
                        return candidate;
                    }
                }
                return entry.AddressList.Length > 0 ? entry.AddressList[0] : null;
            }
            catch (Exception ex)
            {
                CustomServerPlugin.Log?.LogWarning("解析中继地址出错：" + ex.Message);
                return null;
            }
        }

        private static void MainLoop()
        {
            UdpClient udp = new UdpClient(0);
            udp.Client.ReceiveTimeout = 500;
            s_udp = udp;
            byte[] register = Encoding.ASCII.GetBytes(RegisterMagic + s_roomId);
            long lastRegisterAt = 0;
            while (s_running)
            {
                long now = NowMs();
                IPEndPoint relay = s_relay;
                if (relay == null)
                {
                    break;
                }
                if (now - lastRegisterAt > 4000)
                {
                    try
                    {
                        udp.Send(register, register.Length, relay);
                    }
                    catch (Exception ex)
                    {
                        CustomServerPlugin.Log?.LogWarning("中继登记失败：" + ex.Message);
                    }
                    lastRegisterAt = now;
                }

                IPEndPoint remote = null;
                byte[] data;
                try
                {
                    data = udp.Receive(ref remote);
                }
                catch (SocketException ex)
                {
                    if (!s_running)
                    {
                        break;
                    }
                    if (ex.SocketErrorCode == SocketError.TimedOut ||
                        ex.SocketErrorCode == SocketError.WouldBlock ||
                        ex.SocketErrorCode == SocketError.ConnectionReset)
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
                    CustomServerPlugin.Log?.LogWarning("中继接收异常：" + ex.Message);
                    continue;
                }

                if (data.Length < 5)
                {
                    continue;
                }
                uint token = BitConverter.ToUInt32(data, 0);
                byte[] payload = new byte[data.Length - 4];
                Array.Copy(data, 4, payload, 0, payload.Length);
                UdpClient local = GetLocal(token);
                try
                {
                    local.Send(payload, payload.Length, new IPEndPoint(IPAddress.Loopback, s_gamePort));
                }
                catch (Exception ex)
                {
                    CustomServerPlugin.Log?.LogWarning("转发到本地 KCP 失败：" + ex.Message);
                }
            }
            try
            {
                udp.Close();
            }
            catch
            {
            }
            if (s_udp == udp)
            {
                s_udp = null;
            }
        }

        private static long NowMs()
        {
            return (long)(DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond);
        }

        private static UdpClient GetLocal(uint token)
        {
            lock (s_sendLock)
            {
                UdpClient existing;
                if (s_locals.TryGetValue(token, out existing))
                {
                    return existing;
                }
                if (s_locals.Count >= MaxClients)
                {
                    // 超出上限时复用第一个，避免无限增长
                    foreach (KeyValuePair<uint, UdpClient> pair in s_locals)
                    {
                        return pair.Value;
                    }
                }
                UdpClient local = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                local.Client.ReceiveTimeout = 500;
                s_locals[token] = local;
                Thread thread = new Thread(() => LocalLoop(token, local));
                thread.IsBackground = true;
                thread.Name = "wce-relay-local-" + token;
                thread.Start();
                return local;
            }
        }

        private static void LocalLoop(uint token, UdpClient local)
        {
            while (s_running)
            {
                IPEndPoint remote = null;
                byte[] data;
                try
                {
                    data = local.Receive(ref remote);
                }
                catch (SocketException ex)
                {
                    if (!s_running)
                    {
                        break;
                    }
                    if (ex.SocketErrorCode == SocketError.TimedOut ||
                        ex.SocketErrorCode == SocketError.WouldBlock ||
                        ex.SocketErrorCode == SocketError.ConnectionReset)
                    {
                        continue;
                    }
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception)
                {
                    break;
                }
                if (data.Length == 0)
                {
                    continue;
                }
                byte[] outData = new byte[data.Length + 4];
                BitConverter.GetBytes(token).CopyTo(outData, 0);
                Array.Copy(data, 0, outData, 4, data.Length);
                lock (s_sendLock)
                {
                    UdpClient udp = s_udp;
                    IPEndPoint relay = s_relay;
                    if (udp == null || relay == null)
                    {
                        break;
                    }
                    try
                    {
                        udp.Send(outData, outData.Length, relay);
                    }
                    catch (Exception ex)
                    {
                        CustomServerPlugin.Log?.LogWarning("中继回包失败：" + ex.Message);
                        break;
                    }
                }
            }
            try
            {
                local.Close();
            }
            catch
            {
            }
        }
    }
}
