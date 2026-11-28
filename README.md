# MpConfigSync（主机配置自动下发 / Host Config Sync）

联机开局时，主机自动将本机全部 **BaseLib 模组配置**推送给所有客机，从根源消除两端本地设置不同引发的 State Divergence（状态分歧）断线。

> **English**: At multiplayer run start (and rejoin), the host automatically pushes all local BaseLib mod config values to every peer in memory — preventing state divergence caused by mismatched local configs without overwriting peers' `.cfg` files.

- **Current Version**: `0.1.4`（rebuilt & verified against Slay the Spire 2 **`v0.114.0`**, `min_game_version: 0.111.0`）
- **Steam Workshop**: [3799210379](https://steamcommunity.com/sharedfiles/filedetails/?id=3799210379)
- **Dependency**: [BaseLib](https://steamcommunity.com/sharedfiles/filedetails/?id=3737335127) `>= 3.4.5` ([GitHub Releases](https://github.com/Alchyr/BaseLib-StS2/releases))

## 特性 / Features

- **开局与重连自动同步**：在新建联机局、读档建房以及共享初始化阶段兜底同步，确保在游戏生成随机遗物与卡池之前就完成配置对齐。
- **仅写入客机内存，不改写本地配置**：会话期间冻结客机 `.cfg` 文件写回，对局结束或断线后自动恢复客机原有设置。
- **分组开关与安全降级**：在 `设置 → Mod 设置 → MpConfigSync` 提供总开关及发送/接收独立开关（均默认开启）；关闭即时生效，启动时为关则需重启游戏后生效；若某方向补丁安装失败，该方向自动停用并回退原版行为，不会误改配置。
- **未安装模组自动跳过**：遇到对端未安装的模组或未知配置键自动忽略，不会崩溃。

## 安装 / Installation

### 方式一：Steam 创意工坊
1. 主机与所有客机均订阅 [MpConfigSync](https://steamcommunity.com/sharedfiles/filedetails/?id=3799210379) 与 [BaseLib](https://steamcommunity.com/sharedfiles/filedetails/?id=3737335127)。
2. 保持 `设置 → Mod 设置 → MpConfigSync` 中总开关开启即可。

### 方式二：手动安装（GitHub Releases）
如果创意工坊审核延迟，可直接从本仓库 [Releases](https://github.com/Twelve-eight/sts2-mpconfigsync/releases) 下载最新压缩包，解压至 `<Slay the Spire 2>/mods/`，得到 `mods/MpConfigSync/{MpConfigSync.dll,MpConfigSync.pck,MpConfigSync.json}`（**请勿**同时启用工坊版与手动本地版，以免双加载冲突）。

## 从源码构建 / Building from source

前置要求：**.NET 9 SDK** 与已安装的《杀戮尖塔 2》。

```bash
dotnet build mod/MpConfigSync.csproj -c Release \
  -p:Sts2Path="<path-to-Slay-the-Spire-2>" \
  -p:CopyToModsFolderOnBuild=false
```

构建产物位于 `mod/.godot/mono/temp/bin/Release/MpConfigSync.{dll,pck}`（配合 `mod/MpConfigSync.json`）。详细设计与验证记录见 [DEVELOP.md](DEVELOP.md) 与 [DEVLOG.md](DEVLOG.md)。
