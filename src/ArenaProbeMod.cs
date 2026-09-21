using System;
using System.Collections.Generic;
using System.Globalization;
using Proto;
using Yx.ModSdk;
using Yx.Shared;

namespace YxArenaProbe
{
    /// <summary>
    /// 练习场方案 B（完全本地、不要任何服务器房间）的实机探针。不是成品 mod：每个热键做一步、把结果写进日志，
    /// 用来回答 docs/superpowers/notes/2026-09-19-practice-arena-recon.md 里的未知点。
    ///
    /// 依据（反编译实证）：
    ///   · 不在房间里进 Battle 场景 = BattleManager.OnStart 的「回放」分支，不向服务器要任何东西；
    ///     游戏自己的战绩回放就是 currentBattleResult = …; SceneLoader.LoadScene("Battle")。
    ///   · BattleManager.Refresh(GameStatus) 是 public；延迟观战就是本地造一份 GameStatus（PracticeMode）喂给它。
    ///   · 战斗 = 造一份 BattleResult 输入 + PlayBattle()，引擎在客户端（用户旧练习场 2026-09-10 实机验证过）。
    ///
    /// 安全闸：每一步都要求【不在任何房间里】（isInRoom / needReconnectToRoom 任一为真就拒绝），
    /// 进场只允许从大厅发起。全程不发包。manifest 标了 sandboxOnly。
    /// </summary>
    public sealed class ArenaProbeMod : YxMod
    {
        const string DummyUid = "YxArenaDummy";
        const int CharacterId = 1000001;   // 0 号皮肤每个角色都有（旧练习场的事故记录）
        const int Grids = 8;
        const int ParamCount = 4096;

        static readonly int[] HandA = { 1000004, 1000002, 1000012, 1000010, 1000021 };      // 轻剑 飞刺 剑挡 剑劈 飞牙剑
        static readonly int[] HandB = { 1000024, 1000001, 1000005, 1010004, 1020004 };      // 引气剑 探云 护身灵气 轻剑2 轻剑3

        bool _entered;
        int _muteDepth;
        int _moveHooks;
        int _moves;
        int _refines;
        string _refineHook = "未登记";
        ISubscription _muteTick;

        public override void OnLoad(ModContext ctx)
        {
            ctx.Input.RegisterHotkey("dump", "CTRL+ALT+0", Dump);
            ctx.Input.RegisterHotkey("enter", "CTRL+ALT+1", Enter);
            ctx.Input.RegisterHotkey("status", "CTRL+ALT+2", ApplyStatus);
            ctx.Input.RegisterHotkey("deal", "CTRL+ALT+3", Deal);
            ctx.Input.RegisterHotkey("fight", "CTRL+ALT+4", Fight);
            ctx.Input.RegisterHotkey("leave", "CTRL+ALT+5", Leave);
            HookMove(ctx, "MoveToGrid", 2);
            HookMove(ctx, "MoveToHand", 1);
            HookMove(ctx, "TryUpgradeHandCard", 2);
            HookMove(ctx, "TryFuseHandCard", 3);
            HookMove(ctx, "InsertCard", 1);
            _muteTick = ctx.MainThread.EveryFrame(MuteTick);
            try
            {
                ctx.Hooks.Prefix("CardPanel", "RefineCardAsync", 2, OnRefinePrefix);
                _refineHook = "已登记";
            }
            catch (Exception e)
            {
                _refineHook = "登记失败";
                ctx.Log.Warn("炼化钩子登记失败（炼化在练习场里会发包并报断开）：" + e.Message);
            }
            ctx.Log.Info("探针就绪：CTRL+ALT+0 状态 / 1 进场 / 2 本地对局状态 / 3 换手牌 / 4 开打 / 5 回大厅");
        }

        // ── 小工具 ─────────────────────────────────────────────────────────

        static string N(int value) { return value.ToString(CultureInfo.InvariantCulture); }

        static string B(bool value) { return value ? "true" : "false"; }

        /// <summary>不在房间里才返回 null；否则返回拒绝的原因。</summary>
        static string RoomGuard()
        {
            GameClient client = GameClientUtil.client;
            if (client == null) return "没有 GameClient";
            if (client.isInRoom) return "你在一个房间 / 对局里（isInRoom），探针拒绝工作";
            if (client.needReconnectToRoom) return "有待重连的房间（needReconnectToRoom），探针拒绝工作";
            return null;
        }

        void Step(string name, string outcome)
        {
            Context.Log.Info("[" + name + "] " + outcome);
        }

        void Fail(string name, Exception e)
        {
            Context.Log.Error("[" + name + "] 抛异常", e);
        }

