using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace CrossplaySteamDoor
{
    [BepInPlugin(Guid, Name, Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "iwo.valheim.crossplaysteamdoor";
        public const string Name = "Crossplay Steam Door";
        public const string Version = "1.2.0";

        internal static ManualLogSource Log { get; private set; }
        internal static ConfigEntry<bool> SendIpJoinsToSteamDoor { get; private set; }
        internal static ConfigEntry<int> SteamSendBudgetKB { get; private set; }
        internal static ConfigEntry<int> CrossplaySendBudgetKB { get; private set; }
        internal static ConfigEntry<int> SteamSendRateKB { get; private set; }
        internal static ConfigEntry<bool> FairTurns { get; private set; }
        internal static ConfigEntry<int> ReportMinutes { get; private set; }

        private static readonly Type[] DoorPatches =
        {
            typeof(OpenDoorWithServer), typeof(AcceptDoorConnections), typeof(CloseDoorWithServer),
            typeof(HandshakeByPeerSocket), typeof(SteamHostNameWithPlatform), typeof(ListCrossplayOnNextPort),
        };

        private Harmony harmony;
        private bool reportFailed;

        private void Awake()
        {
            Log = Logger;
            var enabled = Config.Bind("General", "Enabled", true,
                "Open a direct Steam connection next to crossplay on a crossplay server. " +
                "False leaves the server completely vanilla.");
            SendIpJoinsToSteamDoor = Config.Bind("General", "SendIpJoinsToSteamDoor", true,
                "A Steam PC that joins by IP on the game port uses the Steam door. The crossplay listing then shows the " +
                "game port + 1, which still joins through crossplay, as do the server list and join codes. " +
                "False keeps the vanilla listing; the door then only serves PCs that are not logged in to crossplay.");
            SteamSendBudgetKB = Config.Bind("WorldSync", "SteamSendBudgetKB", 64, new ConfigDescription(
                "World data the server may send a Steam player per turn, and may have under way to them, in KB. " +
                "The game's own value is 10, which a large base overwhelms: items, chests and doors then respond late. " +
                "One turn is one Steam message, and Steam refuses messages over 512 KB.",
                new AcceptableValueRange<int>(10, 256)));
            CrossplaySendBudgetKB = Config.Bind("WorldSync", "CrossplaySendBudgetKB", 10, new ConfigDescription(
                "The same for a crossplay player. 10 is the game's own value; the crossplay relay is the fragile link " +
                "and nothing above 10 has been tested, so raise it with care.",
                new AcceptableValueRange<int>(10, 64)));
            SteamSendRateKB = Config.Bind("WorldSync", "SteamSendRateKB", 1000, new ConfigDescription(
                "Steam's send rate per player in KB/s. The game sets 150, less than the larger budget can use.",
                new AcceptableValueRange<int>(150, 4000)));
            FairTurns = Config.Bind("WorldSync", "FairTurns", true,
                "The game sends buildings before items and creatures every turn, so a burning base can keep items " +
                "from being picked up for minutes. True sends items and creatures before building updates on every " +
                "other turn, and buildings a player has never been sent before both. False is the game's own order.");
            ReportMinutes = Config.Bind("WorldSync", "ReportMinutes", 10, new ConfigDescription(
                "Minutes between 'World sync' lines in the server log: frame rate, turns and how long players waited " +
                "for the world to catch up. 0 turns the line off.",
                new AcceptableValueRange<int>(0, 120)));

            if (!enabled.Value)
            {
                Log.LogInfo("Disabled in the config; the server runs vanilla.");
                return;
            }

            var missing = GameHooks.Resolve();
            if (missing.Count > 0)
            {
                Log.LogError($"This Valheim version lacks {string.Join(", ", missing)}; the server runs vanilla.");
                return;
            }

            Patch();
        }

        private void Patch()
        {
            harmony = new Harmony(Guid);
            try
            {
                foreach (var patch in DoorPatches) harmony.CreateClassProcessor(patch).Patch();
                if (HandshakeByPeerSocket.Applied)
                {
                    PatchWorldSync();
                    return;
                }
                Log.LogError("Could not adapt the join handshake; the server runs vanilla.");
            }
            catch (Exception e)
            {
                Log.LogError($"Patching failed, the server runs vanilla: {e}");
            }

            harmony.UnpatchSelf();
            harmony = null;
        }

        // The door works without these; a game update that breaks one leaves the game's own sync in place.
        private void PatchWorldSync()
        {
            var turns = TryPatch(typeof(SendBudgetPerPlayer), "the send budget");
            var lists = TryPatch(typeof(TakeTurnsWithBuildings), "fair turns");
            SyncWatch.TurnOn(turns && lists);
        }

        private bool TryPatch(Type patch, string feature)
        {
            try
            {
                harmony.CreateClassProcessor(patch).Patch();
                return true;
            }
            catch (Exception e)
            {
                Log.LogError($"World sync keeps the game's own behaviour for {feature}: {e.GetBaseException().Message}");
                return false;
            }
        }

        // A game update that removes something the report reads would otherwise throw here on every frame.
        private void Update()
        {
            if (reportFailed) return;

            try
            {
                SyncWatch.Frame();
            }
            catch (Exception e)
            {
                reportFailed = true;
                Log.LogWarning($"'World sync' report stopped: {e.GetBaseException().Message}");
            }
        }

        private void OnDestroy() => harmony?.UnpatchSelf();
    }
}
