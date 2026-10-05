using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CrossplaySteamDoor
{
    // Measures what the players feel: how long each of them went without the server catching up on everything that
    // changed around them. One line in the server log per report period.
    internal static class SyncWatch
    {
        private sealed class Line
        {
            internal string Player;
            internal bool Steam;
            internal int Turns;
            internal int BusyTurns;
            internal int Sent;
            internal int WorstBacklog;
            internal float FirstTurn = -1f;
            internal float LastTurn;
            internal float BehindSince = -1f;
            internal float LongestBehind;

            internal float Seconds => Math.Max(1f, LastTurn - FirstTurn);

            // A player who left while behind stays behind only until their last turn, not until the report.
            internal float LongestBehindSoFar => BehindSince < 0f ? LongestBehind : Math.Max(LongestBehind, LastTurn - BehindSince);
        }

        private static readonly Dictionary<long, Line> Lines = new Dictionary<long, Line>();
        private static readonly List<long> Gone = new List<long>();
        private static FieldInfo sentCounter;
        private static int sentBefore;
        private static int waiting;
        private static float periodStart = -1f;
        private static float secondsWithPlayers;
        private static int frames;
        private static float slowestFrame;

        private static bool IsOn => sentCounter != null && Plugin.ReportMinutes.Value > 0;

        // The report needs both the turn and the sorted list; with one of them it would report nonsense.
        internal static void TurnOn(bool turnsAndListsPatched)
        {
            if (!turnsAndListsPatched) return;
            sentCounter = AccessTools.Field(typeof(ZDOMan), "m_zdosSent");
            if (sentCounter == null || sentCounter.FieldType != typeof(int))
            {
                sentCounter = null;
                Plugin.Log.LogWarning("No 'World sync' report on this Valheim version: ZDOMan.m_zdosSent is gone.");
            }
        }

        internal static void TurnStarts(ZDOMan world)
        {
            if (!IsOn) return;

            try
            {
                waiting = -1;
                sentBefore = (int)sentCounter.GetValue(world);
            }
            catch (Exception e)
            {
                Stop(e);
            }
        }

        // The objects near the player that are due, before the game adds far-away scenery to fill a quiet turn.
        internal static void ListSorted(List<ZDO> near)
        {
            if (IsOn) waiting = near.Count;
        }

        internal static void TurnEnds(ZDOMan world, object zdoPeer)
        {
            if (!IsOn) return;

            try
            {
                var peer = WorldSync.NetPeer(zdoPeer);
                if (peer == null || !peer.IsReady()) return;

                var line = LineFor(peer);
                var sent = (int)sentCounter.GetValue(world) - sentBefore;
                var left = waiting < 0 ? -1 : Math.Max(0, waiting - sent);
                line.Turns++;
                line.Sent += sent;
                if (waiting < 0) line.BusyTurns++;
                if (left > line.WorstBacklog) line.WorstBacklog = left;

                var now = Time.unscaledTime;
                if (line.FirstTurn < 0f) line.FirstTurn = now;
                line.LastTurn = now;
                if (left != 0)
                {
                    if (line.BehindSince < 0f) line.BehindSince = now;
                }
                else if (line.BehindSince >= 0f)
                {
                    line.LongestBehind = Math.Max(line.LongestBehind, now - line.BehindSince);
                    line.BehindSince = -1f;
                }
            }
            catch (Exception e)
            {
                Stop(e);
            }
        }

        internal static void Frame()
        {
            if (!IsOn || ZNet.instance == null || !ZNet.instance.IsServer()) return;

            var now = Time.unscaledTime;
            if (periodStart < 0f) periodStart = now;
            if (ZNet.instance.GetPeerConnections() > 0)
            {
                frames++;
                secondsWithPlayers += Time.unscaledDeltaTime;
                if (Time.unscaledDeltaTime > slowestFrame) slowestFrame = Time.unscaledDeltaTime;
            }
            if (now - periodStart < Plugin.ReportMinutes.Value * 60f) return;

            if (Lines.Count > 0 && secondsWithPlayers > 0f) Report();
            StartNextPeriod(now);
        }

        private static void Report() =>
            Plugin.Log.LogInfo($"World sync, {secondsWithPlayers / 60f:0.#} min with players: server {frames / secondsWithPlayers:0} fps, " +
                               $"slowest frame {slowestFrame * 1000f:0} ms." + Describe(true, "door") + Describe(false, "crossplay"));

        private static string Describe(bool steam, string kind)
        {
            var players = 0;
            int turns = 0, busy = 0, worstBacklog = 0;
            float turnsPerSecond = 0f, sentPerSecond = 0f, longest = 0f;
            string slowest = null;
            foreach (var line in Lines.Values)
            {
                if (line.Steam != steam || line.Turns == 0) continue;

                players++;
                turns += line.Turns;
                busy += line.BusyTurns;
                turnsPerSecond += line.Turns / line.Seconds;
                sentPerSecond += line.Sent / line.Seconds;
                worstBacklog = Math.Max(worstBacklog, line.WorstBacklog);
                if (slowest != null && line.LongestBehindSoFar <= longest) continue;
                longest = line.LongestBehindSoFar;
                slowest = line.Player;
            }
            if (players == 0) return "";

            return $" {players} {kind} player{(players == 1 ? "" : "s")}: {turnsPerSecond / players:0.0} turns/s each, " +
                   $"{sentPerSecond / players:0} objects/s each, longest time behind {longest:0.0} s ({slowest}), " +
                   $"most waiting {worstBacklog} objects, {100f * busy / turns:0}% of turns skipped for a busy line.";
        }

        // A player still behind at the end of a period stays behind into the next one, so a long stall is not cut in two.
        private static void StartNextPeriod(float now)
        {
            Gone.Clear();
            foreach (var entry in Lines)
            {
                var line = entry.Value;
                if (line.Turns == 0)
                {
                    Gone.Add(entry.Key);
                    continue;
                }
                line.Turns = line.BusyTurns = line.Sent = line.WorstBacklog = 0;
                line.FirstTurn = -1f;
                line.LongestBehind = 0f;
            }
            foreach (var uid in Gone) Lines.Remove(uid);

            periodStart = now;
            secondsWithPlayers = 0f;
            frames = 0;
            slowestFrame = 0f;
        }

        private static Line LineFor(ZNetPeer peer)
        {
            if (!Lines.TryGetValue(peer.m_uid, out var line))
            {
                line = new Line { Steam = peer.m_socket is ZSteamSocket };
                Lines[peer.m_uid] = line;
            }
            line.Player = string.IsNullOrEmpty(peer.m_playerName) ? peer.m_uid.ToString() : peer.m_playerName;
            return line;
        }

        private static void Stop(Exception e)
        {
            sentCounter = null;
            Plugin.Log.LogWarning($"'World sync' report stopped: {e.Message}");
        }
    }
}
