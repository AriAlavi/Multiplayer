﻿using Harmony;
using Multiplayer.Common;
using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;
using Verse;
using Verse.AI;

namespace Multiplayer.Client
{
    [HarmonyPatch(typeof(TickManager), nameof(TickManager.TickManagerUpdate))]
    public static class TickPatch
    {
        const float TimeStep = 1 / 6f;

        public static int Timer => (int)timerInt;

        public static double accumulator;
        public static double timerInt;
        public static int tickUntil;
        public static bool currentExecutingCmdIssuedBySelf;

        public static TimeSpeed replayTimeSpeed;
        public static int skipTo = -1;

        public const bool asyncTime = false;

        public static IEnumerable<ITickable> AllTickables
        {
            get
            {
                MultiplayerWorldComp comp = Multiplayer.WorldComp;
                yield return comp;
                yield return comp.ticker;

                foreach (Map map in Find.Maps)
                    yield return map.GetComponent<MapAsyncTimeComp>();
            }
        }

        static bool Prefix()
        {
            if (Multiplayer.Client == null) return true;
            if (LongEventHandler.AnyEventNowOrWaiting) return false;

            double delta = Time.deltaTime * 60.0;
            if (delta > 3)
                delta = 3;

            accumulator += delta;

            if (Timer >= tickUntil)
                accumulator = 0;
            else if (!Multiplayer.IsReplay && delta < 1.5 && tickUntil - timerInt > 8)
                accumulator += Math.Min(100, tickUntil - timerInt - 8);

            if (Multiplayer.IsReplay && replayTimeSpeed == TimeSpeed.Paused)
                accumulator = 0;

            if (skipTo > 0)
            {
                if (Timer >= skipTo)
                    skipTo = -1;
                else
                    accumulator = Math.Min(60, skipTo - Timer);
            }

            Tick();

            return false;
        }

        static ITickable CurrentTickable()
        {
            if (WorldRendererUtility.WorldRenderedNow)
                return Multiplayer.WorldComp;
            else if (Find.CurrentMap != null)
                return Find.CurrentMap.AsyncTime();

            return null;
        }

        static void Postfix()
        {
            if (Multiplayer.Client == null || Find.CurrentMap == null) return;

            MapAsyncTimeComp comp = Find.CurrentMap.GetComponent<MapAsyncTimeComp>();
            Shader.SetGlobalFloat(ShaderPropertyIDs.GameSeconds, comp.mapTicks.TicksToSeconds());
        }

        public static void Tick()
        {
            while (accumulator > 0)
            {
                int curTimer = Timer;

                foreach (ITickable tickable in AllTickables)
                {
                    while (tickable.Cmds.Count > 0 && tickable.Cmds.Peek().ticks == curTimer)
                    {
                        ScheduledCommand cmd = tickable.Cmds.Dequeue();
                        tickable.ExecuteCmd(cmd);
                    }
                }

                foreach (ITickable tickable in AllTickables)
                {
                    if (tickable.TimePerTick(tickable.TimeSpeed) == 0) continue;
                    tickable.RealTimeToTickThrough += TimeStep;

                    Tick(tickable);
                }

                accumulator -= TimeStep * ReplayMultiplier();
                timerInt += TimeStep;

                if (Timer >= tickUntil)
                    accumulator = 0;
            }
        }

        private static void Tick(ITickable tickable)
        {
            while (tickable.RealTimeToTickThrough >= 0)
            {
                float timePerTick = tickable.TimePerTick(tickable.TimeSpeed);
                if (timePerTick == 0) break;

                tickable.RealTimeToTickThrough -= timePerTick;
                tickable.Tick();
            }
        }

        private static float ReplayMultiplier()
        {
            if (!Multiplayer.IsReplay || skipTo > 0 || Multiplayer.simulating) return 1f;

            if (replayTimeSpeed == TimeSpeed.Paused)
                return 0f;

            ITickable tickable = CurrentTickable();
            if (tickable.TimeSpeed == TimeSpeed.Paused)
                return 1 / 60f; // So paused sections of the timeline are skipped through asap

            return tickable.TimePerTick(replayTimeSpeed) / tickable.TimePerTick(tickable.TimeSpeed);
        }
    }

