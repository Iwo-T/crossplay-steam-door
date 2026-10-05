using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;

namespace CrossplaySteamDoor
{
    // A prefix, so the door exists before vanilla registers the crossplay lobby and asks for the address to list.
    [HarmonyPatch(typeof(ZNet), nameof(ZNet.OpenServer))]
    internal static class OpenDoorWithServer
    {
        private static void Prefix(ZNet __instance)
        {
            if (__instance.IsServer() && ZNet.m_onlineBackend == OnlineBackendType.PlayFab) SteamDoor.Open();
        }
    }

    [HarmonyPatch(typeof(ZNet), "CheckForIncommingServerConnections")]
    internal static class AcceptDoorConnections
    {
        private static void Postfix(ZNet __instance) => SteamDoor.AcceptPending(__instance);
    }

    [HarmonyPatch(typeof(ZNet), "StopAll")]
    internal static class CloseDoorWithServer
    {
        private static void Postfix() => SteamDoor.Close();
    }

    // Vanilla picks the handshake by the server-wide backend: Steam verifies a session ticket, crossplay reads
    // crossplay-only socket fields that a Steam socket lacks. Each peer gets the branch for its own socket.
    [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
    internal static class HandshakeByPeerSocket
    {
        private const int ExpectedBackendReads = 2;

        // HarmonyX re-runs every transpiler from the original method whenever any mod patches it again, so this can
        // turn false long after startup.
        internal static bool Applied { get; private set; }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            var reads = code.FindAll(IsBackendRead).Count;
            Applied = reads == ExpectedBackendReads;
            if (!Applied)
            {
                Plugin.Log.LogError($"RPC_PeerInfo reads the backend {reads} times, expected {ExpectedBackendReads}.");
                return code;
            }

            var patched = new List<CodeInstruction>(code.Count + reads);
            foreach (var instruction in code)
            {
                if (!IsBackendRead(instruction))
                {
                    patched.Add(instruction);
                    continue;
                }

                patched.Add(new CodeInstruction(OpCodes.Ldarg_1) { labels = instruction.labels, blocks = instruction.blocks });
                patched.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SteamDoor), nameof(SteamDoor.BackendFor))));
            }

            return patched;
        }

        private static void Postfix(ZNet __instance, ZRpc __0) => SteamDoor.AnnounceIfJoined(__instance, __0);

        private static bool IsBackendRead(CodeInstruction instruction) =>
            instruction.opcode == OpCodes.Ldsfld && Equals(instruction.operand, GameHooks.OnlineBackend);
    }

    // Player list, player history, display names, bans and admin checks all derive a player's platform ID from the
    // host name, and a crossplay server expects "<platform>_<id>"; a Steam socket's bare SteamID fails to parse.
    [HarmonyPatch(typeof(ZSteamSocket), nameof(ZSteamSocket.GetHostName))]
    internal static class SteamHostNameWithPlatform
    {
        private static void Postfix(ref string __result)
        {
            if (SteamDoor.IsCrossplayServer) __result = "Steam_" + __result;
        }
    }

    // A PC client that joins by IP first looks the exact "ip:port" up in the crossplay listings and only uses Steam
    // when nothing matches, so the listing names the next port. An empty address would not do: vanilla treats it as
    // "public IP not known yet" and re-sends the lobby every frame until PlayFab throttles the server.
    [HarmonyPatch(typeof(ZPlayFabMatchmaking), "GetServerIPAndPort")]
    internal static class ListCrossplayOnNextPort
    {
        private static void Postfix(ref string __result)
        {
            if (SteamDoor.IsOpen && Plugin.SendIpJoinsToSteamDoor.Value) __result = CrossplayAddress.NextPort(__result);
        }
    }
}