        // ── 移牌离线静音 ────────────────────────────────────────────────────
        // CardPanel 的 5 个移牌方法（MoveToGrid / MoveToHand / TryUpgradeHandCard / TryFuseHandCard / InsertCard）
        // 发包处都包在 if (!BattleManager.Instance.isRealtimeSpectating) 里，且发包判断之前没有任何 await。
        // 同一个标志又是 CardItem「能不能拖」的闸，所以只在这 5 个方法的同步段里打开：前置 → true，后置（async 外壳
        // 返回时，即同步段跑完）→ false。0.2.0 试过「onCardCompleteDrag 之后开窗」，对格子不管用：
        // CardGrid.OnDrop → MoveToGrid 发生在 OnEndDrag / CompleteDrag 之前（实机 2026-09-19：能挪动但照样报断开）。
        // 出了练习场处理器什么也不改。每帧兜底：标志还开着就关掉（原方法同步段抛异常时后置不会跑）。

        static ReadyLayer FindReadyLayer()
        {
            BattlePanel bp = ILRPanelBase.FindILRPanel<BattlePanel>();
            return bp != null ? bp.readyLayer : null;
        }

        void SetMuted(bool muted)
        {
            ReadyLayer layer = FindReadyLayer();
            if (layer == null) return;
            if (layer.isRealtimeSpectating != muted) layer.isRealtimeSpectating = muted;
        }

        bool InArena()
        {
            return _entered && RoomGuard() == null;
        }

        bool OnMovePrefix(HookContext h)
        {
            if (!InArena()) return true;
            _muteDepth++;
            SetMuted(true);
            _moves++;
            return true;
        }

        void OnMovePostfix(HookContext h)
        {
            if (_muteDepth <= 0) return;
            _muteDepth--;
            if (_muteDepth == 0) SetMuted(false);
        }

        void MuteTick()
        {
            if (!_entered) return;
            try
            {
                _muteDepth = 0;
                ReadyLayer layer = FindReadyLayer();
                if (layer != null && layer.isRealtimeSpectating && RoomGuard() == null) layer.isRealtimeSpectating = false;
            }
            catch (Exception e) { Fail("mute-tick", e); }
        }

        void HookMove(ModContext ctx, string method, int paramCount)
        {
            try
            {
                ctx.Hooks.Prefix("CardPanel", method, paramCount, OnMovePrefix);
                ctx.Hooks.Postfix("CardPanel", method, paramCount, OnMovePostfix);
                _moveHooks++;
            }
            catch (Exception e)
            {
                ctx.Log.Warn("移牌钩子 " + method + " 登记失败（这条路在练习场里会发包并报断开）：" + e.Message);
            }
        }

        public override void OnDisable()
        {
            if (_muteTick != null) { _muteTick.Cancel(); _muteTick = null; }
        }

        // ── 炼化离线化 ──────────────────────────────────────────────────────
        // 游戏自己的 CardPanel.RefineCardAsync(CardItem, RefineCardResp simulationResp = null)：
        // 给了 simulationResp 就不发包，直接拿它当服务器的答复走 OnRefineSucceed（加修为、炼化牌的 OnRefined 效果都在里面）。
        // 玩家把牌拖进炼化区时游戏传的是 null；这里在练习场里（且不在任何房间里）把它补成一份本地的成功答复。
        // 出了练习场处理器什么也不改。

        bool OnRefinePrefix(HookContext h)
        {
            if (!_entered || RoomGuard() != null) return true;
            if (h.Args == null || h.Args.Length < 2 || h.Args[1] != null) return true;
            CardItem card = h.Args[0] as CardItem;
            if (card == null) return true;
            var resp = new RefineCardResp();
            resp.result = true;
            resp.targetCard = card.cardInfo;
            h.Args[1] = resp;
            _refines++;
            return true;
        }

        // ── 0：状态 ────────────────────────────────────────────────────────

        void Dump()
        {
            try
            {
                string scene = SceneLoader.currentSceneName;
                string text = "scene=" + scene + " loading=" + B(SceneLoader.isLoading) + " entered=" + B(_entered)
                              + " moveHooks=" + N(_moveHooks) + "/5 moves=" + N(_moves)
                              + " refineHook=" + _refineHook + " refines=" + N(_refines);
                GameClient client = GameClientUtil.client;
                if (client != null) text += " isInRoom=" + B(client.isInRoom) + " needReconnect=" + B(client.needReconnectToRoom);
                BattleManager bm = BattleManager.Instance;
                if (bm == null) { Step("0", text + " bm=null"); return; }
                text += " replaying=" + B(bm.replaying) + " phase=" + N((int)bm.currentScene);
                GameStatus gs = bm.currentGameStatus;
                if (gs == null) { Step("0", text + " gameStatus=null"); return; }
                text += " mode=" + N((int)gs.gameMode) + " round=" + N(gs.round) + " players=" + N(gs.battlePlayerDatas.Count)
                        + " hand=" + N(gs.playerPrivateData.handCards.Count) + " used=" + N(gs.playerPrivateData.usedCards.Count);
                BattleExecuter be = bm.defaultBattleExecuter;
                if (be != null) text += " executing=" + B(be.isExecuting);
                Step("0", text);
            }
            catch (Exception e) { Fail("0", e); }
        }