    [HarmonyPatch(typeof(Prefs))]
    [HarmonyPatch(nameof(Prefs.PauseOnLoad), PropertyMethod.Getter)]
    public static class CancelSingleTick
    {
        // Cancel ticking after loading as its handled seperately
        static void Postfix(ref bool __result)
        {
            if (Multiplayer.Client != null)
                __result = false;
        }
    }

    public interface ITickable
    {
        float RealTimeToTickThrough { get; set; }

        TimeSpeed TimeSpeed { get; }

        Queue<ScheduledCommand> Cmds { get; }

        float TimePerTick(TimeSpeed speed);

        void Tick();

        void ExecuteCmd(ScheduledCommand cmd);
    }

    public class ConstantTicker : ITickable
    {
        public static bool ticking;

        public float RealTimeToTickThrough { get; set; }
        public TimeSpeed TimeSpeed => TimeSpeed.Normal;
        public Queue<ScheduledCommand> Cmds => cmds;
        public Queue<ScheduledCommand> cmds = new Queue<ScheduledCommand>();

        public void ExecuteCmd(ScheduledCommand cmd)
        {
        }

        public float TimePerTick(TimeSpeed speed) => 1f;

        public void Tick()
        {
            ticking = true;

            try
            {
                TickResearch();

                // Not really deterministic but here for possible future server-side game state verification
                Extensions.PushFaction(null, Multiplayer.RealPlayerFaction);
                TickSync();
                SyncResearch.ConstantTick();
                Extensions.PopFaction();
            }
            finally
            {
                ticking = false;
            }
        }

        private static void TickSync()
        {
            foreach (SyncField f in Sync.bufferedFields)
            {
                if (!f.inGameLoop) continue;

                Sync.bufferedChanges[f].RemoveAll((k, data) =>
                {
                    if (OnMainThread.CheckShouldRemove(f, k, data))
                        return true;

                    if (TickPatch.Timer - data.timestamp > 30)
                    {
                        f.DoSync(k.first, data.toSend, k.second);
                        data.sent = true;
                        data.timestamp = TickPatch.Timer;
                    }

                    return false;
                });
            }
        }

        private static Pawn dummyPawn = new Pawn()
        {
            relations = new Pawn_RelationsTracker(dummyPawn),
        };

        public void TickResearch()
        {
            MultiplayerWorldComp comp = Multiplayer.WorldComp;
            foreach (FactionWorldData factionData in comp.factionData.Values)
            {
                if (factionData.researchManager.currentProj == null)
                    continue;

                Extensions.PushFaction(null, factionData.factionId);

                foreach (var kv in factionData.researchSpeed.data)
                {
                    Pawn pawn = PawnsFinder.AllMaps_Spawned.FirstOrDefault(p => p.thingIDNumber == kv.Key);
                    if (pawn == null)
                    {
                        dummyPawn.factionInt = Faction.OfPlayer;
                        pawn = dummyPawn;
                    }

                    Find.ResearchManager.ResearchPerformed(kv.Value, pawn);

                    dummyPawn.factionInt = null;
                }

                Extensions.PopFaction();
            }
        }
    }

    [HarmonyPatch(typeof(Map), nameof(Map.MapUpdate))]
    public static class MapUpdateTimePatch
    {
        static void Prefix(Map __instance, ref Container<int, TimeSpeed> __state)
        {
            if (Multiplayer.Client == null) return;

            __state = new Container<int, TimeSpeed>(Find.TickManager.TicksGame, Find.TickManager.CurTimeSpeed);

            MapAsyncTimeComp comp = __instance.GetComponent<MapAsyncTimeComp>();
            Find.TickManager.DebugSetTicksGame(comp.mapTicks);
            Find.TickManager.CurTimeSpeed = comp.TimeSpeed;
        }

        static void Postfix(Container<int, TimeSpeed> __state)
        {
            if (__state == null) return;

            Find.TickManager.DebugSetTicksGame(__state.First);
            Find.TickManager.CurTimeSpeed = __state.Second;
        }
    }

