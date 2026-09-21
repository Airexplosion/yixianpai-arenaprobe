# 练习场探针（com.yx.arenaprobe）

**不是成品 mod**，是练习场方案 B（完全本地、不要任何服务器房间）的实机探针：每个热键做一步，把结果写进日志。背景与依据见 `docs/superpowers/notes/2026-09-19-practice-arena-recon.md`。

安全闸：每一步都要求**不在任何房间 / 对局里**（`isInRoom` 或 `needReconnectToRoom` 为真就拒绝），进场只允许从大厅发起；全程不发包，不改任何存档。manifest 标了 `sandboxOnly`，要在管理器里给配置档开启沙箱模式才能启用（冲突栏里有一键开启）。

## 步骤

在**大厅**里（不要在对局、残局、复盘里）依次按：

| 热键 | 做什么 | 要观察的 |
|---|---|---|
| `CTRL+ALT+0` | 把当前状态写进日志（随时可按） | —— |
| `CTRL+ALT+1` | 从大厅进战斗场景（游戏自己的「回放」分支，没有要播的战斗） | **按完会黑屏，这是正常的**：`BattleManager.OnStart()` 一上来就拉上幕布（`SetBlockActive(true)`），要等第一份对局状态或开始演战斗才拉开，而这一步两样都没给。2026-09-19 实机确认：场景已加载、`Player.log` 无异常。直接按 2。只需要留意有没有弹错误框 |
| `CTRL+ALT+2` | 本地造一份对局状态（练习模式、我 + 木人、5 张手牌）喂给 `BattleManager.Refresh`，并装上拖牌的离线静音 | 备战界面、手牌（0.1.0 已确认正常）。**0.3.0 要看的：牌拖到格子里、格子之间互换、拖回手牌，还会不会报「断开」**（炼化 0.2.0 已确认正常） |
| `CTRL+ALT+3` | 把手牌换成另一组 | 有没有发牌动画、牌面对不对 |
| `CTRL+ALT+4` | 用场上摆的牌（没摆就用第一组手牌）打木人（300 血） | 战斗演没演？打完之后停在哪：自己回到备战，还是要点「退出」，还是卡住？ |
| `CTRL+ALT+5` | 回大厅 | 能不能正常回去，回去之后游戏是否一切正常（能正常匹配 / 进单人模式） |

每一步之后按一下 `CTRL+ALT+0`，日志里会多一行状态（带 `moveHooks=n/5` 移牌钩子登记数、`moves=` 触发次数、`refineHook=` / `refines=` 炼化钩子的登记结果与触发次数）。哪一步卡死了就直接关游戏——探针没有改任何持久数据，重开即恢复。

战斗里的异常会被游戏自己的 catch 吞掉（表现为「进了战斗什么都不演」），详情在 `%USERPROFILE%\AppData\LocalLow\` 下游戏目录的 `Player.log` 里。

## 0.2.0 / 0.3.0 加了什么

- **移牌离线静音（0.3.0 重做）**：游戏的 5 处移牌发包都包在 `if (!isRealtimeSpectating)` 里，而同一个标志又是「能不能拖」的闸。0.2.0 在松手事件之后开窗，实机证明对格子不管用（`CardGrid.OnDrop → MoveToGrid` 先于松手事件，能挪动但照样报「断开」）。0.3.0 改成在 `CardPanel` 的 5 个移牌方法上挂前置 / 后置钩子，只在方法的同步段里把闸打开。
- **炼化离线化**：`CardPanel.RefineCardAsync(CardItem, RefineCardResp simulationResp = null)` 是游戏自己留的本地模拟口子，给了 `simulationResp` 就不发包。探针用前置钩子在练习场里把它补上，其余（加修为、炼化牌的 `OnRefined` 效果）全是游戏自己的代码。出了练习场钩子什么也不改。

依据与后续（步进、伤害计数）见侦察笔记的「拖牌 / 炼化 / 步进 / 伤害计数」一节。

## 构建与打包（仓库根目录）

```powershell
dotnet build mods/com.yx.arenaprobe/ArenaProbe.csproj -c Release
tools/yx-patch/bin/Release/net8.0/yx-patch pack mods/com.yx.arenaprobe      # 先 check（有 error 不打），再生成 dist/<id>-<version>.zip
```
