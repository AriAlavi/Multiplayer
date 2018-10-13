﻿using Harmony;
using Multiplayer.Common;
using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml;
using Verse;
using Verse.AI;

namespace Multiplayer.Client
{
    public class MultiplayerWorldComp : WorldComponent, ITickable
    {
        public static bool tickingWorld;
        public static bool executingCmdWorld;

        public float RealTimeToTickThrough { get; set; }

        public float TimePerTick(TimeSpeed speed)
        {
            if (TickRateMultiplier(speed) == 0f)
                return 0f;
            return 1f / TickRateMultiplier(speed);
        }

        private float TickRateMultiplier(TimeSpeed speed)
        {
            switch (speed)
            {
                case TimeSpeed.Paused:
                    return 0f;
                case TimeSpeed.Normal:
                    return 1f;
                case TimeSpeed.Fast:
                    return 3f;
                case TimeSpeed.Superfast:
                    if (Find.TickManager.NothingHappeningInGame())
                        return 12f;
                    return 6f;
                case TimeSpeed.Ultrafast:
                    return 15f;
                default:
                    return -1f;
            }
        }

        public TimeSpeed TimeSpeed
        {
            get => Find.TickManager.CurTimeSpeed;
            set => Find.TickManager.CurTimeSpeed = value;
        }

        /*public TimeSpeed TimeSpeed
        {
            get => timeSpeedInt;
            set => timeSpeedInt = value;
        }*/

        public Queue<ScheduledCommand> Cmds { get => cmds; }

        public Dictionary<int, FactionWorldData> factionData = new Dictionary<int, FactionWorldData>();

        public ConstantTicker ticker = new ConstantTicker();
        public IdBlock globalIdBlock;
        public ulong randState = 2;
        //private TimeSpeed timeSpeedInt;

        public List<MpTradeSession> trading = new List<MpTradeSession>();

        public Queue<ScheduledCommand> cmds = new Queue<ScheduledCommand>();

        public MultiplayerWorldComp(World world) : base(world)
        {
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref TickPatch.timerInt, "timer");

            TimeSpeed timeSpeed = Find.TickManager.CurTimeSpeed;
            Scribe_Values.Look(ref timeSpeed, "timeSpeed");
            Find.TickManager.CurTimeSpeed = timeSpeed;

            ExposeFactionData();

            Multiplayer.ExposeIdBlock(ref globalIdBlock, "globalIdBlock");
        }

