﻿using Harmony;
using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using Verse;

namespace Multiplayer.Client
{
    public class CaravanFormingSession : IExposable, ISessionWithTransferables
    {
        public Map map;

        public int sessionId;
        public bool reform;
        public Action onClosed;
        public bool mapAboutToBeRemoved;
        public int startingTile = -1;
        public int destinationTile = -1;
        public List<TransferableOneWay> transferables;

        public bool uiDirty;

        public int SessionId => sessionId;

        public CaravanFormingSession(Map map)
        {
            this.map = map;
        }

        public CaravanFormingSession(Map map, bool reform, Action onClosed, bool mapAboutToBeRemoved) : this(map)
        {
            sessionId = map.MpComp().mapIdBlock.NextId();

            this.reform = reform;
            this.onClosed = onClosed;
            this.mapAboutToBeRemoved = mapAboutToBeRemoved;

            AddItems();
        }

        private void AddItems()
        {
            var dialog = new MpFormingCaravanWindow(map, reform, null, mapAboutToBeRemoved);
            dialog.CalculateAndRecacheTransferables();
            transferables = dialog.transferables;
        }

        public void OpenWindow(bool sound = true)
        {
            Find.Selector.ClearSelection();

            var dialog = PrepareDummyDialog();
            if (!sound)
                dialog.soundAppear = null;
            dialog.doCloseX = true;

            CaravanUIUtility.CreateCaravanTransferableWidgets(transferables, out dialog.pawnsTransfer, out dialog.itemsTransfer, "FormCaravanColonyThingCountTip".Translate(), dialog.IgnoreInventoryMode, () => dialog.MassCapacity - dialog.MassUsage, dialog.AutoStripSpawnedCorpses, dialog.CurrentTile, mapAboutToBeRemoved);
            dialog.CountToTransferChanged();

            Find.WindowStack.Add(dialog);
        }

        private MpFormingCaravanWindow PrepareDummyDialog()
        {
            var dialog = new MpFormingCaravanWindow(map, reform, null, mapAboutToBeRemoved)
            {
                transferables = transferables,
                startingTile = startingTile,
                destinationTile = destinationTile,
                thisWindowInstanceEverOpened = true
            };

            return dialog;
        }

        public void ChooseRoute(int destinationTile)
        {
            var dialog = PrepareDummyDialog();
            dialog.Notify_ChoseRoute(destinationTile);

            startingTile = dialog.startingTile;
            destinationTile = dialog.destinationTile;

            uiDirty = true;
        }

        public void TryReformCaravan()
        {
            if (PrepareDummyDialog().TryReformCaravan())
                Remove();
        }

        public void TryFormAndSendCaravan()
        {
            if (PrepareDummyDialog().TryFormAndSendCaravan())
                Remove();
        }

        public void Reset()
        {
            transferables.ForEach(t => t.CountToTransfer = 0);
            uiDirty = true;
        }

        public void Remove()
        {
            map.MpComp().caravanForming = null;
            Find.WorldRoutePlanner.Stop();
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref sessionId, "sessionId");
            Scribe_Values.Look(ref reform, "reform");
            Scribe_Values.Look(ref onClosed, "onClosed");
            Scribe_Values.Look(ref mapAboutToBeRemoved, "mapAboutToBeRemoved");
            Scribe_Values.Look(ref startingTile, "startingTile");
            Scribe_Values.Look(ref destinationTile, "destinationTile");

            Scribe_Collections.Look(ref transferables, "transferables", LookMode.Deep);
        }

