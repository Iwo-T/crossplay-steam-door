using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Steamworks;
using UnityEngine;

namespace SyncBench
{
    // Scratch load test for the local test server only: simulated players on loopback Steam connections, a synthetic
    // base, steady changes to it, and probes that time how long an ownership change takes to be sent to each player.
    [BepInPlugin("scratch.syncbench", "Sync Bench", "0.0.1")]
    public sealed class Bench : BaseUnityPlugin
    {
        internal const uint NetworkVersion = 40;
        private const string DoorGuid = "iwo.valheim.crossplaysteamdoor";
        // Any id but 0 marks a piece as built by a player.
        private const long SomeBuilder = 4242L;

        private sealed class PeerTurns
        {
            internal int Turns;
            internal int Skipped;
            internal long Sent;
            internal long WaitingSum;
            internal int WaitingTurns;
            internal int WaitingLast;
            internal int WaitingMax;
        }

        private sealed class Arrival
        {
            internal string Name;
            internal float Start;
            internal bool HadBacklog;
            internal bool GotAnItem;
            internal long BytesAtStart;
        }

        private sealed class Probe
        {
            internal ZDO Zdo;
            internal ushort Revision;
            internal float ChangedAt;
            internal readonly HashSet<long> Told = new HashSet<long>();
        }

        internal static ManualLogSource L;
        internal static int WaitingThisTurn = -1;

        private static Bench instance;
        private static FieldInfo zdoManPeers, zdoPeerNet, zdoPeerZdos, knownOwnerRevision, sentCounter, serverPassword;
        private static MethodInfo createWithId;

        private readonly List<FakeClient> fakes = new List<FakeClient>();
        private readonly List<ZDOID> baseIds = new List<ZDOID>();
        private readonly List<ZDOID> drops = new List<ZDOID>();
        private float dropStart;
        private int dropTotal, dropLeftLast;
        private readonly List<Probe> probes = new List<Probe>();
        private readonly Dictionary<long, PeerTurns> turns = new Dictionary<long, PeerTurns>();
        private readonly Dictionary<long, long> bytesAtWindowStart = new Dictionary<long, long>();
        private readonly List<float> latencies = new List<float>();
        private readonly Dictionary<long, Arrival> arrivals = new Dictionary<long, Arrival>();

        private string inbox;
        private float nextInbox;
        private string label = "idle";
        private float churnPerSecond, churnDebt;
        private float churnHealthMin = 1f, churnHealthMax = 100f;
        private float probeEvery, nextProbeWave;
        private int missedProbes;
        private float reportEvery, windowStart;
        private int frames;
        private float slowestFrame;
        private long sendTicks, slowestSendTicks;
        private int sendCalls;
        private int sentBefore;
        private long nextUid = 7000000001L;
        private bool orderCheck;
        private int orderTurns, orderRestFirst, orderViolations;

        private void Awake()
        {
            instance = this;
            L = Logger;
            inbox = Path.Combine(Paths.BepInExRootPath, "bench");
            Directory.CreateDirectory(inbox);

            var zdoPeer = AccessTools.Inner(typeof(ZDOMan), "ZDOPeer");
            zdoManPeers = AccessTools.Field(typeof(ZDOMan), "m_peers");
            zdoPeerNet = AccessTools.Field(zdoPeer, "m_peer");
            zdoPeerZdos = AccessTools.Field(zdoPeer, "m_zdos");
            knownOwnerRevision = AccessTools.Field(zdoPeerZdos.FieldType.GetGenericArguments()[1], "m_ownerRevision");
            sentCounter = AccessTools.Field(typeof(ZDOMan), "m_zdosSent");
            serverPassword = AccessTools.Field(typeof(ZNet), "m_serverPassword");
            createWithId = AccessTools.Method(typeof(ZDOMan), "CreateNewZDO", new[] { typeof(ZDOID), typeof(Vector3), typeof(int) });
            new Harmony("scratch.syncbench").PatchAll(typeof(Bench).Assembly);
        }

        internal static string ServerPasswordHash() => (string)serverPassword.GetValue(null) ?? "";

        private void Update()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || ZDOMan.instance == null || ZNetScene.instance == null) return;