    [MpPatch(typeof(Map), nameof(Map.MapPreTick))]
    [MpPatch(typeof(Map), nameof(Map.MapPostTick))]
    [MpPatch(typeof(TickList), nameof(TickList.Tick))]
    static class CancelMapManagersTick
    {
        static bool Prefix() => Multiplayer.Client == null || MapAsyncTimeComp.tickingMap != null;
    }

    [HarmonyPatch(typeof(Autosaver), nameof(Autosaver.AutosaverTick))]
    static class DisableAutosaver
    {
        static bool Prefix() => Multiplayer.Client == null;
    }

    [HarmonyPatch(typeof(Map), nameof(Map.MapUpdate))]
    static class MapUpdateMarker
    {
        public static bool updating;

        static void Prefix() => updating = true;
        static void Postfix() => updating = false;
    }

    [MpPatch(typeof(PowerNetManager), nameof(PowerNetManager.UpdatePowerNetsAndConnections_First))]
    [MpPatch(typeof(GlowGrid), nameof(GlowGrid.GlowGridUpdate_First))]
    [MpPatch(typeof(RegionGrid), nameof(RegionGrid.UpdateClean))]
    [MpPatch(typeof(RegionAndRoomUpdater), nameof(RegionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms))]
    static class CancelMapManagersUpdate
    {
        static bool Prefix() => Multiplayer.Client == null || !MapUpdateMarker.updating;
    }

    [HarmonyPatch(typeof(RegionAndRoomUpdater), nameof(RegionAndRoomUpdater.RegenerateNewRegionsFromDirtyCells))]
    static class TellMe
    {
        static void Prefix()
        {
            if (Multiplayer.Client != null && Multiplayer.ShouldSync)
            {
                int hash = new StackTrace().Hash();
                Log.ErrorOnce($"should sync {hash}", hash);
            }
        }
    }

    [HarmonyPatch(typeof(DateNotifier), nameof(DateNotifier.DateNotifierTick))]
    static class DateNotifierPatch
    {
        static void Prefix(DateNotifier __instance, ref int? __state)
        {
            if (Multiplayer.Client == null && Multiplayer.RealPlayerFaction != null) return;

            Map map = __instance.FindPlayerHomeWithMinTimezone();
            if (map == null) return;

            __state = Find.TickManager.TicksGame;
            FactionContext.Push(Multiplayer.RealPlayerFaction);
            Find.TickManager.DebugSetTicksGame(map.AsyncTime().mapTicks);
        }

        static void Postfix(int? __state)
        {
            if (!__state.HasValue) return;
            Find.TickManager.DebugSetTicksGame(__state.Value);
            FactionContext.Pop();
        }
    }

    [HarmonyPatch(typeof(TickManager), nameof(TickManager.RegisterAllTickabilityFor))]
    public static class TickListAdd
    {
        static bool Prefix(Thing t)
        {
            if (Multiplayer.Client == null) return true;
            MapAsyncTimeComp comp = t.Map.GetComponent<MapAsyncTimeComp>();

            if (t.def.tickerType == TickerType.Normal)
                comp.tickListNormal.RegisterThing(t);
            else if (t.def.tickerType == TickerType.Rare)
                comp.tickListRare.RegisterThing(t);
            else if (t.def.tickerType == TickerType.Long)
                comp.tickListLong.RegisterThing(t);

            return false;
        }
    }

    [HarmonyPatch(typeof(TickManager), nameof(TickManager.DeRegisterAllTickabilityFor))]
    public static class TickListRemove
    {
        static bool Prefix(Thing t)
        {
            if (Multiplayer.Client == null) return true;
            MapAsyncTimeComp comp = t.Map.GetComponent<MapAsyncTimeComp>();

            if (t.def.tickerType == TickerType.Normal)
                comp.tickListNormal.DeregisterThing(t);
            else if (t.def.tickerType == TickerType.Rare)
                comp.tickListRare.DeregisterThing(t);
            else if (t.def.tickerType == TickerType.Long)
                comp.tickListLong.DeregisterThing(t);

            return false;
        }
    }

    [HarmonyPatch(typeof(TimeControls), nameof(TimeControls.DoTimeControlsGUI))]
    public static class TimeControlPatch
    {
        private static TimeSpeed lastSpeed;

