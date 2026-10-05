using System;

namespace CrossplaySteamDoor
{
    public static class SteamDoor
    {
        private static ZSteamSocket listener;

        internal static bool IsOpen => listener != null;

        // A crossplay server opens no Steam sockets of its own, so every Steam socket on it belongs to the door.
        internal static bool IsCrossplayServer =>
            ZNet.m_onlineBackend == OnlineBackendType.PlayFab && ZNet.instance != null && ZNet.instance.IsServer();

        // Called from RPC_PeerInfo in place of its two reads of the server-wide backend: a Steam-door peer gets the
        // Steam session-ticket branch, everyone else the branch vanilla would have taken.
        public static OnlineBackendType BackendFor(ZRpc rpc) =>
            ZNet.m_onlineBackend == OnlineBackendType.PlayFab && rpc?.GetSocket() is ZSteamSocket
                ? OnlineBackendType.Steamworks
                : ZNet.m_onlineBackend;

        internal static void Open()
        {
            if (listener != null) return;

            ZSteamSocket socket = null;
            try
            {
                socket = new ZSteamSocket();
                if (socket.StartHost() && GameHooks.IsListening(socket))
                {
                    WorldSync.RaiseSteamSendRate();
                    Plugin.Log.LogInfo($"Steam door open on UDP port {GameHooks.SteamDataPort()}: a Steam PC that joins by IP " +
                                       "connects directly, without the crossplay relay.");
                    listener = socket;
                    return;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Steam door failed to open: {e}");
            }

            TryDispose(socket);
            Plugin.Log.LogError("Could not open the Steam door; everyone keeps using crossplay.");
        }

        internal static void AcceptPending(ZNet net)
        {
            if (listener == null) return;
            if (!HandshakeByPeerSocket.Applied)
            {
                Plugin.Log.LogError("Another mod rebuilt the join handshake without the Steam door's change; " +
                                    "closing the door so Steam PCs do not hang. Everyone keeps using crossplay.");
                Close();
                return;
            }

            ISocket socket = null;
            try
            {
                socket = listener.Accept();
                if (socket == null) return;
                if (!socket.IsConnected())
                {
                    socket.Dispose();
                    return;
                }

                Plugin.Log.LogInfo($"Direct Steam connection from {socket.GetHostName()}");
                GameHooks.OnNewConnection(net, new ZNetPeer(socket, false));
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Dropped a Steam door connection: {e}");
                TryDispose(socket);
            }
        }

        internal static void AnnounceIfJoined(ZNet net, ZRpc rpc)
        {
            if (!IsOpen || !(rpc?.GetSocket() is ZSteamSocket socket)) return;

            try
            {
                var peer = GameHooks.GetPeer(net, rpc);
                if (peer != null && peer.IsReady())
                {
                    Plugin.Log.LogInfo($"{peer.m_playerName} joined through the Steam door as {socket.GetHostName()}");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Could not report a Steam door join: {e.Message}");
            }
        }

        internal static void Close()
        {
            if (listener == null) return;

            TryDispose(listener);
            listener = null;
            Plugin.Log.LogInfo("Steam door closed.");
        }

        private static void TryDispose(ISocket socket)
        {
            try
            {
                socket?.Dispose();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Could not close a Steam socket: {e.Message}");
            }
        }
    }
}