            var now = Time.unscaledTime;
            var dt = Time.unscaledDeltaTime;
            frames++;
            if (dt > slowestFrame) slowestFrame = dt;

            if (now >= nextInbox)
            {
                nextInbox = now + 0.5f;
                ReadInbox();
            }
            foreach (var fake in fakes) fake.Update(dt);
            Churn(dt);
            UpdateProbes(now);
            WatchDrops(now);
            if (reportEvery > 0f && now - windowStart >= reportEvery) Report(now);
        }

        private void ReadInbox()
        {
            var files = Directory.GetFiles(inbox, "*.json");
            Array.Sort(files, StringComparer.Ordinal);
            foreach (var file in files)
            {
                JObject command;
                try
                {
                    command = JObject.Parse(File.ReadAllText(file));
                }
                catch
                {
                    continue;
                }
                File.Delete(file);
                try
                {
                    Run(command);
                }
                catch (Exception e)
                {
                    L.LogError("BENCH failed: " + e);
                }
            }
        }

        private void Run(JObject c)
        {
            var at = new Vector3(c.Value<float?>("x") ?? -207f, 40f, c.Value<float?>("z") ?? 1f);
            if (c.Value<string>("at") == "player")
            {
                var body = RealPlayerBody() ?? throw new InvalidOperationException("no real player with a body online");
                at = body.GetPosition() + new Vector3(c.Value<float?>("dx") ?? 0f, 0f, c.Value<float?>("dz") ?? 0f);
            }
            switch (c.Value<string>("cmd"))
            {
                case "carpet":
                    SpawnCarpet(c.Value<int?>("side") ?? 50, c.Value<float?>("spacing") ?? 2f, at, c.Value<float?>("sink") ?? 0.2f);
                    churnHealthMin = 150f;
                    churnHealthMax = 400f;
                    break;
                case "drop":
                    Drop(c.Value<int?>("count") ?? 10, c.Value<float?>("radius") ?? 1.2f, at, c.Value<int?>("owner") ?? -1, c.Value<string>("item") ?? "Coal");
                    break;
                case "fakes":
                    for (var i = 0; i < (c.Value<int?>("count") ?? 5); i++) Connect(at);
                    break;
                case "base":
                    SpawnBase(c.Value<int?>("count") ?? 20000, c.Value<float?>("radius") ?? 60f, at, c.Value<string>("prefab") ?? "wood_floor");
                    break;
                case "probes":
                    SpawnProbes(c.Value<int?>("count") ?? 20, at);
                    probeEvery = c.Value<float?>("every") ?? 10f;
                    nextProbeWave = Time.unscaledTime + 2f;
                    break;
                case "churn":
                    churnPerSecond = c.Value<float?>("perSecond") ?? 0f;
                    L.LogWarning($"BENCH churn {churnPerSecond}/s over {baseIds.Count} base objects");
                    break;
                case "set":
                    Set(c);
                    break;
                case "move":
                    Move(c.Value<int?>("fake") ?? -1, at, c.Value<bool?>("time") ?? false);
                    if (c.Value<bool?>("timeDrops") ?? false) dropStart = Time.unscaledTime;
                    break;
                case "dropinfo":
                    foreach (var id in drops)
                    {
                        var zdo = ZDOMan.instance.GetZDO(id);
                        if (zdo != null) L.LogWarning($"BENCH drop {id}: owner {zdo.GetOwner()} ownerRev {zdo.OwnerRevision} pos {zdo.GetPosition()}");
                    }
                    break;
                case "arrive":
                    Time_(Connect(at));
                    break;
                case "leave":
                    Leave(c.Value<int?>("fake") ?? -1);
                    break;
                case "label":
                    label = c.Value<string>("text") ?? "";
                    StartWindow(Time.unscaledTime);
                    L.LogWarning($"BENCH label {label}");
                    break;
                case "report":
                    reportEvery = c.Value<float?>("every") ?? 5f;
                    StartWindow(Time.unscaledTime);
                    break;
                case "status":
                    Status();
                    break;
                case "bodies":
                    GiveBodies();
                    break;
                case "inbed":
                    PutInBed(c.Value<int?>("count") ?? 0);
                    break;
                case "kick":
                    ZNet.instance.Kick(c.Value<string>("name"));
                    L.LogWarning($"BENCH kicked {c.Value<string>("name")}");
                    break;
                case "clear":
                    Clear();
                    break;
                case "quit":
                    L.LogWarning("BENCH quitting");
                    foreach (var fake in fakes) fake.Leave();
                    Application.Quit();
                    break;
                default:
                    L.LogWarning("BENCH unknown " + c);
                    break;
            }
        }

        private FakeClient Connect(Vector3 at)
        {
            var fake = new FakeClient(ServerPort(), nextUid++, "Fake" + (nextUid - 7000000001L), at);
            fakes.Add(fake);
            L.LogWarning($"BENCH {fake.Name} connecting as uid {fake.Uid} at {at}");
            return fake;
        }

        private void Time_(FakeClient fake) =>
            arrivals[fake.Uid] = new Arrival { Name = fake.Name, Start = Time.unscaledTime, BytesAtStart = fake.ZdoBytes };

        private static ushort ServerPort()
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "-port" && ushort.TryParse(args[i + 1], out var port)) return port;
            }
            return 2456;
        }

        private void SpawnBase(int count, float radius, Vector3 centre, string prefabName)
        {
            var prefab = ZNetScene.instance.GetPrefab(prefabName);
            var view = prefab.GetComponent<ZNetView>();
            var hash = prefabName.GetStableHashCode();
            for (var i = 0; i < count; i++)
            {
                var offset = UnityEngine.Random.insideUnitCircle * radius;
                var zdo = ZDOMan.instance.CreateNewZDO(centre + new Vector3(offset.x, 0f, offset.y), hash);
                zdo.Persistent = view.m_persistent;
                zdo.Type = view.m_type;
                zdo.Distant = view.m_distant;
                zdo.SetPrefab(hash);
                zdo.SetRotation(Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f));
                zdo.Set(ZDOVars.s_health, 100f);
                zdo.Set(ZDOVars.s_creator, SomeBuilder);
                zdo.Set(ZDOVars.s_support, 80f);
                zdo.SetOwnerInternal(0L);
                baseIds.Add(zdo.m_uid);
            }
            L.LogWarning($"BENCH base: {count} x {prefabName} ({view.m_type}) within {radius} m of {centre}; {baseIds.Count} base objects, {ZDOMan.instance.NrOfObjects()} in the world");
        }

        // A simulated player has no character. For rules that count characters (sleeping), each gets a bare one where
        // it stands, far from the real player so that no game has to draw it.
        private void GiveBodies()
        {
            var hash = "Player".GetStableHashCode();
            var given = 0;
            foreach (var peer in ZNet.instance.GetPeers())
            {
                var fake = fakes.Find(f => f.Uid == peer.m_uid);
                if (fake == null || !peer.m_characterID.IsNone()) continue;

                // A game makes its own character, so the character's id carries that game's id. Chat is addressed to
                // that id: with the server's id in it, a real player's chat copies would stop at the server.
                var body = (ZDO)createWithId.Invoke(ZDOMan.instance, new object[] { new ZDOID(peer.m_uid, 1), fake.RefPos, hash });
                body.Persistent = false;
                body.Type = ZDO.ObjectType.Prioritized;
                body.SetPrefab(hash);
                body.SetOwner(peer.m_uid);
                peer.m_characterID = body.m_uid;
                given++;
            }
            L.LogWarning($"BENCH bodies: {given} simulated players now have a character");
        }

        private void PutInBed(int count)
        {
            var inBed = 0;
            foreach (var peer in ZNet.instance.GetPeers())
            {
                if (!fakes.Exists(f => f.Uid == peer.m_uid) || peer.m_characterID.IsNone()) continue;
                var body = ZDOMan.instance.GetZDO(peer.m_characterID);
                if (body == null) continue;
                body.Set(ZDOVars.s_inBed, inBed < count);
                if (inBed < count) inBed++;
            }
            L.LogWarning($"BENCH inbed: {inBed} simulated players are in bed, {ZNet.instance.GetAllCharacterZDOS().Count} characters online");
        }

        private ZDO RealPlayerBody()
        {
            foreach (var peer in ZNet.instance.GetPeers())
            {
                if (!peer.IsReady() || peer.m_characterID.IsNone() || fakes.Exists(f => f.Uid == peer.m_uid)) continue;
                var body = ZDOMan.instance.GetZDO(peer.m_characterID);
                if (body != null) return body;
            }
            return null;
        }

        // Floor pieces laid on the ground, sunk a little so every one touches the terrain and counts as supported.
        private void SpawnCarpet(int side, float spacing, Vector3 centre, float sink)
        {
            var view = ZNetScene.instance.GetPrefab("wood_floor").GetComponent<ZNetView>();
            var hash = "wood_floor".GetStableHashCode();
            for (var ix = 0; ix < side; ix++)
            {
                for (var iz = 0; iz < side; iz++)
                {
                    var position = centre + new Vector3((ix - side / 2f) * spacing, 0f, (iz - side / 2f) * spacing);
                    position.y = WorldGenerator.instance.GetHeight(position.x, position.z) - sink;
                    var zdo = ZDOMan.instance.CreateNewZDO(position, hash);
                    zdo.Persistent = view.m_persistent;
                    zdo.Type = view.m_type;
                    zdo.Distant = view.m_distant;
                    zdo.SetPrefab(hash);
                    zdo.Set(ZDOVars.s_health, 400f);
                    zdo.Set(ZDOVars.s_creator, SomeBuilder);
                    zdo.SetOwnerInternal(0L);
                    baseIds.Add(zdo.m_uid);
                }
            }
            L.LogWarning($"BENCH carpet: {side * side} x wood_floor around {centre}, {baseIds.Count} base objects");
        }

        // Items on the ground next to a player. With an owner they wait for that owner to hand them over.
        private void Drop(int count, float radius, Vector3 centre, int ownerFake, string item)
        {
            var view = ZNetScene.instance.GetPrefab(item).GetComponent<ZNetView>();
            var hash = item.GetStableHashCode();
            var owner = ownerFake >= 0 && ownerFake < fakes.Count ? fakes[ownerFake].Uid : 0L;
            drops.Clear();
            for (var i = 0; i < count; i++)
            {
                var offset = UnityEngine.Random.insideUnitCircle * radius;
                var zdo = ZDOMan.instance.CreateNewZDO(centre + new Vector3(offset.x, 0.6f, offset.y), hash);
                zdo.Persistent = view.m_persistent;
                zdo.Type = view.m_type;
                zdo.Distant = view.m_distant;
                zdo.SetPrefab(hash);
                zdo.Set(ZDOVars.s_spawnTime, ZNet.instance.GetTime().Ticks);
                zdo.Set(ZDOVars.s_stack, 1);
                zdo.SetOwner(owner);
                drops.Add(zdo.m_uid);
            }
            dropStart = Time.unscaledTime;
            dropTotal = dropLeftLast = count;
            L.LogWarning($"BENCH drop: {count} x {item} at {centre}, owner {(owner == 0 ? "nobody" : fakes[ownerFake].Name)}");
        }

        private void WatchDrops(float now)
        {
            if (dropTotal == 0) return;

            var left = 0;
            long owner = 0;
            foreach (var id in drops)
            {
                var zdo = ZDOMan.instance.GetZDO(id);
                if (zdo == null) continue;
                left++;
                owner = zdo.GetOwner();
            }
            if (left == dropLeftLast) return;

            dropLeftLast = left;
            L.LogWarning($"BENCH drop: {dropTotal - left} of {dropTotal} picked up after {now - dropStart:0.0} s");
            if (left == 0) dropTotal = 0;
        }

        private void SpawnProbes(int count, Vector3 centre)
        {
            var view = ZNetScene.instance.GetPrefab("Coal").GetComponent<ZNetView>();
            var hash = "Coal".GetStableHashCode();
            for (var i = 0; i < count; i++)
            {
                var offset = UnityEngine.Random.insideUnitCircle * 8f;
                var zdo = ZDOMan.instance.CreateNewZDO(centre + new Vector3(offset.x, 0f, offset.y), hash);
                zdo.Persistent = view.m_persistent;
                zdo.Type = view.m_type;
                zdo.Distant = view.m_distant;
                zdo.SetPrefab(hash);
                zdo.SetOwnerInternal(0L);
                probes.Add(new Probe { Zdo = zdo, ChangedAt = -1f });
            }
            L.LogWarning($"BENCH probes: {probes.Count} x Coal ({view.m_type})");
        }

        private void Set(JObject c)
        {
            var door = Chainloader.PluginInfos.TryGetValue(DoorGuid, out var info) ? info.Instance : null;
            var said = "";
            if (c["budgetKB"] != null && door != null)
            {
                door.Config[new ConfigDefinition("WorldSync", "SteamSendBudgetKB")].BoxedValue = c.Value<int>("budgetKB");
                said += $" budget {c.Value<int>("budgetKB")} KB";
            }
            if (c["rateKB"] != null)
            {
                var rate = c.Value<int>("rateKB") * 1024;
                var pinned = GCHandle.Alloc(rate, GCHandleType.Pinned);
                var max = SteamGameServerNetworkingUtils.SetConfigValue(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax,
                    ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero,
                    ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, pinned.AddrOfPinnedObject());
                var min = SteamGameServerNetworkingUtils.SetConfigValue(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin,
                    ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero,
                    ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, pinned.AddrOfPinnedObject());
                if (rate < 153600)
                {
                    max = SteamGameServerNetworkingUtils.SetConfigValue(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax,
                        ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero,
                        ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, pinned.AddrOfPinnedObject());
                }
                pinned.Free();
                said += $" rate {c.Value<int>("rateKB")} KB/s (max {max}, min {min})";
            }
            if (c["fair"] != null && door != null)
            {
                door.Config[new ConfigDefinition("WorldSync", "FairTurns")].BoxedValue = c.Value<bool>("fair");
                said += $" fair turns {c.Value<bool>("fair")}";
            }
            if (c["orderCheck"] != null)
            {
                orderCheck = c.Value<bool>("orderCheck");
                said += $" order check {orderCheck}";
            }
            if (c["fps"] != null)
            {
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = c.Value<int>("fps");
                said += $" fps {c.Value<int>("fps")}";
            }
            L.LogWarning("BENCH set" + said);
        }

        private void Move(int index, Vector3 to, bool time)
        {
            for (var i = 0; i < fakes.Count; i++)
            {
                if (index >= 0 && i != index) continue;
                fakes[i].RefPos = to;
                if (time) Time_(fakes[i]);
            }
            L.LogWarning($"BENCH moved {(index < 0 ? "all fakes" : fakes[index].Name)} to {to}");
        }

        private void Leave(int index)
        {
            for (var i = fakes.Count - 1; i >= 0; i--)
            {
                if (index >= 0 && i != index) continue;
                fakes[i].Leave();
                L.LogWarning($"BENCH {fakes[i].Name} left");
                fakes.RemoveAt(i);
            }
        }

        private void Status()
        {
            foreach (var fake in fakes)
            {
                L.LogWarning($"BENCH {fake.Name}: {fake.State} joined={fake.Joined} failure={fake.Failure ?? "-"} zdoKB={fake.ZdoBytes / 1024} packages={fake.ZdoPackages} at {fake.RefPos}");
            }
            var alive = 0;
            foreach (var id in baseIds)
            {
                if (ZDOMan.instance.GetZDO(id) != null) alive++;
            }
            foreach (var peer in ZNet.instance.GetPeers())
            {
                var body = peer.m_characterID.IsNone() ? null : ZDOMan.instance.GetZDO(peer.m_characterID);
                L.LogWarning($"BENCH peer {peer.m_playerName} uid {peer.m_uid} {peer.m_socket.GetType().Name} host {peer.m_socket.GetHostName()} ready={peer.IsReady()} refPos={peer.m_refPos} body={(body == null ? "none" : body.GetPosition().ToString())}");
            }
            L.LogWarning($"BENCH world: {ZDOMan.instance.NrOfObjects()} objects, {baseIds.Count} base ({alive} alive), {probes.Count} probes, fps target {Application.targetFrameRate}, peers {ZNet.instance.GetPeers().Count}");
        }

        private void Clear()
        {
            churnPerSecond = 0f;
            probeEvery = 0f;
            foreach (var id in baseIds) Destroy(ZDOMan.instance.GetZDO(id));
            foreach (var id in drops) Destroy(ZDOMan.instance.GetZDO(id));
            foreach (var probe in probes) Destroy(probe.Zdo);
            L.LogWarning($"BENCH cleared {baseIds.Count} base objects, {drops.Count} drops and {probes.Count} probes");
            baseIds.Clear();
            drops.Clear();
            probes.Clear();
        }

        private static void Destroy(ZDO zdo)
        {
            if (zdo == null) return;
            zdo.SetOwner(ZDOMan.GetSessionID());
            ZDOMan.instance.DestroyZDO(zdo);
        }

        // Stands in for a base on fire: every changed object has to be sent to every player again.
        private void Churn(float dt)
        {
            if (churnPerSecond <= 0f || baseIds.Count == 0) return;

            churnDebt += churnPerSecond * Mathf.Min(dt, 0.5f);
            while (churnDebt >= 1f && baseIds.Count > 0)
            {
                churnDebt -= 1f;
                var index = UnityEngine.Random.Range(0, baseIds.Count);
                var zdo = ZDOMan.instance.GetZDO(baseIds[index]);
                if (zdo == null)
                {
                    baseIds[index] = baseIds[baseIds.Count - 1];
                    baseIds.RemoveAt(baseIds.Count - 1);
                    continue;
                }
                zdo.Set(ZDOVars.s_health, UnityEngine.Random.Range(churnHealthMin, churnHealthMax));
            }
        }

        // An item changing hands: the moment every player has been sent the new owner is when they could pick it up.
        private void UpdateProbes(float now)
        {
            if (probes.Count == 0 || probeEvery <= 0f) return;

            var owners = new List<long>();
            foreach (var peer in ZNet.instance.GetPeers())
            {
                if (peer.IsReady()) owners.Add(peer.m_uid);
            }
            if (owners.Count == 0) return;

            foreach (var probe in probes)
            {
                if (probe.ChangedAt < 0f) continue;
                foreach (var zdoPeer in (IList)zdoManPeers.GetValue(ZDOMan.instance))
                {
                    var uid = ((ZNetPeer)zdoPeerNet.GetValue(zdoPeer)).m_uid;
                    if (probe.Told.Contains(uid)) continue;

                    var known = (IDictionary)zdoPeerZdos.GetValue(zdoPeer);
                    if (!known.Contains(probe.Zdo.m_uid)) continue;
                    if ((ushort)knownOwnerRevision.GetValue(known[probe.Zdo.m_uid]) < probe.Revision) continue;

                    probe.Told.Add(uid);
                    latencies.Add(now - probe.ChangedAt);
                    if (arrivals.TryGetValue(uid, out var arrival) && !arrival.GotAnItem)
                    {
                        arrival.GotAnItem = true;
                        var unsent = 0;
                        foreach (var id in baseIds)
                        {
                            var building = ZDOMan.instance.GetZDO(id);
                            if (building != null && !known.Contains(id)) unsent++;
                        }
                        L.LogWarning($"BENCH order: {arrival.Name} was sent its first item {now - arrival.Start:0.0} s after arriving; base objects still unsent at that moment: {unsent} of {baseIds.Count}");
                    }
                }
            }

            if (now < nextProbeWave) return;
            nextProbeWave = now + probeEvery;
            for (var i = 0; i < probes.Count; i++)
            {
                var probe = probes[i];
                if (probe.ChangedAt >= 0f) missedProbes += Math.Max(0, owners.Count - probe.Told.Count);
                probe.Zdo.SetOwner(owners[(i + Time.frameCount) % owners.Count]);
                if (probe.Zdo.OwnerRevision == probe.Revision) probe.Zdo.SetOwner(owners[(i + Time.frameCount + 1) % owners.Count]);
                probe.Revision = probe.Zdo.OwnerRevision;
                probe.ChangedAt = now;
                probe.Told.Clear();
            }
        }

        // What must hold whatever the door plugin does to the order: nothing that is not solid goes to a player before
        // a solid object that player has never been sent. Runs after the door's own change to the list.
        internal static void CheckOrder(List<ZDO> objects, object zdoPeer)
        {
            var bench = instance;
            if (bench == null || !bench.orderCheck || zdoPeer == null) return;

            var known = (IDictionary)zdoPeerZdos.GetValue(zdoPeer);
            var restSeen = false;
            var restBeforeSolid = false;
            var violation = false;
            foreach (var zdo in objects)
            {
                if (zdo.Type == ZDO.ObjectType.Default)
                {
                    restSeen = true;
                    continue;
                }
                if (zdo.Type != ZDO.ObjectType.Solid || !restSeen) continue;

                restBeforeSolid = true;
                if (!known.Contains(zdo.m_uid)) violation = true;
            }
            bench.orderTurns++;
            if (restBeforeSolid) bench.orderRestFirst++;
            if (violation)
            {
                bench.orderViolations++;
                if (bench.orderViolations <= 3) L.LogError("BENCH ORDER VIOLATION: an object that is not solid is ahead of a solid object this player was never sent");
            }
        }

        internal static void TurnStarts()
        {
            WaitingThisTurn = -1;
            instance.sentBefore = (int)sentCounter.GetValue(ZDOMan.instance);
        }

        internal static void TurnEnds(object zdoPeer, long ticks)
        {
            var bench = instance;
            bench.sendCalls++;
            bench.sendTicks += ticks;
            if (ticks > bench.slowestSendTicks) bench.slowestSendTicks = ticks;

            var peer = (ZNetPeer)zdoPeerNet.GetValue(zdoPeer);
            if (peer == null) return;
            if (!bench.turns.TryGetValue(peer.m_uid, out var line)) bench.turns[peer.m_uid] = line = new PeerTurns();

            var sent = (int)sentCounter.GetValue(ZDOMan.instance) - bench.sentBefore;
            line.Turns++;
            line.Sent += sent;
            if (WaitingThisTurn < 0)
            {
                line.Skipped++;
                return;
            }
            bench.TrackArrival(peer.m_uid, WaitingThisTurn, sent);
            line.WaitingTurns++;
            line.WaitingSum += WaitingThisTurn;
            line.WaitingLast = WaitingThisTurn;
            if (WaitingThisTurn > line.WaitingMax) line.WaitingMax = WaitingThisTurn;
        }

        private void TrackArrival(long uid, int waiting, int sent)
        {
            if (!arrivals.TryGetValue(uid, out var arrival)) return;

            var now = Time.unscaledTime;
            if (waiting > 0) arrival.HadBacklog = true;
            if (arrival.HadBacklog && sent >= waiting)
            {
                var fake = fakes.Find(f => f.Uid == uid);
                var kb = fake == null ? 0 : (fake.ZdoBytes - arrival.BytesAtStart) / 1024;
                L.LogWarning($"BENCH arrival: {arrival.Name} had everything after {now - arrival.Start:0.0} s ({kb} KB)");
                arrivals.Remove(uid);
            }
            else if (!arrival.HadBacklog && now - arrival.Start > 20f)
            {
                L.LogWarning($"BENCH arrival: {arrival.Name} had nothing to catch up on");
                arrivals.Remove(uid);
            }
        }

        private void StartWindow(float now)
        {
            windowStart = now;
            frames = 0;
            slowestFrame = 0f;
            sendTicks = slowestSendTicks = 0;
            sendCalls = 0;
            turns.Clear();
            latencies.Clear();
            missedProbes = 0;
            bytesAtWindowStart.Clear();
            foreach (var fake in fakes) bytesAtWindowStart[fake.Uid] = fake.ZdoBytes;
        }

        private void Report(float now)
        {
            var seconds = now - windowStart;
            var toMs = 1000.0 / Stopwatch.Frequency;

            int players = 0, turnCount = 0, skipped = 0, waitingMax = 0;
            long sent = 0, waitingLast = 0;
            double kbSum = 0, kbMin = double.MaxValue;
            foreach (var fake in fakes)
            {
                if (!fake.Joined) continue;
                players++;
                var kb = (fake.ZdoBytes - (bytesAtWindowStart.TryGetValue(fake.Uid, out var before) ? before : 0)) / 1024.0 / seconds;
                kbSum += kb;
                if (kb < kbMin) kbMin = kb;
                if (!turns.TryGetValue(fake.Uid, out var line)) continue;
                turnCount += line.Turns;
                skipped += line.Skipped;
                sent += line.Sent;
                waitingLast += line.WaitingLast;
                if (line.WaitingMax > waitingMax) waitingMax = line.WaitingMax;
            }
            if (players == 0) players = 1;

            latencies.Sort();
            var waitingProbes = 0;
            var oldest = 0f;
            foreach (var probe in probes)
            {
                if (probe.ChangedAt < 0f || probe.Told.Count >= players) continue;
                waitingProbes += players - probe.Told.Count;
                oldest = Mathf.Max(oldest, now - probe.ChangedAt);
            }
            var probeText = probes.Count == 0
                ? "no probes"
                : $"owner changes told {latencies.Count} (median {Percentile(0.5f):0.00} s, p95 {Percentile(0.95f):0.00} s, worst {Percentile(1f):0.00} s), " +
                  $"still untold {waitingProbes} (oldest {oldest:0.0} s), never told before the next change {missedProbes}";

            var real = "";
            foreach (var peer in ZNet.instance.GetPeers())
            {
                if (!peer.IsReady() || fakes.Exists(f => f.Uid == peer.m_uid) || !turns.TryGetValue(peer.m_uid, out var line)) continue;
                real += $" | REAL {peer.m_playerName} ({(peer.m_socket is ZSteamSocket ? "Steam door" : "crossplay")}): {line.Turns / seconds:0.0} turns/s, " +
                        $"{100f * line.Skipped / Math.Max(1, line.Turns):0}% skipped, {line.Sent / seconds:0} objects/s, waiting now {line.WaitingLast} (peak {line.WaitingMax}), " +
                        $"line queue {peer.m_socket.GetSendQueueSize()} B";
            }

            L.LogWarning(
                $"BENCH [{label}] {seconds:0.0} s: fps {frames / seconds:0.0} (slowest frame {slowestFrame * 1000f:0} ms), " +
                $"send turn {sendTicks * toMs / Math.Max(1, sendCalls):0.00} ms avg / {slowestSendTicks * toMs:0.0} ms worst | " +
                $"{players} fakes: {turnCount / seconds / players:0.0} turns/s each, {100f * skipped / Math.Max(1, turnCount):0}% skipped (line busy), " +
                $"{sent / seconds / players:0} objects/s each, {kbSum / players:0.0} KB/s each (slowest {kbMin:0.0}), " +
                $"waiting now {waitingLast / players} objects each (peak {waitingMax}) | {probeText}{real}" +
                (orderCheck ? $" | order: {orderTurns} lists checked, items ahead of building updates in {orderRestFirst}, VIOLATIONS {orderViolations}" : ""));
            orderTurns = orderRestFirst = 0;
            StartWindow(now);
        }

        private float Percentile(float share) =>
            latencies.Count == 0 ? 0f : latencies[Mathf.Clamp(Mathf.CeilToInt(share * latencies.Count) - 1, 0, latencies.Count - 1)];
    }

    [HarmonyPatch(typeof(ZSteamMatchmaking), nameof(ZSteamMatchmaking.VerifySessionTicket))]
    internal static class AcceptBenchTickets
    {
        private static bool Prefix(byte[] __0, ref bool __result)
        {
            if (__0 == null || __0.Length != FakeClient.Ticket.Length) return true;
            for (var i = 0; i < __0.Length; i++)
            {
                if (__0[i] != FakeClient.Ticket[i]) return true;
            }
            __result = true;
            return false;
        }
    }

    [HarmonyPatch(typeof(ZDOMan), "SendZDOs")]
    internal static class TimeTurns
    {
        private static void Prefix(out long __state)
        {
            Bench.TurnStarts();
            __state = Stopwatch.GetTimestamp();
        }

        private static void Postfix(object __0, long __state) => Bench.TurnEnds(__0, Stopwatch.GetTimestamp() - __state);
    }

    [HarmonyPatch(typeof(ZDOMan), "ServerSortSendZDOS")]
    internal static class CheckOrderAfterTheDoor
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(List<ZDO> __0, object __2) => Bench.CheckOrder(__0, __2);
    }

    [HarmonyPatch(typeof(ZDOMan), "CreateSyncList")]
    internal static class SeeWaiting
    {
        private static void Postfix(List<ZDO> __1) => Bench.WaitingThisTurn = __1.Count;
    }
}
