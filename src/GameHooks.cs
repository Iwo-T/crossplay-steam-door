using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Steamworks;

namespace CrossplaySteamDoor
{
    // Every private member and patch target is looked up before anything is patched, so a game update that renames
    // one leaves the server vanilla instead of half-patched.
    internal static class GameHooks
    {
        internal static FieldInfo OnlineBackend { get; private set; }

        private static MethodInfo onNewConnection;
        private static MethodInfo getPeerByRpc;
        private static FieldInfo listenSocket;
        private static FieldInfo steamDataPort;

        internal static List<string> Resolve()
        {
            var missing = new List<string>();
            OnlineBackend = Require(AccessTools.Field(typeof(ZNet), nameof(ZNet.m_onlineBackend)), "ZNet.m_onlineBackend", missing);
            onNewConnection = Require(AccessTools.Method(typeof(ZNet), "OnNewConnection", new[] { typeof(ZNetPeer) }),
                "ZNet.OnNewConnection", missing);
            getPeerByRpc = Require(AccessTools.Method(typeof(ZNet), "GetPeer", new[] { typeof(ZRpc) }), "ZNet.GetPeer(ZRpc)", missing);
            listenSocket = Require(AccessTools.Field(typeof(ZSteamSocket), "m_listenSocket"), "ZSteamSocket.m_listenSocket", missing);
            steamDataPort = Require(AccessTools.Field(typeof(ZSteamSocket), "m_steamDataPort"), "ZSteamSocket.m_steamDataPort", missing);

            Require(AccessTools.Method(typeof(ZNet), nameof(ZNet.OpenServer)), "ZNet.OpenServer", missing);
            Require(AccessTools.Method(typeof(ZNet), "CheckForIncommingServerConnections"), "ZNet.CheckForIncommingServerConnections", missing);
            Require(AccessTools.Method(typeof(ZNet), "StopAll"), "ZNet.StopAll", missing);
            Require(AccessTools.Method(typeof(ZNet), "RPC_PeerInfo", new[] { typeof(ZRpc), typeof(ZPackage) }), "ZNet.RPC_PeerInfo", missing);
            Require(AccessTools.Method(typeof(ZSteamSocket), nameof(ZSteamSocket.GetHostName)), "ZSteamSocket.GetHostName", missing);
            Require(AccessTools.Method(typeof(ZPlayFabMatchmaking), "GetServerIPAndPort"), "ZPlayFabMatchmaking.GetServerIPAndPort", missing);
            return missing;
        }

        internal static void OnNewConnection(ZNet net, ZNetPeer peer) => onNewConnection.Invoke(net, new object[] { peer });

        internal static ZNetPeer GetPeer(ZNet net, ZRpc rpc) => (ZNetPeer)getPeerByRpc.Invoke(net, new object[] { rpc });

        internal static bool IsListening(ZSteamSocket socket) =>
            (HSteamListenSocket)listenSocket.GetValue(socket) != HSteamListenSocket.Invalid;

        internal static object SteamDataPort() => steamDataPort.GetValue(null);

        private static T Require<T>(T member, string name, List<string> missing) where T : MemberInfo
        {
            if (member == null) missing.Add(name);
            return member;
        }
    }
}
