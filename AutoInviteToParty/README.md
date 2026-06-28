# AutoInviteToParty — 自动邀请组队（DailyRoutines 本地模块）

模仿 [Inviter](https://github.com/Bluefissure/Inviter) 的逻辑：监听聊天频道，当有人发送指定**关键词**（或正则）时，自动向其发送**组队邀请**。

## 原理

Hook 游戏的 `RaptureLogModule.AddMsgSourceEntry`（每条聊天消息回调，含发言者 `contentId` / `worldId`）→ 取出消息文本做匹配 → 通过 `InfoProxyPartyInvite` 发送邀请。与 Inviter 完全一致：

- 普通场景：`InfoProxyPartyInvite.InviteToParty(contentId, name, worldId)`
- 可跨副本邀请的副本内：`InviteToPartyInInstanceByContentId(contentId)`

## 功能

- **触发关键词**：默认**开启正则**，预设 `111|求组队`（即匹配「111」或「求组队」，忽略大小写），可改；也可关掉正则改用普通包含匹配。
- **监听频道**：可勾选 说话 / 呼喊 / 喊话 / 悄悄话 / 小队 / 团队 / 部队 / 新人频道 / LS1-8 / CWLS1-8（**默认仅「喊话」**）。
- **邀请间隔**：两次邀请最小间隔（默认 500ms，防刷）。
- **本地提示**：可选在发送邀请时于本地聊天打印一行提示。
- **自动跳过**：队伍满员（≥8）、在队中但你不是队长、对方已在队内时，均不邀请。
- **运行开关 + 文本指令**：面板顶部「启用自动邀请」勾选，或用指令 `**/pdr autoinvite [on|off|toggle]**` 快速开关（不带参数=切换），无需禁用整个模块。

> 启用本模块后即加载监听；「启用自动邀请」开关 / 指令控制是否真正发邀请。

## 构建

```powershell
dotnet build AutoInviteToParty.sln -c Release
```

产物：`bin\Release\AutoInviteToParty.dll`（已用 .NET SDK 10 + Dalamud.CN.NET.Sdk 15.0.0 构建通过，0 警告 0 错误）。
DR 程序集引用自 `%APPDATA%\XIVLauncherCN\pluginConfigs\DailyRoutines\Dev\`（如安装在别处，改 csproj 的 `DailyRoutinesLibPath`）。

## 安装

在 DailyRoutines 设置中通过「本地模块 / 导入」加载该 DLL，列表里搜 **“自动邀请组队”** 启用。
