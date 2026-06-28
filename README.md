# OccultPotNotifier — 新月岛「魔法罐」刷新提醒（DailyRoutines 本地模块）

监控 FF14 **蜃景幻界·新月岛（The Occult Crescent）** 中「幸福的魔法罐」宝兔 FATE
（北 `FateId 1976` / 南 `FateId 1977`，30 分钟一刷）的刷新倒计时：

- **在线同步 + 上报**（默认开启，单一开关）：根据你所在**大区**（陆行鸟 / 莫古力 / 猫小胖 / 豆豆柴）+ 当前常规 FATE，从社区在线追踪器拉取**同实例**其他玩家上报的魔法罐刷新时间，并把你**亲眼观测到**的刷新 / 结束时间一并上报回去——因此**只要场上有任意一个常规 FATE，进副本即可显示倒计时**，无需亲眼看到魔法罐。**关闭此项时，仅用你本地观测到的数据推算。**
- **倒计时显示方式**（可选）：**服务器信息栏** / **悬浮窗** / **不显示**，显示倒计时与方位（北 / 南）。**点击信息栏条目或悬浮窗**即在地图上落下该魔法罐位置的 `<flag>` 标记并打开地图。
- 在刷新 **提前 N 分钟（默认 5，可配置 1–15）** 发送提醒：
  - **TTS 语音播报**（可开关）
  - **游戏内通知**（可开关）
  - **转发到聊天频道**（可开关，默认关闭）：可选 **小队 `/p`** 或 **默语 `/e`**。
  - 消息文案：`魔法罐约 N 分钟后在北/南处刷新`；转发聊天时为 `魔法罐约 N 分钟后在北/南<flag>处刷新`（会先在该位置落旗，`<flag>` 由游戏展开为可点击坐标）。

作者：黑川启太。原理参考 [EurekaTrackerAutoPopper](https://github.com/Infiziert90/EurekaTrackerAutoPopper)
（`PotDtrBar.cs` / `Fates.cs`），开发约定参考
[DailyRoutines.ModulesPublic](https://github.com/Dalamud-DailyRoutines/DailyRoutines.ModulesPublic)。

## 工程结构

```
.
├─ OccultPotNotifier.sln
├─ Directory.Build.props            # <Use_Dalamud_CN>true</Use_Dalamud_CN>
├─ OccultPotNotifier/
│  ├─ OccultPotNotifier.csproj      # SDK = Dalamud.CN.NET.Sdk/15.0.0
│  └─ OccultPotNotifier.cs          # 模块本体
└─ bin/Release/OccultPotNotifier.dll   # 构建产物
```

## 构建（已验证可通过）

本机使用 **.NET SDK 10.0.301** 构建成功（`Dalamud.CN.NET.Sdk` 15.0.0 来自 nuget.org，
DR 程序集引用自 `%APPDATA%\XIVLauncherCN\pluginConfigs\DailyRoutines\Dev\`）：

```powershell
dotnet build OccultPotNotifier.sln -c Release
```

产物：`bin\Release\OccultPotNotifier.dll`（0 警告 0 错误）。

> 若 DR 安装在别处，改 `OccultPotNotifier/OccultPotNotifier.csproj` 中的 `DailyRoutinesLibPath`。

## 安装 / 启用

在 DailyRoutines 设置中通过「本地模块 / 导入模块」加载 `bin\Release\OccultPotNotifier.dll`
（DR 支持从外部 DLL 加载本地模块），随后在模块列表中搜索 **“新月岛 魔法罐刷新提醒”** 启用。

## 验证

1. 进入新月岛 → 按所选方式（信息栏 / 悬浮窗）出现「下个魔法罐 mm:ss (北/南)」。
2. 到点前 N 分钟应触发 TTS / 通知 /（开启时）小队 `<flag>` 消息。
3. 信息栏条目点击应打开魔法罐所在地图并落旗。

## 已知行为

- 北 / 南两罐交替刷新（间隔 30 分钟）。倒计时数据来源（自动合并，本地观测优先）：
  1. **在线同步**：按「大区 + 当前常规 FATE 的 `FateId` 与 `StartTimeEpoch`」做 `SHA256` 指纹
     （与 EurekaTrackerAutoPopper `TrackerHandler` 一致），
     `GET infi.ovh/api/OccultTrackerV3?last_fate=eq.<指纹>` 读取该实例的 `pot_history`。
     取到数据前每 ~5 秒重试、取到后每 ~60 秒刷新；依赖该实例有其他玩家在上报数据。
  2. **本地观测**：你亲眼看到任意一罐刷新时，用其真实刷新时间精确推算下一个（方位为另一侧），覆盖在线值。**关闭在线同步时只用本地观测推算。**
  两者都没有数据时显示「等待刷新」。
- 上报与在线同步**绑定为同一开关**（无单独上报开关）：开启同步时，本地亲眼看到一罐刷新 / 结束会合并进该实例记录的 `pot_history`（`PATCH ?id=eq.<id>`，仅改 `pot_history`，保留他人贡献的 CE / FATE 数据）；该实例尚无记录且你有观测时新建一行。仅上报魔法罐时间 + 大区，使用追踪器内置的公开匿名密钥。
- 开启「转发到聊天频道」后，每次提醒会在你的地图上落下旗标（覆盖当前旗标）以生成 `<flag>` 坐标。