        static void Prefix(ref ITickable __state)
        {
            if (Multiplayer.Client == null) return;
            if (!WorldRendererUtility.WorldRenderedNow && Find.CurrentMap == null) return;

            ITickable tickable = Multiplayer.WorldComp;
            if (!WorldRendererUtility.WorldRenderedNow)
                tickable = Find.CurrentMap.AsyncTime();

            TimeSpeed speed = tickable.TimeSpeed;
            if (Multiplayer.IsReplay && !KeyBindingDefOf.QueueOrder.IsDownEvent)
                speed = TickPatch.replayTimeSpeed;

            Find.TickManager.CurTimeSpeed = speed;
            lastSpeed = speed;
            __state = tickable;
        }

        static void Postfix(ITickable __state)
        {
            if (__state == null) return;

            TimeSpeed newSpeed = Find.TickManager.CurTimeSpeed;
            if (lastSpeed == newSpeed) return;

            if (Multiplayer.IsReplay)
            {
                if (KeyBindingDefOf.QueueOrder.IsDownEvent)
                    OnMainThread.Enqueue(() => Find.TickManager.CurTimeSpeed = newSpeed);
                else
                    TickPatch.replayTimeSpeed = newSpeed;
            }

            if (__state is MultiplayerWorldComp)
                Multiplayer.Client.SendCommand(CommandType.WorldTimeSpeed, ScheduledCommand.Global, (byte)newSpeed);
            else if (__state is MapAsyncTimeComp comp)
                Multiplayer.Client.SendCommand(CommandType.MapTimeSpeed, comp.map.uniqueID, (byte)newSpeed);

            Find.TickManager.CurTimeSpeed = lastSpeed;
        }
    }

    public static class SetMapTimeForUI
    {
        static void Prefix(ref Container<int, TimeSpeed> __state)
        {
            if (Multiplayer.Client == null || WorldRendererUtility.WorldRenderedNow || Find.CurrentMap == null) return;

            Map map = Find.CurrentMap;
            __state = new Container<int, TimeSpeed>(Find.TickManager.TicksGame, Find.TickManager.CurTimeSpeed);
            MapAsyncTimeComp comp = map.GetComponent<MapAsyncTimeComp>();
            Find.TickManager.DebugSetTicksGame(comp.mapTicks);
            Find.TickManager.CurTimeSpeed = comp.TimeSpeed;
        }

        static void Postfix(Container<int, TimeSpeed> __state)
        {
            if (__state == null) return;

            Find.TickManager.DebugSetTicksGame(__state.First);
            Find.TickManager.CurTimeSpeed = __state.Second;
        }
    }

    [HarmonyPatch(typeof(PortraitsCache))]
    [HarmonyPatch(nameof(PortraitsCache.IsAnimated))]
    public static class PawnPortraitMapTime
    {
        static void Prefix(Pawn pawn, ref Container<int, TimeSpeed> __state)
        {
            if (Multiplayer.Client == null || Current.ProgramState != ProgramState.Playing) return;

            Map map = pawn.Map;
            if (map == null) return;

            __state = new Container<int, TimeSpeed>(Find.TickManager.TicksGame, Find.TickManager.CurTimeSpeed);
            MapAsyncTimeComp comp = map.GetComponent<MapAsyncTimeComp>();
            Find.TickManager.DebugSetTicksGame(comp.mapTicks);
            Find.TickManager.CurTimeSpeed = comp.TimeSpeed;
        }

        static void Postfix(Container<int, TimeSpeed> __state)
        {
            if (__state == null) return;

            Find.TickManager.DebugSetTicksGame(__state.First);
            Find.TickManager.CurTimeSpeed = __state.Second;
        }
    }

