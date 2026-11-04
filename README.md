# 我带过的最差一届 · 自定义联机服务器

为 Unity 游戏 **《我带过的最差一届》**（Worst Class Ever Demo）实现的自定义联机方案：

- **服务端**：C# + WinForms 图形化房间服务器，单文件发布，自带 .NET 8 运行时
- **客户端**：BepInEx 5 + Harmony 插件，在游戏登录界面弹出「服务器地址 / 玩家名」窗口，不修改游戏本体

原版游戏依赖 Steam / UOS 的中继服务，本方案自己实现房间目录 + UDP 中继，玩家点一下房间即可加入，**双方都不需要做端口映射**。

## 功能

- 启动游戏即弹出配置窗口：可填写服务器地址与玩家名，确认后向服务器注册
- 登录界面服务器下拉框新增「自定义服务器」并自动选中
- 房间列表由自定义服务器下发，支持创建 / 加入 / 关闭房间
- UDP 中继：房主与玩家都主动连接服务器，由服务器转发 KCP 报文
- 管理面板：房间列表、玩家列表、运行日志、服务端地址复制、可视化设置、关于页
- 关闭房间（或心跳超时）会将房间内所有玩家踢回登录界面
- 玩家名在游戏内正常显示与同步（替换原版依赖 Steam 的取名逻辑）

## 目录结构

```
client/                    客户端插件（BepInEx 插件）
  CustomServerPlugin.cs    全部逻辑：配置、HTTP、房间、KCP 中继代理、Harmony 补丁、弹窗
  CustomServer.cfg         配置模板
  build.ps1                编译脚本
server/                    服务端（WinForms）
  Program.cs               自建 TCP HTTP 服务、路由、API
  Store.cs                 配置与内存数据（房间 / 玩家）
  Relay.cs                 UDP 中继
  MainForm.cs              管理面板
  SettingsForm.cs          可视化设置
  AboutForm.cs             关于窗口
  WceRoomServer.csproj
  build.ps1
docs/                      发布用的使用说明
```

## 环境要求

| 项目 | 要求 |
| --- | --- |
| 服务端构建 | .NET 8 SDK |
| 服务端运行 | Windows 10/11 x64（无需安装运行时） |
| 客户端构建 | .NET 8 SDK（调用 Roslyn csc）+ 游戏本体 + BepInEx 5.4.x |
| 客户端运行 | Windows 游戏本体（Mono x64） |

## 构建

### 服务端

```powershell
cd server
./build.ps1
```

产物在 `server/dist/`，其中 `WceRoomServer.exe` 为自包含单文件。

### 客户端

先把 BepInEx 5.4.23.x（win x64）解压到游戏根目录，然后：

```powershell
cd client
./build.ps1 -GameDir "D:\Games\Worst Class Ever Demo"
```

脚本会引用游戏 `Worst Class Ever_Data/Managed/*.dll` 与 `BepInEx/core/*.dll` 编译，
产物 `CustomServerPlugin.dll` 复制到游戏的 `BepInEx/plugins/` 即完成安装。

脚本可以在 PowerShell 里直接执行；若提示「禁止运行脚本」，改用：

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

## 使用

1. 运行 `WceRoomServer.exe`，记下面板上的服务端地址（如 `http://192.168.1.5:8080`）
2. 把 `CustomServerPlugin.dll` 与 `CustomServer.cfg` 放到游戏的 `BepInEx/plugins/` 与游戏根目录
3. 启动游戏，在弹窗中填写服务器地址与玩家名，点「确定」
4. 房间列表选「自定义服务器」，创建或加入房间即可

详细步骤见 [docs/服务端使用说明.txt](docs/服务端使用说明.txt) 与 [docs/客户端安装说明.txt](docs/客户端安装说明.txt)。

## 网络与端口

| 端口 | 用途 | 谁需要放行 |
| --- | --- | --- |
| TCP 8080 | 房间目录 / 注册 / 心跳 API | 服务器 |
| UDP 7100-7299 | 房间中继（每房间一个端口） | 服务器 |
| UDP 6565 | 仅直连模式下的游戏端口 | 房主（仅 `relayEnabled=false` 时） |

公网部署时把 `server-config.json` 里的 `relayHost` 设为公网 IP 或域名，
客户端拿到的就是该地址，无需再映射房主端口。

## 工作原理

- 游戏使用 **Mirror + kcp2k（KCP over UDP）**，`Local` 模式下房主自己就是游戏服务器
- 中继模式下：房主向服务器的中继端口周期登记，客户端通过 API 拿到「中继地址 + 端口」后连接
- 服务器按「房间端口 + 来源地址」做双向 UDP 转发，不解析 KCP 内容，因此协议透明
- 房间心跳每 5 秒一次，服务器 30 秒无心跳视为掉线；管理端关闭房间后房主收到 404 并 `DisconnectAll`

## API 摘要

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET | `/api/health` | 健康检查，返回是否启用中继 |
| GET | `/api/info` | 服务器信息 |
| GET | `/api/stats` | 统计信息 |
| GET | `/api/rooms` | 房间列表 |
| GET | `/api/rooms/by-code/{code}` | 按加入码查询房间地址 |
| POST | `/api/rooms` | 创建房间 |
| POST | `/api/rooms/{id}/heartbeat` | 心跳 |
| POST | `/api/rooms/{id}/status` | 更新房间状态 |
| DELETE | `/api/rooms/{id}` | 关闭房间 |
| POST | `/api/players/register` | 注册玩家（不落盘，进程内有效） |
| POST | `/api/players/rename` | 改名 |
| GET | `/api/players/{id}` | 查询玩家 |

## 许可

MIT License，见 [LICENSE](LICENSE)。

本内容仅供学习交流，游戏开发殊为不易，若您喜欢，还请支持正版。
