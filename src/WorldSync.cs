using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace CrossplaySteamDoor
{
    // How fast the server passes the world on to each player. The game serves one player per frame and gives each turn
    // at most 10 KB, and only while less than 10 KB is still under way to that player. With six players that is about
    // 40 KB/s each. Buildings are sent before items and creatures, so in a large base, or while a base burns, who owns
    // an item or a chest reaches nobody for minutes: nothing can be picked up or opened.
    public static class WorldSync
    {
        internal const int VanillaBudget = 10240;
        private const int VanillaSendRate = 153600;

        private sealed class Turn
        {
            internal bool RestFirst;
        }

        private static readonly ConditionalWeakTable<object, Turn> Turns = new ConditionalWeakTable<object, Turn>();
        private static readonly List<ZDO> Unsent = new List<ZDO>();
        private static readonly List<ZDO> Changed = new List<ZDO>();
        private static readonly List<ZDO> Rest = new List<ZDO>();
        private static FieldInfo netPeer;
        private static FieldInfo sentObjects;
        private static Func<object, ZDOID, bool> wasSent;
        private static bool budgetFailureReported;

        // Throws when this Valheim version no longer has what the patches below were written against. Checked by type
        // as well as by name: a patch that is handed a different object than it expects would corrupt the send loop.
        internal static void Resolve()
        {
            if (wasSent != null) return;

            var zdoPeer = AccessTools.Inner(typeof(ZDOMan), "ZDOPeer") ?? throw Lacks("ZDOMan.ZDOPeer");
            var peer = AccessTools.Field(zdoPeer, "m_peer");
            if (peer == null || peer.FieldType != typeof(ZNetPeer)) throw Lacks("ZDOPeer.m_peer as a ZNetPeer");
            var sent = AccessTools.Field(zdoPeer, "m_zdos");
            var has = sent?.FieldType.GetMethod("ContainsKey", new[] { typeof(ZDOID) }) ?? throw Lacks("ZDOPeer.m_zdos keyed by ZDOID");

            Require("SendZDOs", typeof(bool), zdoPeer, typeof(bool));
            Require("ServerSortSendZDOS", typeof(void), typeof(List<ZDO>), typeof(Vector3), zdoPeer);

            // The hooks below use these directly. A missing one would only fail when a hook first runs, inside the
            // game's send loop, where nothing catches it.
            if (AccessTools.Field(typeof(ZNetPeer), "m_socket")?.FieldType != typeof(ISocket)) throw Lacks("ZNetPeer.m_socket");
            if (AccessTools.Method(typeof(ZNetPeer), "IsReady", Type.EmptyTypes)?.ReturnType != typeof(bool)) throw Lacks("ZNetPeer.IsReady()");
            if (AccessTools.Field(typeof(ZDO), "m_uid")?.FieldType != typeof(ZDOID)) throw Lacks("ZDO.m_uid");
            if (AccessTools.PropertyGetter(typeof(ZDO), "Type")?.ReturnType != typeof(ZDO.ObjectType)) throw Lacks("ZDO.Type");

            var objects = Expression.Parameter(typeof(object));
            var id = Expression.Parameter(typeof(ZDOID));
            netPeer = peer;
            sentObjects = sent;
            wasSent = Expression.Lambda<Func<object, ZDOID, bool>>(
                Expression.Call(Expression.Convert(objects, sent.FieldType), has, id), objects, id).Compile();
        }

        internal static ZNetPeer NetPeer(object zdoPeer) => (ZNetPeer)netPeer.GetValue(zdoPeer);

        // Called from ZDOMan.SendZDOs in place of its two 10 KB constants.
        public static int BudgetFor(object zdoPeer)
        {
            try
            {
                var steam = NetPeer(zdoPeer)?.m_socket is ZSteamSocket;
                return (steam ? Plugin.SteamSendBudgetKB.Value : Plugin.CrossplaySendBudgetKB.Value) * 1024;
            }
            catch (Exception e)
            {
                if (!budgetFailureReported) Plugin.Log.LogError($"Send budget falls back to the game's 10 KB: {e}");
                budgetFailureReported = true;
                return VanillaBudget;
            }
        }

        // The game's order is other players, terrain, everything solid (buildings, rocks, trees), then the rest: this
        // player's ships and carts, items, creatures. Two changes to it, neither of which lets anything reach a game
        // before a solid object that game does not have yet: a wall torch or an item stand whose wall is missing breaks
        // there, and an animal falls through a floor or a rock that is not there yet.
        // Solid objects the player was never sent go before solid objects that only changed, so a burning base cannot
        // keep the rest of the base from someone who arrives. And on every other turn the rest goes before the solid
        // objects that only changed, so a stream of building updates cannot keep it from being sent.
        internal static void PutNewFirstAndTakeTurns(List<ZDO> objects, object zdoPeer)
        {
            var first = objects.FindIndex(IsSolid);
            if (first < 0) return;
            var end = objects.FindLastIndex(IsSolid) + 1;

            var turn = Turns.GetOrCreateValue(zdoPeer);
            turn.RestFirst = !turn.RestFirst;
            var restFirst = turn.RestFirst && end < objects.Count;

            try
            {
                if (!SplitSolids(objects, first, end, zdoPeer)) return;
                if (Changed.Count == 0 || (Unsent.Count == 0 && !restFirst)) return;

                for (var i = end; i < objects.Count; i++) Rest.Add(objects[i]);
                objects.RemoveRange(first, objects.Count - first);
                objects.AddRange(Unsent);
                objects.AddRange(restFirst ? Rest : Changed);
                objects.AddRange(restFirst ? Changed : Rest);
            }
            finally
            {
                Unsent.Clear();
                Changed.Clear();
                Rest.Clear();
            }
        }

        // False when the solid objects are not one unbroken run; then the game sorts differently than this was written
        // for, and its order is left alone.
        private static bool SplitSolids(List<ZDO> objects, int first, int end, object zdoPeer)
        {
            var sent = sentObjects.GetValue(zdoPeer);
            for (var i = first; i < end; i++)
            {
                var solid = objects[i];
                if (!IsSolid(solid)) return false;
                (wasSent(sent, solid.m_uid) ? Changed : Unsent).Add(solid);
            }
            return true;
        }

        private static bool IsSolid(ZDO zdo) => zdo.Type == ZDO.ObjectType.Solid;

        // The game pins Steam's send rate by setting both ends of its range to 150 KB/s when the first Steam socket is
        // created, so this has to follow it, and pins both ends again.
        internal static void RaiseSteamSendRate()
        {
            var rate = Plugin.SteamSendRateKB.Value * 1024;
            if (rate <= VanillaSendRate) return;

            var pinned = GCHandle.Alloc(rate, GCHandleType.Pinned);
            try
            {
                var raised = SetSendRate(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax, pinned)
                             && SetSendRate(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin, pinned);
                if (raised) Plugin.Log.LogInfo($"Steam send rate per player raised to {Plugin.SteamSendRateKB.Value} KB/s.");
                else Plugin.Log.LogWarning("Steam refused the higher send rate; it stays at the game's 150 KB/s.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Could not raise Steam's send rate; it stays at the game's 150 KB/s. {e.Message}");
            }
            finally
            {
                pinned.Free();
            }
        }

        // An instance method with exactly these arguments: the patches reach the peer as argument 1 and argument 2.
        private static void Require(string method, Type returns, params Type[] parameters)
        {
            var found = AccessTools.Method(typeof(ZDOMan), method) ?? throw Lacks("ZDOMan." + method);
            var actual = found.GetParameters();
            var same = !found.IsStatic && found.ReturnType == returns && actual.Length == parameters.Length;
            for (var i = 0; same && i < actual.Length; i++) same = actual[i].ParameterType == parameters[i];
            if (!same) throw Lacks($"ZDOMan.{method} with the arguments this was written for");
        }

        private static MissingMemberException Lacks(string member) => new MissingMemberException($"this Valheim version has no {member}");

        private static bool SetSendRate(ESteamNetworkingConfigValue end, GCHandle rate) =>
            SteamGameServerNetworkingUtils.SetConfigValue(end, ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global,
                IntPtr.Zero, ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, rate.AddrOfPinnedObject());
    }

    [HarmonyPatch(typeof(ZDOMan), "SendZDOs")]
    internal static class SendBudgetPerPlayer
    {
        private const int ExpectedLimits = 2;

        private static void Prepare() => WorldSync.Resolve();

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            var limits = code.FindAll(IsVanillaLimit).Count;
            if (limits != ExpectedLimits)
            {
                Plugin.Log.LogError($"ZDOMan.SendZDOs has {limits} send limits, expected {ExpectedLimits}; world sync keeps the game's own budget.");
                return code;
            }

            var patched = new List<CodeInstruction>(code.Count + limits);
            foreach (var instruction in code)
            {
                if (!IsVanillaLimit(instruction))
                {
                    patched.Add(instruction);
                    continue;
                }

                patched.Add(new CodeInstruction(OpCodes.Ldarg_1) { labels = instruction.labels, blocks = instruction.blocks });
                patched.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(WorldSync), nameof(WorldSync.BudgetFor))));
            }
            return patched;
        }

        private static void Prefix(ZDOMan __instance) => SyncWatch.TurnStarts(__instance);

        private static void Postfix(ZDOMan __instance, object __0) => SyncWatch.TurnEnds(__instance, __0);

        private static bool IsVanillaLimit(CodeInstruction instruction) =>
            instruction.opcode == OpCodes.Ldc_I4 && instruction.operand is int value && value == WorldSync.VanillaBudget;
    }

    // An exception here would escape into the game's send loop, which then never moves on to the next player.
    [HarmonyPatch(typeof(ZDOMan), "ServerSortSendZDOS")]
    internal static class TakeTurnsWithBuildings
    {
        private static bool failed;

        private static void Prepare() => WorldSync.Resolve();

        private static void Postfix(List<ZDO> __0, object __2)
        {
            SyncWatch.ListSorted(__0);
            if (failed || !Plugin.FairTurns.Value || __2 == null) return;

            try
            {
                WorldSync.PutNewFirstAndTakeTurns(__0, __2);
            }
            catch (Exception e)
            {
                failed = true;
                Plugin.Log.LogError($"Fair turns stopped; the game's own order stays. {e}");
            }
        }
    }
}