        // ── 1：从大厅进战斗场景（回放分支，不要房间）────────────────────────

        void Enter()
        {
            try
            {
                string guard = RoomGuard();
                if (guard != null) { Step("1", "拒绝：" + guard); return; }
                if (SceneLoader.isLoading) { Step("1", "拒绝：场景正在加载"); return; }
                if (SceneLoader.currentSceneName != "Lobby") { Step("1", "拒绝：只能从大厅进，当前 " + SceneLoader.currentSceneName); return; }
                // 没有要播的战斗：PlayBattle → Execute(null) 直接 return，场景应当空着立起来。
                BattleManager.currentBattleResult = null;
                _entered = true;
                SceneLoader.LoadScene("Battle");
                Step("1", "已请求加载 Battle 场景（回放分支）。等场景出来后按 CTRL+ALT+0 看状态，再按 2");
            }
            catch (Exception e) { Fail("1", e); }
        }

        // ── 2：本地造一份 GameStatus 喂给 Refresh ─────────────────────────────

        static BattlePlayerData MakePlayer(string uid, string name, int hp)
        {
            var d = new BattlePlayerData();
            d.uid = uid;
            d.username = name;
            d.characterId = CharacterId;
            d.skinNumber = 0;
            d.skinColor = 0;
            d.cardBack = 1;
            d.level = Level.LianQi;
            d.life = 10;
            d.extraMaxHp = hp;
            d.lastRoundData.level = Level.LianQi;
            d.lastRoundData.life = 10;
            d.lastRoundData.extraMaxHp = hp;
            d.lastRoundData.unlockGrids = Grids;
            for (int i = 0; i < Grids; i++) d.lastRoundData.usedCards.Add(0);
            return d;
        }

        void ApplyStatus()
        {
            try
            {
                string guard = RoomGuard();
                if (guard != null) { Step("2", "拒绝：" + guard); return; }
                if (!_entered || SceneLoader.currentSceneName != "Battle") { Step("2", "拒绝：先按 1 进场（当前 " + SceneLoader.currentSceneName + "）"); return; }
                BattleManager bm = BattleManager.Instance;
                if (bm == null) { Step("2", "拒绝：没有 BattleManager"); return; }

                string myUid = GameClientUtil.uid;
                var gs = new GameStatus();
                gs.round = 1;
                gs.timer = 999;
                gs.gameMode = GameMode.PracticeMode;
                BattlePlayerData me = MakePlayer(myUid, "练习场", 60);
                BattlePlayerData dummy = MakePlayer(DummyUid, "木人", 300);
                dummy.isAI = true;
                me.nextOpponent = DummyUid;
                dummy.nextOpponent = myUid;
                gs.battlePlayerDatas.Add(me);
                gs.battlePlayerDatas.Add(dummy);
                BattlePlayerPrivateData priv = gs.playerPrivateData;
                priv.uid = myUid;
                priv.unlockGrids = Grids;
                for (int i = 0; i < HandA.Length; i++) priv.handCards.Add(HandA[i]);
                for (int i = 0; i < Grids; i++) priv.usedCards.Add(0);

                bm.Refresh(gs);
                Step("2", "已调用 Refresh(本地 GameStatus)：PracticeMode、我 + 木人、手牌 " + N(HandA.Length) + " 张。看备战界面出没出来、牌能不能拖");
            }
            catch (Exception e) { Fail("2", e); }
        }

        // ── 3：换一手牌（旧练习场的发牌做法）──────────────────────────────────