    [HarmonyPatch(typeof(ColonistBar), nameof(ColonistBar.ColonistBarOnGUI))]
    [StaticConstructorOnStartup]
    public static class ColonistBarTimeControl
    {
        static void Postfix()
        {
            if (Multiplayer.Client == null) return;

            ColonistBar bar = Find.ColonistBar;
            if (bar.Entries.Count == 0 || bar.Entries[bar.Entries.Count - 1].group == 0) return;

            int curGroup = -1;
            foreach (ColonistBar.Entry entry in bar.Entries)
            {
                if (entry.map == null || curGroup == entry.group) continue;

                float alpha = 1.0f;
                if (entry.map != Find.CurrentMap || WorldRendererUtility.WorldRenderedNow)
                    alpha = 0.75f;

                MapAsyncTimeComp comp = entry.map.AsyncTime();
                Rect rect = bar.drawer.GroupFrameRect(entry.group);
                Rect button = new Rect(rect.x - TimeControls.TimeButSize.x / 2f, rect.yMax - TimeControls.TimeButSize.y / 2f, TimeControls.TimeButSize.x, TimeControls.TimeButSize.y);
                Widgets.DrawRectFast(button, new Color(0.5f, 0.5f, 0.5f, 0.4f * alpha));
                Widgets.ButtonImage(button, TexButton.SpeedButtonTextures[(int)comp.TimeSpeed]);

                curGroup = entry.group;
            }
        }
    }

    [HarmonyPatch(typeof(Storyteller))]
    [HarmonyPatch(nameof(Storyteller.StorytellerTick))]
    public class StorytellerTickPatch
    {
        static bool Prefix()
        {
            // The storyteller is currently only enabled for maps
            return Multiplayer.Client == null || !MultiplayerWorldComp.tickingWorld;
        }
    }

    [HarmonyPatch(typeof(Storyteller))]
    [HarmonyPatch(nameof(Storyteller.AllIncidentTargets), PropertyMethod.Getter)]
    public class StorytellerTargetsPatch
    {
        public static Map target;

        static void Postfix(ref List<IIncidentTarget> __result)
        {
            if (Multiplayer.Client == null) return;
            if (target == null) return;

            __result.Clear();
            __result.Add(target);
        }
    }

    public class MapAsyncTimeComp : MapComponent, ITickable
    {
        public static Map tickingMap;
        public static Map executingCmdMap;

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

        public float RealTimeToTickThrough { get; set; }

        public Queue<ScheduledCommand> Cmds { get => cmds; }

        public int mapTicks;
        //private TimeSpeed timeSpeedInt;
        //public bool forcedNormalSpeed;

        public Storyteller storyteller;

        public TickList tickListNormal = new TickList(TickerType.Normal);
        public TickList tickListRare = new TickList(TickerType.Rare);
        public TickList tickListLong = new TickList(TickerType.Long);

        // Shared random state for ticking and commands
        public ulong randState = 1;

        public Queue<ScheduledCommand> cmds = new Queue<ScheduledCommand>();

        public MapAsyncTimeComp(Map map) : base(map)
        {
        }

        public int tickRel;

        public void Tick()
        {
            tickingMap = map;
            PreContext();

            //SimpleProfiler.Start();

            try
            {
                map.MapPreTick();
                mapTicks++;
                Find.TickManager.ticksGameInt = mapTicks;

                tickListNormal.Tick();
                tickListRare.Tick();
                tickListLong.Tick();

                map.PushFaction(map.ParentFaction);
                storyteller.StorytellerTick();
                map.PopFaction();

                map.MapPostTick();

                UpdateManagers();
            }
            finally
            {
                PostContext();

                tickingMap = null;

                if (false)
                    if (Multiplayer.IsReplay || Multiplayer.LocalServer != null)
                    {
                        if (Multiplayer.mapSeeds.Count > tickRel)
                        {
                            if (Multiplayer.mapSeeds[tickRel] != randState)
                            {
                                MpLog.Error("Desync tick " + mapTicks);
                            }
                        }
                        else
                        {
                            Multiplayer.mapSeeds.Add(randState);
                        }
                    }

                tickRel++;

                //SimpleProfiler.Pause();

                if (false)
                    if (tickRel % 1000 == 0)
                    {
                        SimpleProfiler.Print("profiler_alltick.txt");
                        SimpleProfiler.Init(Multiplayer.username);
                    }

                if (RandPatch.collect && !Multiplayer.IsReplay && tickRel % 1000 == 0)
                {
                    Multiplayer.Client.Send(Packets.Client_Debug, RandPatch.called);
                    RandPatch.called.Clear();
                    RandPatch.traces.Insert(0, new List<RandContext>());

                    if (!Multiplayer.simulating)
                    {
                        //SimpleProfiler.Print($"profiler_{Multiplayer.username}_tick.txt");
                        //SimpleProfiler.Init(Multiplayer.username);

                        //byte[] mapData = ScribeUtil.WriteExposable(map, "map", true);
                        //File.WriteAllBytes($"map_0_{Multiplayer.username}.xml", mapData);
                    }
                }
            }
        }