        private void ExposeFactionData()
        {
            // The faction whose data is currently set
            int currentFactionId = Faction.OfPlayer.loadID;
            Scribe_Values.Look(ref currentFactionId, "currentFactionId");

            if (Scribe.mode == LoadSaveMode.Saving)
            {
                var factionData = new Dictionary<int, FactionWorldData>(this.factionData);
                factionData.Remove(currentFactionId);

                ScribeUtil.Look(ref factionData, "factionData", LookMode.Deep);
            }
            else
            {
                ScribeUtil.Look(ref factionData, "factionData", LookMode.Deep);
                if (factionData == null)
                    factionData = new Dictionary<int, FactionWorldData>();
            }

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                factionData[currentFactionId] = FactionWorldData.FromCurrent();
            }
        }

        public void Tick()
        {
            tickingWorld = true;
            PreContext();

            try
            {
                Find.TickManager.DoSingleTick();
                TickTrading();
            }
            finally
            {
                PostContext();
                tickingWorld = false;
            }
        }

        public void TickTrading()
        {
            for (int i = trading.Count - 1; i >= 0; i--)
            {
                var session = trading[i];
                if (session.ShouldCancel())
                {
                    RemoveTradeSession(session);
                    continue;
                }

                Pawn negotiator = session.playerNegotiator;
                if (!session.startedWaitJobs && negotiator.Spawned && session.trader is Pawn pawn && pawn.Spawned)
                {
                    negotiator.jobs.StartJob(new Job(JobDefOf.Wait, 10, true) { count = 1234, targetA = pawn }, JobCondition.InterruptForced);
                    pawn.jobs.StartJob(new Job(JobDefOf.Wait, 10, true) { count = 1234, targetA = negotiator }, JobCondition.InterruptForced);

                    session.startedWaitJobs = true;
                }
            }
        }

        public void RemoveTradeSession(MpTradeSession session)
        {
            int index = trading.IndexOf(session);
            trading.Remove(session);
            Find.WindowStack?.WindowOfType<TradingWindow>()?.Notify_RemovedSession(index);
        }

        public void PreContext()
        {
            UniqueIdsPatch.CurrentBlock = globalIdBlock;
            Rand.StateCompressed = randState;
        }

        public void PostContext()
        {
            randState = Rand.StateCompressed;
            UniqueIdsPatch.CurrentBlock = null;
        }

        public void SetFaction(Faction faction)
        {
            if (!factionData.TryGetValue(faction.loadID, out FactionWorldData data))
            {
                if (!Multiplayer.simulating)
                    MpLog.Log("No world faction data for faction {0} {1}", faction.loadID, faction);
                return;
            }

            Game game = Current.Game;
            game.researchManager = data.researchManager;
            game.drugPolicyDatabase = data.drugPolicyDatabase;
            game.outfitDatabase = data.outfitDatabase;
            game.playSettings = data.playSettings;

            SyncResearch.researchSpeed = data.researchSpeed;
        }

        public void ExecuteCmd(ScheduledCommand cmd)
        {
            CommandType cmdType = cmd.type;
            ByteReader data = new ByteReader(cmd.data);

            executingCmdWorld = true;
            TickPatch.currentExecutingCmdIssuedBySelf = cmd.issuedBySelf;

            PreContext();
            FactionContext.Push(cmd.GetFaction());

            try
            {
                if (cmdType == CommandType.Sync)
                {
                    Sync.HandleCmd(data);
                }

                if (cmdType == CommandType.WorldTimeSpeed)
                {
                    TimeSpeed speed = (TimeSpeed)data.ReadByte();
                    Multiplayer.WorldComp.TimeSpeed = speed;

                    MpLog.Log("Set world speed " + speed + " " + TickPatch.Timer + " " + Find.TickManager.TicksGame);
                }

                if (cmdType == CommandType.SetupFaction)
                {
                    HandleSetupFaction(cmd, data);
                }

                if (cmdType == CommandType.FactionOffline)
                {
                    int factionId = data.ReadInt32();
                    Multiplayer.WorldComp.factionData[factionId].online = false;

                    if (Multiplayer.session.myFactionId == factionId)
                        Multiplayer.RealPlayerFaction = Multiplayer.DummyFaction;
                }

                if (cmdType == CommandType.FactionOnline)
                {
                    int factionId = data.ReadInt32();
                    Multiplayer.WorldComp.factionData[factionId].online = true;

                    if (Multiplayer.session.myFactionId == factionId)
                        Multiplayer.RealPlayerFaction = Find.FactionManager.AllFactionsListForReading.Find(f => f.loadID == factionId);
                }

                if (cmdType == CommandType.Autosave)
                {
                    Multiplayer.WorldComp.TimeSpeed = TimeSpeed.Paused;

                    LongEventHandler.QueueLongEvent(() =>
                    {
                        OnMainThread.ClearCaches();

                        XmlDocument doc = Multiplayer.SaveAndReload();
                        //Multiplayer.CacheAndSendGameData(doc);
                    }, "Autosaving", false, null);
                }
            }
            catch (Exception e)
            {
                Log.Error($"World cmd exception ({cmdType}): {e}");
            }
            finally
            {
                FactionContext.Pop();
                PostContext();
                TickPatch.currentExecutingCmdIssuedBySelf = false;
                executingCmdWorld = false;
            }
        }

        private void HandleSetupFaction(ScheduledCommand command, ByteReader data)
        {
            int factionId = data.ReadInt32();
            Faction faction = Find.FactionManager.GetById(factionId);

            if (faction == null)
            {
                faction = new Faction
                {
                    loadID = factionId,
                    def = Multiplayer.factionDef,
                    Name = "Multiplayer faction",
                    centralMelanin = Rand.Value
                };

                Find.FactionManager.Add(faction);

                foreach (Faction current in Find.FactionManager.AllFactionsListForReading)
                {
                    if (current == faction) continue;
                    current.TryMakeInitialRelationsWith(faction);
                }

                Multiplayer.WorldComp.factionData[factionId] = FactionWorldData.New(factionId);

                MpLog.Log("New faction {0}", faction.GetUniqueLoadID());
            }
        }

        public void DirtyTradeForMaps(Map map)
        {
            if (map == null) return;
            foreach (MpTradeSession session in trading.Where(s => s.playerNegotiator.Map == map))
                session.deal.fullRecache = true;
        }

        public void DirtyTradeForThing(Thing t)
        {
            if (t == null) return;
            foreach (MpTradeSession session in trading.Where(s => s.playerNegotiator.Map == t.Map))
                session.deal.recacheThings.Add(t);
        }
    }

    public class MpTradeSession
    {
        public static MpTradeSession current;

        public int sessionId;
        public ITrader trader;
        public Pawn playerNegotiator;
        public bool giftMode;
        public MpTradeDeal deal;
        public bool giftsOnly;

        public bool startedWaitJobs;

        public string Label
        {
            get
            {
                if (trader is Pawn pawn)
                    return pawn.Faction.Name;
                return trader.TraderName;
            }
        }

        public MpTradeSession() { }

        private MpTradeSession(ITrader trader, Pawn playerNegotiator, bool giftMode)
        {
            sessionId = Multiplayer.GlobalIdBlock.NextId();

            this.trader = trader;
            this.playerNegotiator = playerNegotiator;
            this.giftMode = giftMode;
            giftsOnly = giftMode;

            SetTradeSession(this, true);
            deal = new MpTradeDeal(this);
            SetTradeSession(null);
        }

        public static void TryCreate(ITrader trader, Pawn playerNegotiator, bool giftMode)
        {
            if (Multiplayer.WorldComp.trading.Any(s => s.trader == trader))
                return;

            if (Multiplayer.WorldComp.trading.Any(s => s.playerNegotiator == playerNegotiator))
                return;

            Multiplayer.WorldComp.trading.Add(new MpTradeSession(trader, playerNegotiator, giftMode));
        }

        public bool ShouldCancel()
        {
            if (!trader.CanTradeNow)
                return true;

            if (playerNegotiator.Drafted)
                return true;

            if (trader is Pawn pawn && pawn.Spawned && playerNegotiator.Spawned)
                return pawn.Position.DistanceToSquared(playerNegotiator.Position) > 2 * 2;

            return false;
        }

        public void TryExecute()
        {
            SetTradeSession(this);
            deal.TryExecute(out bool traded);
            SetTradeSession(null);

            Multiplayer.WorldComp.RemoveTradeSession(this);
        }

        public void Reset()
        {
            deal.tradeables.ForEach(t => t.countToTransfer = 0);
            deal.uiShouldReset = UIShouldReset.Silent;
        }

        public void ToggleGiftMode()
        {
            giftMode = !giftMode;
            deal.tradeables.ForEach(t => t.countToTransfer = 0);
            deal.uiShouldReset = UIShouldReset.Silent;
        }

        public Tradeable GetTradeableByThingId(int thingId)
        {
            for (int i = 0; i < deal.tradeables.Count; i++)
            {
                Tradeable tr = deal.tradeables[i];
                if (tr.FirstThingColony?.thingIDNumber == thingId)
                    return tr;
                if (tr.FirstThingTrader?.thingIDNumber == thingId)
                    return tr;
            }

            return null;
        }

        public static void SetTradeSession(MpTradeSession session, bool force = false)
        {
            if (!force && TradeSession.deal == session?.deal) return;

            current = session;
            TradeSession.trader = session?.trader;
            TradeSession.playerNegotiator = session?.playerNegotiator;
            TradeSession.giftMode = session?.giftMode ?? false;
            TradeSession.deal = session?.deal;
        }
    }

    public class MpTradeDeal : TradeDeal
    {
        public MpTradeSession session;

        private static HashSet<Thing> newThings = new HashSet<Thing>();
        private static HashSet<Thing> oldThings = new HashSet<Thing>();

        public UIShouldReset uiShouldReset;

        public HashSet<Thing> recacheThings = new HashSet<Thing>();
        public bool fullRecache;
        public bool ShouldRecache => fullRecache || recacheThings.Count > 0;

        public MpTradeDeal(MpTradeSession session)
        {
            this.session = session;
        }

        public void Recache()
        {
            if (fullRecache)
                CheckAddRemove();

            if (recacheThings.Count > 0)
                CheckReassign();

            newThings.Clear();
            oldThings.Clear();

            uiShouldReset = UIShouldReset.Full;
            recacheThings.Clear();
            fullRecache = false;
        }

        private void CheckAddRemove()
        {
            foreach (Thing t in TradeSession.trader.ColonyThingsWillingToBuy(TradeSession.playerNegotiator))
                newThings.Add(t);

            for (int i = tradeables.Count - 1; i >= 0; i--)
            {
                Tradeable tradeable = tradeables[i];
                int toRemove = 0;

                for (int j = tradeable.thingsColony.Count - 1; j >= 0; j--)
                {
                    Thing thingColony = tradeable.thingsColony[j];
                    if (!newThings.Contains(thingColony))
                        toRemove++;
                    else
                        oldThings.Add(thingColony);
                }

                if (toRemove == 0) continue;

                if (toRemove == tradeable.thingsColony.Count + tradeable.thingsTrader.Count)
                    tradeables.RemoveAt(i);
                else
                    tradeable.thingsColony.RemoveAll(t => !newThings.Contains(t));
            }

            foreach (Thing newThing in newThings)
                if (!oldThings.Contains(newThing))
                    AddToTradeables(newThing, Transactor.Colony);
        }

        private void CheckReassign()
        {
            for (int i = tradeables.Count - 1; i >= 0; i--)
            {
                Tradeable tradeable = tradeables[i];
                for (int j = tradeable.thingsColony.Count - 1; j >= 1; j--)
                {
                    Thing thingColony = tradeable.thingsColony[j];
                    TransferAsOneMode mode = (!tradeable.TraderWillTrade) ? TransferAsOneMode.InactiveTradeable : TransferAsOneMode.Normal;

                    if (recacheThings.Contains(thingColony))
                    {
                        if (!TransferableUtility.TransferAsOne(tradeable.AnyThing, thingColony, mode))
                            tradeable.thingsColony.RemoveAt(j);
                        else
                            AddToTradeables(thingColony, Transactor.Colony);
                    }
                }

                if (recacheThings.Count == 0) break;
            }
        }
    }

    public enum UIShouldReset
    {
        None,
        Silent,
        Full
    }

    public class FactionWorldData : IExposable
    {
        public int factionId;
        public bool online;

        public ResearchManager researchManager;
        public DrugPolicyDatabase drugPolicyDatabase;
        public OutfitDatabase outfitDatabase;
        public PlaySettings playSettings;

        public ResearchSpeed researchSpeed;

        public FactionWorldData() { }

        public void ExposeData()
        {
            Scribe_Values.Look(ref factionId, "factionId");
            Scribe_Values.Look(ref online, "online");

            Scribe_Deep.Look(ref researchManager, "researchManager");
            Scribe_Deep.Look(ref drugPolicyDatabase, "drugPolicyDatabase");
            Scribe_Deep.Look(ref outfitDatabase, "outfitDatabase");
            Scribe_Deep.Look(ref playSettings, "playSettings");

            Scribe_Deep.Look(ref researchSpeed, "researchSpeed");
        }

        public void Tick()
        {
        }

        public static FactionWorldData New(int factionId)
        {
            return new FactionWorldData()
            {
                factionId = factionId,

                researchManager = new ResearchManager(),
                drugPolicyDatabase = new DrugPolicyDatabase(),
                outfitDatabase = new OutfitDatabase(),
                playSettings = new PlaySettings(),
                researchSpeed = new ResearchSpeed(),
            };
        }

        public static FactionWorldData FromCurrent()
        {
            return new FactionWorldData()
            {
                factionId = Faction.OfPlayer.loadID,
                online = true,

                researchManager = Find.ResearchManager,
                drugPolicyDatabase = Current.Game.drugPolicyDatabase,
                outfitDatabase = Current.Game.outfitDatabase,
                playSettings = Current.Game.playSettings,

                researchSpeed = new ResearchSpeed(),
            };
        }

        public static XmlDocument ExtractFromGameDoc(XmlDocument gameDoc)
        {
            XmlDocument doc = new XmlDocument();
            doc.AppendChild(doc.CreateElement("factionWorldData"));
            XmlNode root = doc.DocumentElement;

            string[] fromGame = new[] {
                "researchManager",
                "drugPolicyDatabase",
                "outfitDatabase",
                "playSettings",
                "history"
            };

            string[] fromWorld = new[] {
                "settings"
            };

            foreach (string s in fromGame)
                root.AppendChild(doc.ImportNode(gameDoc.DocumentElement["game"][s], true));

            foreach (string s in fromWorld)
                root.AppendChild(doc.ImportNode(gameDoc.DocumentElement["game"]["world"][s], true));

            return doc;
        }
    }
}
