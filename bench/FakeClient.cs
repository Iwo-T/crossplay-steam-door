using System;
using System.Runtime.InteropServices;
using Steamworks;
using UnityEngine;

namespace SyncBench
{
    // A loopback Steam connection made from inside the server process, sent and read the way ZSteamSocket does it.
    internal sealed class FakeSocket : ISocket
    {
        private const int Reliable = 8;

        private readonly IntPtr[] incoming = new IntPtr[1];
        private HSteamNetConnection connection;
        private bool gotData;
        private int traced;

        internal long BytesIn;

        internal FakeSocket(ushort port)
        {
            var address = new SteamNetworkingIPAddr();
            address.Clear();
            address.SetIPv4(0x7F000001u, port);
            connection = SteamGameServerNetworkingSockets.ConnectByIPAddress(ref address, 0, null);
        }

        internal ESteamNetworkingConnectionState State
        {
            get
            {
                if (connection == HSteamNetConnection.Invalid) return ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_None;
                return SteamGameServerNetworkingSockets.GetConnectionInfo(connection, out var info)
                    ? info.m_eState
                    : ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_None;
            }
        }

        public bool IsConnected() => State == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected;

        public void Send(ZPackage pkg)
        {
            if (pkg.Size() == 0 || !IsConnected()) return;

            var data = pkg.GetArray();
            if (traced++ < 0) Bench.L.LogWarning($"BENCH trace send {data.Length} bytes: {BitConverter.ToString(data, 0, Math.Min(24, data.Length))}");
            var buffer = Marshal.AllocHGlobal(data.Length);
            try
            {
                Marshal.Copy(data, 0, buffer, data.Length);
                SteamGameServerNetworkingSockets.SendMessageToConnection(connection, buffer, (uint)data.Length, Reliable, out _);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public ZPackage Recv()
        {
            if (!IsConnected()) return null;
            if (SteamGameServerNetworkingSockets.ReceiveMessagesOnConnection(connection, incoming, 1) != 1) return null;

            var message = Marshal.PtrToStructure<SteamNetworkingMessage_t>(incoming[0]);
            var data = new byte[message.m_cbSize];
            Marshal.Copy(message.m_pData, data, 0, message.m_cbSize);
            SteamNetworkingMessage_t.Release(incoming[0]);
            BytesIn += data.Length;
            gotData = true;
            if (traced++ < 0) Bench.L.LogWarning($"BENCH trace recv {data.Length} bytes: {BitConverter.ToString(data, 0, Math.Min(24, data.Length))}");
            return new ZPackage(data);
        }

        public void Close()
        {
            if (connection == HSteamNetConnection.Invalid) return;
            SteamGameServerNetworkingSockets.CloseConnection(connection, 0, "bench", false);
            connection = HSteamNetConnection.Invalid;
        }

        public void Dispose() => Close();
        public int GetSendQueueSize() => 0;
        public int GetCurrentSendRate() => 0;
        public bool IsHost() => false;
        public string GetEndPointString() => "bench";
        public string GetHostName() => "bench";
        public ISocket Accept() => null;
        public int GetHostPort() => 0;
        public bool Flush() => true;
        public void VersionMatch() { }

        public bool GotNewData()
        {
            var got = gotData;
            gotData = false;
            return got;
        }

        public void GetAndResetStats(out int totalSent, out int totalRecv)
        {
            totalSent = 0;
            totalRecv = 0;
        }

        public void GetConnectionQuality(out float localQuality, out float remoteQuality, out int ping, out float outByteSec, out float inByteSec)
        {
            localQuality = remoteQuality = 1f;
            ping = 0;
            outByteSec = inByteSec = 0f;
        }
    }

    // Speaks just enough of the join handshake to become a player the server keeps in sync, then only listens.
    internal sealed class FakeClient
    {
        internal static readonly byte[] Ticket = { 0xB3, 0x4C, 0x11, 0x7E, 0x5A, 0xF0, 0x0D, 0x99, 0x42, 0x42, 0x10, 0x01, 0xDE, 0xAD, 0xBE, 0x4F };

        private readonly FakeSocket socket;
        private readonly ZRpc rpc;
        private bool greeted;
        private float nextRefPos;

        internal readonly long Uid;
        internal readonly string Name;
        internal Vector3 RefPos;
        internal bool Joined;
        internal string Failure;
        internal long ZdoBytes;
        internal int ZdoPackages;

        internal long BytesIn => socket.BytesIn;

        internal FakeClient(ushort port, long uid, string name, Vector3 refPos)
        {
            Uid = uid;
            Name = name;
            RefPos = refPos;
            socket = new FakeSocket(port);
            rpc = new ZRpc(socket);
            rpc.Register<bool, string>("ClientHandshake", OnClientHandshake);
            rpc.Register<int>("Error", (_, code) => Failure = "server error " + code);
            rpc.Register("Disconnect", _ => Failure = "disconnected by the server");
            rpc.Register<ZPackage>("PeerInfo", (_, pkg) => Joined = true);
            rpc.Register<ZPackage>("ZDOData", (_, pkg) =>
            {
                ZdoBytes += pkg.Size();
                ZdoPackages++;
            });
            rpc.Register<ZPackage>("RoutedRPC", (_, pkg) => { });
            rpc.Register<ZPackage>("PlayerList", (_, pkg) => { });
            rpc.Register<ZPackage>("AdminList", (_, pkg) => { });
            rpc.Register<string>("RemotePrint", (_, text) => { });
            rpc.Register<double>("NetTime", (_, time) => { });
            rpc.Register<ZPackage>("RPC_ValidatedSimulationDistance", (_, pkg) => { });
            rpc.Register("SavePlayerProfile", _ => { });
            rpc.Register("Kicked", _ => Failure = "kicked");
        }

        internal string State => socket.State.ToString().Replace("k_ESteamNetworkingConnectionState_", "");

        internal void Update(float dt)
        {
            if (!socket.IsConnected()) return;

            if (!greeted)
            {
                rpc.Invoke("ServerHandshake", "");
                greeted = true;
            }
            rpc.Update(dt);

            if (!Joined || Time.unscaledTime < nextRefPos) return;
            var data = new ZPackage();
            data.Write(RefPos);
            data.Write(false);
            data.Write(0);
            rpc.Invoke("ServerSyncedPlayerData", data);
            nextRefPos = Time.unscaledTime + 1f;
        }

        internal void Leave() => socket.Close();

        private void OnClientHandshake(ZRpc sender, bool needPassword, string salt)
        {
            var pkg = new ZPackage();
            pkg.Write(Uid);
            pkg.Write(global::Version.CurrentVersion.ToString());
            pkg.Write(Bench.NetworkVersion);
            pkg.Write(RefPos);
            pkg.Write(Name);
            pkg.Write("");
            ZNet.instance.GetSyncedSimulationDistance().Serialize(ref pkg);
            pkg.Write(Bench.ServerPasswordHash());
            pkg.Write("");
            pkg.Write(Ticket);
            sender.Invoke("PeerInfo", pkg);
        }
    }
}