        // These are normally called in Map.MapUpdate() and react to changes in the game state even when the game is paused (not ticking)
        // Update() methods are not deterministic, but in multiplayer all game state changes (which don't happen during ticking) happen in commands
        // Thus these methods can be moved to Tick() and ExecuteCmd()
        public void UpdateManagers()
        {
            map.regionGrid.UpdateClean();
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();

            map.powerNetManager.UpdatePowerNetsAndConnections_First();
            map.glowGrid.GlowGridUpdate_First();
        }

        private int prevTicksGame;
        private TimeSpeed prevTimeSpeed;
        private Storyteller prevStoryteller;

        public void PreContext()
        {
            map.PushFaction(map.ParentFaction);

            if (TickPatch.asyncTime)
            {
                prevTicksGame = Find.TickManager.TicksGame;
                prevTimeSpeed = Find.TickManager.CurTimeSpeed;
                Find.TickManager.ticksGameInt = mapTicks;
                Find.TickManager.CurTimeSpeed = TimeSpeed;
            }

            prevStoryteller = Current.Game.storyteller;
            Current.Game.storyteller = storyteller;
            StorytellerTargetsPatch.target = map;

            UniqueIdsPatch.CurrentBlock = map.GetComponent<MultiplayerMapComp>().mapIdBlock;

            Rand.StateCompressed = randState;

            // Reset the effects of SkyManager.SkyManagerUpdate
            map.skyManager.curSkyGlowInt = map.skyManager.CurrentSkyTarget().glow;
        }

        public void PostContext()
        {
            UniqueIdsPatch.CurrentBlock = null;

            Current.Game.storyteller = prevStoryteller;
            StorytellerTargetsPatch.target = null;

            if (TickPatch.asyncTime)
            {
                Find.TickManager.ticksGameInt = prevTicksGame;
                Find.TickManager.CurTimeSpeed = prevTimeSpeed;
            }

            randState = Rand.StateCompressed;

            map.PopFaction();
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref mapTicks, "mapTicks");
            Scribe_Deep.Look(ref storyteller, "storyteller");
            //Scribe_Values.Look(ref timeSpeedInt, "timeSpeed");
        }

        public void ExecuteCmd(ScheduledCommand cmd)
        {
            ByteReader data = new ByteReader(cmd.data);
            MpContext context = data.MpContext();

            CommandType cmdType = cmd.type;

            executingCmdMap = map;
            TickPatch.currentExecutingCmdIssuedBySelf = cmd.issuedBySelf;

            CurrentMapGetPatch.currentMap = map;
            CurrentMapSetPatch.ignore = true;

            PreContext();
            map.PushFaction(cmd.GetFaction());

            context.map = map;

            try
            {
                if (cmdType == CommandType.Sync)
                {
                    Sync.HandleCmd(data);
                }

                if (cmdType == CommandType.CreateMapFactionData)
                {
                    HandleMapFactionData(cmd, data);
                }

                if (cmdType == CommandType.MapTimeSpeed)
                {
                    TimeSpeed speed = (TimeSpeed)data.ReadByte();
                    TimeSpeed = speed;

                    MpLog.Log("Set map time speed " + speed);
                }

                if (cmdType == CommandType.MapIdBlock)
                {
                    IdBlock block = IdBlock.Deserialize(data);

                    if (map != null)
                    {
                        map.MpComp().mapIdBlock = block;
                        Log.Message(Multiplayer.username + "encounter id block set");
                    }
                }

                if (cmdType == CommandType.Designator)
                {
                    HandleDesignator(cmd, data);
                }

                if (cmdType == CommandType.SpawnPawn)
                {
                    Pawn pawn = ScribeUtil.ReadExposable<Pawn>(data.ReadPrefixedBytes());

                    IntVec3 spawn = CellFinderLoose.TryFindCentralCell(map, 7, 10, (IntVec3 x) => !x.Roofed(map));
                    GenSpawn.Spawn(pawn, spawn, map);
                    Log.Message("spawned " + pawn);
                }

                if (cmdType == CommandType.Forbid)
                {
                    HandleForbid(cmd, data);
                }

                UpdateManagers();
            }
            catch (Exception e)
            {
                Log.Error($"Map cmd exception ({cmdType}): {e}");
            }
            finally
            {
                CurrentMapSetPatch.ignore = false;
                CurrentMapGetPatch.currentMap = null;
                map.PopFaction();
                PostContext();
                TickPatch.currentExecutingCmdIssuedBySelf = false;
                executingCmdMap = null;
            }
        }