        void Deal()
        {
            try
            {
                string guard = RoomGuard();
                if (guard != null) { Step("3", "拒绝：" + guard); return; }
                BattleManager bm = BattleManager.Instance;
                GameStatus gs = bm != null ? bm.currentGameStatus : null;
                if (gs == null) { Step("3", "拒绝：还没有本地对局状态（先按 2）"); return; }
                BattlePlayerPrivateData priv = gs.playerPrivateData;
                priv.handCards.Clear();
                for (int i = 0; i < HandB.Length; i++) priv.handCards.Add(HandB[i]);
                var pd = new PlayerData();
                pd.publicData = gs.GetSelfBattlePlayerData() ?? gs.GetMainPlayerData();
                pd.privateData = priv;
                bm.RefreshMainPlayerInfo(pd);
                Step("3", "已把手牌换成第二组（" + N(HandB.Length) + " 张）并 RefreshMainPlayerInfo");
            }
            catch (Exception e) { Fail("3", e); }
        }

        // ── 4：用场上摆的牌打木人 ─────────────────────────────────────────────

        List<int> ReadBoard()
        {
            var board = new List<int>();
            BattlePanel bp = ILRPanelBase.FindILRPanel<BattlePanel>();
            CardPanel cp = bp != null ? bp.FindILRSubPanel<CardPanel>() : null;
            if (cp == null) return board;
            List<CardGrid> grids = cp.GetCardGrids();
            for (int i = 0; i < grids.Count; i++)
            {
                if (grids[i] == null || !grids[i].unlocked) continue;
                CardItem card = grids[i].GetCard();
                board.Add(card != null && card.cardInfo != null ? card.cardInfo.id : 0);
            }
            return board;
        }

        static void FillBoard(BattlePlayerData d, List<int> board)
        {
            d.lastRoundData.usedCards.Clear();
            for (int i = 0; i < Grids; i++) d.lastRoundData.usedCards.Add(board != null && i < board.Count ? board[i] : 0);
        }

        void Fight()
        {
            try
            {
                string guard = RoomGuard();
                if (guard != null) { Step("4", "拒绝：" + guard); return; }
                BattleManager bm = BattleManager.Instance;
                if (bm == null || !_entered) { Step("4", "拒绝：先按 1 进场"); return; }
                BattleExecuter be = bm.defaultBattleExecuter;
                if (be != null && be.isExecuting) { Step("4", "拒绝：上一场还在演"); return; }

                List<int> board = ReadBoard();
                int placed = 0;
                for (int i = 0; i < board.Count; i++) if (board[i] != 0) placed++;
                if (placed == 0)
                {
                    // 牌拖不上去也要能测战斗：直接用第一组手牌当阵容。
                    board = new List<int>();
                    for (int i = 0; i < HandA.Length; i++) board.Add(HandA[i]);
                }

                string myUid = GameClientUtil.uid;
                var r = new BattleResult();
                r.battleScene = 1;
                r.battleSubScene = 1;
                r.battleTime = 1800;
                r.round = 1;
                r.gameMode = GameMode.PracticeMode;
                r.p1.publicData = MakePlayer(myUid, "练习场", 60);
                r.p1.publicData.level = Level.InvalidLevel;      // InvalidLevel：总血 = extraMaxHp，填多少是多少
                r.p1.publicData.lastRoundData.level = Level.InvalidLevel;
                FillBoard(r.p1.publicData, board);
                r.p2.publicData = MakePlayer(DummyUid, "木人", 300);
                r.p2.publicData.level = Level.InvalidLevel;
                r.p2.publicData.lastRoundData.level = Level.InvalidLevel;
                r.firstPlayerId = myUid;
                r.homePlayerId = myUid;
                r.mainViewId = myUid;
                // 随机数流平时由服务器预生成；有两处取用是裸 Dequeue，空了会抛并被 Execute 吞掉。固定种子，结果可复现。
                uint state = 20260919u;
                for (int i = 0; i < ParamCount; i++)
                {
                    state ^= state << 13;
                    state ^= state >> 17;
                    state ^= state << 5;
                    r.battleParams.Add((int)(state % 100u));
                }

                BattleManager.currentBattleResult = r;
                bm.PlayBattle();
                Step("4", "已 PlayBattle：我方 " + N(placed == 0 ? HandA.Length : placed) + " 张（" + (placed == 0 ? "场上没牌，用第一组手牌" : "读自场上") + "）对木人 300 血");
            }
            catch (Exception e) { Fail("4", e); }
        }

        // ── 5：回大厅 ────────────────────────────────────────────────────────

        void Leave()
        {
            try
            {
                string guard = RoomGuard();
                if (guard != null) { Step("5", "拒绝：" + guard); return; }
                if (!_entered) { Step("5", "拒绝：不是探针带进来的场景，不动它"); return; }
                _entered = false;
                BattleManager.currentBattleResult = null;
                SceneLoader.LoadScene("Lobby");
                Step("5", "已请求回大厅");
            }
            catch (Exception e) { Fail("5", e); }
        }
    }
}