        public Transferable GetTransferableByThingId(int thingId)
        {
            return transferables.FirstOrDefault(tr => tr.things.Any(t => t.thingIDNumber == thingId));
        }
    }

    public interface ISessionWithTransferables
    {
        int SessionId { get; }

        Transferable GetTransferableByThingId(int thingId);
    }

    public class MpFormingCaravanWindow : Dialog_FormCaravan
    {
        public static MpFormingCaravanWindow drawing;

        private bool sessionRemoved;

        public CaravanFormingSession Session => map.MpComp().caravanForming;

        public MpFormingCaravanWindow(Map map, bool reform = false, Action onClosed = null, bool mapAboutToBeRemoved = false) : base(map, reform, onClosed, mapAboutToBeRemoved)
        {
        }

        public override void PostClose()
        {
            base.PostClose();

            if (!sessionRemoved)
                Find.World.renderer.wantedMode = WorldRenderMode.Planet;
        }

        public override void DoWindowContents(Rect inRect)
        {
            drawing = this;

            try
            {
                var session = Session;

                if (session == null)
                {
                    sessionRemoved = true;
                    Close();
                }
                else if (session.uiDirty)
                {
                    CountToTransferChanged();
                    session.uiDirty = false;
                }

                base.DoWindowContents(inRect);
            }
            finally
            {
                drawing = null;
            }
        }
    }

    [HarmonyPatch(typeof(Widgets), nameof(Widgets.ButtonText), new[] { typeof(Rect), typeof(string), typeof(bool), typeof(bool), typeof(bool) })]
    static class MakeCancelFormingButtonRed
    {
        static void Prefix(string label, ref bool __state)
        {
            if (MpFormingCaravanWindow.drawing == null) return;
            if (label != "CancelButton".Translate()) return;

            GUI.color = new Color(1f, 0.3f, 0.35f);
            __state = true;
        }

        static void Postfix(bool __state, ref bool __result)
        {
            if (!__state) return;

            GUI.color = Color.white;
            if (__result)
            {
                MpFormingCaravanWindow.drawing.Session?.Remove();
                __result = false;
            }
        }
    }

    [HarmonyPatch(typeof(Widgets), nameof(Widgets.ButtonText), new[] { typeof(Rect), typeof(string), typeof(bool), typeof(bool), typeof(bool) })]
    static class FormCaravanHandleReset
    {
        static void Prefix(string label, ref bool __state)
        {
            if (MpFormingCaravanWindow.drawing == null) return;
            if (label != "ResetButton".Translate()) return;

            __state = true;
        }

        static void Postfix(bool __state, ref bool __result)
        {
            if (!__state) return;

            if (__result)
            {
                MpFormingCaravanWindow.drawing.Session?.Reset();
                __result = false;
            }
        }
    }

    [HarmonyPatch(typeof(Dialog_FormCaravan), nameof(Dialog_FormCaravan.TryFormAndSendCaravan))]
    static class TryFormAndSendCaravanPatch
    {
        static bool Prefix()
        {
            if (MpFormingCaravanWindow.drawing != null)
            {
                MpFormingCaravanWindow.drawing.Session?.TryFormAndSendCaravan();
                return false;
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(Dialog_FormCaravan), nameof(Dialog_FormCaravan.TryReformCaravan))]
    static class TryReformCaravanPatch
    {
        static bool Prefix()
        {
            if (MpFormingCaravanWindow.drawing != null)
            {
                MpFormingCaravanWindow.drawing.Session?.TryReformCaravan();
                return false;
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(Dialog_FormCaravan), nameof(Dialog_FormCaravan.Notify_ChoseRoute))]
    static class Notify_ChoseRoutePatch
    {
        static bool Prefix(int destinationTile)
        {
            if (MpFormingCaravanWindow.drawing != null)
            {
                MpFormingCaravanWindow.drawing.Session?.ChooseRoute(destinationTile);
                return false;
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(MapDrawer), nameof(MapDrawer.DrawMapMesh))]
    static class ForceShowFormingDialog
    {
        static void Prefix(MapDrawer __instance)
        {
            if (Multiplayer.Client != null &&
                __instance.map.MpComp().caravanForming != null &&
                !Find.WindowStack.IsOpen(typeof(MpFormingCaravanWindow)))
                __instance.map.MpComp().caravanForming.OpenWindow(false);
        }
    }

    [HarmonyPatch(typeof(WindowStack), nameof(WindowStack.Add))]
    static class CancelDialogFormCaravan
    {
        static bool Prefix(Window window)
        {
            if (window.GetType() == typeof(Dialog_FormCaravan) && (Multiplayer.ExecutingCmds || Multiplayer.Ticking))
                return false;

            return true;
        }
    }

    [HarmonyPatch(typeof(Dialog_FormCaravan))]
    [HarmonyPatch(new[] { typeof(Map), typeof(bool), typeof(Action), typeof(bool) })]
    static class CancelDialogFormCaravanCtor
    {
        static bool Prefix(Dialog_FormCaravan __instance, Map map, bool reform, Action onClosed, bool mapAboutToBeRemoved)
        {
            if (__instance.GetType() != typeof(Dialog_FormCaravan)) return true;

            if (Multiplayer.ExecutingCmds || Multiplayer.Ticking)
            {
                var comp = map.MpComp();
                if (comp.caravanForming == null)
                    comp.CreateCaravanFormingSession(reform, onClosed, mapAboutToBeRemoved);

                return true;
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(TimedForcedExit), nameof(TimedForcedExit.CompTick))]
    static class TimedForcedExitTickPatch
    {
        static bool Prefix(TimedForcedExit __instance)
        {
            if (Multiplayer.Client != null && __instance.parent is MapParent mapParent && mapParent.HasMap)
                return !mapParent.Map.AsyncTime().Paused;

            return true;
        }
    }

}