        private void HandleForbid(ScheduledCommand cmd, ByteReader data)
        {
            int thingId = data.ReadInt32();
            bool value = data.ReadBool();

            ThingWithComps thing = map.listerThings.AllThings.Find(t => t.thingIDNumber == thingId) as ThingWithComps;
            if (thing == null) return;

            CompForbiddable forbiddable = thing.GetComp<CompForbiddable>();
            if (forbiddable == null) return;

            forbiddable.Forbidden = value;
        }

        private void HandleMapFactionData(ScheduledCommand cmd, ByteReader data)
        {
            int factionId = data.ReadInt32();

            Faction faction = Find.FactionManager.GetById(factionId);
            MultiplayerMapComp comp = map.MpComp();

            if (!comp.factionMapData.ContainsKey(factionId))
            {
                FactionMapData factionMapData = FactionMapData.New(factionId, map);
                comp.factionMapData[factionId] = factionMapData;

                factionMapData.areaManager.AddStartingAreas();
                map.pawnDestinationReservationManager.RegisterFaction(faction);

                MpLog.Log("New map faction data for {0}", faction.GetUniqueLoadID());
            }
        }

        private void HandleDesignator(ScheduledCommand command, ByteReader data)
        {
            DesignatorMode mode = Sync.ReadSync<DesignatorMode>(data);
            Designator designator = Sync.ReadSync<Designator>(data);
            if (designator == null) return;

            try
            {
                if (!SetDesignatorState(designator, data)) return;

                if (mode == DesignatorMode.SingleCell)
                {
                    IntVec3 cell = Sync.ReadSync<IntVec3>(data);
                    designator.DesignateSingleCell(cell);
                    designator.Finalize(true);
                }
                else if (mode == DesignatorMode.MultiCell)
                {
                    IntVec3[] cells = Sync.ReadSync<IntVec3[]>(data);
                    designator.DesignateMultiCell(cells);

                    Find.Selector.ClearSelection();
                }
                else if (mode == DesignatorMode.Thing)
                {
                    Thing thing = Sync.ReadSync<Thing>(data);

                    if (thing != null)
                    {
                        designator.DesignateThing(thing);
                        designator.Finalize(true);
                    }
                }

                foreach (Zone zone in map.zoneManager.AllZones)
                    zone.cellsShuffled = true;
            }
            finally
            {
                DesignatorInstallPatch.thingToInstall = null;
            }
        }

        private bool SetDesignatorState(Designator designator, ByteReader data)
        {
            if (designator is Designator_AreaAllowed)
            {
                Area area = Sync.ReadSync<Area>(data);
                if (area == null) return false;
                Designator_AreaAllowed.selectedArea = area;
            }

            if (designator is Designator_Place place)
            {
                place.placingRot = Sync.ReadSync<Rot4>(data);
            }

            if (designator is Designator_Build build && build.PlacingDef.MadeFromStuff)
            {
                ThingDef stuffDef = Sync.ReadSync<ThingDef>(data);
                if (stuffDef == null) return false;
                build.stuffDef = stuffDef;
            }

            if (designator is Designator_Install)
            {
                Thing thing = Sync.ReadSync<Thing>(data);
                if (thing == null) return false;
                DesignatorInstallPatch.thingToInstall = thing;
            }

            return true;
        }
    }

    public enum DesignatorMode
    {
        SingleCell,
        MultiCell,
        Thing
    }

}
