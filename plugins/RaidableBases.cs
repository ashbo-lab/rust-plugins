using Facepunch;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Configuration;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Rust;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace Oxide.Plugins
{
    [Info("Raidable Bases", "nivex", "1.0.8")]
    [Description("Create fully automated raidable bases with npcs.")]
    class RaidableBases : RustPlugin
    {
        [PluginReference] 
        private Plugin DangerousTreasures, Vanish, LustyMap, ZoneManager, Economics, ServerRewards, Map, GUIAnnouncements, CopyPaste, Friends, Clans, Kits, TruePVE, Spawns, NightLantern, Wizardry;

        protected static SingletonBackbone Backbone { get; set; }
        protected Dictionary<int, List<BaseEntity>> Bases { get; } = new Dictionary<int, List<BaseEntity>>();
        protected Dictionary<int, RaidableBase> Raids { get; } = new Dictionary<int, RaidableBase>();
        protected List<uint> NetworkList { get; set; } = new List<uint>();
        protected Dictionary<ulong, Timer> PvpDelay { get; } = new Dictionary<ulong, Timer>();
        private Dictionary<string, List<ulong>> Skins { get; } = new Dictionary<string, List<ulong>>();
        private Dictionary<string, List<ulong>> WorkshopSkins { get; } = new Dictionary<string, List<ulong>>();
        private Dictionary<MonumentInfo, float> monuments { get; set; } = new Dictionary<MonumentInfo, float>();
        private Dictionary<Vector3, float> managedZones { get; set; } = new Dictionary<Vector3, float>();
        private Dictionary<int, MapInfo> mapMarkers { get; set; } = new Dictionary<int, MapInfo>();
        private Dictionary<int, string> lustyMarkers { get; set; } = new Dictionary<int, string>();
        protected Dictionary<RaidableType, RaidableSpawns> raidSpawns { get; set; } = new Dictionary<RaidableType, RaidableSpawns>();
        protected Dictionary<string, float> buyCooldowns { get; set; } = new Dictionary<string, float>();
        protected List<int> AvailableDifficulties { get; set; } = new List<int>();
        private DynamicConfigFile dataFile { get; set; }
        private DynamicConfigFile uidsFile { get; set; }
        private StoredData storedData { get; set; } = new StoredData();
        protected Coroutine despawnCoroutine { get; set; }
        protected Coroutine maintainCoroutine { get; set; }
        protected Coroutine scheduleCoroutine { get; set; }
        protected Coroutine gridCoroutine { get; set; }
        private Stopwatch gridStopwatch { get; } = new Stopwatch();
        private StringBuilder _sb { get; } = new StringBuilder();
        protected float Radius { get; } = 25f;
        private bool wiped { get; set; }
        private float lastSpawnRequestTime { get; set; }
        private float gridTime { get; set; }
        private bool IsUnloading { get; set; }

        public class ResourcePath
        {
            public List<uint> Blocks { get; }
            public ItemDefinition BoxDefinition { get; }
            public string ExplosionMarker { get; }
            public string CodeLock { get; }
            public string Fireball { get; }
            public string HighExternalWoodenWall { get; }
            public string HighExternalStoneWall { get; }
            public string Ladder { get; }
            public string Murderer { get; }
            public string RadiusMarker { get; }
            public string Sphere { get; }
            public string Scientist { get; }
            public string VendingMarker { get; }

            public ResourcePath()
            {
                Blocks = new List<uint> { 803699375, 2194854973, 919059809, 3531096400, 310235277, 2326657495, 3234260181, 72949757 };
                BoxDefinition = ItemManager.FindItemDefinition(StringPool.Get(2735448871));
                CodeLock = StringPool.Get(3518824735);
                ExplosionMarker = StringPool.Get(4060989661);
                Fireball = StringPool.Get(2086405370); //3550347674
                HighExternalWoodenWall = StringPool.Get(1745077396);
                HighExternalStoneWall = StringPool.Get(1585379529);
                Ladder = StringPool.Get(2150203378);
                Murderer = StringPool.Get(3879041546);
                RadiusMarker = StringPool.Get(2849728229);
                Sphere = StringPool.Get(3211242734);
                Scientist = StringPool.Get(4223875851);
                VendingMarker = StringPool.Get(3459945130);
            }
        }

        public class SingletonBackbone : SingletonComponent<SingletonBackbone>
        {
            public RaidableBases Plugin { get; private set; }
            public Oxide.Core.Libraries.Lang lang => Plugin.lang;
            private StringBuilder sb => Plugin._sb;
            
            public ResourcePath Path;

            public SingletonBackbone(RaidableBases plugin) 
            {
                Plugin = plugin;
                Path = new ResourcePath();
            }

            public void Destroy()
            {
                foreach (var timer in Plugin.PvpDelay.Values)
                {
                    if (timer == null || timer.Destroyed)
                    {
                        continue;
                    }

                    timer.Destroy();
                }
                
                Path = null;
                Plugin = null;
                DestroyImmediate(Instance);
            }

            public void Message(BasePlayer player, string key, string id, params object[] args)
            {
                if (player.IsValid())
                {
                    Plugin.Player.Message(player, GetMessage(key, id, args), _config.Settings.ChatID);
                }
            }

            public string GetMessage(string key, string id = null, params object[] args)
            {
                sb.Length = 0;

                if (_config.EventMessages.Prefix && id != "server_console" && id != null)
                {
                    sb.Append(lang.GetMessage("Prefix", Plugin, id));
                }

                sb.Append(id == "server_console" || id == null ? RemoveFormatting(lang.GetMessage(key, Plugin, id)) : lang.GetMessage(key, Plugin, id));

                return args.Length > 0 ? string.Format(sb.ToString(), args) : sb.ToString();
            }

            public string RemoveFormatting(string source) => source.Contains(">") ? Regex.Replace(source, "<.*?>", string.Empty) : source;

            public Timer Timer(float seconds, Action action) => Plugin.timer.Once(seconds, action);
        }

        public class Elevation
        {
            public float Min { get; set; }
            public float Max { get; set; }
        }

        public class RaidableSpawnLocation
        {
            public Elevation Elevation = new Elevation();
            public Vector3 Location = Vector3.zero;
        }

        public class RaidableSpawns
        {
            private readonly List<RaidableSpawnLocation> Spawns = new List<RaidableSpawnLocation>();
            private readonly List<RaidableSpawnLocation> Cache = new List<RaidableSpawnLocation>();

            public void Add(RaidableSpawnLocation rsl)
            {
                Spawns.Add(rsl);
            }

            public void AddRange()
            {
                if (Cache.Count > 0)
                {
                    Spawns.AddRange(new List<RaidableSpawnLocation>(Cache));
                    Cache.Clear();
                }
            }

            public IEnumerable<RaidableSpawnLocation> All
            {
                get
                {
                    if (Cache.Count > 0)
                    {
                        return Spawns.Union(Cache);
                    }
                    else return Spawns;
                }
            }

            public void Check()
            {
                if (Spawns.Count == 0)
                {
                    AddRange();
                }
            }

            public void Clear()
            {
                Spawns.Clear();
                Cache.Clear();
            }

            public int Count
            {
                get
                {
                    return Spawns.Count;
                }
            }

            public RaidableSpawnLocation GetRandom()
            {
                var rsl = Spawns.GetRandom();

                Remove(rsl);

                return rsl;
            }

            private void Remove(RaidableSpawnLocation a)
            {
                Spawns.Remove(a);
                Cache.Add(a);
            }

            public void RemoveNear(RaidableSpawnLocation a, float radius)
            {
                var list = new List<RaidableSpawnLocation>(Spawns);

                foreach (var b in list)
                {
                    if (Distance2D(a.Location, b.Location) <= radius)
                    {
                        Remove(b);
                    }
                }

                list.Clear();
            }

            public RaidableSpawns(List<RaidableSpawnLocation> spawns)
            {
                Spawns = spawns;
            }

            public RaidableSpawns()
            {

            }
        }

        private class MapInfo
        {
            public string Url;
            public string IconName;
            public Vector3 Position;
        }

        private class PlayerInfo
        {
            public int TotalRaids { get; set; }
            public int Raids { get; set; }
            public PlayerInfo() { }
        }

        private class StoredData
        {
            public Dictionary<string, PlayerInfo> Players { get; } = new Dictionary<string, PlayerInfo>();
            public double SecondsUntilRaid { get; set; } = double.MinValue;
            public int TotalEvents { get; set; }
            public StoredData() { }
        }

        private void OnPluginLoaded(Plugin plugin)
        {
            switch (plugin.Title)
            {
                case "Dangerous Treasures":
                    {
                        DangerousTreasures = plugin;

                        if (DangerousTreasures.Version < new VersionNumber(1, 4, 1))
                        {
                            DangerousTreasures = null;
                        }
                        break;
                    }
                case "Vanish":
                    {
                        Vanish = plugin;
                        break;
                    }
                case "Wizardry":
                    {
                        Wizardry = plugin;
                        break;
                    }
                case "Night Lantern":
                    {
                        NightLantern = plugin;
                        break;
                    }
                case "Spawns":
                    {
                        Spawns = plugin;
                        break;
                    }
                case "TruePVE":
                    {
                        TruePVE = plugin;
                        break;
                    }
                case "LustyMap":
                    {
                        LustyMap = plugin;
                        break;
                    }
                case "ZoneManager":
                    {
                        ZoneManager = plugin;
                        break;
                    }
                case "Economics":
                    {
                        Economics = plugin;
                        break;
                    }
                case "ServerRewards":
                    {
                        ServerRewards = plugin;
                        break;
                    }
                case "Map":
                    {
                        Map = plugin;
                        break;
                    }
                case "GUIAnnouncements":
                    {
                        GUIAnnouncements = plugin;
                        break;
                    }
                case "CopyPaste":
                    {
                        CopyPaste = plugin;
                        break;
                    }
                case "Clans":
                    {
                        Clans = plugin;
                        break;
                    }
                case "Friends":
                    {
                        Friends = plugin;
                        break;
                    }
                case "Kits":
                    {
                        Kits = plugin;
                        break;
                    }
            }
        }

        private void OnPluginUnloaded(Plugin plugin)
        {
            switch (plugin.Title)
            {
                case "Dangerous Treasures":
                    {
                        DangerousTreasures = null;
                        break;
                    }
                case "Vanish":
                    {
                        Vanish = null;
                        break;
                    }
                case "Wizardry":
                    {
                        Wizardry = null;
                        break;
                    }
                case "Night Lantern":
                    {
                        NightLantern = null;
                        break;
                    }
                case "Spawns":
                    {
                        Spawns = null;
                        break;
                    }
                case "TruePVE":
                    {
                        TruePVE = null;
                        break;
                    }
                case "LustyMap":
                    {
                        LustyMap = null;
                        break;
                    }
                case "ZoneManager":
                    {
                        ZoneManager = null;
                        break;
                    }
                case "Economics":
                    {
                        Economics = null;
                        break;
                    }
                case "ServerRewards":
                    {
                        ServerRewards = null;
                        break;
                    }
                case "Map":
                    {
                        Map = null;
                        break;
                    }
                case "GUIAnnouncements":
                    {
                        GUIAnnouncements = null;
                        break;
                    }
                case "CopyPaste":
                    {
                        CopyPaste = null;
                        break;
                    }
                case "Clans":
                    {
                        Clans = null;
                        break;
                    }
                case "Friends":
                    {
                        Friends = null;
                        break;
                    }
                case "Kits":
                    {
                        Kits = null;
                        break;
                    }
            }
        }

        private class PlayWithFire : FacepunchBehaviour
        {
            private FireBall fireball { get; set; }
            private BaseEntity target { get; set; }
            private bool fireFlung { get; set; }
            private Coroutine mcCoroutine { get; set; }

            public BaseEntity Target
            {
                get
                {
                    return target;
                }
                set
                {
                    target = value;
                    enabled = true;
                }
            }

            private void Awake()
            {
                fireball = GetComponent<FireBall>();
                enabled = false;
            }

            private void FixedUpdate()
            {
                if (!IsValid(target) || target.IsDestroyed || target.Health() <= 0)
                {
                    fireball.Extinguish();
                    Destroy(this);
                    return;
                }

                fireball.transform.RotateAround(target.transform.position, Vector3.up, 5f);
                fireball.transform.hasChanged = true;
            }

            public void FlingFire(BaseEntity attacker)
            {
                if (fireFlung) return;
                fireFlung = true;
                mcCoroutine = StartCoroutine(MakeContact(attacker));
            }

            private IEnumerator MakeContact(BaseEntity attacker)
            {
                float distance = Vector3.Distance(fireball.ServerPosition, attacker.transform.position);

                while (!Backbone.Plugin.IsUnloading && attacker != null && fireball != null && !fireball.IsDestroyed && Distance2D(fireball.ServerPosition, attacker.transform.position) > 2.5f)
                {
                    fireball.ServerPosition = Vector3.MoveTowards(fireball.ServerPosition, attacker.transform.position, distance * 0.1f);
                    yield return Coroutines.WaitForSeconds(0.3f);
                }
            }

            private void OnDestroy()
            {
                if (mcCoroutine != null)
                {
                    StopCoroutine(mcCoroutine);
                    mcCoroutine = null;
                }
                
                Destroy(this);
            }
        }

        public enum RaidableType
        {
            None = 0,
            Manual = 1,
            Scheduled = 2,
            Purchased = 3,
            Maintained = 4,
            Grid = 5
        }

        public class PlayerInputEx : FacepunchBehaviour
        {
            public BasePlayer player { get; set; }
            private InputState input { get; set; }
            private RaidableBase raid { get; set; }

            private void Awake()
            {
                player = GetComponent<BasePlayer>();
                input = player.serverInput;
            }

            public void Setup(RaidableBase raid)
            {
                this.raid = raid;
                raid.Inputs.Add(this);
                InvokeRepeating(Repeater, 0f, 0.1f);
            }

            private void Repeater()
            {
                if (!player || !player.IsConnected || player.inventory == null || player.inventory.containerBelt == null || player.IsDead() || raid == null)
                {
                    if (raid != null && raid.IsPayLocked && raid.IsOpened)
                    {
                        raid.TryInvokeResetOwner();
                    }

                    Destroy(this);
                    return;
                }

                if (player.svActiveItemID == 0)
                {
                    return;
                }

                if (!input.WasJustReleased(BUTTON.FIRE_PRIMARY) && !input.IsDown(BUTTON.FIRE_PRIMARY))
                {
                    return;
                }

                Item item = player.GetActiveItem();

                if (item?.info.shortname != "ladder.wooden.wall")
                {
                    return;
                }

                RaycastHit hit;
                if (!Physics.Raycast(player.eyes.HeadRay(), out hit, 4f, Layers.Mask.Construction, QueryTriggerInteraction.Ignore))
                {
                    return;
                }

                var block = hit.GetEntity();

                if (!IsValid(block) || block.OwnerID != 0 || !Backbone.Path.Blocks.Contains(block.prefabID) || !Backbone.Plugin.NetworkList.Contains(block.net.ID)) // walls and foundations
                {
                    return;
                }

                int amount = item.amount;
                var action = new Action(() =>
                {
                    if (raid == null || item?.amount != amount || IsLadderNear(hit.point))
                    {
                        return;
                    }

                    //* Quaternion.Euler(0f, planner.transform.eulerAngles.y, 0f) * Quaternion.Euler(???);
                    var rot = Quaternion.LookRotation(hit.normal, Vector3.up);
                    var e = GameManager.server.CreateEntity(Backbone.Path.Ladder, hit.point, rot, true); // as BaseLadder;

                    if (e == null)
                    {
                        Destroy(this);
                        return;
                    }

                    e.gameObject.SendMessage("SetDeployedBy", player, SendMessageOptions.DontRequireReceiver);
                    e.OwnerID = 0;
                    e.Spawn();
                    item.UseItem(1);

                    var planner = item.GetHeldEntity() as Planner;
                    var deployable = planner.GetDeployable();

                    if (deployable != null && deployable.setSocketParent && block.SupportsChildDeployables())
                    {
                        e.SetParent(block, true, false);
                    }

                    Backbone.Plugin.NetworkList.Add(e.net.ID);
                });

                player.Invoke(action, 0.1f);
            }

            public bool IsLadderNear(Vector3 target)
            {
                int hits = Physics.OverlapSphereNonAlloc(target, 0.3f, Vis.colBuffer, Layers.Mask.Deployed, QueryTriggerInteraction.Ignore);
                bool flag = false;

                for (int i = 0; i < hits; i++)
                {
                    if (Vis.colBuffer[i].name == Backbone.Path.Ladder)
                    {
                        flag = true;
                    }

                    Vis.colBuffer[i] = null;
                }

                return flag;
            }

            private void OnDestroy()
            {
                if (raid != null)
                {
                    raid.Inputs.Remove(this);
                }

                CancelInvoke();
                Destroy(this);
            }
        }

        private class FinalDestination : FacepunchBehaviour
        {
            public NPCPlayerApex apex;
            Vector3 lastPosition;
            List<Vector3> list;

            void Awake()
            {
                apex = GetComponent<NPCPlayerApex>();
            }

            void OnDestroy()
            {
                StopAllCoroutines();
                Destroy(this);
            }

            public void Set(List<Vector3> list)
            {
                lastPosition = apex.transform.position;
                this.list = list;
                StartCoroutine(Go());
            }

            IEnumerator Go()
            {
                while (true)
                {
                    if (apex.AttackTarget == null)
                    {
                        var position = list.GetRandom();

                        if (apex.IsStuck)
                        {
                            apex.Pause();
                            apex.ServerPosition = position;
                            apex.GetNavAgent.Warp(position);
                            apex.stuckDuration = 0f;
                            apex.IsStuck = false;
                            apex.Resume();
                        }

                        if (apex.GetNavAgent == null || !apex.GetNavAgent.isOnNavMesh)
                        {
                            apex.finalDestination = position;
                        }
                        else apex.GetNavAgent.SetDestination(position);

                        apex.IsStopped = false;
                        apex.Destination = position;
                        lastPosition = position;
                    }

                    yield return Coroutines.WaitForSeconds(10f);
                }
            }
        }

        public class RaidableBase : FacepunchBehaviour
        {
            public readonly Hash<uint, float> conditions = new Hash<uint, float>();
            private Dictionary<string, List<string>> _clans { get; set; } = new Dictionary<string, List<string>>();
            private Dictionary<string, List<string>> _friends { get; set; } = new Dictionary<string, List<string>>();
            public List<StorageContainer> _containers { get; set; } = new List<StorageContainer>();
            public List<BuildingPrivlidge> privs { get; set; } = new List<BuildingPrivlidge>();
            public List<PlayerInputEx> Inputs { get; set; } = new List<PlayerInputEx>();
            public List<NPCPlayerApex> npcs { get; set; } = new List<NPCPlayerApex>();
            public List<BasePlayer> raiders { get; set; } = new List<BasePlayer>();
            private List<BasePlayer> intruders { get; set; } = new List<BasePlayer>();
            private List<FireBall> fireballs { get; set; } = new List<FireBall>();
            private List<ulong> allies { get; set; } = new List<ulong>();
            private List<Vector3> foundations { get; set; } = new List<Vector3>();
            private List<SphereEntity> spheres { get; set; } = new List<SphereEntity>();
            private List<BaseEntity> lights { get; set; } = new List<BaseEntity>();
            private List<BaseOven> ovens { get; set; } = new List<BaseOven>();
            private List<AutoTurret> turrets { get; set; } = new List<AutoTurret>();
            private List<string> treasureItems { get; } = new List<string>();
            private List<BaseEntity> weapons { get; set; } = new List<BaseEntity>();
            private Dictionary<string, List<string>> npcKits { get; set; }
            private List<ulong> friends { get; set; } = new List<ulong>();
            private MapMarkerExplosion explosionMarker { get; set; }
            private MapMarkerGenericRadius genericMarker { get; set; }
            private VendingMachineMapMarker vendingMarker { get; set; }
            private Coroutine setupRoutine { get; set; } = null;
            private bool IsInvokingCanFinish { get; set; }
            public bool selfDestruct { get; set; }
            public Vector3 PastedLocation { get; set; }
            public Vector3 Location { get; set; }
            public string BaseName { get; set; }
            public int BaseIndex { get; set; } = -1;
            public uint NetworkID { get; set; } = uint.MaxValue;
            public BasePlayer owner { get; set; }
            public float OwnerLastHitTime { get; set; }
            public float despawnTime { get; set; }
            private ulong skinId { get; set; }
            public bool AllowPVP { get; set; }
            public BuildingOptions Options { get; set; }
            public bool IsAuthed { get; set; }
            public bool IsOpened { get; set; } = true;
            public bool IsUnloading { get; set; }
            public int uid { get; set; }
            public bool IsPayLocked { get; set; }
            public int npcSpawnedAmount { get; set; }
            public RaidableType Type { get; set; }
            public string DifficultyMode { get; set; }
            public bool IsLooted => CanUndo(_containers);
            public bool IsLoading => setupRoutine != null;
            public bool IsOwnerLocked => IsPayLocked || IsOwnerActive;
            private float Radius { get; } = 25f;
            private bool markerCreated { get; set; }
            private bool lightsOn { get; set; }
            private bool killed { get; set; }
            private ItemComparerer itemComparer { get; } = new ItemComparerer();

            public class ItemComparerer : IEqualityComparer<TreasureItem>
            {
                public bool Equals(TreasureItem x, TreasureItem y)
                {
                    return x != null && y != null && x.GetHashCode() == y.GetHashCode();
                }
                
                public int GetHashCode(TreasureItem obj)
                {
                    return obj.GetHashCode();
                }
            }

            private bool IsOwnerActive
            {
                get
                {
                    if (IsPayLocked)
                    {
                        return true;
                    }

                    bool inactive = _config.Settings.Management.LockTime <= 0 ? false : Time.realtimeSinceStartup - OwnerLastHitTime > _config.Settings.Management.LockTime;

                    if (owner == null || !owner.IsConnected || Distance2D(owner.transform.position, Location) > _config.Settings.Management.MaxOwnerDistance || inactive)
                    {
                        if (owner != null)
                        {
                            owner = null;
                            UpdateMarker();
                        }
                    }

                    return owner != null;
                }
            }

            private void OnTriggerEnter(Collider col)
            {
                var m = col?.ToBaseEntity() as BaseMountable;

                if (m.IsValid() && RemoveMountable(m))
                {
                    return;
                }

                var p = col?.ToBaseEntity() as BasePlayer;
                
                if (!p.IsValid() || p.IsNpc)
                {
                    return;
                }

                if (!intruders.Contains(p))
                {
                    intruders.Add(p);
                }

                Protector();

                if (!intruders.Contains(p))
                {
                    return;
                }

                if (!p.GetComponent<PlayerInputEx>())
                {
                    p.gameObject.AddComponent<PlayerInputEx>().Setup(this);
                }

                StopUsingWand(p);

                if (_config.EventMessages.AnnounceEnterExit)
                {
                    Backbone.Message(p, AllowPVP ? "OnPlayerEntered" : "OnPlayerEnteredPVE", p.UserIDString);
                }
            }

            private void OnTriggerExit(Collider col)
            {
                if (_config.Settings.Management.PVPDelay <= 0 || !AllowPVP)
                {
                    return;
                }

                var p = col?.ToBaseEntity() as BasePlayer;

                if (!p.IsValid())
                {
                    return;
                }

                var component = p.GetComponent<PlayerInputEx>();

                if (component)
                {
                    Destroy(component);
                }

                if (!intruders.Contains(p))
                {
                    return;
                }

                intruders.Remove(p);

                if (Backbone.Plugin.TruePVE == null)
                {
                    return;
                }

                ulong id = p.userID;
                Timer timer;

                if (!Backbone.Plugin.PvpDelay.TryGetValue(id, out timer))
                {
                    if (!p.IsNpc && _config.EventMessages.AnnounceEnterExit)
                    {
                        Backbone.Message(p, "DoomAndGloom", p.UserIDString, "PVP", _config.Settings.Management.PVPDelay);
                    }

                    Backbone.Plugin.PvpDelay[id] = timer = Backbone.Timer(_config.Settings.Management.PVPDelay, () => Backbone.Plugin.PvpDelay.Remove(id));
                    return;
                }

                timer.Reset();
            }

            private void Protector()
            {
                if (!CanEject())
                {
                    return;
                }

                var targets = new List<BasePlayer>(intruders);

                if (targets.Count == 0)
                {
                    return;
                }

                foreach (var target in targets)
                {
                    if (target == owner || friends.Contains(target.userID) || target.IsAdmin)
                    {
                        continue;
                    }

                    if (!IsAlly(target))
                    {
                        intruders.Remove(target);
                        RemovePlayer(target);
                    }
                    else friends.Add(target.userID);
                }

                targets.Clear();
            }

            private void OnDestroy()
            {
                Despawn();
                Destroy(this);
            }

            public void Despawn()
            {
                IsOpened = false;

                if (killed) return;

                killed = true;
                CancelInvoke();
                DestroyFire();
                DestroyInputs();
                RemoveSpheres();
                EnableBags();
                KillNpc();
                StopAllCoroutines();
                RemoveMapMarkers();

                if (!IsUnloading)
                {
                    ServerMgr.Instance.StartCoroutine(Backbone.Plugin.UndoRoutine(BaseIndex));
                }

                Backbone.Plugin.Raids.Remove(uid);

                if (Backbone.Plugin.Raids.Count == 0)
                {
                    if (IsUnloading)
                    {
                        UnsetStatics();
                    }
                    else Backbone.Plugin.UnsubscribeHooks();
                }

                Destroy(this);
            }

            private void FillAmmoTurret(AutoTurret turret)
            {
                var attachedWeapon = turret.GetAttachedWeapon();

                if (attachedWeapon != null)
                {
                    if (!HasAmmo(turret.inventory, attachedWeapon.primaryMagazine.ammoType))
                    {
                        Item item = ItemManager.Create(attachedWeapon.primaryMagazine.ammoType, _config.Weapons.Ammo.AutoTurret);

                        if (!item.MoveToContainer(turret.inventory))
                        {
                            item.Remove();
                        }
                    }
                    
                    attachedWeapon.primaryMagazine.contents = attachedWeapon.primaryMagazine.capacity;
                    attachedWeapon.SendNetworkUpdateImmediate();
                    turret.UpdateTotalAmmo();
                }
            }

            private bool HasAmmo(ItemContainer container, ItemDefinition ammoType)
            {
                foreach (Item item in container.itemList)
                {
                    if (item.info == ammoType)
                    {
                        return true;
                    }
                }

                return false;
            }

            private void FillAmmoGunTrap(GunTrap gt)
            {
                if (gt.ammoType != null && gt.inventory.itemList.Count == 0)
                {
                    Item item = ItemManager.Create(gt.ammoType, _config.Weapons.Ammo.GunTrap);

                    if (!item.MoveToContainer(gt.inventory))
                    {
                        item.Remove();
                    }
                }
            }

            private void FillAmmoFogMachine(FogMachine fm)
            {
                if (!fm.HasFuel())
                {
                    var ammoType = ItemManager.FindItemDefinition("lowgradefuel");

                    if (ammoType != null)
                    {
                        Item item = ItemManager.Create(ammoType, _config.Weapons.Ammo.FogMachine);

                        if (!item.MoveToContainer(fm.inventory))
                        {
                            item.Remove();
                        }
                    }
                }
            }

            private void FillAmmoFlameTurret(FlameTurret ft)
            {
                if (!ft.HasFuel())
                {
                    var ammoType = ItemManager.FindItemDefinition("lowgradefuel");

                    if (ammoType != null)
                    {
                        Item item = ItemManager.Create(ammoType, _config.Weapons.Ammo.FlameTurret);

                        if (!item.MoveToContainer(ft.inventory))
                        {
                            item.Remove();
                        }
                    }
                }
            }

            private void FillAmmoSamSite(SamSite ss)
            {
                if (!ss.HasAmmo())
                {
                    Item item = ItemManager.Create(ss.ammoType, _config.Weapons.Ammo.SamSite);

                    if (!item.MoveToContainer(ss.inventory))
                    {
                        item.Remove();
                    }
                }
            }

            private void OnWeaponItemAddedRemoved(Item item, bool bAdded)
            {
                if (bAdded)
                {
                    return;
                }

                weapons.RemoveAll(x => !x.IsValid() || x.IsDestroyed);

                foreach (var weapon in weapons)
                {
                    if (weapon is AutoTurret)
                    {
                        weapon.Invoke(() => FillAmmoTurret(weapon as AutoTurret), 0.1f);
                    }
                    else if (weapon is GunTrap)
                    {
                        weapon.Invoke(() => FillAmmoGunTrap(weapon as GunTrap), 0.1f);
                    }
                    else if (weapon is SamSite)
                    {
                        weapon.Invoke(() => FillAmmoSamSite(weapon as SamSite), 0.1f);
                    }
                }
            }

            private void SetupContainers()
            {
                _containers.RemoveAll(x => !x.IsValid() || x.IsDestroyed);

                foreach (var container in _containers)
                {
                    if (container.inventory == null) continue;
                    container.inventory.onItemAddedRemoved += new Action<Item, bool>(OnItemAddedRemoved);
                }
            }

            private void OnItemAddedRemoved(Item item, bool bAdded)
            {
                if (!bAdded)
                {
                    Check();
                }
            }

            public void Check()
            {
                if (!IsInvokingCanFinish)
                {
                    IsInvokingCanFinish = true;
                    InvokeRepeating(TryToEnd, 0f, 1f);
                }
            }

            private void TryToEnd()
            {
                if (IsOpened && IsLooted)
                {
                    CancelInvoke(TryToEnd);
                    AwardRaiders();
                    Undo();
                }
            }

            private void AwardRaiders()
            {
                if (_config.Settings.RemoveAdminRaiders)
                {
                    raiders.RemoveAll(x => !x || x.IsAdmin);
                }
                else raiders.RemoveAll(x => !x);

                if (raiders.Count == 0)
                {
                    return;
                }

                if (Options.Levels.Level2)
                {
                    SpawnNpcs();
                }

                HandleAwards();

                string thieves = string.Join(", ", raiders.Select(x => x.displayName));
                string posStr = FormatGridReference(Location);

                Backbone.Plugin.Puts(Backbone.GetMessage("Thief", null, posStr, thieves));

                if (_config.EventMessages.AnnounceThief)
                {
                    foreach (var target in BasePlayer.activePlayerList)
                    {
                        Backbone.Message(target, "Thief", target.UserIDString, posStr, thieves);
                    }
                }

                raiders.Clear();
                Backbone.Plugin.SaveData();
            }

            private void HandleAwards()
            {
                foreach (var raider in raiders)
                {
                    if (_config.RankedLadder.Enabled)
                    {
                        PlayerInfo playerInfo;
                        if (!Backbone.Plugin.storedData.Players.TryGetValue(raider.UserIDString, out playerInfo))
                        {
                            Backbone.Plugin.storedData.Players[raider.UserIDString] = playerInfo = new PlayerInfo();
                        }

                        playerInfo.TotalRaids++;
                        playerInfo.Raids++;
                    }

                    if (Options.Rewards.Money > 0 && Backbone.Plugin.Economics != null && Backbone.Plugin.Economics.IsLoaded)
                    {
                        Backbone.Plugin.Economics.Call("Deposit", raider.UserIDString, Options.Rewards.Money);
                        Backbone.Message(raider, "EconomicsDeposit", raider.UserIDString, Options.Rewards.Money);
                    }

                    if (Options.Rewards.Points > 0 && Backbone.Plugin.ServerRewards != null && Backbone.Plugin.ServerRewards.IsLoaded)
                    {
                        Backbone.Plugin.ServerRewards.Call("AddPoints", raider.userID, Options.Rewards.Points);
                        Backbone.Message(raider, "ServerRewardPoints", raider.UserIDString, Options.Rewards.Points);
                    }
                }
            }

            public string Mode()
            {
                if (owner.IsValid())
                {
                    return string.Format("{0} {1}", owner.displayName, DifficultyMode.SentenceCase());
                }

                return DifficultyMode.SentenceCase();
            }

            public void TrySetPayLock(BasePlayer player)
            {
                if (player.IsValid() && player.IsConnected)
                {
                    IsPayLocked = true;
                    owner = player;
                }
            }

            public void TrySetOwner(BasePlayer attacker, BaseEntity entity, HitInfo hitInfo)
            {
                if (!_config.Settings.Management.UseOwners || IsOwnerLocked)
                {
                    return;
                }

                if (owner.IsValid() && owner.userID != attacker.userID)
                {
                    return;
                }

                if (_config.Settings.Management.BypassUseOwnersForPVP && AllowPVP)
                {
                    return;
                }

                if (_config.Settings.Management.BypassUseOwnersForPVE && !AllowPVP)
                {
                    return;
                }

                if (entity is NPCPlayerApex)
                {
                    TrySetOwner(attacker);
                    return;
                }

                if (hitInfo == null || hitInfo.damageTypes == null || (!(entity is BuildingBlock) && !(entity is Door)))
                {
                    return;
                }

                if (hitInfo.damageTypes.Has(DamageType.Explosion) || hitInfo.damageTypes.Has(DamageType.Blunt) || hitInfo.damageTypes.Has(DamageType.Stab))
                {
                    TrySetOwner(attacker);
                }
            }

            private void TrySetOwner(BasePlayer player)
            {
                if (owner.IsValid() && player.userID == owner.userID)
                {
                    OwnerLastHitTime = Time.realtimeSinceStartup;
                }

                if (!IsOwnerLocked)
                {
                    OwnerLastHitTime = Time.realtimeSinceStartup;
                    owner = player;
                    UpdateMarker();
                }
            }

            private float lastCheckTime;

            public void CheckDespawn()
            {
                if (_config.Settings.Management.DespawnMinutesInactive <= 0 || selfDestruct)
                {
                    return;
                }
                
                if (lastCheckTime != 0 && Time.realtimeSinceStartup - lastCheckTime < 60f)
                {
                    return;
                }

                lastCheckTime = Time.realtimeSinceStartup;

                if (IsInvoking(Despawn))
                {
                    CancelInvoke(Despawn);
                }

                float time = _config.Settings.Management.DespawnMinutesInactive * 60f;
                despawnTime = Time.realtimeSinceStartup + time;
                Invoke(Despawn, time);
                UpdateMarker();
            }

            public bool CanUndo(List<StorageContainer> containers)
            {
                containers.RemoveAll(x => !x.IsValid() || x.IsDestroyed || x.inventory == null);

                foreach (var container in containers)
                {
                    if (container.inventory.itemList.Count > 0)
                    {
                        return false;
                    }
                }

                return true;
            }

            private bool CanPlayerBeLooted()
            {
                if (!_config.Settings.Management.PlayersLootableInPVE && !AllowPVP)
                {
                    return false;
                }
                else if (!_config.Settings.Management.PlayersLootableInPVP && AllowPVP)
                {
                    return false;
                }

                return true;
            }

            private bool CanBeLooted(BasePlayer player, BaseEntity e)
            {
                if (e is NPCPlayerCorpse)
                {
                    return true;
                }

                if (e is LootableCorpse)
                {
                    var corpse = e as LootableCorpse;

                    if (!corpse.playerSteamID.IsSteamId() || corpse.playerSteamID == player.userID || corpse.playerName == player.displayName)
                    {
                        return true;
                    }

                    return CanPlayerBeLooted();
                }
                else if (e is DroppedItemContainer)
                {
                    var container = e as DroppedItemContainer;

                    if (!container.playerSteamID.IsSteamId() || container.playerSteamID == player.userID || container.playerName == player.displayName)
                    {
                        return true;
                    }

                    return CanPlayerBeLooted();
                }

                if (e is GunTrap || e is FlameTurret || e is FogMachine || e is SamSite || e is AutoTurret)
                {
                    return false;
                }

                return true;
            }

            public void OnLootEntityInternal(BasePlayer player, BaseEntity e)
            {
                if (!CanBeLooted(player, e))
                {
                    player.Invoke(player.EndLooting, 0.1f);
                    return;
                }

                if (e is LootableCorpse || e is DroppedItemContainer)
                {
                    return;
                }

                if (player.isMounted)
                {
                    Backbone.Message(player, "CannotBeMounted", player.UserIDString);
                    player.Invoke(player.EndLooting, 0.1f);
                    return;
                }

                if (Options.RequiresCupboardAccess && !player.CanBuild()) //player.IsBuildingBlocked())
                {
                    Backbone.Message(player, "MustBeAuthorized", player.UserIDString);
                    player.Invoke(player.EndLooting, 0.1f);
                    return;
                }

                if (!IsAlly(player))
                {
                    Backbone.Message(player, "OwnerLocked", player.UserIDString);
                    player.Invoke(player.EndLooting, 0.1f);
                    return;
                }

                if (raiders.Any())
                {
                    CheckDespawn();
                }

                if (AddLooter(player, this))
                {
                    Check();
                }
            }

            private void StartPlayingWithFire()
            {
                if (npcs.Count == 0)
                {
                    return;
                }

                fireballs.RemoveAll(x => x == null || x.IsDestroyed);

                if (fireballs.Count >= Options.Levels.Level1.Amount || UnityEngine.Random.value > Options.Levels.Level1.Chance)
                {
                    return;
                }

                var npc = npcs.GetRandom();

                if (!IsValid(npc))
                {
                    return;
                }

                var fireball = GameManager.server.CreateEntity(Backbone.Path.Fireball, npc.transform.position + new Vector3(0f, 3f, 0f), Quaternion.identity, true) as FireBall;

                if (fireball == null)
                {
                    return;
                }

                var rb = fireball.GetComponent<Rigidbody>();

                if (rb != null)
                {
                    rb.isKinematic = false;
                    rb.useGravity = false;
                    rb.drag = 0f;
                }

                fireball.lifeTimeMax = 15f;
                fireball.lifeTimeMin = 15f;
                fireball.canMerge = false;
                fireball.Spawn();
                fireball.CancelInvoke(fireball.TryToSpread);
                fireball.gameObject.AddComponent<PlayWithFire>().Target = npc;
                fireballs.Add(fireball);
            }

            public void DestroyInputs()
            {
                foreach (var input in Inputs)
                {
                    Destroy(input);
                }

                Inputs.Clear();
            }

            public void DestroyFire()
            {
                if (fireballs.Count == 0)
                {
                    return;
                }

                foreach (var fireball in fireballs)
                {
                    if (fireball == null)
                    {
                        continue;
                    }

                    var component = fireball.GetComponent<PlayWithFire>();

                    if (component)
                    {
                        Destroy(component);
                    }

                    fireball.Extinguish();
                }

                fireballs.Clear();
            }

            public void SetEntities(int baseIndex)
            {
                BaseIndex = baseIndex;
                KillTrees();
                setupRoutine = StartCoroutine(EntitySetup());
            }

            private Vector3 GetCenterFromMulitplePoints()
            {
                if (foundations.Count <= 1)
                {
                    return transform.position;
                }

                float x = 0f;
                float z = 0f;

                foreach (var position in foundations)
                {
                    x += position.x;
                    z += position.z;
                }

                var vector = new Vector3(x / foundations.Count, 0f, z / foundations.Count);

                vector.y = Mathf.Max(GetSpawnHeight(vector), TerrainMeta.WaterMap.GetHeight(vector));

                foundations.Clear();

                return vector;
            }

            private void CreateSpheres()
            {
                if (Options.SphereAmount > 0)
                {
                    for (int i = 0; i < Options.SphereAmount; i++)
                    {
                        var sphere = GameManager.server.CreateEntity(Backbone.Path.Sphere, Location, default(Quaternion), true) as SphereEntity;

                        if (sphere == null)
                        {
                            break;
                        }

                        sphere.currentRadius = 1f;
                        sphere.Spawn();
                        sphere.LerpRadiusTo(Options.ProtectionRadius * 2f, Options.ProtectionRadius * 0.75f);
                        spheres.Add(sphere);
                    }
                }
            }

            private bool CreateZoneWalls()
            {
                var center = new Vector3(Location.x, Location.y, Location.z);
                string prefab = Options.ArenaWalls.Stone ? Backbone.Path.HighExternalStoneWall : Backbone.Path.HighExternalWoodenWall;
                float maxHeight = -200f;
                float minHeight = 200f;
                float ufoY = center.y;
                float y = center.y;
                int raycasts = Mathf.CeilToInt(360 / Radius * 0.1375f);

                foreach (var position in GetCircumferencePositions(center, Radius, raycasts))
                {
                    float w = TerrainMeta.WaterMap.GetHeight(position);
                    maxHeight = Mathf.Max(Mathf.Max(position.y, maxHeight), w);
                    minHeight = Mathf.Min(position.y, minHeight);
                    center.y = minHeight;
                    ufoY = minHeight;
                }

                float gap = prefab == Backbone.Path.HighExternalStoneWall ? 0.3f : 0.5f;
                int stacks = Mathf.CeilToInt((maxHeight - minHeight) / 6f) + Math.Max(1, Options.ArenaWalls.Stacks);
                float next = 360 / Radius - gap;
                float j = Math.Max(1, Options.ArenaWalls.Stacks) * 6f + 6f;

                for (int i = 0; i < stacks; i++)
                {
                    foreach (var position in GetCircumferencePositions(center, Radius, next, Options.ArenaWalls.UseUFOWalls ? ufoY : center.y))
                    {
                        float groundHeight = GetSpawnHeight(new Vector3(position.x, position.y + 6f, position.z));

                        if (groundHeight > position.y + 9f)
                        {
                            continue;
                        }

                        if (Options.ArenaWalls.LeastAmount)
                        {
                            //float h = GetGroundHeight(position);
                            float h = TerrainMeta.HeightMap.GetHeight(position);

                            if (position.y - groundHeight > j && position.y < h)
                            {
                                continue;
                            }
                        }

                        var e = GameManager.server.CreateEntity(prefab, position, default(Quaternion), false);

                        if (e != null)
                        {
                            e.OwnerID = 0;
                            e.transform.LookAt(Options.ArenaWalls.UseUFOWalls ? new Vector3(center.x, y, center.z) : center, Vector3.up);
                            e.enableSaving = false;
                            e.Spawn();
                            e.gameObject.SetActive(true);

                            if (CanSetupEntity(e))
                            {
                                if (!Backbone.Plugin.NetworkList.Contains(e.net.ID))
                                {
                                    Backbone.Plugin.NetworkList.Add(e.net.ID);
                                }

                                if (Backbone.Plugin.Bases.ContainsKey(BaseIndex) && !Backbone.Plugin.Bases[BaseIndex].Contains(e))
                                {
                                    Backbone.Plugin.Bases[BaseIndex].Add(e);
                                }
                            }
                        }
                        else return false;

                        if (stacks == i - 1)
                        {
                            RaycastHit hit;
                            if (Physics.Raycast(new Vector3(position.x, position.y + 6f, position.z), Vector3.down, out hit, 12f, Layers.Mask.World))
                            {
                                stacks++;
                            }
                        }
                    }

                    center.y += 6f;
                    ufoY += 6f;
                }

                return true;
            }

            private void KillTrees()
            {
                int hits = Physics.OverlapSphereNonAlloc(Location, Radius, Vis.colBuffer, Layers.Mask.Tree | Layers.Mask.World, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < hits; i++)
                {
                    var e = Vis.colBuffer[i].ToBaseEntity() as TreeEntity;

                    if (e != null && !e.IsDestroyed)
                    {
                        e.Kill();
                    }

                    Vis.colBuffer[i] = null;
                }
            }

            private void DisableBags()
            {
                foreach (var bag in SleepingBag.sleepingBags)
                {
                    if (Distance2D(bag.transform.position, Location) > Options.ProtectionRadius)
                    {
                        continue;
                    }

                    bag.unlockTime = float.PositiveInfinity;
                }
            }

            private void EnableBags()
            {
                foreach (var bag in SleepingBag.sleepingBags)
                {
                    if (bag.unlockTime == float.PositiveInfinity)
                    {
                        bag.unlockTime = 0f;
                    }
                }
            }

            private IEnumerator EntitySetup()
            {
                if (!Backbone.Plugin.Bases.ContainsKey(BaseIndex))
                {
                    yield break;
                }

                var list = new List<BaseEntity>(Backbone.Plugin.Bases[BaseIndex]);

                foreach (var e in list)
                {
                    if (!CanSetupEntity(e))
                    {
                        yield return null;
                        continue;
                    }

                    if (!Backbone.Plugin.NetworkList.Contains(e.net.ID))
                    {
                        Backbone.Plugin.NetworkList.Add(e.net.ID);
                    }

                    if (e.net.ID < NetworkID)
                    {
                        NetworkID = e.net.ID;
                    }

                    e.OwnerID = 0;

                    if (!Options.AllowPickup && e is BaseCombatEntity)
                    {
                        SetupPickup(e as BaseCombatEntity);
                    }

                    if (e is StorageContainer)
                    {
                        SetupContainer(e as StorageContainer);
                    }
                    else if (e is ContainerIOEntity)
                    {
                        SetupIO(e as ContainerIOEntity);
                    }
                    else if (e is BaseLock)
                    {
                        SetupLock(e);
                    }

                    if (e is BaseOven)
                    {
                        ovens.Add(e as BaseOven);
                    }
                    else if (e is SearchLight)
                    {
                        SetupSearchLight(e as SearchLight);
                    }
                    else if (e is BuildingBlock)
                    {
                        SetupBuildingBlock(e as BuildingBlock);
                    }
                    else if (e is TeslaCoil)
                    {
                        SetupTeslaCoil(e as TeslaCoil);
                    }
                    else if (e is Igniter)
                    {
                        SetupIgniter(e as Igniter);
                    }
                    else if (e is AutoTurret)
                    {
                        SetupTurret(e as AutoTurret);
                    }
                    else if (e is GunTrap)
                    {
                        SetupGunTrap(e as GunTrap);
                    }
                    else if (e is FogMachine)
                    {
                        SetupFogMachine(e as FogMachine);
                    }
                    else if (e is FlameTurret)
                    {
                        SetupFlameTurret(e as FlameTurret);
                    }
                    else if (e is SamSite)
                    {
                        SetupSamSite(e as SamSite);
                    }
                    else if (e is Door)
                    {
                        SetupDoor(e as Door);
                    }
                    else if (e is BuildingPrivlidge)
                    {
                        SetupBuildingPriviledge(e as BuildingPrivlidge);
                    }
                    else if (e is SleepingBag)
                    {
                        SetupSleepingBag(e as SleepingBag);
                    }

                    yield return null;
                }

                yield return Coroutines.WaitForSeconds(2f);

                setupRoutine = null;
                list.Clear();
                FinishSetup();
            }
            
            private void SetupPickup(BaseCombatEntity e)
            {
                e.pickup.enabled = false;
            }

            private void SetupContainer(StorageContainer container)
            {
                if (container is BoxStorage)
                {
                    if (skinId == 0uL)
                    {
                        if (_config.Skins.PresetSkin != 0uL)
                        {
                            skinId = _config.Skins.PresetSkin;
                            container.skinID = skinId;
                        }
                        else if (_config.Skins.RandomSkins && Backbone.Path.BoxDefinition != null)
                        {
                            var skins = GetItemSkins(Backbone.Path.BoxDefinition);

                            if (skins.Count > 0)
                            {
                                skinId = skins.GetRandom();
                                container.skinID = skinId;
                            }
                        }
                    }

                    if (Options.SetSkins && container.skinID != skinId)
                    {
                        container.skinID = skinId;
                        container.SendNetworkUpdate(BasePlayer.NetworkQueue.Update);
                    }

                    _containers.Add(container);
                }

                if (Options.SkipTreasureLoot && ShouldAddContainer(container) && !_containers.Contains(container))
                {
                    _containers.Add(container);
                }

                container.dropChance = 0f;
                container.inventory.SetFlag(ItemContainer.Flag.NoItemInput, true);
            }

            private void SetupIO(ContainerIOEntity io)
            {
                io.dropChance = 0f;
                io.inventory.SetFlag(ItemContainer.Flag.NoItemInput, true);
            }

            private void SetupLock(BaseEntity e, bool justCreated = false)
            {
                if (e is CodeLock)
                {
                    var codeLock = e as CodeLock;

                    if (_config.Settings.Management.RandomCodes || justCreated)
                    {
                        codeLock.code = UnityEngine.Random.Range(1000, 9999).ToString();
                        codeLock.hasCode = true;
                        codeLock.guestCode = string.Empty;
                        codeLock.hasGuestCode = false;
                        codeLock.guestPlayers.Clear();
                        codeLock.whitelistPlayers.Clear();
                    }

                    codeLock.SetFlag(BaseEntity.Flags.Locked, true);
                }
                else if (e is KeyLock)
                {
                    var keyLock = e as KeyLock;

                    if (_config.Settings.Management.RandomCodes)
                    {
                        keyLock.keyCode = UnityEngine.Random.Range(1, 100000);
                    }

                    keyLock.OwnerID = 0;
                    keyLock.firstKeyCreated = true;
                    keyLock.SetFlag(BaseEntity.Flags.Locked, true);
                }
            }

            private void SetupSearchLight(SearchLight light)
            {
                if (!_config.Settings.Management.Lights && !_config.Settings.Management.AlwaysLights)
                {
                    return;
                }

                lights.Add(light);

                light.enabled = false;

                if (light.inventory.GetSlot(0) == null)
                {
                    Item item = ItemManager.Create(light.fuelType, 5);

                    if (!item.MoveToContainer(light.inventory))
                    {
                        item.Remove();
                    }
                }

                light.secondsRemaining = 10000f;
            }

            private void SetupBuildingBlock(BuildingBlock block)
            {
                if (Options.Tiers.Any())
                {
                    ChangeTier(block);
                }

                block.StopBeingDemolishable();
                block.StopBeingRotatable();

                if (block.transform == null)
                {
                    return;
                }

                if (block.prefabID == 3234260181 || block.prefabID == 72949757) // triangle and square foundations
                {
                    foundations.Add(block.transform.position);
                }
            }

            private void ChangeTier(BuildingBlock block)
            {
                if (Options.Tiers.HQM && block.grade != BuildingGrade.Enum.TopTier)
                {
                    SetGrade(block, BuildingGrade.Enum.TopTier);
                }
                else if (Options.Tiers.Metal && block.grade != BuildingGrade.Enum.Metal)
                {
                    SetGrade(block, BuildingGrade.Enum.Metal);
                }
                else if (Options.Tiers.Stone && block.grade != BuildingGrade.Enum.Stone)
                {
                    SetGrade(block, BuildingGrade.Enum.Stone);
                }
                else if (Options.Tiers.Wooden && block.grade != BuildingGrade.Enum.Wood)
                {
                    SetGrade(block, BuildingGrade.Enum.Wood);
                }
            }

            private void SetGrade(BuildingBlock block, BuildingGrade.Enum grade)
            {
                block.SetGrade(grade);
                block.SetHealthToMax();
                block.SendNetworkUpdate();
                block.UpdateSkin();
            }

            private void SetupTeslaCoil(TeslaCoil tc)
            {
                tc.maxDischargeSelfDamageSeconds = 0f;
            }

            private void SetupIgniter(Igniter igniter)
            {
                igniter.SelfDamagePerIgnite = 0f;
            }

            private void SetupTurret(AutoTurret turret)
            {
                turret.authorizedPlayers.Clear();
                turrets.Add(turret);
                weapons.Add(turret);

                if (Options.RemoveTurretWeapon)
                {
                    turret.AttachedWeapon = null;
                    Item slot = turret.inventory.GetSlot(0);

                    if (slot != null && (slot.info.category == ItemCategory.Weapon || slot.info.category == ItemCategory.Fun))
                    {
                        slot.RemoveFromContainer();
                        slot.Remove();
                    }
                }

                if (turret.AttachedWeapon == null)
                {
                    var itemToCreate = ItemManager.FindItemDefinition(Options.AutoTurretShortname);

                    if (itemToCreate != null)
                    {
                        Item item = ItemManager.Create(itemToCreate, 1, (ulong)itemToCreate.skins.GetRandom().id);

                        if (!item.MoveToContainer(turret.inventory, 0, false))
                        {
                            item.Remove();
                        }
                    }
                }

                turret.UpdateAttachedWeapon();
                turret.InitiateStartup();

                if (_config.Weapons.Ammo.AutoTurret > 0)
                {
                    turret.Invoke(() => FillAmmoTurret(turret), 0.2f);
                }

                if (_config.Weapons.InfiniteAmmo.AutoTurret)
                {
                    turret.inventory.onItemAddedRemoved += new Action<Item, bool>(OnWeaponItemAddedRemoved);
                }
            }

            private void SetupGunTrap(GunTrap gt)
            {
                weapons.Add(gt);

                if (_config.Weapons.Ammo.GunTrap > 0)
                {
                    FillAmmoGunTrap(gt);
                }

                if (_config.Weapons.InfiniteAmmo.GunTrap)
                {
                    gt.inventory.onItemAddedRemoved += new Action<Item, bool>(OnWeaponItemAddedRemoved);
                }
            }

            private void SetupFogMachine(FogMachine fm)
            {
                weapons.Add(fm);

                if (_config.Weapons.Ammo.FogMachine > 0)
                {
                    FillAmmoFogMachine(fm);
                }

                if (_config.Weapons.InfiniteAmmo.FogMachine)
                {
                    fm.fuelPerSec = 0f;
                }
            }

            private void SetupFlameTurret(FlameTurret ft)
            {
                if (_config.Weapons.Ammo.FlameTurret > 0)
                {
                    FillAmmoFlameTurret(ft);
                }

                if (_config.Weapons.InfiniteAmmo.FlameTurret)
                {
                    ft.fuelPerSec = 0f;
                }
            }

            private void SetupSamSite(SamSite ss)
            {
                weapons.Add(ss);

                ss.UpdateHasPower(25, 1);

                if (_config.Weapons.SamSiteRepair > 0f)
                {
                    ss.staticRespawn = true;
                    ss.InvokeRepeating(ss.SelfHeal, _config.Weapons.SamSiteRepair * 60f, _config.Weapons.SamSiteRepair * 60f);
                }
                
                ss.scanRadius = Mathf.Max(150f, Options.ProtectionRadius);

                if (_config.Weapons.Ammo.SamSite > 0)
                {
                    FillAmmoSamSite(ss);
                }

                if (_config.Weapons.InfiniteAmmo.SamSite)
                {
                    ss.inventory.onItemAddedRemoved += new Action<Item, bool>(OnWeaponItemAddedRemoved);
                }
            }

            private void SetupDoor(Door door)
            {
                if (Options.DoorLock)
                {
                    CreateLock(door);
                }

                door.SetFlag(BaseEntity.Flags.Open, false);
                door.SendNetworkUpdateImmediate();
            }

            private void CreateLock(Door door)
            {
                var slot = door.GetSlot(BaseEntity.Slot.Lock) as BaseLock;

                if (slot == null)
                {
                    CreateCodeLock(door);
                    return;
                }
                
                var keyLock = slot.GetComponent<KeyLock>();

                if (keyLock.IsValid() && !keyLock.IsDestroyed)
                {
                    keyLock.SetParent(null);
                    keyLock.Kill();
                }

                CreateCodeLock(door);
            }

            private void CreateCodeLock(Door door)
            {
                var codeLock = GameManager.server.CreateEntity(Backbone.Path.CodeLock, default(Vector3), default(Quaternion), true) as CodeLock;

                codeLock.gameObject.Identity();
                codeLock.SetParent(door, BaseEntity.Slot.Lock.ToString().ToLower());
                codeLock.OnDeployed(door);
                codeLock.enableSaving = false;
                codeLock.Spawn();
                door.SetSlot(BaseEntity.Slot.Lock, codeLock);

                SetupLock(codeLock, true);
            }

            private void SetupBuildingPriviledge(BuildingPrivlidge priv)
            {
                priv.authorizedPlayers.Clear();
                priv.SendNetworkUpdate(BasePlayer.NetworkQueue.Update);
                privs.Add(priv);
            }

            private void SetupSleepingBag(SleepingBag bag)
            {
                bag.deployerUserID = 0uL;
                bag.niceName = "You found an easter egg!";
            }

            private bool ShouldAddContainer(StorageContainer container)
            {
                return container is BoxStorage || container is BuildingPrivlidge;
            }

            private void FinishSetup()
            {
                if (IsUnloading)
                {
                    return;
                }

                Location = GetCenterFromMulitplePoints();
                transform.position = Location;

                var collider = gameObject.GetComponent<SphereCollider>() ?? gameObject.AddComponent<SphereCollider>();
                collider.radius = Options.ProtectionRadius;
                collider.isTrigger = true;
                gameObject.layer = (int)Layer.Trigger;

                if (Options.ArenaWalls.Enabled)
                {
                    CreateZoneWalls();
                }
                
                if (Options.Levels.Level1.Amount > 0)
                {
                    InvokeRepeating(StartPlayingWithFire, 2f, 2f);
                }

                if (Backbone.Plugin.NightLantern == null)
                {
                    if (_config.Settings.Management.AlwaysLights)
                    {
                        Lights(true);
                    }
                    else if (_config.Settings.Management.Lights)
                    {
                        InvokeRepeating("Lights", 5f, 60f);
                    }
                }
                
                CreateGenericMarker();
                CreateSpheres();
                DisableBags();
                SetupLoot();
                MakeAnnouncements();
                CheckDespawn();
                SetupContainers();
                Subscribe();
                InvokeRepeating(UpdateMarker, 0f, 30f);
                InvokeRepeating(Protector, 1f, 1f);
            }

            private void CreateGenericMarker()
            {
                if (_config.Settings.Markers.UseExplosionMarker || _config.Settings.Markers.UseVendingMarker)
                {
                    genericMarker = GameManager.server.CreateEntity(Backbone.Path.RadiusMarker, Location) as MapMarkerGenericRadius;

                    if (genericMarker != null)
                    {
                        genericMarker.alpha = 0.75f;
                        genericMarker.color2 = Options.Difficulty == 0 ? Color.green : Options.Difficulty == 1 ? Color.yellow : Color.red;
                        genericMarker.radius = Mathf.Min(2.5f, _config.Settings.Markers.Radius);
                        genericMarker.Spawn();
                        genericMarker.SendUpdate();
                    }
                }
            }

            private void Subscribe()
            {
                if (IsUnloading)
                {
                    return;
                }

                if (Options.EnforceDurability)
                {
                    Backbone.Plugin.Subscribe(nameof(OnLoseCondition));
                }

                if (!Options.AllowPickup)
                {
                    Backbone.Plugin.Subscribe(nameof(CanPickupEntity));
                }

                if (_containers.Count > 0)
                {
                    Backbone.Plugin.Subscribe(nameof(OnEntityDeath));
                    Backbone.Plugin.Subscribe(nameof(OnEntityKill));
                }

                if (_containers.Count > 0)
                {
                    Backbone.Plugin.Subscribe(nameof(OnEntityGroundMissing));
                }

                if (Options.NPC.Enabled)
                {
                    if (Options.NPC.SpawnAmount < 1) Options.NPC.Enabled = false;
                    if (Options.NPC.SpawnAmount > 25) Options.NPC.SpawnAmount = 25;
                    if (Options.NPC.SpawnMinAmount < 1 || Options.NPC.SpawnMinAmount > Options.NPC.SpawnAmount) Options.NPC.SpawnMinAmount = 1;
                    if (Options.NPC.ScientistHealth < 100) Options.NPC.ScientistHealth = 100f;
                    if (Options.NPC.ScientistHealth > 5000) Options.NPC.ScientistHealth = 5000f;
                    if (Options.NPC.MurdererHealth < 100) Options.NPC.MurdererHealth = 100f;
                    if (Options.NPC.MurdererHealth > 5000) Options.NPC.MurdererHealth = 5000f;

                    Backbone.Plugin.Subscribe(nameof(OnNpcTarget));
                    Backbone.Plugin.Subscribe(nameof(OnPlayerCorpse));
                    Backbone.Plugin.Subscribe(nameof(OnPlayerDeath));
                    Invoke(SpawnNpcs, 1f);
                    SetupNpcKits();
                }

                if (!_config.Settings.Management.AllowTeleport)
                {
                    Backbone.Plugin.Subscribe(nameof(CanTeleport));
                    Backbone.Plugin.Subscribe(nameof(canTeleport));
                }

                if (_config.Settings.Management.BlockRestorePVP && AllowPVP)
                {
                    Backbone.Plugin.Subscribe(nameof(OnRestoreUponDeath));
                }
                else if (_config.Settings.Management.BlockRestorePVE && !AllowPVP)
                {
                    Backbone.Plugin.Subscribe(nameof(OnRestoreUponDeath));
                }

                if (_config.Settings.Management.UseOwners || _config.Settings.Buyable.UsePayLock)
                {
                    Backbone.Plugin.Subscribe(nameof(OnFriendAdded));
                    Backbone.Plugin.Subscribe(nameof(OnFriendRemoved));
                    Backbone.Plugin.Subscribe(nameof(OnClanUpdate));
                }

                Backbone.Plugin.Subscribe(nameof(CanEntityBeTargeted));
                Backbone.Plugin.Subscribe(nameof(CanEntityTrapTrigger));
                Backbone.Plugin.Subscribe(nameof(OnLootEntity));
                Backbone.Plugin.Subscribe(nameof(OnBuildRevertBlock));
                Backbone.Plugin.Subscribe(nameof(OnEntityBuilt));
                Backbone.Plugin.Subscribe(nameof(OnCupboardAuthorize));
                
                foreach (var x in Backbone.Plugin.Raids.Values)
                {
                    if (x.Options.DropTimeAfterLooting > 0)
                    {
                        Backbone.Plugin.Subscribe(nameof(OnLootEntityEnd));
                        break;
                    }
                }
            }

            private void MakeAnnouncements()
            {
                if (treasureItems.Count == 0)
                {
                    foreach (var container in _containers)
                    {
                        if (container is BoxStorage) // TODO: this should include cupboard count if applicable
                        {
                            foreach (Item item in container.inventory.itemList)
                            {
                                treasureItems.Add(string.Format("{0} ({1})", item.info.displayName.translated, item.amount));
                            }
                        }
                    }
                }

                if (treasureItems.Count > 12)
                {
                    string message = string.Format("{0} items", treasureItems.Count);
                    treasureItems.Clear();
                    treasureItems.Add(message);
                }

                string itemList = string.Join(", ", treasureItems.ToArray());
                var posStr = FormatGridReference(Location);

                /*if (Options.ExplosionModifier != 100f)
                {
                    if (Options.ExplosionModifier <= 75)
                    {
                        difficulty = modeVeryHard;
                    }
                    else if (Options.ExplosionModifier >= 150)
                    {
                        difficulty = modeVeryEasy;
                    }
                }*/

                Backbone.Plugin.Puts("{0} @ {1} : {2}", BaseName, posStr, itemList);

                if (_config.EventMessages.Opened || _config.GUIAnnouncement.Enabled && Backbone.Plugin.GUIAnnouncements != null && Backbone.Plugin.GUIAnnouncements.IsLoaded)
                {
                    if (!_config.EventMessages.AnnounceBuy && IsPayLocked)
                    {
                        return;
                    }

                    foreach (var target in BasePlayer.activePlayerList)
                    {
                        float distance = Mathf.Floor((target.transform.position - Location).magnitude);
                        string api = Backbone.GetMessage("RaidOpenMessage", target.UserIDString, DifficultyMode, posStr, distance, AllowPVP ? "PVP" : "PVE");
                        string message = owner.IsValid() ? string.Format("[{0}] {1}", owner.displayName, api) : api;

                        if (_config.EventMessages.Opened)
                        {
                            target.SendConsoleCommand("chat.add", 2, _config.Settings.ChatID, message);
                        }
                        
                        if (_config.GUIAnnouncement.Enabled && Backbone.Plugin.GUIAnnouncements != null && Backbone.Plugin.GUIAnnouncements.IsLoaded && distance <= _config.GUIAnnouncement.Distance)
                        {
                            Backbone.Plugin.GUIAnnouncements.Call("CreateAnnouncement", message, _config.GUIAnnouncement.TintColor, _config.GUIAnnouncement.TextColor, target);
                        }
                    }
                }
            }

            public void TryInvokeResetOwner()
            {
                if (_config.Settings.Buyable.ResetDuration > 0)
                {
                    CancelInvoke(ResetOwner);
                    Invoke(ResetOwner, _config.Settings.Buyable.ResetDuration * 60f);
                }
            }

            private void ResetOwner()
            {
                if (owner.IsValid() && owner.IsConnected)
                {
                    return;
                }

                owner = null;
                IsPayLocked = false;
                UpdateMarker();
            }

            private List<TreasureItem> GetLoot()
            {
                var loot = new List<TreasureItem>(Options.Loot);

                if (loot.Count < Options.TreasureAmount)
                {
                    switch (Options.Difficulty)
                    {
                        case 0:
                            {
                                if (_config.Treasure.LootEasy.Count > 0)
                                {
                                    loot.AddRange(new List<TreasureItem>(_config.Treasure.LootEasy));
                                }
                                break;
                            }
                        case 1:
                            {
                                if (_config.Treasure.LootMedium.Count > 0)
                                {
                                    loot.AddRange(new List<TreasureItem>(_config.Treasure.LootMedium));
                                }
                                break;
                            }
                        case 2:
                            {
                                if (_config.Treasure.LootHard.Count > 0)
                                {
                                    loot.AddRange(new List<TreasureItem>(_config.Treasure.LootHard));
                                }
                                break;
                            }
                    }
                }

                if (loot.Count < Options.TreasureAmount && ChestLoot.Count > 0)
                {
                    loot.AddRange(ChestLoot);
                }

                return loot;
            }

            private void SetupLoot()
            {
                var list = new List<StorageContainer>(_containers);

                foreach (var container in list)
                {
                    if (!container.IsValid() || container.IsDestroyed)
                    {
                        _containers.Remove(container);
                    }
                    else if (container.transform == null)
                    {
                        container.Kill();
                        _containers.Remove(container);
                    }
                }

                list.Clear();

                if (_containers.Count == 0)
                {
                    return;
                }

                if (_config.Settings.ExpansionMode && Backbone.Plugin.DangerousTreasures != null && Backbone.Plugin.DangerousTreasures.IsLoaded && Backbone.Plugin.DangerousTreasures.Version > new VersionNumber(1, 4, 0))
                {
                    var containers = new List<StorageContainer>();

                    foreach (var x in _containers)
                    {
                        if (x is BoxStorage)
                        {
                            containers.Add(x);
                        }
                    }

                    if (containers.Count > 0)
                    {
                        Backbone.Plugin.DangerousTreasures.Call("API_SetContainer", containers.GetRandom(), Radius, !Options.NPC.Enabled || Options.NPC.UseExpansionNpcs);
                    }

                    containers.Clear();
                }

                if (Options.SkipTreasureLoot)
                {
                    return;
                }

                var loot = GetLoot();

                loot.RemoveAll(ti => ti.amount <= 0);

                if (loot.Count == 0)
                {
                    Backbone.Plugin.Puts(Backbone.GetMessage("NoConfiguredLoot"));
                    return;
                }

                if (Options.EmptyAll)
                {
                    foreach (var container in _containers)
                    {
                        var items = new List<Item>(container.inventory.itemList);

                        foreach (Item item in items)
                        {
                            RemoveItem(item);
                        }

                        items.Clear();
                    }
                }

                if (_config.Settings.Management.IgnoreContainedLoot)
                {
                    _containers.RemoveAll(x => x.inventory.itemList.Count != 0);
                }

                Shuffle(loot);

                if (Options.AllowDuplicates)
                {
                    if (loot.Count < Options.TreasureAmount)
                    {
                        do
                        {
                            loot.Add(loot.GetRandom());
                        } while (loot.Count < Options.TreasureAmount);
                    }
                }
                else
                {
                    loot = new List<TreasureItem>(loot.Distinct(itemComparer));
                }

                if (loot.Count > Options.TreasureAmount)
                {
                    Shuffle(loot);
                    loot = new List<TreasureItem>(loot.Take(Options.TreasureAmount));
                }

                int amountSpawned = 0;

                if (Options.DivideLoot)
                {
                    var containers = new List<StorageContainer>(_containers);
                    bool flag = false;

                    foreach (var x in containers)
                    {
                        if (x.inventory.itemList.Count == 0)
                        {
                            flag = true;
                            break;
                        }
                    }

                    if (flag)
                    {
                        containers.RemoveAll(x => x.inventory.itemList.Count != 0);
                    }

                    int num = Options.TreasureAmount / containers.Count;

                    foreach (var container in containers)
                    {
                        amountSpawned += SpawnLoot(container, loot, num);
                    }

                    FinishDivision(containers, loot, ref amountSpawned, ref num);
                    containers.Clear();
                }
                else
                {
                    var containers = new List<StorageContainer>();

                    foreach (var x in _containers)
                    {
                        if (x.inventory.itemList.Count == 0)
                        {
                            containers.Add(x);
                        }
                    }

                    if (containers.Count > 0)
                    {
                        containers.Sort((x, y) => y.inventory.capacity.CompareTo(x.inventory.capacity));
                        amountSpawned = SpawnLoot(containers[0], loot, Options.TreasureAmount);
                        containers.Clear();
                    }
                }

                if (amountSpawned == 0)
                {
                    Backbone.Plugin.Puts(Backbone.GetMessage("NoLootSpawned"));
                }
            }

            private void FinishDivision(List<StorageContainer> containers, List<TreasureItem> loot, ref int amountSpawned, ref int num)
            {
                if (amountSpawned < Options.TreasureAmount)
                {
                    int amountLeft = Options.TreasureAmount - amountSpawned;

                    containers.RemoveAll(container => container.inventory.itemList.Count + (amountLeft / containers.Count) > container.inventory.capacity);

                    if (containers.Count == 0)
                    {
                        return;
                    }

                    num = amountLeft / containers.Count;

                    foreach (var container in containers)
                    {
                        if (containers.Count % 1 == 0)
                        {
                            num++;
                        }

                        if (num + amountSpawned > Options.TreasureAmount)
                        {
                            num = Options.TreasureAmount - amountSpawned;
                        }

                        int spawned = SpawnLoot(container, loot, num);
                        amountSpawned += spawned;

                        if (amountSpawned == Options.TreasureAmount)
                        {
                            return;
                        }
                    }
                }
            }

            private int SpawnLoot(StorageContainer container, List<TreasureItem> loot, int total)
            {
                int amountSpawned = 0;

                if (total > container.inventory.capacity)
                {
                    total = container.inventory.capacity;
                }

                for (int j = 0; j < total; j++)
                {
                    if (loot.Count == 0)
                    {
                        break;
                    }

                    var lootItem = loot.GetRandom();

                    loot.Remove(lootItem);

                    if (lootItem.amount <= 0)
                    {
                        continue;
                    }

                    var def = ItemManager.FindItemDefinition(lootItem.shortname);

                    if (def == null)
                    {
                        Backbone.Plugin.PrintError("Invalid shortname in config: {0}", lootItem.shortname);
                        continue;
                    }

                    ulong skin = lootItem.skin;

                    if (_config.Treasure.RandomSkins && skin == 0)
                    {
                        var skins = GetItemSkins(def);

                        if (skins.Count > 0)
                        {
                            skin = skins.GetRandom();
                        }
                    }

                    int amount = lootItem.amount;

                    if (lootItem.amountMin > 0 && lootItem.amountMin < lootItem.amount)
                    {
                        amount = UnityEngine.Random.Range(lootItem.amountMin, lootItem.amount);
                    }

                    amount = GetPercentIncreasedAmount(amount);

                    Item item = ItemManager.Create(def, amount, skin);

                    if (MoveToOven(item, ref amountSpawned))
                    {
                        continue;
                    }

                    if (item.MoveToContainer(container.inventory, -1, false))
                    {
                        amountSpawned++;
                    }
                    else item.Remove();
                }

                return amountSpawned;
            }

            private bool MoveToOven(Item item, ref int amountSpawned)
            {
                if (ovens.Count == 0 || item.info.shortname.EndsWith(".cooked") || !item.info.GetComponent<ItemModBurnable>())
                {
                    return false;
                }

                BaseOven oven = null;

                if (ovens.Count > 1)
                {
                    Shuffle(ovens);
                }

                foreach (var x in ovens)
                {
                    if (x.inventory.itemList.Count < x.inventory.capacity)
                    {
                        oven = x;
                        break;
                    }
                }

                if (!oven.IsValid())
                {
                    return false;
                }

                if (item.MoveToContainer(oven.inventory, -1, true))
                {
                    if (!oven.IsOn())
                    {
                        oven.SetFlag(BaseEntity.Flags.On, true, false, true);
                    }

                    if (!item.HasFlag(global::Item.Flag.OnFire))
                    {
                        item.SetFlag(global::Item.Flag.OnFire, true);
                        item.MarkDirty();
                    }

                    amountSpawned++;
                    return true;
                }

                return false;
            }

            private void Lights(bool bypass = false)
            {
                if (lights.Count == 0 && ovens.Count == 0)
                {
                    CancelInvoke("Lights");
                    return;
                }

                if (bypass || (!lightsOn && !IsDayTime()))
                {
                    lights.RemoveAll(e => e == null);
                    ovens.RemoveAll(e => e == null);

                    foreach (var e in lights.Union(ovens))
                    {
                        if (!e.IsOn())
                        {
                            e.SetFlag(BaseEntity.Flags.On, true);
                        }
                    }

                    lightsOn = true;
                }
                else if (lightsOn && IsDayTime())
                {
                    lights.RemoveAll(e => e == null);
                    ovens.RemoveAll(e => e == null);

                    foreach (var e in lights.Union(ovens))
                    {
                        if (e.IsOn())
                        {
                            e.SetFlag(BaseEntity.Flags.On, false);
                        }
                    }

                    lightsOn = false;
                }
            }

            public bool IsDayTime() => TOD_Sky.Instance?.Cycle.DateTime.Hour >= 8 && TOD_Sky.Instance?.Cycle.DateTime.Hour < 20;

            public void Undo()
            {
                if (IsOpened)
                {
                    IsOpened = false;
                    Backbone.Plugin.UndoPaste(Location, this, BaseIndex);
                }
            }

            public static bool Has(BaseEntity entity)
            {
                foreach (var entry in Backbone.Plugin.Bases)
                {
                    if (entry.Value.Contains(entity))
                    {
                        return true;
                    }
                }

                return false;
            }

            public static bool Has(NPCPlayerApex player)
            {
                return player.IsValid() && Has(player.userID);
            }

            public static bool Has(ulong userID)
            {
                foreach (var raid in Backbone.Plugin.Raids.Values)
                {
                    foreach (var npc in raid.npcs)
                    {
                        if (npc.userID == userID)
                        {
                            return true;
                        }
                    }
                }

                return false;
            }

            public static int Get(RaidableType type)
            {
                int amount = 0;

                foreach (var x in Backbone.Plugin.Raids.Values)
                {
                    if (x.Type == type)
                    {
                        amount++;
                    }
                }

                return amount;
            }

            public static RaidableBase Get(ulong userID)
            {
                foreach (var raid in Backbone.Plugin.Raids.Values)
                {
                    foreach (var npc in raid.npcs)
                    {
                        if (npc.userID == userID)
                        {
                            return raid;
                        }
                    }
                }

                return null;
            }

            public static RaidableBase Get(Vector3 target)
            {
                foreach (var raid in Backbone.Plugin.Raids.Values)
                {
                    if (Distance2D(raid.Location, target) <= raid.Options.ProtectionRadius)
                    {
                        return raid;
                    }
                }

                return null;
            }

            public static RaidableBase Get(int baseIndex)
            {
                foreach (var raid in Backbone.Plugin.Raids.Values)
                {
                    if (raid.BaseIndex == baseIndex)
                    {
                        return raid;
                    }
                }

                return null;
            }

            public static RaidableBase Get(BaseEntity entity)
            {
                foreach (var entry in Backbone.Plugin.Bases)
                {
                    if (entry.Value.Contains(entity))
                    {
                        return Get(entry.Key);
                    }
                }

                return null;
            }

            public static RaidableBase Get(List<BaseEntity> entities)
            {
                if (entities == null || entities.Count == 0)
                {
                    return null;
                }

                foreach (var raid in Backbone.Plugin.Raids.Values)
                {
                    foreach (var e in entities)
                    {
                        if (e == null || e.IsDestroyed || e.transform == null)
                        {
                            continue;
                        }

                        if (Distance2D(raid.PastedLocation, e.transform.position) <= raid.Radius)
                        {
                            return raid;
                        }
                    }
                }

                return null;
            }

            public static bool IsTooClose(Vector3 target, float radius)
            {
                foreach (var raid in Backbone.Plugin.Raids.Values)
                {
                    if (Distance2D(raid.Location, target) <= radius)
                    {
                        return true;
                    }
                }

                return false;
            }

            private List<ulong> GetItemSkins(ItemDefinition def)
            {
                List<ulong> skins;
                if (!Backbone.Plugin.Skins.TryGetValue(def.shortname, out skins))
                {
                    Backbone.Plugin.Skins[def.shortname] = skins = ExtractItemSkins(def, skins);
                }

                return skins;
            }

            private List<ulong> ExtractItemSkins(ItemDefinition def, List<ulong> skins)
            {
                skins = new List<ulong>();

                foreach (var skin in def.skins)
                {
                    skins.Add(Convert.ToUInt64(skin.id));
                }

                if (Backbone.Plugin.WorkshopSkins.ContainsKey(def.shortname))
                {
                    skins.AddRange(Backbone.Plugin.WorkshopSkins[def.shortname]);
                    Backbone.Plugin.WorkshopSkins.Remove(def.shortname);
                }

                return skins;
            }

            private void AuthorizePlayer(NPCPlayerApex apex)
            {
                turrets.RemoveAll(x => !x.IsValid() || x.IsDestroyed);

                foreach (var turret in turrets)
                {
                    turret.authorizedPlayers.Add(new ProtoBuf.PlayerNameID
                    {
                        userid = apex.userID,
                        username = apex.displayName
                    });

                    turret.SendNetworkUpdate(BasePlayer.NetworkQueue.Update);
                }

                privs.RemoveAll(x => !x.IsValid() || x.IsDestroyed);

                foreach (var priv in privs)
                {
                    priv.authorizedPlayers.Add(new ProtoBuf.PlayerNameID
                    {
                        userid = apex.userID,
                        username = apex.displayName
                    });

                    priv.SendNetworkUpdate(BasePlayer.NetworkQueue.Update);
                }
            }

            private bool IsInSameClan(string playerId, string targetId)
            {
                if (Backbone.Plugin.Clans == null || !Backbone.Plugin.Clans.IsLoaded)
                {
                    return false;
                }

                List<string> playerList;
                if (!_clans.TryGetValue(playerId, out playerList))
                {
                    _clans[playerId] = playerList = new List<string>();
                }

                List<string> targetList;
                if (!_clans.TryGetValue(targetId, out targetList))
                {
                    _clans[targetId] = targetList = new List<string>();
                }

                if (playerList.Contains(targetId) || targetList.Contains(playerId))
                {
                    return true;
                }

                string playerClan = Backbone.Plugin.Clans.Call<string>("GetClanOf", playerId);

                if (string.IsNullOrEmpty(playerClan))
                {
                    return false;
                }

                string targetClan = Backbone.Plugin.Clans.Call<string>("GetClanOf", targetId);

                if (string.IsNullOrEmpty(targetClan))
                {
                    return false;
                }

                if (playerClan == targetClan)
                {
                    playerList.Add(targetId);
                    targetList.Add(playerId);
                    return true;
                }

                return false;
            }

            public void UpdateClans()
            {
                _clans.Clear();
            }

            public void UpdateFriends(string playerId, string targetId, bool added)
            {
                if (added)
                {
                    List<string> playerList;
                    if (_friends.TryGetValue(playerId, out playerList))
                    {
                        playerList.Add(targetId);
                    }

                    List<string> targetList;
                    if (_friends.TryGetValue(targetId, out targetList))
                    {
                        targetList.Add(playerId);
                    }
                }
                else
                {
                    List<string> playerList;
                    if (_friends.TryGetValue(playerId, out playerList))
                    {
                        playerList.Remove(targetId);
                    }

                    List<string> targetList;
                    if (_friends.TryGetValue(targetId, out targetList))
                    {
                        targetList.Remove(playerId);
                    }
                }
            }

            private bool IsFriends(string playerId, string targetId)
            {
                if (Backbone.Plugin.Friends == null || !Backbone.Plugin.Friends.IsLoaded)
                {
                    return false;
                }

                List<string> playerList;
                if (!_friends.TryGetValue(playerId, out playerList))
                {
                    _friends[playerId] = playerList = new List<string>();
                }

                List<string> targetList;
                if (!_friends.TryGetValue(targetId, out targetList))
                {
                    _friends[targetId] = targetList = new List<string>();
                }

                if (playerList.Contains(targetId) || targetList.Contains(playerId))
                {
                    return true;
                }

                if (Backbone.Plugin.Friends.Call<bool>("AreFriends", playerId, targetId))
                {
                    playerList.Add(targetId);
                    targetList.Add(playerId);
                    return true;
                }

                return false;
            }

            public bool IsOnSameTeam(ulong playerId, ulong targetId)
            {
                RelationshipManager.PlayerTeam team1;
                if (!RelationshipManager.Instance.playerToTeam.TryGetValue(playerId, out team1))
                {
                    return false;
                }

                RelationshipManager.PlayerTeam team2;
                if (!RelationshipManager.Instance.playerToTeam.TryGetValue(targetId, out team2))
                {
                    return false;
                }

                return team1.teamID == team2.teamID;
            }

            public bool IsAlly(BasePlayer player)
            {
                if (!IsOwnerLocked || player.IsAdmin || player == owner || allies.Contains(player.userID))
                {
                    return true;
                }

                bool flag = false;

                if (IsOnSameTeam(player.userID, owner.userID))
                {
                    flag = true;
                }
                else if (IsInSameClan(player.UserIDString, owner.UserIDString))
                {
                    flag = true;
                }
                else if (IsFriends(player.UserIDString, owner.UserIDString))
                {
                    flag = true;
                }

                if (flag)
                {
                    allies.Add(player.userID);
                }

                return flag;
            }

            public static void StopUsingWand(BasePlayer player)
            {
                if (!_config.Settings.NoWizardry || !Backbone.Plugin.Wizardry || !Backbone.Plugin.Wizardry.IsLoaded)
                {
                    return;
                }

                if (player.svActiveItemID == 0)
                {
                    return;
                }

                Item item = player.GetActiveItem();

                if (item?.info.shortname != "knife.bone")
                {
                    return;
                }

                if (!item.MoveToContainer(player.inventory.containerMain))
                {
                    item.DropAndTossUpwards(player.GetDropPosition() + player.transform.forward, 2f);
                    Backbone.Message(player, "TooPowerfulDrop", player.UserIDString);
                }
                else Backbone.Message(player, "TooPowerful", player.UserIDString);
            }

            private int targetLayer { get; set; } = ~(Layers.Mask.Invisible | Layers.Mask.Trigger | Layers.Mask.Prevent_Movement | Layers.Mask.Prevent_Building); // credits ZoneManager

            private void RemovePlayer(BasePlayer player)
            {
                if (player.isMounted)
                {
                    RemoveMountable(player.GetMounted());
                    return;
                }

                var position = ((player.transform.position.XZ3D() - Location.XZ3D()).normalized * (Options.ProtectionRadius + 10f)) + Location; // credits ZoneManager
                float y = TerrainMeta.HighestPoint.y + 250f;
                RaycastHit hit;
                if (Physics.Raycast(position + new Vector3(0f, y, 0f), Vector3.down, out hit, position.y + y + 1f, targetLayer, QueryTriggerInteraction.Ignore))
                {
                    position.y = hit.point.y;
                }
                else position.y = GetSpawnHeight(position) + 0.5f;

                player.Teleport(position);
                player.SendNetworkUpdateImmediate();
            }

            private bool CanEject()
            {
                if (IsPayLocked && AllowPVP && Options.EjectPurchasedPVP)
                {
                    return true;
                }

                if (IsPayLocked && !AllowPVP && Options.EjectPurchasedPVE)
                {
                    return true;
                }

                if (IsOwnerActive && AllowPVP && Options.EjectLockedPVP)
                {
                    return true;
                }

                if (IsOwnerActive && !AllowPVP && Options.EjectLockedPVE)
                {
                    return true;
                }

                return false;
            }

            private bool RemoveMountable(BaseMountable m)
            {
                if (!CanEject())
                {
                    return false;
                }

                var player = GetMountedPlayer(m);

                if (player.IsValid() && IsAlly(player))
                {
                    return false;
                }

                var position = ((m.transform.position.XZ3D() - Location.XZ3D()).normalized * (Options.ProtectionRadius + 10f)) + Location; // credits ZoneManager
                float y = TerrainMeta.HighestPoint.y + 250f;

                RaycastHit hit;
                if (Physics.Raycast(position + new Vector3(0f, y, 0f), Vector3.down, out hit, position.y + y + 1f, targetLayer, QueryTriggerInteraction.Ignore))
                {
                    position.y = hit.point.y;
                }
                else position.y = GetSpawnHeight(position);

                if (m.transform.position.y > position.y)
                {
                    position.y = m.transform.position.y;
                }

                m.transform.position = m.mountAnchor.transform.position = position;
                m.TransformChanged();

                return true;
            }

            public bool CanSetupEntity(BaseEntity e)
            {
                BaseEntity.saveList.Remove(e);

                if (!e.IsValid() || e.IsDestroyed)
                {
                    if (e != null)
                    {
                        e.enableSaving = false;
                    }

                    return false;
                }

                e.enableSaving = false;
                return true;
            }

            public void RespawnNpc()
            {
                if (!IsOpened)
                {
                    return;
                }

                Invoke(() => RespawnNpcNow(true), Mathf.Max(1f, Options.RespawnRate));
            }

            public void RespawnNpcNow(bool flag)
            {
                if (!IsOpened)
                {
                    return;
                }

                SpawnNPC(Options.NPC.SpawnScientistsOnly ? false : Options.NPC.SpawnBoth ? UnityEngine.Random.value > 0.5f : Options.NPC.SpawnMurderers);

                if (flag && npcs.Count < npcSpawnedAmount)
                {
                    RespawnNpc();
                }
            }

            public void SpawnNpcs()
            {
                if (!Options.NPC.Enabled || (Options.NPC.UseExpansionNpcs && _config.Settings.ExpansionMode && Backbone.Plugin.DangerousTreasures != null && Backbone.Plugin.DangerousTreasures.IsLoaded))
                {
                    return;
                }

                int amount = Options.NPC.SpawnRandomAmount && Options.NPC.SpawnAmount > 1 ? UnityEngine.Random.Range(Options.NPC.SpawnMinAmount, Options.NPC.SpawnAmount) : Options.NPC.SpawnAmount;

                for (int i = 0; i < amount; i++)
                {
                    SpawnNPC(Options.NPC.SpawnScientistsOnly ? false : Options.NPC.SpawnBoth ? UnityEngine.Random.value > 0.5f : Options.NPC.SpawnMurderers);
                }

                npcSpawnedAmount = npcs.Count;
            }

            private Vector3 FindPointOnNavmesh(Vector3 target, float radius)
            {
                int tries = 0;
                NavMeshHit navHit;

                while (++tries < 100)
                {
                    if (NavMesh.SamplePosition(target, out navHit, radius, 1))
                    {
                        float y = TerrainMeta.HeightMap.GetHeight(navHit.position);

                        if (IsInOrOnRock(navHit.position, "rock_") || navHit.position.y < y)
                        {
                            continue;
                        }

                        if (TerrainMeta.WaterMap.GetHeight(navHit.position) - y > 3.5f)
                        {
                            continue;
                        }

                        if ((navHit.position - Location).magnitude > Mathf.Max(radius * 2f, Options.ProtectionRadius) - 2.5f)
                        {
                            continue;
                        }

                        return navHit.position;
                    }
                }

                return Vector3.zero;
            }

            private bool IsRockTooLarge(Bounds bounds, float extents = 1.5f)
            {
                return bounds.extents.Max() > extents;
            }

            private bool IsInOrOnRock(Vector3 position, string meshName, float radius = 2f)
            {
                bool flag = false;
                int hits = Physics.OverlapSphereNonAlloc(position, radius, Vis.colBuffer, Layers.Mask.World, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < hits; i++)
                {
                    if (Vis.colBuffer[i].name.StartsWith(meshName) && IsRockTooLarge(Vis.colBuffer[i].bounds))
                    {
                        flag = true;
                    }

                    Vis.colBuffer[i] = null;
                }
                if (!flag)
                {
                    float y = TerrainMeta.HighestPoint.y + 250f;
                    RaycastHit hit;
                    if (Physics.Raycast(position, Vector3.up, out hit, y, Layers.Mask.World, QueryTriggerInteraction.Ignore))
                    {
                        if (hit.collider.name.StartsWith(meshName) && IsRockTooLarge(hit.collider.bounds)) flag = true;
                    }
                    if (!flag && Physics.Raycast(position, Vector3.down, out hit, y, Layers.Mask.World, QueryTriggerInteraction.Ignore))
                    {
                        if (hit.collider.name.StartsWith(meshName) && IsRockTooLarge(hit.collider.bounds)) flag = true;
                    }
                    if (!flag && Physics.Raycast(position + new Vector3(0f, y, 0f), Vector3.down, out hit, y + 1f, Layers.Mask.World, QueryTriggerInteraction.Ignore))
                    {
                        if (hit.collider.name.StartsWith(meshName) && IsRockTooLarge(hit.collider.bounds)) flag = true;
                    }
                }
                return flag;
            }

            private static NPCPlayerApex InstantiateEntity(Vector3 position, bool murd)
            {
                var prefabName = murd ? Backbone.Path.Murderer : Backbone.Path.Scientist;
                var prefab = GameManager.server.FindPrefab(prefabName);
                var go = Facepunch.Instantiate.GameObject(prefab, position, default(Quaternion));

                go.name = prefabName;
                SceneManager.MoveGameObjectToScene(go, Rust.Server.EntityScene);

                if (go.GetComponent<Spawnable>())
                {
                    Destroy(go.GetComponent<Spawnable>());
                }

                if (!go.activeSelf)
                {
                    go.SetActive(true);
                }

                return go.GetComponent<NPCPlayerApex>();
            }

            private List<Vector3> RandomWanderPositions
            {
                get
                {
                    var list = new List<Vector3>();
                    float maxRoamRange = Mathf.Max(Radius * 2f, Options.ProtectionRadius);

                    for (int i = 0; i < 10; i++)
                    {
                        var vector = FindPointOnNavmesh(GetRandomPoint(maxRoamRange), 15f);

                        if (vector != Vector3.zero)
                        {
                            list.Add(vector);
                        }
                    }

                    return list;
                }
            }

            private Vector3 GetRandomPoint(float radius)
            {
                var vector = Location + UnityEngine.Random.onUnitSphere * radius;

                vector.y = TerrainMeta.HeightMap.GetHeight(vector);

                return vector;
            }

            private NPCPlayerApex SpawnNPC(bool murd)
            {
                var list = RandomWanderPositions;

                if (list.Count == 0)
                    return null;

                var apex = InstantiateEntity(GetRandomPoint(Radius * 0.85f), murd);

                if (apex == null)
                    return null;

                apex.Spawn();
                apex.startHealth = murd ? Options.NPC.MurdererHealth : Options.NPC.ScientistHealth;
                apex.InitializeHealth(apex.startHealth, apex.startHealth);
                apex.CommunicationRadius = 0;
                apex.RadioEffect.guid = null;
                apex.displayName = Options.NPC.RandomNames.Count > 0 ? Options.NPC.RandomNames.GetRandom() : Facepunch.RandomUsernames.All.GetRandom();
                apex.Stats.AggressionRange = Options.NPC.AggressionRange;
                apex.Stats.DeaggroRange = Options.NPC.AggressionRange * 1.125f;
                apex.NeverMove = true;

                apex.Invoke(() => EquipNpc(apex, murd), 1f);

                apex.Invoke(() =>
                {
                    var heldEntity = apex.GetHeldEntity();

                    if (heldEntity != null)
                    {
                        heldEntity.SetHeld(true);
                    }

                    apex.EquipWeapon();
                }, 2f);

                if (Options.NPC.DespawnInventory)
                {
                    if (murd)
                    {
                        apex.GetComponent<NPCMurderer>().LootSpawnSlots = new LootContainer.LootSpawnSlot[0];
                    }
                    else apex.GetComponent<Scientist>().LootSpawnSlots = new LootContainer.LootSpawnSlot[0];
                }

                if (!murd)
                {
                    apex.GetComponent<Scientist>().LootPanelName = apex.displayName;
                }

                npcs.Add(apex);
                AuthorizePlayer(apex);
                apex.Invoke(() => UpdateDestination(apex, list), 0.25f);

                return apex;
            }

            private void SetupNpcKits()
            {
                var murdererKits = new List<string>();
                var scientistKits = new List<string>();

                foreach (string kit in Options.NPC.MurdererKits)
                {
                    if (IsKit(kit))
                    {
                        murdererKits.Add(kit);
                    }
                }

                foreach (string kit in Options.NPC.ScientistKits)
                {
                    if (IsKit(kit))
                    {
                        scientistKits.Add(kit);
                    }
                }

                npcKits = new Dictionary<string, List<string>>
                {
                    { "murderer", murdererKits },
                    { "scientist", scientistKits }
                };
            }

            private bool IsKit(string kit)
            {
                return Backbone.Plugin.Kits != null && Backbone.Plugin.Kits.IsLoaded && Backbone.Plugin.Kits.Call<bool>("isKit", kit);
            }

            private void EquipNpc(NPCPlayerApex apex, bool murd)
            {
                List<string> kits;
                if (npcKits.TryGetValue(murd ? "murderer" : "scientist", out kits) && kits.Count > 0)
                {
                    apex.inventory.Strip();

                    object success = Backbone.Plugin.Kits.Call("GiveKit", apex, kits.GetRandom());

                    if (success is bool && (bool)success)
                    {
                        return;
                    }
                }

                var items = murd ? Options.NPC.MurdererItems : Options.NPC.ScientistItems;
                
                if (items.Count == 0)
                {
                    return;
                }

                apex.inventory.Strip();

                foreach (string name in items)
                {
                    Item item = ItemManager.CreateByName(name, 1, 0);

                    if (item == null)
                    {
                        continue;
                    }

                    var skins = GetItemSkins(item.info);

                    if (skins.Count > 0)
                    {
                        ulong skin = skins.GetRandom();
                        item.skin = skin;
                    }

                    if (item.skin != 0 && item.GetHeldEntity())
                    {
                        item.GetHeldEntity().skinID = item.skin;
                    }

                    var weapon = item.GetHeldEntity()?.GetComponent<BaseProjectile>();

                    if (weapon != null)
                    {
                        weapon.primaryMagazine.contents = weapon.primaryMagazine.capacity;
                        weapon.SendNetworkUpdateImmediate();
                    }

                    item.MarkDirty();

                    if (!item.MoveToContainer(apex.inventory.containerWear, -1, false) && !item.MoveToContainer(apex.inventory.containerBelt, -1, false) && !item.MoveToContainer(apex.inventory.containerMain, -1, true))
                    {
                        item.Remove();
                    }
                }
            }

            private void UpdateDestination(NPCPlayerApex apex, List<Vector3> list)
            {
                apex.gameObject.AddComponent<FinalDestination>().Set(list);
            }

            public void UpdateMarker()
            {
                if (IsLoading)
                {
                    Backbone.Plugin.timer.Once(0.1f, UpdateMarker);
                    return;
                }

                if (!_config.Settings.Markers.UseVendingMarker && !_config.Settings.Markers.UseExplosionMarker && IsInvoking(UpdateMarker))
                {
                    CancelInvoke(UpdateMarker);
                }

                if (genericMarker != null && !genericMarker.IsDestroyed)
                {
                    genericMarker.SendUpdate();
                }

                if (vendingMarker != null && !vendingMarker.IsDestroyed)
                {
                    float seconds = despawnTime - Time.realtimeSinceStartup;
                    string despawnText = _config.Settings.Management.DespawnMinutesInactive > 0 && seconds > 0 ? Math.Floor(TimeSpan.FromSeconds(seconds).TotalMinutes).ToString() : null;
                    string markerShopName = string.Format("{0} {1} {2}", AllowPVP ? "[PVP]" : "[PVE]", Mode(), _config.Settings.Markers.MarkerName);
                    vendingMarker.markerShopName = string.IsNullOrEmpty(despawnText) ? markerShopName : string.Format("{0} [{1}m]", markerShopName, despawnText);
                    vendingMarker.SendNetworkUpdate();
                }

                if (markerCreated)
                {
                    return;
                }

                if (_config.Settings.Markers.UseExplosionMarker)
                {
                    explosionMarker = GameManager.server.CreateEntity(Backbone.Path.ExplosionMarker, Location) as MapMarkerExplosion;

                    if (explosionMarker != null)
                    {
                        explosionMarker.Spawn();
                        explosionMarker.SendMessage("SetDuration", 60, SendMessageOptions.DontRequireReceiver);
                    }
                }
                else if (_config.Settings.Markers.UseVendingMarker)
                {
                    vendingMarker = GameManager.server.CreateEntity(Backbone.Path.VendingMarker, Location) as VendingMachineMapMarker;

                    if (vendingMarker != null)
                    {
                        vendingMarker.enabled = false;
                        vendingMarker.markerShopName = string.Format("{0} {1} {2}", AllowPVP ? "[PVP]" : "[PVE]", Mode(), _config.Settings.Markers.MarkerName);
                        vendingMarker.Spawn();
                    }
                }

                markerCreated = true;
            }

            private void KillNpc()
            {
                var list = new List<BaseEntity>(npcs);

                foreach (var npc in list)
                {
                    if (npc != null && !npc.IsDestroyed)
                    {
                        npc.Kill();
                    }
                }

                npcs.Clear();
                list.Clear();
            }

            private void RemoveSpheres()
            {
                if (spheres.Count > 0)
                {
                    foreach (var sphere in spheres)
                    {
                        if (sphere != null && !sphere.IsDestroyed)
                        {
                            sphere.Kill();
                        }
                    }

                    spheres.Clear();
                }
            }

            private void RemoveMapMarkers()
            {
                Interface.CallHook("RemoveTemporaryLustyMarker", uid);
                Interface.CallHook("RemoveMapPrivatePluginMarker", uid);

                if (explosionMarker != null && !explosionMarker.IsDestroyed)
                {
                    explosionMarker.CancelInvoke(explosionMarker.DelayedDestroy);
                    explosionMarker.Kill(BaseNetworkable.DestroyMode.None);
                }

                if (genericMarker != null && !genericMarker.IsDestroyed)
                {
                    genericMarker.Kill();
                }

                if (vendingMarker != null && !vendingMarker.IsDestroyed)
                {
                    vendingMarker.Kill();
                }
            }
        }

        public static class Coroutines // Credits to Jake Rich
        {
            private static Dictionary<float, YieldInstruction> _waitForSecondDict;

            public static YieldInstruction WaitForSeconds(float delay)
            {
                if (_waitForSecondDict == null)
                {
                    _waitForSecondDict = new Dictionary<float, YieldInstruction>();
                }

                YieldInstruction yield;
                if (!_waitForSecondDict.TryGetValue(delay, out yield))
                {
                    //Cache the yield instruction for later
                    yield = new WaitForSeconds(delay);
                    _waitForSecondDict.Add(delay, yield);
                }

                return yield;
            }

            public static void Clear()
            {
                if (_waitForSecondDict != null)
                {
                    _waitForSecondDict.Clear();
                    _waitForSecondDict = null;
                }
            }
        }

        #region Hooks

        private void UnsubscribeHooks()
        {
            if (IsUnloading)
            {
                return;
            }

            Unsubscribe(nameof(OnBuildRevertBlock));
            Unsubscribe(nameof(OnRestoreUponDeath));
            Unsubscribe(nameof(OnNpcKits));
            Unsubscribe(nameof(CanTeleport));
            Unsubscribe(nameof(canTeleport));
            Unsubscribe(nameof(CanEntityBeTargeted));
            Unsubscribe(nameof(CanEntityTrapTrigger));
            Unsubscribe(nameof(CanEntityTakeDamage));
            Unsubscribe(nameof(OnFriendAdded));
            Unsubscribe(nameof(OnFriendRemoved));
            Unsubscribe(nameof(OnClanUpdate));

            Unsubscribe(nameof(OnEntityBuilt));
            Unsubscribe(nameof(OnEntityGroundMissing));
            Unsubscribe(nameof(OnEntityKill));
            Unsubscribe(nameof(OnEntityTakeDamage));
            Unsubscribe(nameof(OnLootEntity));
            Unsubscribe(nameof(OnLootEntityEnd));
            Unsubscribe(nameof(OnEntityDeath));
            Unsubscribe(nameof(OnEntityMarkHostile));
            Unsubscribe(nameof(CanPickupEntity));
            Unsubscribe(nameof(OnPlayerDeath));
            Unsubscribe(nameof(OnNpcTarget));
            Unsubscribe(nameof(OnEntitySpawned));
            Unsubscribe(nameof(OnPlayerCorpse));
            Unsubscribe(nameof(OnCupboardAuthorize));
            Unsubscribe(nameof(OnActiveItemChanged));
            Unsubscribe(nameof(OnLoseCondition));
        }

        private void OnMapMarkerAdded(BasePlayer player, ProtoBuf.MapNote note)
        {
            if (player.IsValid() && note != null && player.IsAdmin)
            {
                player.Teleport(new Vector3(note.worldPosition.x, GetSpawnHeight(note.worldPosition), note.worldPosition.z));
            }
        }

        private void OnNewSave(string filename) => wiped = true;

        private void Init()
        {
            permission.CreateGroup(rankLadderGroup, rankLadderGroup, 0);
            permission.GrantGroupPermission(rankLadderGroup, rankLadderPermission, this);
            permission.RegisterPermission(adminPermission, this);
            permission.RegisterPermission(rankLadderPermission, this);
            lastSpawnRequestTime = Time.realtimeSinceStartup;
            Backbone = new SingletonBackbone(this);
            Unsubscribe(nameof(OnMapMarkerAdded));
            UnsubscribeHooks();
        }

        private void OnServerInitialized(bool isStartup)
        {
            if (!configLoaded)
            {
                return;
            }

            LoadData();
            Reinitialize();
            BlockZoneManagerZones();
            SetupMonuments();
            LoadSpawns();
            SetupGrid();
            RegisterCommands();
            RemoveAllThirdPartyMarkers();
            CheckForWipe();
            
            AvailableDifficulties = GetAvailableDifficulties;
        }

        private void Unload()
        {
            IsUnloading = true;

            if (!configLoaded)
            {
                UnsetStatics();
                return;
            }

            SetUnloading();
            StopScheduleCoroutine();
            StopMaintainCoroutine();
            StopGridCoroutine();
            StopDespawnCoroutine();
            DestroyComponents();
            RemoveAllThirdPartyMarkers();
            Skins.Clear();
            PvpDelay.Clear();
            WorkshopSkins.Clear();

            if (Raids.Count > 0 || Bases.Count > 0)
            {
                DespawnAllBasesNow();
                return;
            }

            UnsetStatics();
        }

        private static void UnsetStatics()
        {
            Coroutines.Clear();
            Backbone.Destroy();
            Backbone = null;
            _config = null;
        }

        private void RegisterCommands()
        {
            AddCovalenceCommand(_config.Settings.BuyCommand, nameof(CommandBuyRaid));
            AddCovalenceCommand(_config.Settings.EventCommand, nameof(CommandRaidBase));
            AddCovalenceCommand(_config.Settings.HunterCommand, nameof(CommandRaidHunter));
            AddCovalenceCommand(_config.Settings.ConsoleCommand, nameof(CommandRaidBase));
            AddCovalenceCommand("rb.reloadconfig", nameof(CommandReloadConfig));
            AddCovalenceCommand("rb.config", nameof(CommandConfig), "raidablebases.config");
        }

        private void LoadData()
        {
            dataFile = Interface.Oxide.DataFileSystem.GetFile(Name);

            try
            {
                storedData = dataFile.ReadObject<StoredData>();
            }
            catch
            {
            }

            if (storedData?.Players == null)
            {
                storedData = new StoredData();
                SaveData();
            }

            uidsFile = Interface.Oxide.DataFileSystem.GetFile($"{Name}_uids");

            try
            {
                NetworkList = uidsFile.ReadObject<List<uint>>();
            }
            catch 
            {
            }

            if (NetworkList == null)
            {
                NetworkList = new List<uint>();
                SaveData();
            }

            DestroyUids();
        }

        private void CheckForWipe()
        {
            if (!wiped && BuildingManager.server.buildingDictionary.Count == 0)
            {
                if (storedData.Players.Count >= _config.RankedLadder.Amount)
                {
                    foreach (var pi in storedData.Players.Values)
                    {
                        if (pi.Raids > 0)
                        {
                            wiped = true;
                            break;
                        }
                    }
                }
            }

            if (wiped)
            {
                if (storedData.Players.Count > 0)
                {
                    var ladder = new List<KeyValuePair<string, int>>();

                    foreach (var entry in storedData.Players)
                    {
                        int value = entry.Value.Raids;

                        if (value > 0)
                        {
                            ladder.Add(new KeyValuePair<string, int>(entry.Key, value));
                        }
                    }

                    if (AssignTreasureHunters(ladder))
                    {
                        foreach (string key in storedData.Players.Keys)
                        {
                            storedData.Players[key].Raids = 0;
                        }
                    }

                    ladder.Clear();
                }

                wiped = false;
                SaveData();
            }
        }

        private void SetupMonuments()
        {
            foreach (var monument in TerrainMeta.Path.Monuments)
            {
                if (string.IsNullOrEmpty(monument.displayPhrase.translated))
                {
                    float size = monument.name.Contains("power_sub") ? 35f : Mathf.Max(monument.Bounds.size.Max(), 75f); // 75 is for invalid custom map prefabs with no bounds, or when its size is less than 75
                    monuments[monument] = monument.name.Contains("cave") ? 75f : monument.name.Contains("OilrigAI") ? 150f : size;
                }
                else
                {
                    monuments[monument] = GetMonumentFloat(monument.displayPhrase.translated.TrimEnd());
                }
            }
        }

        private void BlockZoneManagerZones()
        {
            if (ZoneManager == null || !ZoneManager.IsLoaded)
            {
                return;
            }

            var zoneIds = ZoneManager.Call("GetZoneIDs") as string[];

            if (zoneIds == null)
            {
                return;
            }

            managedZones.Clear();

            foreach (string zoneId in zoneIds)
            {
                if (zoneId.Contains("pvp"))
                {
                    continue;
                }

                var zoneLoc = ZoneManager.Call("GetZoneLocation", zoneId);

                if (!(zoneLoc is Vector3))
                {
                    continue;
                }

                var position = (Vector3)zoneLoc;

                if (position == Vector3.zero)
                {
                    continue;
                }

                var zoneRadius = ZoneManager.Call("GetZoneRadius", zoneId);
                float distance = 0f;

                if (zoneRadius is float)
                {
                    distance = (float)zoneRadius;
                }

                if (distance == 0f)
                {
                    var zoneSize = ZoneManager.Call("GetZoneSize", zoneId);

                    if (zoneSize is Vector3)
                    {
                        distance = ((Vector3)zoneSize).Max();
                    }
                }

                if (distance > 0f)
                {
                    distance += Radius + 5f;
                    managedZones[position] = distance;
                }
            }

            if (managedZones.Count > 0)
            {
                Puts(Backbone.GetMessage("BlockedZones", null, managedZones.Count));
            }
        }

        private void Reinitialize()
        {
            Backbone.Plugin.Skins.Clear();

            if (_config.Skins.RandomWorkshopSkins || _config.Treasure.RandomWorkshopSkins)
            {
                SetWorkshopIDs(); // webrequest.Enqueue("http://s3.amazonaws.com/s3.playrust.com/icons/inventory/rust/schema.json", null, GetWorkshopIDs, this, Core.Libraries.RequestMethod.GET);
            }

            if (_config.Settings.ExpansionMode && DangerousTreasures != null && DangerousTreasures.IsLoaded && DangerousTreasures.Version < new VersionNumber(1, 4, 1))
            {
                Puts(Backbone.GetMessage("NotCompatible", null, DangerousTreasures.Version));
                DangerousTreasures = null;
            }

            if (_config.Settings.TeleportMarker)
            {
                Subscribe(nameof(OnMapMarkerAdded));
            }
        }

        private void OnClanUpdate(string tag)
        {
            foreach (var raid in Raids.Values)
            {
                raid.UpdateClans();
            }
        }

        private void OnFriendAdded(string playerId, string targetId)
        {
            foreach (var raid in Raids.Values)
            {
                raid.UpdateFriends(playerId, targetId, true);
            }
        }

        private void OnFriendRemoved(string playerId, string targetId)
        {
            foreach (var raid in Raids.Values)
            {
                raid.UpdateFriends(playerId, targetId, false);
            }
        }

        private object OnBuildRevertBlock(BasePlayer player, Vector3 target)
        {
            var raid = RaidableBase.Get(target);

            return raid == null ? (object)null : true;
        }

        private object OnRestoreUponDeath(BasePlayer player)
        {
            var raid = RaidableBase.Get(player.transform.position);

            if (raid == null)
            {
                return null;
            }

            return _config.Settings.Management.BlockRestorePVE && !raid.AllowPVP || _config.Settings.Management.BlockRestorePVP && raid.AllowPVP ? true : (object)null;
        }

        private object OnNpcKits(ulong targetId)
        {
            return RaidableBase.Has(targetId) ? true : (object)null;
        }

        private object canTeleport(BasePlayer player)
        {
            return CanTeleport(player);
        }

        private object CanTeleport(BasePlayer player)
        {
            return EventTerritory(player.transform.position) ? Backbone.GetMessage("CannotTeleport", player.UserIDString) : null;
        }

        private object OnLoseCondition(Item item, float amount)
        {
            var player = item.GetOwnerPlayer();

            if (!IsValid(player))
            {
                return null;
            }

            var raid = RaidableBase.Get(player.transform.position);

            if (raid == null || !raid.Options.EnforceDurability)
            {
                return null;
            }

            uint uid = item.uid;
            float condition;
            if (!raid.conditions.TryGetValue(uid, out condition))
            {
                raid.conditions[uid] = condition = item.condition;
            }

            NextTick(() =>
            {
                if (raid == null)
                {
                    return;
                }

                if (!IsValid(item))
                {
                    raid.conditions.Remove(uid);
                    return;
                }

                item.condition = condition - amount;

                if (item.condition <= 0f && item.condition < condition)
                {
                    item.OnBroken();
                    raid.conditions.Remove(uid);
                }
                else raid.conditions[uid] = item.condition;
            });

            return true;
        }

        private void OnEntityBuilt(Planner planner, GameObject go)
        {
            var e = go.ToBaseEntity();

            if (!IsValid(e))
            {
                return;
            }

            var raid = RaidableBase.Get(e.transform.position);

            if (raid == null)
            {
                return;
            }

            if (e is BuildingPrivlidge && !raid.Options.AllowBuildingPriviledges)
            {
                var player = planner.GetOwnerPlayer();
                var slot = player.inventory.FindItemID("cupboard.tool");
                if (slot != null) slot.amount++;
                else player.GiveItem(ItemManager.CreateByName("cupboard.tool"));
                e.Kill();
                return;
            }

            if (Distance2D(raid.Location, e.transform.position) <= Radius)
            {
                AddEntity(e, raid.BaseIndex);
                return;
            }

            var priv = e.GetBuildingPrivilege();

            if (priv.IsValid() && priv.net.ID < raid.NetworkID)
            {
                return;
            }

            AddEntity(e, raid.BaseIndex);
        }

        private void AddEntity(BaseEntity e, int baseIndex)
        {
            if (Bases.ContainsKey(baseIndex))
            {
                Bases[baseIndex].Add(e);
            }

            e.OwnerID = 0;
            NetworkList.Add(e.net.ID);
        }

        private object OnNpcTarget(NPCPlayerApex apex, BaseEntity entity)
        {
            return RaidableBase.Has(apex) && entity.IsNpc ? true : (object)null;
        }

        private object OnNpcTarget(BaseEntity entity, NPCPlayerApex apex)
        {
            return RaidableBase.Has(apex) && entity.IsNpc ? true : (object)null;
        }

        private void OnActiveItemChanged(BasePlayer player, Item oldItem, Item newItem)
        {
            if (player.inventory?.containerBelt == null || player.IsNpc || !EventTerritory(player.transform.position))
            {
                return;
            }

            RaidableBase.StopUsingWand(player);
        }

        private void OnPlayerDeath(NPCPlayerApex player)
        {
            var raid = RaidableBase.Get(player.userID);

            if (raid == null)
            {
                return;
            }

            raid.CheckDespawn();

            if (_config.Settings.Management.UseOwners)
            {
                var attacker = player.lastAttacker as BasePlayer;

                if (attacker.IsValid() && !attacker.IsNpc)
                {
                    raid.TrySetOwner(attacker, player, null);
                }
            }

            player.svActiveItemID = 0;
            player.SendNetworkUpdate();
            
            if (raid.Options.NPC.DespawnInventory)
            {
                foreach (Item item in player.inventory.AllItems())
                {
                    RemoveItem(item);
                }
            }
        }

        private object OnEntityMarkHostile(BasePlayer player, float duration)
        {
            return duration <= 60f && EventTerritory(player.transform.position) ? true : (object)null;
        }

        private void OnEntityDeath(BuildingPrivlidge priv, HitInfo info)
        {
            if (!_config.EventMessages.AnnounceRaidUnlock)
            {
                return;
            }

            var raid = RaidableBase.Get(priv);

            if (raid == null || !raid.Options.RequiresCupboardAccess)
            {
                return;
            }

            OnCupboardAuthorize(priv, null);
        }

        private void OnEntityKill(StorageContainer container) => EntityHandler(container, null);

        private void OnEntityDeath(StorageContainer container, HitInfo hitInfo) => EntityHandler(container, hitInfo);

        private object OnEntityGroundMissing(StorageContainer container)
        {
            if (_config.Settings.Management.Invulnerable && RaidableBase.Has(container))
            {
                return true;
            }

            EntityHandler(container, null);
            return null;
        }

        private void EntityHandler(StorageContainer container, HitInfo hitInfo)
        {
            var raid = RaidableBase.Get(container);

            if (raid == null || !raid.IsOpened)
            {
                return;
            }

            if (hitInfo != null)
            {
                var player = GetPlayerFromHitInfo(hitInfo);

                if (player.IsValid())
                {
                    AddLooter(player, raid);
                }
            }

            if (container.inventory?.itemList?.Count > 0)
            {
                DropOrRemoveItems(container);
                raid._containers.Remove(container);
            }

            raid.Check();

            foreach (var x in Raids.Values)
            {
                if (x._containers.Count > 0)
                {
                    return;
                }
            }

            Unsubscribe(nameof(OnEntityKill));
            Unsubscribe(nameof(OnEntityGroundMissing));
        }

        private void OnCupboardAuthorize(BuildingPrivlidge priv, BasePlayer player)
        {
            if (priv == null)
            {
                return;
            }

            foreach (var raid in Raids.Values)
            {
                if (raid.privs.Contains(priv) && raid.Options.RequiresCupboardAccess && !raid.IsAuthed)
                {
                    raid.IsAuthed = true;

                    if (raid.Options.RequiresCupboardAccess && _config.EventMessages.AnnounceRaidUnlock)
                    {
                        foreach (var p in BasePlayer.activePlayerList)
                        {
                            Backbone.Message(p, "OnRaidFinished", p.UserIDString, FormatGridReference(raid.Location));
                        }
                    }

                    break;
                }
            }

            foreach (var raid in Raids.Values)
            {
                if (!raid.IsAuthed)
                {
                    return;
                }
            }

            Unsubscribe(nameof(OnCupboardAuthorize));
        }

        private object CanPickupEntity(BasePlayer player, BaseEntity entity)
        {
            var raid = RaidableBase.Get(entity);

            return raid != null && !raid.Options.AllowPickup ? false : (object)null;
        }

        private void OnEntitySpawned(BaseLock entity)
        {
            foreach (var x in Raids.Values)
            {
                foreach (var container in x._containers)
                {
                    if (entity.HasParent() && entity.GetParentEntity() == container)
                    {
                        entity.Invoke(entity.KillMessage, 0.1f);
                        break;
                    }
                }
            }
        }

        private void OnPlayerCorpse(BasePlayer player, PlayerCorpse corpse)
        {
            if (player == null || player.transform == null)
            {
                return;
            }

            var raid = RaidableBase.Get(player.transform.position);

            if (raid == null)
            {
                return;
            }

            if (!player.IsNpc)
            {
                if (_config.Settings.Management.PlayersLootableInPVE && !raid.AllowPVP)
                {
                    corpse.playerSteamID = 0;
                    return;
                }
                else if (_config.Settings.Management.PlayersLootableInPVP && raid.AllowPVP)
                {
                    corpse.playerSteamID = 0;
                    return;
                }
            }

            var npc = player as NPCPlayerApex;

            if (npc.IsValid() && raid.npcs.Contains(npc))
            {
                if (raid.Options.NPC.DespawnInventory)
                {
                    corpse.Invoke(corpse.KillMessage, 30f);
                }

                raid.npcs.Remove(npc);

                if (raid.Options.RespawnRate > 0)
                {
                    raid.RespawnNpc();
                    return;
                }

                if (!AnyNpcs())
                {
                    if (!AnyLootable())
                    {
                        Unsubscribe(nameof(OnPlayerCorpse));
                    }

                    Unsubscribe(nameof(OnNpcTarget));
                    Unsubscribe(nameof(OnPlayerDeath));
                }
            }
        }

        private void OnLootEntityEnd(BasePlayer player, StorageContainer container)
        {
            if (container.inventory == null || container.inventory.itemList == null || container.inventory.itemList.Count == 0)
            {
                return;
            }

            var raid = RaidableBase.Get(container);

            if (raid == null || raid.Options.DropTimeAfterLooting <= 0)
            {
                return;
            }

            container.Invoke(() => DropOrRemoveItems(container), raid.Options.DropTimeAfterLooting);
        }

        private void OnLootEntity(BasePlayer player, BaseCombatEntity entity)
        {
            var raid = RaidableBase.Get(entity.transform.position);

            if (raid == null)
            {
                return;
            }

            raid.OnLootEntityInternal(player, entity);
        }

        private object CanEntityBeTargeted(BasePlayer player, BaseEntity turret)
        {
            return IsValid(player) && !player.IsNpc && turret.IsValid() && EventTerritory(player.transform.position) && IsTrueDamage(turret) && !IsInvisible(player) ? true : (object)null;
        }

        private object CanEntityTrapTrigger(BaseTrap trap, BasePlayer player)
        {
            return IsValid(player) && !player.IsNpc && trap.IsValid() && EventTerritory(player.transform.position) && !IsInvisible(player) ? true : (object)null;
        }

        private bool IsInvisible(BasePlayer player)
        {
            if (!Vanish || !Vanish.IsLoaded)
            {
                return false;
            }
            
            return Vanish.Call<bool>("IsInvisible", player);
        }

        private object CanEntityTakeDamage(BaseEntity entity, HitInfo hitInfo)
        {
            if (hitInfo == null || !IsValid(entity) || entity.IsDestroyed)
            {
                return null;
            }

            var attacker = GetPlayerFromHitInfo(hitInfo);

            if (IsValid(attacker))
            {
                var victim = entity as BasePlayer;

                if (IsValid(victim))
                {
                    if (_config.TruePVE.ServerWidePVP && Raids.Count > 0)
                    {
                        return true;
                    }

                    if (EventTerritory(attacker.transform.position))
                    {
                        if (PvpDelay.ContainsKey(victim.userID))
                        {
                            return true;
                        }

                        if (EventTerritory(victim.transform.position))
                        {
                            if (victim.IsNpc)
                            {
                                return true;
                            }

                            var raid = RaidableBase.Get(attacker.transform.position);

                            if (raid != null)
                            {
                                if (raid.AllowPVP)
                                {
                                    return true;
                                }

                                return null;
                            }
                        }
                    }
                }
                else if (entity.OwnerID == 0 && NetworkList.Contains(entity.net.ID))
                {
                    return true;
                }
            }
            else if (EventTerritory(entity.transform.position) && IsTrueDamage(hitInfo.Initiator))
            {
                return true;
            }

            return null;
        }

        private void OnEntityTakeDamage(BaseEntity entity, HitInfo hitInfo)
        {
            if (!IsValid(entity) || entity.IsDestroyed || hitInfo == null || hitInfo.damageTypes == null)
            {
                return;
            }

            if (entity is BasePlayer)
            {
                HandlePlayerDamage(entity as BasePlayer, hitInfo);
            }
            else if (entity.OwnerID == 0 && ShouldCheckEntity(entity))
            {
                HandleEntityDamage(entity, hitInfo);
            }
        }

        private void HandlePlayerDamage(BasePlayer victim, HitInfo hitInfo)
        {
            var raid = RaidableBase.Get(victim.transform.position);

            if (raid == null)
            {
                return;
            }

            var attacker = GetPlayerFromHitInfo(hitInfo);

            if (IsValid(attacker))
            {
                if (!victim.IsNpc && !attacker.IsNpc && victim.userID != attacker.userID)
                {
                    if (!raid.Options.AllowFriendlyFire && raid.IsOnSameTeam(victim.userID, attacker.userID))
                    {
                        NullifyDamage(hitInfo);
                        return;
                    }

                    bool flag = ShouldBlockDamage(attacker.transform.position, raid.Location, raid.Options.BlockOutsideDamageToPlayers, raid.Options.ProtectionRadius);

                    if (flag || !raid.AllowPVP || (_config.Settings.Management.BlockMounts && attacker.isMounted))
                    {
                        NullifyDamage(hitInfo);
                        return;
                    }
                }
                else if (raid.Options.NPC.Accuracy < UnityEngine.Random.Range(0f, 100f) && attacker is Scientist && raid.npcs.Contains(attacker))
                {
                    NullifyDamage(hitInfo);
                }
            }
            else if (victim is NPCPlayerApex && raid.npcs.Contains(victim as NPCPlayerApex))
            {
                NullifyDamage(hitInfo); // make npc's immune to all damage which isn't from a player
            }
        }

        private bool ShouldBlockDamage(Vector3 a, Vector3 b, bool flag, float radius)
        {
            return flag && Distance2D(a, b) > radius;
        }

        private void HandleEntityDamage(BaseEntity entity, HitInfo hitInfo)
        {
            var raid = RaidableBase.Get(entity);

            if (raid == null)
            {
                return;
            }
            
            if (hitInfo.Initiator == null && hitInfo.damageTypes.GetMajorityDamageType() == DamageType.Decay)
            {
                NullifyDamage(hitInfo);
                return;
            }

            if (hitInfo.damageTypes.Has(DamageType.Explosion) || hitInfo.damageTypes.Has(DamageType.Blunt) || hitInfo.damageTypes.Has(DamageType.Stab))
            {
                raid.CheckDespawn();
            }

            if (_config.Settings.Management.Invulnerable && entity is BoxStorage)
            {
                NullifyDamage(hitInfo);
            }

            var attacker = GetPlayerFromHitInfo(hitInfo);

            if (IsValid(attacker) && !attacker.IsNpc)
            {
                bool flag = ShouldBlockDamage(attacker.transform.position, raid.Location, raid.Options.BlockOutsideDamageToBase, Mathf.Max(150f, raid.Options.ProtectionRadius));

                if (flag || (_config.Settings.Management.BlockMounts && attacker.isMounted))
                {
                    NullifyDamage(hitInfo);
                    return;
                }

                raid.TrySetOwner(attacker, entity, hitInfo);
                AddLooter(attacker, raid);

                if (!raid.Options.ExplosionModifier.Equals(100) && hitInfo.damageTypes.Has(DamageType.Explosion))
                {
                    float m = Mathf.Clamp(raid.Options.ExplosionModifier, 0f, 999f);

                    hitInfo.damageTypes.Scale(DamageType.Explosion, m.Equals(0f) ? 0f : m / 100f);
                }
            }
        }

        private void NullifyDamage(HitInfo hitInfo)
        {
            hitInfo.damageTypes = new DamageTypeList();
            hitInfo.DidHit = false;
            hitInfo.DoHitEffects = false;
            hitInfo.HitEntity = null;
        }

        private bool ShouldCheckEntity(BaseEntity entity)
        {
            return entity is BuildingBlock || entity is SimpleBuildingBlock || entity.name.Contains("assets/prefabs/deployable/") || entity is BoxStorage || entity is BuildingPrivlidge;
        }

        #endregion Hooks

        #region Spawn

        public static float GetSpawnHeight(Vector3 pos)
        {
            float y = TerrainMeta.HeightMap.GetHeight(pos);
            NavMeshHit navMeshHit;

            if (NavMesh.SamplePosition(pos, out navMeshHit, 2, 1))
            {
                y = navMeshHit.position.y;
            }

            return y;
        }

        private bool OnIceSheetOrInDeepWater(Vector3 vector)
        {
            if (TerrainMeta.WaterMap.GetHeight(vector) - TerrainMeta.HeightMap.GetHeight(vector) > 15f)
            {
                return true;
            }

            vector.y += TerrainMeta.HighestPoint.y;

            RaycastHit hit;            
            if (Physics.Raycast(vector, Vector3.down, out hit, vector.y + 1f, Layers.Mask.World, QueryTriggerInteraction.Ignore))
            {
                if (hit.collider.name.StartsWith("ice_sheet"))
                {
                    return true;
                }
            }

            return false;
        }

        protected void LoadSpawns()
        {
            raidSpawns.Clear();
            raidSpawns.Add(RaidableType.Grid, new RaidableSpawns());

            if (SpawnsFileValid(_config.Settings.Manual.SpawnsFile))
            {
                var spawns = GetSpawnsLocations(_config.Settings.Manual.SpawnsFile);

                if (spawns?.Count > 0)
                {
                    Puts(Backbone.GetMessage("LoadedManual", null, spawns.Count));
                    raidSpawns[RaidableType.Manual] = new RaidableSpawns(spawns);
                }
            }

            if (SpawnsFileValid(_config.Settings.Schedule.SpawnsFile))
            {
                var spawns = GetSpawnsLocations(_config.Settings.Schedule.SpawnsFile);

                if (spawns?.Count > 0)
                {
                    Puts(Backbone.GetMessage("LoadedScheduled", null, spawns.Count));
                    raidSpawns[RaidableType.Scheduled] = new RaidableSpawns(spawns);
                }
            }

            if (SpawnsFileValid(_config.Settings.Maintained.SpawnsFile))
            {
                var spawns = GetSpawnsLocations(_config.Settings.Maintained.SpawnsFile);

                if (spawns?.Count > 0)
                {
                    Puts(Backbone.GetMessage("LoadedMaintained", null, spawns.Count));
                    raidSpawns[RaidableType.Maintained] = new RaidableSpawns(spawns);
                }
            }

            if (SpawnsFileValid(_config.Settings.Buyable.SpawnsFile))
            {
                var spawns = GetSpawnsLocations(_config.Settings.Buyable.SpawnsFile);

                if (spawns?.Count > 0)
                {
                    Puts(Backbone.GetMessage("LoadedBuyable", null, spawns.Count));
                    raidSpawns[RaidableType.Purchased] = new RaidableSpawns(spawns);
                }
            }
        }

        protected void SetupGrid()
        {
            if (raidSpawns.Count >= 5)
            {
                StartAutomation();
                return;
            }

            StopGridCoroutine();

            NextTick(() =>
            {
                gridStopwatch.Start();
                gridTime = Time.realtimeSinceStartup;
                gridCoroutine = ServerMgr.Instance.StartCoroutine(GenerateGrid());
            });
        }

        private bool IsValidLocation(Vector3 vector, float radius)
        {
            if (IsMonumentPosition(vector))
            {
                return false;
            }

            if (OnIceSheetOrInDeepWater(vector))
            {
                return false;
            }

            if (!IsAreaSafe(vector, radius, Layers.Mask.World))
            {
                return false;
            }

            if (managedZones.Count > 0)
            {
                foreach (var zone in managedZones)
                {
                    if (Distance2D(zone.Key, vector) <= zone.Value)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private void StopGridCoroutine()
        {
            if (gridCoroutine != null)
            {
                ServerMgr.Instance.StopCoroutine(gridCoroutine);
                gridCoroutine = null;
            }
        }

        private IEnumerator GenerateGrid() // Credits to Jake_Rich for creating this for me!
        {
            RaidableSpawns rs = raidSpawns[RaidableType.Grid] = rs = new RaidableSpawns();
            int minPos = (int)(World.Size / -2f);
            int maxPos = (int)(World.Size / 2f);
            int checks = 0;
            float max = GetMaxElevation();

            for (float x = minPos; x < maxPos; x += 12.5f)
            {
                for (float z = minPos; z < maxPos; z += 12.5f)
                {
                    ExtractLocation(rs, max, x, z);

                    if (++checks >= 300)
                    {
                        checks = 0;
                        yield return null;
                    }
                }
            }

            Puts(Backbone.GetMessage("InitializedGrid", null, gridStopwatch.Elapsed.Seconds, gridStopwatch.Elapsed.Milliseconds, World.Size, rs.Count));

            gridCoroutine = null;
            gridStopwatch.Stop();
            gridStopwatch.Reset();
            StartAutomation();
        }

        private void ExtractLocation(RaidableSpawns rs, float max, float x, float z)
        {
            var pos = new Vector3(x, 0f, z);
            pos.y = GetSpawnHeight(pos);

            if (IsValidLocation(pos, 12.5f))
            {
                var elevation = GetTerrainElevation(pos);

                if (IsFlatTerrain(pos, elevation, max))
                {
                    rs.Add(new RaidableSpawnLocation
                    {
                        Location = pos,
                        Elevation = elevation
                    });
                }
            }
        }

        private float GetMaxElevation()
        {
            float max = 2.5f;

            if (_config.RaidableBases.Buildings.Values.Count > 0)
            {
                foreach (var x in _config.RaidableBases.Buildings.Values)
                {
                    if (x.Elevation > max)
                    {
                        max = x.Elevation;
                    }
                }
            }

            max++;
            return max;
        }

        private bool SpawnsFileValid(string spawnsFile)
        {
            if (!Interface.Oxide.DataFileSystem.ExistsDatafile($"SpawnsDatabase/{spawnsFile}"))
            {
                return false;
            }

            if (Spawns == null)
            {
                return false;
            }

            return Spawns.Call("GetSpawnsCount", spawnsFile) is int;
        }

        private List<RaidableSpawnLocation> GetSpawnsLocations(string spawnsFile)
        {
            object success = Spawns.Call("LoadSpawnFile", spawnsFile);

            if (success == null)
            {
                return null;
            }

            var list = (List<Vector3>)success;
            var locations = new List<RaidableSpawnLocation>();

            foreach (var pos in list)
            {
                locations.Add(new RaidableSpawnLocation
                {
                    Location = pos
                });
            }

            list.Clear();

            return locations;
        }

        private void StartAutomation()
        {
            if (_config.Settings.Schedule.Enabled)
            {
                if (!storedData.SecondsUntilRaid.Equals(double.MinValue) && storedData.SecondsUntilRaid - Facepunch.Math.Epoch.Current > _config.Settings.Schedule.IntervalMax) // Allows users to lower max event time
                {
                    storedData.SecondsUntilRaid = double.MinValue;
                }

                StartScheduleCoroutine();
            }

            StartMaintainCoroutine();
        }

        public static void Shuffle<T>(IList<T> list) // Fisher-Yates shuffle
        {
            int count = list.Count;
            int n = count;
            while (n-- > 0)
            {
                int k = UnityEngine.Random.Range(0, count);
                int j = UnityEngine.Random.Range(0, count);
                T value = list[k];
                list[k] = list[j];
                list[j] = value;
            }
        }

        public Vector3 GetEventPosition(BuildingOptions options, BasePlayer owner, float distanceFrom, bool checkTerrain, RaidableSpawns rs, RaidableType type)
        {
            rs.Check();

            int num1 = 0;

            do
            {
                var rsl = rs.GetRandom();

                if (distanceFrom > 0 && IsValid(owner) && Distance2D(owner.transform.position, rsl.Location) > distanceFrom)
                {
                    num1++;
                    continue;
                }

                if (RaidableBase.IsTooClose(rsl.Location, Mathf.Max(_config.Settings.Management.Distance, 100f)))
                {
                    continue;
                }

                if (!IsAreaSafe(rsl.Location, Radius * 2f, Layers.Mask.Player_Server | Layers.Mask.Construction | Layers.Mask.Deployed))
                {
                    continue;
                }

                if (checkTerrain && !IsFlatTerrain(rsl.Location, rsl.Elevation, options.Elevation))
                {
                    continue;
                }

                var position = new Vector3(rsl.Location.x, rsl.Location.y, rsl.Location.z);
                float w = TerrainMeta.WaterMap.GetHeight(position);

                if (w - position.y > 1.5f)
                {
                    if (!options.Submerged)
                    {
                        continue;
                    }

                    position.y = w - 2.15f;
                }

                if (!checkTerrain)
                {
                    rs.RemoveNear(rsl, options.ProtectionRadius);
                }

                return position;
            } while (rs.Count > 0);

            rs.AddRange();

            if (rs.Count > 0 && num1 >= rs.Count / 2 && (distanceFrom += 50f) < World.Size)
            {
                return GetEventPosition(options, owner, distanceFrom, checkTerrain, rs, type);
            }

            return Vector3.zero;
        }

        public bool IsAreaSafe(Vector3 position, float radius, int layers)
        {
            int hits = Physics.OverlapSphereNonAlloc(position, radius, Vis.colBuffer, layers, QueryTriggerInteraction.Ignore);
            int count = hits;

            for (int i = 0; i < hits; i++)
            {
                var collider = Vis.colBuffer[i];

                if (collider == null)
                {
                    count--;
                    goto next;
                }

                if (collider.bounds.size.Max() > radius && !collider.name.StartsWith("rock"))
                {
                    count--;
                    goto next;
                }

                var e = collider.ToBaseEntity();

                if (e.IsValid())
                {
                    if (e.IsNpc) count--;
                    else if (e is SleepingBag) count--;
                    else if (e is BaseOven) count--;
                    else if (!(e is BuildingBlock) && !(e is BasePlayer) && e.OwnerID == 0) count--;
                }
                else if (collider.name.StartsWith("rock_"))
                {
                    if (collider.bounds.extents.Max() > 0f && collider.bounds.extents.Max() <= 7f) count--;
                }
                else if (collider.gameObject?.layer == (int)Layer.Prevent_Building)
                {
                    if (!e.IsValid()) count--;
                }
                else if (collider.gameObject?.layer == (int)Layer.World)
                {
                    if (collider.transform.position.y < TerrainMeta.HeightMap.GetHeight(collider.transform.position)) goto next;
                    if (collider.name.StartsWith("assets/content/props/")) goto next;
                    if (collider.name.StartsWith("assets/content/structures/")) goto next;
                    if (collider.name.StartsWith("assets/content/building/")) goto next;
                    count--;
                }

                next:
                Vis.colBuffer[i] = null;
            }

            return count == 0;
        }

        public bool IsMonumentPosition(Vector3 target)
        {
            foreach (var monument in monuments)
            {
                if (Distance2D(monument.Key.transform.position, target) < monument.Value)
                {
                    return true;
                }
            }

            return false;
        }

        private bool TryOpenEvent(RaidableType type, Vector3 position, int uid, string BaseName, KeyValuePair<string, BuildingOptions> building, out RaidableBase raid)
        {
            if (IsUnloading)
            {
                raid = null;
                return false;
            }

            raid = new GameObject().AddComponent<RaidableBase>();

            if (type == RaidableType.Maintained && _config.Settings.Maintained.ConvertPVE)
            {
                raid.AllowPVP = true;
            }
            else if (type == RaidableType.Scheduled && _config.Settings.Schedule.ConvertPVE)
            {
                raid.AllowPVP = true;
            }
            else if (type == RaidableType.Manual && _config.Settings.Manual.ConvertPVE)
            {
                raid.AllowPVP = true;
            }
            else if (type == RaidableType.Purchased && _config.Settings.Buyable.ConvertPVE)
            {
                raid.AllowPVP = true;
            }
            else raid.AllowPVP = building.Value.AllowPVP;

            raid.DifficultyMode = building.Value.Difficulty == 0 ? Backbone.GetMessage("ModeEasy") : building.Value.Difficulty == 1 ? Backbone.GetMessage("ModeMedium") : Backbone.GetMessage("ModeHard");
            raid.PastedLocation = position;
            raid.Options = building.Value;
            raid.BaseName = BaseName;
            raid.Type = type;
            raid.uid = uid;

            if (_config.Settings.NoWizardry && Wizardry != null && Wizardry.IsLoaded)
            {
                Subscribe(nameof(OnActiveItemChanged));
            }

            Subscribe(nameof(CanEntityTakeDamage));
            Subscribe(nameof(OnEntitySpawned));
            Subscribe(nameof(OnEntityTakeDamage));
            Subscribe(nameof(OnEntityMarkHostile));

            storedData.TotalEvents++;
            SaveData();

            if (_config.LustyMap.Enabled)
            {
                AddTemporaryLustyMarker(position, uid);
            }

            if (Map)
            {
                AddMapPrivatePluginMarker(position, uid);
            }

            Raids[uid] = raid;

            return true;
        }

        #endregion

        #region Paste

        protected bool IsGridLoading
        {
            get
            {
                return gridCoroutine != null;
            }
        }

        protected bool IsPasteAvailable
        {
            get
            {
                foreach (var raid in Raids.Values)
                {
                    if (raid.IsLoading)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        private bool TryBuyRaidServerRewards(BasePlayer buyer, BasePlayer player, int difficulty, bool isServer)
        {
            if (isServer)
            {
                if (BuyRaid(player, difficulty))
                {
                    int cost = difficulty == 0 ? _config.Settings.ServerRewards.Easy : difficulty == 1 ? _config.Settings.ServerRewards.Medium : _config.Settings.ServerRewards.Hard;
                    Backbone.Message(buyer, "ServerRewardPointsTaken", buyer.UserIDString, cost);
                    if (buyer != player) Backbone.Message(player, "ServerRewardPointsGift", player.UserIDString, buyer.displayName, cost);
                    return true;
                }
            }
            else if (_config.Settings.ServerRewards.Any && ServerRewards != null && ServerRewards.IsLoaded)
            {
                int cost = difficulty == 0 ? _config.Settings.ServerRewards.Easy : difficulty == 1 ? _config.Settings.ServerRewards.Medium : _config.Settings.ServerRewards.Hard;
                int points = ServerRewards.Call<int>("CheckPoints", buyer.userID);

                if (points - cost > 0)
                {
                    if (BuyRaid(player, difficulty))
                    {
                        ServerRewards.Call("TakePoints", buyer.userID, cost);
                        Backbone.Message(buyer, "ServerRewardPointsTaken", buyer.UserIDString, cost);
                        if (buyer != player) Backbone.Message(player, "ServerRewardPointsGift", player.UserIDString, buyer.displayName, cost);
                        return true;
                    }
                }
                else Backbone.Message(buyer, "ServerRewardPointsFailed", buyer.UserIDString, cost);
            }

            return false;
        }

        private bool TryBuyRaidEconomics(BasePlayer buyer, BasePlayer player, int difficulty, bool isServer)
        {
            if (isServer)
            {
                if (BuyRaid(player, difficulty))
                {
                    var cost = difficulty == 0 ? _config.Settings.Economics.Easy : difficulty == 1 ? _config.Settings.Economics.Medium : _config.Settings.Economics.Hard;
                    Backbone.Message(buyer, "EconomicsWithdraw", buyer.UserIDString, cost);
                    if (buyer != player) Backbone.Message(player, "EconomicsWithdrawGift", player.UserIDString, buyer.displayName, cost);
                    return true;
                }
            }
            else if (_config.Settings.Economics.Any && Economics != null && Economics.IsLoaded)
            {
                var cost = difficulty == 0 ? _config.Settings.Economics.Easy : difficulty == 1 ? _config.Settings.Economics.Medium : _config.Settings.Economics.Hard;
                var points = Economics.Call<double>("Balance", buyer.UserIDString);

                if (points - cost > 0)
                {
                    if (BuyRaid(player, difficulty))
                    {
                        Economics.Call("Withdraw", buyer.UserIDString, cost);
                        Backbone.Message(buyer, "EconomicsWithdraw", buyer.UserIDString, cost);
                        if (buyer != player) Backbone.Message(player, "EconomicsWithdrawGift", player.UserIDString, buyer.displayName, cost);
                        return true;
                    }
                }
                else
                {
                    Backbone.Message(buyer, "EconomicsWithdrawFailed", buyer.UserIDString, cost);
                }
            }

            return false;
        }

        private bool BuyRaid(BasePlayer owner, int difficulty)
        {
            string message;
            var position = SpawnRandomBase(out message, RaidableType.Purchased, difficulty, null, false, owner);

            if (position != Vector3.zero)
            {
                Backbone.Message(owner, "BuyBaseSpawnedAt", owner.UserIDString, position, FormatGridReference(position));

                var announcement = Backbone.GetMessage("BuyBaseAnnouncement", owner.UserIDString, owner.displayName, position, FormatGridReference(position));

                if (_config.EventMessages.AnnounceBuy)
                {
                    Broadcast(announcement);
                }

                Puts(Backbone.RemoveFormatting(announcement));

                return true;
            }

            Backbone.Message(owner, message, owner.UserIDString);
            return false;
        }

        private void Broadcast(string format, params object[] args)
        {
            if (BasePlayer.activePlayerList.Count >= 1)
            {
                ConsoleNetwork.BroadcastToAllClients("chat.add", 2, _config.Settings.ChatID, (args.Length != 0) ? string.Format(format, args) : format);
            }
        }

        private bool IsDifficultyAvailable(int difficulty, bool checkAllowPVP)
        {
            if (difficulty < 0 || difficulty > 2 || !AvailableDifficulties.Contains(difficulty))
            {
                return false;
            }

            foreach (var option in _config.RaidableBases.Buildings.Values)
            {
                if (option.Difficulty == difficulty)
                {
                    if (checkAllowPVP && !_config.Settings.Buyable.BuyPVP && option.AllowPVP)
                    {
                        continue;
                    }

                    return true;
                }
            }

            return false;
        }

        private void PasteBuilding(RaidableType type, Vector3 position, KeyValuePair<string, BuildingOptions> building, BasePlayer owner)
        {
            int uid;

            do
            {
                uid = UnityEngine.Random.Range(1000, 100000);
            } while (Raids.ContainsKey(uid));

            var callback = new Action(() =>
            {
                RaidableBase raid;
                if (TryOpenEvent(type, position, uid, building.Key, building, out raid))
                {
                    if (type == RaidableType.Purchased && _config.Settings.Buyable.UsePayLock)
                    {
                        raid.TrySetPayLock(owner);
                    }
                }
            });

            var list = GetListedOptions(building.Value.PasteOptions);
            float rotationCorrection = IsValid(owner) ? DegreeToRadian(owner.GetNetworkRotation().eulerAngles.y) : 0f;
            CopyPaste.Call("TryPasteFromVector3", position, rotationCorrection, building.Key, list.ToArray(), callback);
        }

        private List<string> GetListedOptions(List<PasteOption> options)
        {
            var list = new List<string>();
            bool flag1 = false, flag2 = false, flag3 = false, flag4 = false;

            for (int i = 0; i < options.Count; i++)
            {
                string key = options[i].Key.ToLower();
                string value = options[i].Value.ToLower();

                if (key == "stability") flag1 = true;
                if (key == "autoheight") flag2 = true;
                if (key == "height") flag3 = true;
                if (key == "entityowner") flag4 = true;

                list.Add(key);
                list.Add(value);
            }

            if (!flag1)
            {
                list.Add("stability");
                list.Add("false");
            }

            if (!flag2)
            {
                list.Add("autoheight");
                list.Add("false");
            }

            if (!flag3)
            {
                list.Add("height");
                list.Add("2.5");
            }

            if (!flag4)
            {
                list.Add("entityowner");
                list.Add("false");
            }

            return list;
        }

        private float DegreeToRadian(float angle)
        {
            return angle.Equals(0f) ? 0f : (float)(Math.PI * angle / 180.0f);
        }

        private void OnPasteFinished(List<BaseEntity> pastedEntities)
        {
            Timer t = null;

            t = timer.Repeat(1f, 25, () =>
            {
                if (IsUnloading)
                {
                    if (t != null)
                    {
                        t.Destroy();
                    }

                    return;
                }

                var raid = RaidableBase.Get(pastedEntities);

                if (raid == null)
                {
                    return;
                }

                if (t != null)
                {
                    t.Destroy();
                }

                int baseIndex = 0;

                while (Bases.ContainsKey(baseIndex))
                {
                    baseIndex++;
                }

                pastedEntities.RemoveAll(e => !IsValid(e) || e.IsDestroyed);

                Bases[baseIndex] = pastedEntities;
                
                raid.SetEntities(baseIndex);
            });
        }

        public IEnumerator UndoRoutine(int baseIndex)
        {
            var raid = RaidableBase.Get(baseIndex);

            if (raid != null)
            {
                UnityEngine.Object.Destroy(raid.gameObject);
            }

            if (!Bases.ContainsKey(baseIndex))
            {
                yield break;
            }

            int total = 0;
            var list = new List<BaseEntity>(Bases[baseIndex]);

            foreach (var e in list)
            {
                if (e == null)
                {
                    continue;
                }

                if (e.IsValid())
                {
                    NetworkList.Remove(e.net.ID);
                }

                var ore = e as OreResourceEntity;

                if (ore != null)
                {
                    ore.CleanupBonus();
                }

                if (!e.IsDestroyed)
                {
                    e.Kill();
                }

                //yield return new WaitWhile(() => !e.IsDestroyed);
                
                if (++total % 15 == 0)
                {
                    yield return null;
                }
            }

            list.Clear();

            if (Bases.ContainsKey(baseIndex))
            {
                Bases[baseIndex].Clear();
                Bases[baseIndex] = null;
                Bases.Remove(baseIndex);
            }

            if (Bases.Count == 0)
            {
                UnsubscribeHooks();
            }
        }

        private void UndoPaste(Vector3 position, RaidableBase raid, int baseIndex)
        {
            if (IsUnloading || !Bases.ContainsKey(baseIndex))
            {
                return;
            }

            if (_config.Settings.Management.DespawnMinutes > 0)
            {
                if (_config.EventMessages.ShowWarning)
                {
                    Broadcast(Backbone.GetMessage("DestroyingBaseAt", null, FormatGridReference(position), _config.Settings.Management.DespawnMinutes));
                }

                float time = _config.Settings.Management.DespawnMinutes * 60f;

                if (raid != null)
                {
                    raid.selfDestruct = true;
                    raid.despawnTime = Time.realtimeSinceStartup + time;
                    raid.UpdateMarker();
                }

                timer.Once(time, () =>
                {
                    if (!IsUnloading && Bases.ContainsKey(baseIndex))
                    {
                        ServerMgr.Instance.StartCoroutine(UndoRoutine(baseIndex));
                    }
                });
            }
            else ServerMgr.Instance.StartCoroutine(UndoRoutine(baseIndex));
        }

        private static List<Vector3> GetCircumferencePositions(Vector3 center, float radius, float next, float y = 0f)
        {
            var positions = new List<Vector3>();

            if (next < 1f)
            {
                next = 1f;
            }

            float angle = 0f;
            float angleInRadians = 2 * (float)Math.PI;

            while (angle < 360)
            {
                float radian = (angleInRadians / 360) * angle;
                float x = center.x + radius * (float)Math.Cos(radian);
                float z = center.z + radius * (float)Math.Sin(radian);
                var a = new Vector3(x, 0f, z);

                a.y = y == 0f ? GetSpawnHeight(a) : y;
                positions.Add(a);
                angle += next;
            }

            return positions;
        }

        private Elevation GetTerrainElevation(Vector3 center)
        {
            float maxY = -1000;
            float minY = 1000;

            foreach (var position in GetCircumferencePositions(center, Radius, Radius * 4f))
            {
                if (position.y > maxY) maxY = position.y;
                if (position.y < minY) minY = position.y;
            }

            return new Elevation
            {
                Min = minY,
                Max = maxY
            };
        }

        private bool IsFlatTerrain(Vector3 center, Elevation elevation, float value)
        {
            return elevation.Max - elevation.Min <= value && elevation.Max - center.y <= value;
        }

        private float GetMonumentFloat(string monumentName)
        {
            switch (monumentName)
            {
                case "Abandoned Cabins":
                    return 54f;
                case "Abandoned Supermarket":
                    return 50f;
                case "Airfield":
                    return 200f;
                case "Bandit Camp":
                    return 125f;
                case "Giant Excavator Pit":
                    return 225f;
                case "Harbor":
                    return 150f;
                case "HQM Quarry":
                    return 37.5f;
                case "Large Oil Rig":
                    return 200f;
                case "Launch Site":
                    return 300f;
                case "Lighthouse":
                    return 48f;
                case "Military Tunnel":
                    return 100f;
                case "Mining Outpost":
                    return 45f;
                case "Oil Rig":
                    return 100f;
                case "Outpost":
                    return 125f;
                case "Oxum's Gas Station":
                    return 65f;
                case "Power Plant":
                    return 140f;
                case "Satellite Dish":
                    return 90f;
                case "Sewer Branch":
                    return 100f;
                case "Stone Quarry":
                    return 27.5f;
                case "Sulfur Quarry":
                    return 27.5f;
                case "The Dome":
                    return 70f;
                case "Train Yard":
                    return 150f;
                case "Water Treatment Plant":
                    return 185f;
                case "Water Well":
                    return 24f;
                case "Wild Swamp":
                    return 24f;
            }

            return 200f;
        }

        private Vector3 SpawnRandomBase(out string message, RaidableType type, int difficulty, string baseName = null, bool isAdmin = false, BasePlayer owner = null)
        {
            lastSpawnRequestTime = Time.realtimeSinceStartup;

            if (type == RaidableType.Purchased && !owner.IsValid())
            {
                message = "?";
                return Vector3.zero;
            }

            var building = GetBuilding(type, difficulty, baseName);
            bool flag = IsBuildingValid(building);

            if (flag)
            {
                bool checkTerrain;
                var spawns = GetSpawns(type, out checkTerrain);

                if (spawns != null)
                {
                    var eventPos = GetEventPosition(building.Value, owner, _config.Settings.Buyable.DistanceToSpawnFrom, checkTerrain, spawns, type);

                    if (eventPos != Vector3.zero)
                    {
                        PasteBuilding(type, eventPos, building, owner);
                        message = "Success";
                        return eventPos;
                    }
                }
            }

            if (!flag)
            {
                if (difficulty == -1)
                {
                    message = Backbone.GetMessage("NoValidBuildingsConfigured", owner?.UserIDString);
                }
                else message = Backbone.GetMessage(isAdmin ? "Difficulty Not Available Admin" : "Difficulty Not Available", owner?.UserIDString, difficulty);
            }
            else message = Backbone.GetMessage("CannotFindPosition", owner?.UserIDString);

            return Vector3.zero;
        }

        public RaidableSpawns GetSpawns(RaidableType type, out bool checkTerrain)
        {
            RaidableSpawns spawns;

            switch (type)
            {
                case RaidableType.Maintained:
                    {
                        if (raidSpawns.TryGetValue(RaidableType.Maintained, out spawns))
                        {
                            checkTerrain = false;
                            return spawns;
                        }
                        break;
                    }
                case RaidableType.Manual:
                    {
                        if (raidSpawns.TryGetValue(RaidableType.Manual, out spawns))
                        {
                            checkTerrain = false;
                            return spawns;
                        }
                        break;
                    }
                case RaidableType.Purchased:
                    {
                        if (raidSpawns.TryGetValue(RaidableType.Purchased, out spawns))
                        {
                            checkTerrain = false;
                            return spawns;
                        }
                        break;
                    }
                case RaidableType.Scheduled:
                    {
                        if (raidSpawns.TryGetValue(RaidableType.Scheduled, out spawns))
                        {
                            checkTerrain = false;
                            return spawns;
                        }
                        break;
                    }
            }

            checkTerrain = true;
            return raidSpawns.TryGetValue(RaidableType.Grid, out spawns) ? spawns : null;
        }

        private KeyValuePair<string, BuildingOptions> GetBuilding(RaidableType type, int difficulty, string baseName)
        {
            var list = new List<KeyValuePair<string, BuildingOptions>>();

            if (!string.IsNullOrEmpty(baseName))
            {
                baseName = baseName.ToLower();
            }

            foreach (var building in _config.RaidableBases.Buildings)
            {
                if (!IsBuildingAllowed(type, difficulty, building.Value.Difficulty, building.Value.AllowPVP))
                {
                    continue;
                }

                if (FileExists(building.Key))
                {
                    if (building.Key.ToLower() == baseName)
                    {
                        return building;
                    }
                    else if (string.IsNullOrEmpty(baseName))
                    {
                        list.Add(building);
                    }
                }

                foreach (var extra in building.Value.AdditionalBases)
                {
                    if (!FileExists(extra.Key))
                    {
                        continue;
                    }

                    var kvp = new KeyValuePair<string, BuildingOptions>(extra.Key, BuildingOptions.Clone(building.Value));
                    kvp.Value.PasteOptions = new List<PasteOption>(extra.Value);

                    if (extra.Key.ToLower() == baseName)
                    {
                        return kvp;
                    }
                    else if (string.IsNullOrEmpty(baseName))
                    {
                        list.Add(kvp);
                    }
                }
            }

            if (list.Count > 0)
            {
                var r = list.GetRandom();

                list.Clear();

                return r;
            }

            return default(KeyValuePair<string, BuildingOptions>);
        }

        public bool IsBuildingValid(KeyValuePair<string, BuildingOptions> building)
        {
            if (string.IsNullOrEmpty(building.Key) || building.Value == null)
            {
                return false;
            }

            return true;
        }

        public static bool FileExists(string file)
        {
            return Interface.Oxide.DataFileSystem.ExistsDatafile($"copypaste/{file}");
        }

        public bool IsBuildingAllowed(RaidableType type, int requestedDifficulty, int buildingDifficulty, bool allowPVP)
        {
            if (requestedDifficulty > -1 && buildingDifficulty != requestedDifficulty)
            {
                return false;
            }

            switch (type)
            {
                case RaidableType.Purchased:
                    {
                        if (!_config.Settings.Buyable.BuyPVP && allowPVP)
                        {
                            return false;
                        }
                        if (requestedDifficulty >= 0 && !CanSpawnDifficultyToday(requestedDifficulty))
                        {
                            return false;
                        }
                        break;
                    }
                case RaidableType.Maintained:
                case RaidableType.Scheduled:
                    {
                        if (requestedDifficulty >= 0 && !CanSpawnDifficultyToday(requestedDifficulty))
                        {
                            return false;
                        }
                        break;
                    }
            }

            return true;
        }

        public static bool CanSpawnDifficultyToday(int difficulty)
        {
            switch (DateTime.Now.DayOfWeek)
            {
                case DayOfWeek.Monday:
                    {
                        return difficulty == 0 ? _config.Settings.Management.Easy.Monday : difficulty == 1 ? _config.Settings.Management.Medium.Monday : _config.Settings.Management.Hard.Monday;
                    }
                case DayOfWeek.Tuesday:
                    {
                        return difficulty == 0 ? _config.Settings.Management.Easy.Tuesday : difficulty == 1 ? _config.Settings.Management.Medium.Tuesday : _config.Settings.Management.Hard.Tuesday;
                    }
                case DayOfWeek.Wednesday:
                    {
                        return difficulty == 0 ? _config.Settings.Management.Easy.Wednesday : difficulty == 1 ? _config.Settings.Management.Medium.Wednesday : _config.Settings.Management.Hard.Wednesday;
                    }
                case DayOfWeek.Thursday:
                    {
                        return difficulty == 0 ? _config.Settings.Management.Easy.Thursday : difficulty == 1 ? _config.Settings.Management.Medium.Thursday : _config.Settings.Management.Hard.Thursday;
                    }
                case DayOfWeek.Friday:
                    {
                        return difficulty == 0 ? _config.Settings.Management.Easy.Friday : difficulty == 1 ? _config.Settings.Management.Medium.Friday : _config.Settings.Management.Hard.Friday;
                    }
                case DayOfWeek.Saturday:
                    {
                        return difficulty == 0 ? _config.Settings.Management.Easy.Saturday : difficulty == 1 ? _config.Settings.Management.Medium.Saturday : _config.Settings.Management.Hard.Saturday;
                    }
                case DayOfWeek.Sunday:
                default:
                    {
                        return difficulty == 0 ? _config.Settings.Management.Easy.Sunday : difficulty == 1 ? _config.Settings.Management.Medium.Sunday : _config.Settings.Management.Hard.Sunday;
                    }
            }
        }

        #endregion

        #region Commands

        private void CommandReloadConfig(IPlayer p, string command, string[] args)
        {
            if (p.IsAdmin || ((p.Object as BasePlayer)?.IsAdmin ?? false))
            {
                p.Reply("Reloading config...");
                LoadConfig();

                if (maintainCoroutine != null)
                {
                    StopMaintainCoroutine();
                    p.Reply("Stopped maintain coroutine.");
                }

                if (scheduleCoroutine != null)
                {
                    StopScheduleCoroutine();
                    p.Reply("Stopped schedule coroutine.");
                }

                p.Reply("Initializing...");
                Reinitialize();
                BlockZoneManagerZones();
                LoadSpawns();
                SetupGrid();

                AvailableDifficulties = GetAvailableDifficulties;
            }
        }

        private void CommandBuyRaid(IPlayer p, string command, string[] args)
        {
            if (CopyPaste == null || !CopyPaste.IsLoaded)
            {
                p.Reply(Backbone.GetMessage("LoadCopyPaste", p.Id));
                return;
            }

            if (IsGridLoading)
            {
                p.Reply(Backbone.GetMessage("GridIsLoading", p.Id));
                return;
            }

            if (RaidableBase.Get(RaidableType.Purchased) >= _config.Settings.Buyable.Max)
            {
                p.Reply(Backbone.GetMessage("Max Manual Events", p.Id, _config.Settings.Buyable.Max));
                return;
            }

            if (args.Length == 0)
            {
                p.Reply(Backbone.GetMessage("BuySyntax", p.Id, _config.Settings.BuyCommand, p.IsServer ? "ID" : p.Id));
                return;
            }

            string value = args[0].ToLower();
            int difficulty = value == "0" || value == "easy" ? 0 : value == "1" || value == "med" || value == "medium" ? 1 : value == "2" || value == "hard" ? 2 : -1;

            if (difficulty >= 0 && difficulty <= 3 && !AvailableDifficulties.Contains(difficulty))
            {
                p.Reply(Backbone.GetMessage("BuyDifficultyNotAvailableToday", p.Id, value));
                return;
            }

            if (!IsDifficultyAvailable(difficulty, false))
            {
                p.Reply(Backbone.GetMessage("BuyAnotherDifficulty", p.Id, value));
                return;
            }

            if (!IsDifficultyAvailable(difficulty, true))
            {
                p.Reply(Backbone.GetMessage("BuyPVPRaidsDisabled", p.Id));
                return;
            }

            if (!IsPasteAvailable)
            {
                p.Reply(Backbone.GetMessage("PasteOnCooldown", p.Id));
                return;
            }

            if (IsSpawnOnCooldown())
            {
                p.Reply(Backbone.GetMessage("BuyableOnCooldown", p.Id));
                return;
            }

            BasePlayer player = null;

            if (args.Length > 1 && args[1].IsSteamId())
            {
                ulong playerId;
                if (ulong.TryParse(args[1], out playerId))
                {
                    player = BasePlayer.FindByID(playerId);
                }
            }
            else player = p.Object as BasePlayer;

            if (!IsValid(player))
            {
                p.Reply(args.Length > 1 ? Backbone.GetMessage("TargetNotFoundId", p.Id, args[1]) : Backbone.GetMessage("TargetNotFoundNoId", p.Id));
                return;
            }

            var buyer = p.Object as BasePlayer ?? player;
            string id = buyer.UserIDString;
            float cooldown;

            if (buyCooldowns.TryGetValue(id, out cooldown))
            {
                Backbone.Message(buyer, "BuyCooldown", id, cooldown - Time.realtimeSinceStartup);
                return;
            }

            bool flag = TryBuyRaidServerRewards(buyer, player, difficulty, p.IsServer) || TryBuyRaidEconomics(buyer, player, difficulty, p.IsServer);

            if (flag && _config.Settings.Buyable.Cooldown > 0)
            {
                buyCooldowns.Add(id, Time.realtimeSinceStartup + _config.Settings.Buyable.Cooldown);
                timer.Once(_config.Settings.Buyable.Cooldown, () => buyCooldowns.Remove(id));
            }
        }
        
        private void CommandRaidHunter(IPlayer p, string command, string[] args)
        {
            var player = p.Object as BasePlayer;

            if (player == null) // || drawGrants.Contains(player.UserIDString))
            {
                return;
            }

            if (args.Length >= 1 && player.IsAdmin)
            {
                switch (args[0].ToLower())
                {
                    case "markers":
                        {
                            RemoveAllThirdPartyMarkers();
                            return;
                        }
                    case "resettime":
                        {
                            storedData.SecondsUntilRaid = double.MinValue;
                            return;
                        }
                    case "grid":
                        {
                            ShowGrid(player);
                            return;
                        }
                }
            }

            if (args.Length >= 1 && (args[0].ToLower() == "ladder" || args[0].ToLower() == "lifetime") && _config.RankedLadder.Enabled)
            {
                ShowLadder(p, args);
                return;
            }

            if (_config.RankedLadder.Enabled)
            {
                p.Reply(Backbone.GetMessage("Wins", p.Id, storedData.Players.ContainsKey(p.Id) ? storedData.Players[p.Id].Raids : 0, _config.Settings.HunterCommand));
            }

            if (Raids.Count == 0 && _config.Settings.Schedule.Enabled)
            {
                ShowNextScheduledEvent(p);
                return;
            }

            DrawRaidLocations(player);
        }

        protected void DrawRaidLocations(BasePlayer player)
        {
            if (player.IsAdmin && Raids.Count > 0)
            {
                foreach (var raid in Raids.Values)
                {
                    int num = 0;

                    foreach (var t in BasePlayer.activePlayerList)
                    {
                        if (t.Distance(raid.Location) <= raid.Options.ProtectionRadius * 3f)
                        {
                            num++;
                        }
                    }

                    double distance = Math.Round(Vector3.Distance(player.transform.position, raid.Location), 2);
                    string message = string.Format(lang.GetMessage("RaidMessage", this, player.UserIDString), distance, num);

                    player.SendConsoleCommand("ddraw.text", 15f, Color.yellow, raid.Location, string.Format("[{0}] {1} {2}", raid.AllowPVP ? "PVP" : "PVE", raid.Mode(), message));
                }
            }
        }

        protected void ShowNextScheduledEvent(IPlayer p)
        {
            double time = storedData.SecondsUntilRaid - Facepunch.Math.Epoch.Current;
            string message = FormatTime(time, p.Id);

            if (time <= 0)
            {
                if (BasePlayer.activePlayerList.Count < _config.Settings.Schedule.PlayerLimit)
                {
                    message = Backbone.GetMessage("Not Enough Online", p.Id, _config.Settings.Schedule.PlayerLimit);
                }
                else message = "0s";
            }

            p.Reply(Backbone.GetMessage("Next", p.Id, message));
        }

        protected void ShowLadder(IPlayer p, string[] args)
        {
            if (storedData.Players.Count == 0)
            {
                p.Reply(Backbone.GetMessage("Ladder Insufficient Players", p.Id));
                return;
            }

            if (args.Length == 2 && args[1].ToLower() == "resetme" && storedData.Players.ContainsKey(p.Id))
            {
                storedData.Players[p.Id].Raids = 0;
            }

            int rank = 0;
            var ladder = GetLadder(args[0]);

            ladder.Sort((x, y) => y.Value.CompareTo(x.Value));

            p.Reply(Backbone.GetMessage(args[0].ToLower() == "ladder" ? "Ladder" : "Ladder Total", p.Id));

            foreach (var kvp in ladder)
            {
                if (++rank >= 10)
                {
                    break;
                }

                NotifyPlayer(p, rank, kvp);
            }

            ladder.Clear();
        }

        protected void ShowGrid(BasePlayer player)
        {
            foreach (var rsl in raidSpawns[RaidableType.Grid].All)
            {
                if (Distance2D(rsl.Location, player.transform.position) <= 1000)
                {
                    player.SendConsoleCommand("ddraw.text", 30f, Color.green, rsl.Location, "X");
                }
            }

            foreach (var monument in monuments)
            {
                string text = monument.Key.displayPhrase.translated;

                if (string.IsNullOrEmpty(text))
                {
                    text = GetMonumentName(monument);
                }
                // Add Zone here for NoRaid?
                player.SendConsoleCommand("ddraw.sphere", 30f, Color.blue, monument.Key.transform.position, monument.Value);
                player.SendConsoleCommand("ddraw.text", 30f, Color.cyan, monument.Key.transform.position, text);
            }
        }

        protected List<KeyValuePair<string, int>> GetLadder(string arg)
        {
            var ladder = new List<KeyValuePair<string, int>>();

            foreach (var entry in storedData.Players)
            {
                int value = arg.ToLower() == "ladder" ? entry.Value.Raids : entry.Value.TotalRaids;

                if (value > 0)
                {
                    ladder.Add(new KeyValuePair<string, int>(entry.Key, value));
                }
            }

            return ladder;
        }

        protected void NotifyPlayer(IPlayer p, int rank, KeyValuePair<string, int> kvp)
        {
            string name = covalence.Players.FindPlayerById(kvp.Key)?.Name ?? kvp.Key;
            string value = kvp.Value.ToString("N0");
            string message = lang.GetMessage("NotifyPlayerMessageFormat", this, p.Id);
            
            message = message.Replace("{rank}", rank.ToString());
            message = message.Replace("{name}", name);
            message = message.Replace("{value}", value);

            p.Reply(message);
        }

        protected string GetMonumentName(KeyValuePair<MonumentInfo, float> monument)
        {
            string text;
            if (monument.Key.name.Contains("Oilrig")) text = "Oil Rig";
            else if (monument.Key.name.Contains("cave")) text = "Cave";
            else if (monument.Key.name.Contains("power_sub")) text = "Power Sub Station";
            else text = "Unknown Monument";
            return text;
        }

        private void CommandRaidBase(IPlayer p, string command, string[] args)
        {
            var player = p.Object as BasePlayer;
            bool isAdmin = p.IsAdmin || (player?.IsAdmin ?? false);
            string baseName = null;
            int difficulty = -1;

            if (args.Length == 1 && args[0].ToLower() == "type")
            {
                foreach(RaidableType type in Enum.GetValues(typeof(RaidableType)))
                {
                    var list = new List<string>();

                    foreach (var raid in Raids.Values)
                    {
                        if (raid.Type != type) continue;
                        list.Add(PositionToGrid(raid.Location));
                    }

                    Puts("{0} : {1} @ {2}", type.ToString(), RaidableBase.Get(type), string.Join(", ", list.ToArray()));
                    list.Clear();
                }

                return;
            }

            if (!CanCommandContinue(player, p, args, ref baseName, ref difficulty, isAdmin))
            {
                return;
            }

            if (command == _config.Settings.EventCommand)
            {
                ProcessEventCommand(player, p, args, difficulty, isAdmin);
            }
            else if (command == _config.Settings.ConsoleCommand)
            {
                ProcessConsoleCommand(p, baseName, difficulty, isAdmin);
            }
        }

        protected void ProcessEventCommand(BasePlayer player, IPlayer p, string[] args, int difficulty, bool isAdmin)
        {
            if (!player.IsValid() || (!permission.UserHasPermission(adminPermission, player.UserIDString) && !isAdmin))
            {
                return;
            }

            var building = GetBuilding(RaidableType.Manual, difficulty, args.Length > 0 ? args[0] : null);

            if (IsBuildingValid(building))
            {
                RaycastHit hit;
                int layers = Layers.Mask.Construction | Layers.Mask.Default | Layers.Mask.Deployed | Layers.Mask.Tree | Layers.Mask.Terrain | Layers.Mask.Water | Layers.Mask.World;
                if (Physics.Raycast(player.eyes.HeadRay(), out hit, isAdmin ? Mathf.Infinity : 100f, layers, QueryTriggerInteraction.Ignore))
                {
                    var safe = IsAreaSafe(hit.point, Radius * 2f, Layers.Mask.Player_Server | Layers.Mask.Construction | Layers.Mask.Deployed | Layers.Mask.Prevent_Building);

                    if (safe && (isAdmin || !IsMonumentPosition(hit.point)))
                    {
                        PasteBuilding(RaidableType.Manual, hit.point, building, null);
                        if (isAdmin) player.SendConsoleCommand("ddraw.text", 10f, Color.red, hit.point, "XXX");
                    }
                    else p.Reply(Backbone.GetMessage("PasteIsBlocked", p.Id));
                }
                else p.Reply(Backbone.GetMessage("LookElsewhere", p.Id));
            }
            else p.Reply(Backbone.GetMessage("NoValidBuildingsConfigured", p.Id));
        }

        protected void ProcessConsoleCommand(IPlayer p, string baseName, int difficulty, bool isAdmin)
        {
            if (IsGridLoading)
            {
                p.Reply(GridIsLoadingMessage);
                return;
            }

            string message;
            var pos = SpawnRandomBase(out message, RaidableType.Manual, difficulty, baseName, isAdmin);
            if (isAdmin && p.IsConnected)
            {
                if (pos != Vector3.zero)
                {
                    p.Teleport(pos.x, pos.y, pos.z);
                }
                else p.Reply(message);
            }
            else if (pos == Vector3.zero)
            {
                p.Reply(message);
            }
        }

        private bool CanCommandContinue(BasePlayer player, IPlayer p, string[] args, ref string baseName, ref int difficulty, bool isAdmin)
        {
            if (CopyPaste == null || !CopyPaste.IsLoaded)
            {
                p.Reply(Backbone.GetMessage("LoadCopyPaste", p.Id));
                return false;
            }

            if (!isAdmin && BaseNetworkable.serverEntities.Count > 300000)
            {
                p.Reply(lang.GetMessage("EntityCountMax", this, p.Id));
                return false;
            }

            if (RaidableBase.Get(RaidableType.Manual) >= _config.Settings.Manual.Max && !isAdmin)
            {
                p.Reply(Backbone.GetMessage("Max Manual Events", p.Id, _config.Settings.Manual.Max));
                return false;
            }

            if (HandledCommandArguments(player, p, isAdmin, args))
            {
                return false;
            }

            if (!IsPasteAvailable)
            {
                p.Reply(Backbone.GetMessage("PasteOnCooldown", p.Id));
                return false;
            }

            if (IsSpawnOnCooldown())
            {
                p.Reply(Backbone.GetMessage("SpawnOnCooldown", p.Id));
                return false;
            }

            if (args.Length > 0 && args.Length < 3)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    string value = args[i].ToLower();

                    if (value == "0" || value == "easy") difficulty = 0;
                    else if (value == "1" || value == "med" || value == "medium") difficulty = 1;
                    else if (value == "2" || value == "hard") difficulty = 2;
                    else baseName = args[i];
                }
            }

            return true;
        }

        private bool HandledCommandArguments(BasePlayer player, IPlayer p, bool isAdmin, string[] args)
        {
            if (args.Length == 0 || !isAdmin)
            {
                return false;
            }

            switch (args[0].ToLower())
            {
                case "draw":
                    {
                        if (IsValid(player))
                        {
                            foreach (var raid in Raids.Values)
                            {
                                player.SendConsoleCommand("ddraw.sphere", 30f, Color.blue, raid.Location, raid.Options.ProtectionRadius);
                            }

                            return true;
                        }

                        break;
                    }
                case "despawn":
                    {
                        if (IsValid(player))
                        {
                            bool success = DespawnBase(player.transform.position);
                            Backbone.Message(player, success ? "DespawnBaseSuccess" : "DespawnBaseNoneAvailable", player.UserIDString);
                            if (success) Puts(Backbone.GetMessage("DespawnedAt", null, player.displayName, FormatGridReference(player.transform.position)));
                            return true;
                        }

                        break;
                    }
                case "despawnall":
                    {
                        if (Raids.Count > 0)
                        {
                            DespawnAllBasesNow();
                            Puts(Backbone.GetMessage("DespawnedAll", null, player?.displayName ?? p.Id));
                        }
                        return true;
                    }
            }

            return false;
        }

        private void CommandConfig(IPlayer p, string command, string[] args)
        {
            if (!IsValidArgument(args))
            {
                p.Reply(string.Format(lang.GetMessage("ConfigUseFormat", this, p.Id), args.Length == 0 ? string.Empty : string.Join("|", arguments.ToArray())));
                return;
            }

            switch (args[0].ToLower())
            {
                case "add":
                    {
                        ConfigAddBase(p, args);
                        return;
                    }
                case "remove":
                    {
                        ConfigRemoveBase(p, args);
                        return;
                    }
                case "rename":
                    {
                        ConfigRenameBase(p, args);
                        return;
                    }
                case "list":
                    {
                        ConfigListBases(p);
                        return;
                    }
                case "set":
                case "edit":
                    {
                        ConfigEditBase(p, args);
                        return;
                    }
            }
        }

        #endregion Commands

        #region Helpers

        private BasePlayer GetPlayerFromHitInfo(HitInfo hitInfo)
        {
            var player = hitInfo.InitiatorPlayer;

            if (!player.IsValid() && hitInfo.Initiator is BaseMountable)
            {
                player = GetMountedPlayer(hitInfo.Initiator as BaseMountable);
            }

            return player;
        }

        private static BasePlayer GetMountedPlayer(BaseMountable m)
        {
            if (m.GetMounted())
            {
                return m.GetMounted();
            }

            if (m is BaseVehicle)
            {
                var vehicle = m as BaseVehicle;

                foreach (var point in vehicle.mountPoints)
                {
                    if (point.mountable.IsValid() && point.mountable.GetMounted())
                    {
                        return point.mountable.GetMounted();
                    }
                }
            }

            return null;
        }

        public static void RemoveItem(Item item)
        {
            item.RemoveFromContainer();
            item.Remove();
        }

        private bool AnyNpcs()
        {
            foreach (var x in Raids.Values)
            {
                if (x.npcs.Count > 0)
                {
                    return true;
                }
            }

            return false;
        }

        private bool AnyLootable()
        {
            foreach (var raid in Raids.Values)
            {
                if (_config.Settings.Management.PlayersLootableInPVE && !raid.AllowPVP)
                {
                    return true;
                }

                if (_config.Settings.Management.PlayersLootableInPVP && raid.AllowPVP)
                {
                    return true;
                }
            }

            return false;
        }

        private void DestroyComponents()
        {
            foreach (var raid in Raids.Values)
            {
                raid.DestroyFire();
                raid.DestroyInputs();
            }
        }

        private string GridIsLoadingMessage
        {
            get
            {
                int count = raidSpawns.ContainsKey(RaidableType.Grid) ? raidSpawns[RaidableType.Grid].Count : 0;
                return Backbone.GetMessage("GridIsLoadingFormatted", null, (Time.realtimeSinceStartup - gridTime).ToString("N02"), count);
            }
        }

        private void ConfigRenameBase(IPlayer p, string[] args)
        {
        }

        private void ConfigEditBase(IPlayer p, string[] args)
        {
        }

        private void ConfigAddBase(IPlayer p, string[] args)
        {
            if (args.Length < 2)
            {
                p.Reply(lang.GetMessage("ConfigAddBaseSyntax", this, p.Id));
                return;
            }

            _sb.Length = 0;
            var values = new List<string>(args);
            values.RemoveAt(0);
            string value = values[0];
            int difficulty = -1;

            if (args.Length > 2)
            {
                foreach (string s in values)
                {
                    string str = s.ToLower();

                    if (str == "0" || str == "easy")
                    {
                        difficulty = 0;
                        values.Remove(s);
                        break;
                    }
                    else if (str == "1" || str == "medium")
                    {
                        difficulty = 1;
                        values.Remove(s);
                        break;
                    }
                    else if (str == "2" || str == "hard")
                    {
                        difficulty = 2;
                        values.Remove(s);
                        break;
                    }
                }
            }

            p.Reply(string.Format(lang.GetMessage("Adding", this, p.Id), string.Join(" ", values.ToArray())));

            BuildingOptions building;
            if (!_config.RaidableBases.Buildings.TryGetValue(value, out building))
            {
                _config.RaidableBases.Buildings[value] = building = new BuildingOptions();
                _sb.AppendLine(string.Format(lang.GetMessage("AddedPrimaryBase", this, p.Id), value));
                building.AdditionalBases.Clear();
            }

            if (difficulty != -1 && building.Difficulty != difficulty)
            {
                building.Difficulty = difficulty;
                _sb.AppendLine(string.Format(lang.GetMessage("DifficultySetTo", this, p.Id), difficulty));
            }

            if (args.Length >= 3)
            {
                values.RemoveAt(0);

                foreach (string ab in values)
                {
                    if (!building.AdditionalBases.ContainsKey(ab))
                    {
                        building.AdditionalBases.Add(ab, DefaultPasteOptions);
                        _sb.AppendLine(string.Format(lang.GetMessage("AddedAdditionalBase", this, p.Id), ab));
                    }
                }
            }

            if (_sb.Length > 0)
            {
                p.Reply(_sb.ToString());
                _sb.Length = 0;
                SaveConfig();
            }
            else p.Reply(lang.GetMessage("EntryAlreadyExists", this, p.Id));

            values.Clear();
        }

        private void ConfigRemoveBase(IPlayer p, string[] args)
        {
            if (args.Length < 2)
            {
                p.Reply(lang.GetMessage("RemoveSyntax", this, p.Id));
                return;
            }

            int num = 0;
            var dict = new Dictionary<string, BuildingOptions>(_config.RaidableBases.Buildings);
            var array = args.Skip(1).ToArray();

            _sb.Length = 0;
            _sb.AppendLine(string.Format(lang.GetMessage("RemovingAllBasesFor", this, p.Id), string.Join(" ", array)));

            foreach (var entry in dict)
            {
                var list = new List<KeyValuePair<string, List<PasteOption>>>(entry.Value.AdditionalBases);

                foreach (string value in array)
                {
                    foreach (var ab in list)
                    {
                        if (ab.Key == value || entry.Key == value)
                        {
                            _sb.AppendLine(string.Format(lang.GetMessage("RemovedAdditionalBase", this, p.Id), ab.Key, entry.Key));
                            entry.Value.AdditionalBases.Remove(ab.Key);
                            num++;
                        }
                    }

                    if (entry.Key == value)
                    {
                        _sb.AppendLine(string.Format(lang.GetMessage("RemovedPrimaryBase", this, p.Id), value));
                        _config.RaidableBases.Buildings.Remove(entry.Key);
                        num++;
                    }
                }

                list.Clear();
            }

            _sb.AppendLine(string.Format(lang.GetMessage("RemovedEntries", this, p.Id), num));
            p.Reply(_sb.ToString());
            dict.Clear();
            SaveConfig();
            _sb.Length = 0;
        }

        private void ConfigListBases(IPlayer p)
        {
            _sb.Length = 0;
            _sb.Append(lang.GetMessage("ListingAll", this, p.Id));
            _sb.AppendLine();

            bool buyable = false;
            bool validBase = false;

            foreach (var entry in _config.RaidableBases.Buildings)
            {
                if (!entry.Value.AllowPVP)
                {
                    buyable = true;
                }

                _sb.AppendLine(lang.GetMessage("PrimaryBase", this, p.Id));

                if (FileExists(entry.Key))
                {
                    _sb.AppendLine(entry.Key);
                    validBase = true;
                }
                else _sb.Append(entry.Key).Append(lang.GetMessage("FileDoesNotExist", this, p.Id));

                if (entry.Value.AdditionalBases.Count > 0)
                {
                    _sb.AppendLine(lang.GetMessage("AdditionalBase", this, p.Id));

                    foreach (var ab in entry.Value.AdditionalBases)
                    {
                        if (FileExists(ab.Key))
                        {
                            _sb.AppendLine(ab.Key);
                            validBase = true;
                        }
                        else _sb.Append(ab.Key).Append((lang.GetMessage("FileDoesNotExist", this, p.Id)));
                    }
                }
            }

            if (!buyable && !_config.Settings.Buyable.BuyPVP)
            {
                _sb.AppendLine(lang.GetMessage("RaidPVEWarning", this, p.Id));
            }

            if (!validBase)
            {
                _sb.AppendLine(lang.GetMessage("NoValidBuildingsWarning", this, p.Id));
            }

            p.Reply(_sb.ToString());
            _sb.Length = 0;
        }

        private readonly List<string> arguments = new List<string>
        {
            "add", "remove", "list", "rename"
        };

        private static bool IsValid(BaseEntity e)
        {
            if (e == null || e.net == null || e.transform == null)
            {
                return false;
            }

            return true;
        }

        private static bool IsValid(Item item)
        {
            if (item == null || !item.IsValid() || item.isBroken)
            {
                return false;
            }

            return true;
        }

        private bool IsValidArgument(string[] args)
        {
            return args.Length > 0 && arguments.Contains(args[0]);
        }

        private void DropOrRemoveItems(StorageContainer container)
        {
            if (container is BoxStorage || (container is BuildingPrivlidge && _config.Settings.Management.AllowCupboardLoot))
            {
                container.inventory.Drop(StringPool.Get(545786656), container.GetDropPosition() + new Vector3(0f, 0.25f, 0f), container.transform.rotation);
            }
            else
            {
                var list = new List<Item>(container.inventory.itemList);

                foreach (Item item in list)
                {
                    RemoveItem(item);
                }

                list.Clear();
            }
        }

        private static bool AddLooter(BasePlayer looter, RaidableBase raid)
        {
            if (!looter.IsValid() || !raid.IsAlly(looter))
            {
                return false;
            }

            if (!raid.raiders.Contains(looter))
            {
                raid.raiders.Add(looter);
                return true;
            }

            return false;
        }

        private bool IsSpawnOnCooldown()
        {
            if (Time.realtimeSinceStartup - lastSpawnRequestTime < 2f)
            {
                return true;
            }

            lastSpawnRequestTime = Time.realtimeSinceStartup;
            return false;
        }

        private bool DespawnBase(Vector3 target)
        {
            var values = new List<RaidableBase>();

            foreach (var x in Raids.Values)
            {
                if (Distance2D(x.Location, target) < 100f)
                {
                    values.Add(x);
                }
            }

            if (values.Count == 0)
            {
                return false;
            }

            if (values.Count > 1)
            {
                values.Sort((x, y) => Vector3.Distance(y.Location, x.Location).CompareTo(Vector3.Distance(x.Location, y.Location)));
            }

            values[0].Despawn();
            values.Clear();

            return true;
        }

        private void SetUnloading()
        {
            foreach (var raid in Raids.Values)
            {
                raid.IsUnloading = true;
            }
        }

        private void DespawnAllBasesNow()
        {
            if (!IsUnloading)
            {
                StartDespawnRoutine();
                return;
            }

            if (Interface.Oxide.IsShuttingDown)
            {
                return;
            }

            StartDespawnInvokes();
            DestroyAll();
        }

        private void DestroyAll()
        {
            if (Raids.Count == 0)
            {
                return;
            }

            var raidables = new List<RaidableBase>(Raids.Values);

            foreach (var raid in raidables)
            {
                Puts(Backbone.GetMessage("Destroyed Raid", null, raid.Location));
                raid.Despawn();
                UnityEngine.Object.Destroy(raid.gameObject);
            }

            raidables.Clear();
        }

        private void StartDespawnInvokes()
        {
            if (Bases.Count == 0)
            {
                return;
            }

            var entries = new List<KeyValuePair<int, List<BaseEntity>>>(Bases);
            float num = 0;

            foreach (var entry in entries)
            {
                if (entry.Value == null || entry.Value.Count == 0)
                {
                    continue;
                }

                var entities = new List<BaseEntity>(entry.Value);

                foreach (var e in entities)
                {
                    uint uid = e.IsValid() ? e.net.ID : 0u;

                    if (e != null && !e.IsDestroyed)
                    {
                        e.Invoke(() =>
                        {
                            if (!e.IsDestroyed)
                            {
                                e.KillMessage();
                            }

                            if (uid != 0u)
                            {
                                NetworkList.Remove(uid);
                            }
                        }, num += 0.002f);
                    }
                }

                entities.Clear();
            }

            entries.Clear();
        }

        private void StopDespawnCoroutine()
        {
            if (despawnCoroutine != null)
            {
                ServerMgr.Instance.StopCoroutine(despawnCoroutine);
                despawnCoroutine = null;
            }
        }

        private void StartDespawnRoutine()
        {
            if (Raids.Count == 0)
            {
                return;
            }

            if (despawnCoroutine != null)
            {
                timer.Once(0.1f, () => StartDespawnRoutine());
                return;
            }

            despawnCoroutine = ServerMgr.Instance.StartCoroutine(DespawnCoroutine());
        }

        private IEnumerator DespawnCoroutine()
        {
            var list = new List<RaidableBase>(Raids.Values);

            while (list.Count > 0)
            {
                int baseIndex = list[0].BaseIndex;
                list[0].Despawn();

                do
                {
                    yield return Coroutines.WaitForSeconds(0.1f);
                } while (Bases.ContainsKey(baseIndex));

                list.RemoveAt(0);
            }

            despawnCoroutine = null;
        }

        private void DestroyUids()
        {
            var list = new List<uint>(NetworkList);
            float num = 0;

            foreach (uint uid in list)
            {
                var e = BaseNetworkable.serverEntities.Find(uid);

                if (e != null && !e.IsDestroyed)
                {
                    e.Invoke((() =>
                    {
                        if (!e.IsDestroyed)
                        {
                            e.KillMessage();
                        }

                        NetworkList.Remove(uid);
                    }), num += 0.002f);
                }
                else if (e == null || e.IsDestroyed)
                {
                    NetworkList.Remove(uid);
                }
            }

            list.Clear();
            SaveData();
        }

        private bool IsTrueDamage(BaseEntity entity)
        {
            if (!entity.IsValid())
            {
                return false;
            }

            return entity is SamSite || entity is AutoTurret || entity is FlameTurret || entity is GunTrap || entity is FireBall || entity is BaseTrap || entity.prefabID == 976279966;
        }

        private bool EventTerritory(Vector3 position)
        {
            foreach (var raid in Raids.Values)
            {
                if (Distance2D(raid.Location, position) <= raid.Options.ProtectionRadius)
                {
                    return true;
                }
            }

            return false;
        }

        private static float Distance2D(Vector3 a, Vector3 b)
        {
            return (new Vector3(a.x, 0f, a.z) - new Vector3(b.x, 0f, b.z)).magnitude;
        }

        private void SetWorkshopIDs()
        {
            foreach (var def in ItemManager.GetItemDefinitions())
            {
                var skins = new List<ulong>();

                foreach (var asi in Rust.Workshop.Approved.All)
                {
                    if (!string.IsNullOrEmpty(asi.Skinnable.ItemName) && asi.Skinnable.ItemName == def.shortname)
                    {
                        skins.Add(Convert.ToUInt64(asi.WorkshopdId));
                    }
                }

                if (skins.Count > 0)
                {
                    List<ulong> workshopSkins;
                    if (!WorkshopSkins.TryGetValue(def.shortname, out workshopSkins))
                    {
                        WorkshopSkins[def.shortname] = workshopSkins = new List<ulong>();
                    }

                    foreach (ulong skin in skins)
                    {
                        if (!workshopSkins.Contains(skin))
                        {
                            workshopSkins.Add(skin);
                        }
                    }

                    skins.Clear();
                }
            }
        }

        private bool AssignTreasureHunters(List<KeyValuePair<string, int>> ladder)
        {
            foreach (var target in covalence.Players.All)
            {
                if (target == null || string.IsNullOrEmpty(target.Id))
                    continue;

                if (permission.UserHasPermission(target.Id, rankLadderPermission))
                    permission.RevokeUserPermission(target.Id, rankLadderPermission);

                if (permission.UserHasGroup(target.Id, rankLadderGroup))
                    permission.RemoveUserGroup(target.Id, rankLadderGroup);
            }

            if (!_config.RankedLadder.Enabled)
                return true;

            ladder.RemoveAll(x => x.Value < 1);
            ladder.Sort((x, y) => y.Value.CompareTo(x.Value));

            int permsGiven = 0;

            for (int i = 0; i < ladder.Count; i++)
            {
                var target = covalence.Players.FindPlayerById(ladder[i].Key);

                if (target == null || target.IsBanned || target.IsAdmin)
                    continue;

                permission.GrantUserPermission(target.Id, rankLadderPermission, this);
                permission.AddUserGroup(target.Id, rankLadderGroup);

                LogToFile("treasurehunters", DateTime.Now.ToString() + " : " + Backbone.GetMessage("Log Stolen", null, target.Name, target.Id, ladder[i].Value), this, true);
                Puts(Backbone.GetMessage("Log Granted", null, target.Name, target.Id, rankLadderPermission, rankLadderGroup));

                if (++permsGiven >= _config.RankedLadder.Amount)
                    break;
            }

            if (permsGiven > 0)
            {
                Puts(Backbone.GetMessage("Log Saved", null, "treasurehunters"));
            }

            return true;
        }

        private void AddMapPrivatePluginMarker(Vector3 position, int uid)
        {
            if (Map == null || !Map.IsLoaded)
            {
                return;
            }

            mapMarkers[uid] = new MapInfo { IconName = _config.LustyMap.IconName, Position = position, Url = _config.LustyMap.IconFile };
            Map.Call("ApiAddPointUrl", _config.LustyMap.IconFile, _config.LustyMap.IconName, position);
        }

        private void RemoveMapPrivatePluginMarker(int uid)
        {
            if (Map == null || !Map.IsLoaded || !mapMarkers.ContainsKey(uid))
            {
                return;
            }

            var mapInfo = mapMarkers[uid];
            Map.Call("ApiRemovePointUrl", mapInfo.Url, mapInfo.IconName, mapInfo.Position);
            mapMarkers.Remove(uid);
        }

        private void AddTemporaryLustyMarker(Vector3 pos, int uid)
        {
            if (LustyMap == null || !LustyMap.IsLoaded)
            {
                return;
            }

            string name = string.Format("{0}_{1}", _config.LustyMap.IconName, storedData.TotalEvents).ToLower();
            LustyMap.Call("AddTemporaryMarker", pos.x, pos.z, name, _config.LustyMap.IconFile, _config.LustyMap.IconRotation);
            lustyMarkers[uid] = name;
        }

        private void RemoveTemporaryLustyMarker(int uid)
        {
            if (LustyMap == null || !LustyMap.IsLoaded || !lustyMarkers.ContainsKey(uid))
            {
                return;
            }

            LustyMap.Call("RemoveTemporaryMarker", lustyMarkers[uid]);
            lustyMarkers.Remove(uid);
        }

        private void RemoveAllThirdPartyMarkers()
        {
            if (LustyMap && LustyMap.IsLoaded && lustyMarkers.Count > 0)
            {
                var lusty = new Dictionary<int, string>(lustyMarkers);

                foreach (var entry in lusty)
                {
                    RemoveTemporaryLustyMarker(entry.Key);
                }

                lusty.Clear();
            }

            if (Map && Map.IsLoaded && mapMarkers.Count > 0)
            {
                var maps = new Dictionary<int, MapInfo>(mapMarkers);

                foreach (var entry in maps)
                {
                    RemoveMapPrivatePluginMarker(entry.Key);
                }

                maps.Clear();
            }
        }

        private void StopMaintainCoroutine()
        {
            if (maintainCoroutine != null)
            {
                ServerMgr.Instance.StopCoroutine(maintainCoroutine);
                maintainCoroutine = null;
            }
        }

        private void StartMaintainCoroutine()
        {
            if (_config.Settings.Maintained.Enabled)
            {
                StopMaintainCoroutine();

                NextTick(() =>
                {
                    maintainCoroutine = ServerMgr.Instance.StartCoroutine(MaintainCoroutine());
                });
            }
        }

        private IEnumerator MaintainCoroutine()
        {
            string message;

            if (AvailableDifficulties.Count == 0)
            {
                Puts(Backbone.GetMessage("MaintainCoroutineFailedToday"));
                yield break;
            }

            do
            {
                if (SaveRestore.IsSaving)
                {
                    yield return Coroutines.WaitForSeconds(0.1f);
                    continue;
                }

                if (!CanMaintainOpenEvent() || CopyPaste == null || !CopyPaste.IsLoaded)
                {
                    yield return Coroutines.WaitForSeconds(2f);
                    continue;
                }

                SpawnRandomBase(out message, RaidableType.Maintained, AvailableDifficulties.GetRandom());
                yield return Coroutines.WaitForSeconds(15f);
            } while (true);
        }

        private bool CanMaintainOpenEvent() => IsPasteAvailable && !IsGridLoading && RaidableBase.Get(RaidableType.Maintained) < _config.Settings.Maintained.Max && BasePlayer.activePlayerList.Count >= _config.Settings.Maintained.PlayerLimit;

        private void StopScheduleCoroutine()
        {
            if (scheduleCoroutine != null)
            {
                ServerMgr.Instance.StopCoroutine(scheduleCoroutine);
                scheduleCoroutine = null;
            }
        }

        private void StartScheduleCoroutine()
        {
            if (_config.Settings.Schedule.Enabled)
            {
                StopScheduleCoroutine();

                NextTick(() =>
                {
                    scheduleCoroutine = ServerMgr.Instance.StartCoroutine(ScheduleCoroutine());
                });
            }
        }

        private IEnumerator ScheduleCoroutine()
        {
            if (AvailableDifficulties.Count == 0)
            {
                Puts(Backbone.GetMessage("ScheduleCoroutineFailedToday"));
                yield break;
            }

            float raidInterval = UnityEngine.Random.Range(_config.Settings.Schedule.IntervalMin, _config.Settings.Schedule.IntervalMax);
            float stamp = Facepunch.Math.Epoch.Current;
            string message;

            if (storedData.SecondsUntilRaid.Equals(double.MinValue)) // first time users
            {
                storedData.SecondsUntilRaid = stamp + raidInterval;
                Puts(Backbone.GetMessage("Next Automated Raid", null, FormatTime(raidInterval), DateTime.Now.AddSeconds(raidInterval).ToString()));
                SaveData();
            }

            while (true)
            {
                stamp = Facepunch.Math.Epoch.Current;
                
                if (CanScheduleOpenEvent(stamp) && CopyPaste != null && CopyPaste.IsLoaded)
                {
                    while (RaidableBase.Get(RaidableType.Scheduled) < _config.Settings.Schedule.Max)
                    {
                        if (SaveRestore.IsSaving)
                        {
                            yield return Coroutines.WaitForSeconds(0.1f);
                            continue;
                        }

                        if (SpawnRandomBase(out message, RaidableType.Scheduled, AvailableDifficulties.GetRandom()) != Vector3.zero)
                        {
                            yield return Coroutines.WaitForSeconds(15f);
                            continue;
                        }

                        yield return Coroutines.WaitForSeconds(1f);
                    }

                    raidInterval = UnityEngine.Random.Range(_config.Settings.Schedule.IntervalMin, _config.Settings.Schedule.IntervalMax);
                    storedData.SecondsUntilRaid = stamp + raidInterval;
                    Puts(Backbone.GetMessage("Next Automated Raid", null, FormatTime(raidInterval), DateTime.Now.AddSeconds(raidInterval).ToString()));
                    SaveData();
                }

                yield return Coroutines.WaitForSeconds(1f);
            }
        }

        private bool CanScheduleOpenEvent(float stamp) => storedData.SecondsUntilRaid - stamp <= 0 && RaidableBase.Get(RaidableType.Scheduled) < _config.Settings.Schedule.Max && IsPasteAvailable && !IsGridLoading && BasePlayer.activePlayerList.Count >= _config.Settings.Schedule.PlayerLimit;

        private List<int> GetAvailableDifficulties
        {
            get
            {
                var list = new List<int>();

                if (CanSpawnDifficultyToday(0))
                {
                    list.Add(0);
                }

                if (CanSpawnDifficultyToday(1))
                {
                    list.Add(1);
                }

                if (CanSpawnDifficultyToday(2))
                {
                    list.Add(2);
                }

                return list;
            }
        }

        private void SaveData()
        {
            dataFile.WriteObject(storedData);
            uidsFile.WriteObject(NetworkList);
        }

        public static string FormatGridReference(Vector3 position)
        {
            if (_config.Settings.ShowXZ)
            {
                return string.Format("{0} ({1} {2})", PositionToGrid(position), position.x.ToString("N2"), position.z.ToString("N2"));
            }

            return PositionToGrid(position);
        }

        public static string PositionToGrid(Vector3 position) // Rewrite from yetzt implementation
        {
            var r = new Vector2(World.Size / 2 + position.x, World.Size / 2 + position.z);
            var x = Mathf.Floor(r.x / 146.3f) % 26;
            var z = Mathf.Floor(World.Size / 146.3f) - Mathf.Floor(r.y / 146.3f);

            return $"{(char)('A' + x)}{z - 1}";
        }

        private string FormatTime(double seconds, string id = null)
        {
            var ts = TimeSpan.FromSeconds(seconds);

            return string.Format("{0:D2}h {1:D2}m {2:D2}s", ts.Hours, ts.Minutes, ts.Seconds);
        }

        #endregion

        #region Config

        private Dictionary<string, Dictionary<string, string>> GetMessages()
        {
            return new Dictionary<string, Dictionary<string, string>>
            {
                {"No Permission", new Dictionary<string, string>() {
                    {"en", "You do not have permission to use this command."},
                }},
                {"Building is blocked!", new Dictionary<string, string>() {
                    {"en", "<color=#FF0000>Building is blocked near raidable bases!</color>"},
                }},
                {"Difficulty Not Available", new Dictionary<string, string>() {
                    {"en", "Difficulty <color=#FF0000>{0}</color> is not available on any of your buildings."},
                }},
                {"Difficulty Not Available Admin", new Dictionary<string, string>() {
                    {"en", "Difficulty <color=#FF0000>{0}</color> is not available on any of your buildings. This could indicate that your CopyPaste files are not on this server in the oxide/data/copypaste folder."},
                }},
                {"Max Manual Events", new Dictionary<string, string>() {
                    {"en", "Maximum number of manual events <color=#FF0000>{0}</color> has been reached!"},
                }},
                {"Manual Event Failed", new Dictionary<string, string>() {
                    {"en", "Event failed to start! Unable to obtain a valid position. Please try again."},
                }},
                {"Help", new Dictionary<string, string>() {
                    {"en", "/{0} <tp> - start a manual event, and teleport to the position if TP argument is specified and you are an admin."},
                }},
                {"RaidOpenMessage", new Dictionary<string, string>() {
                    {"en", "<color=#C0C0C0>A {0} raidable base event has opened at <color=#FFFF00>{1}</color>! You are <color=#FFA500>{2}m</color> away. [{3}]</color>"},
                }},
                {"Next", new Dictionary<string, string>() {
                    {"en", "<color=#C0C0C0>No events are open. Next event in <color=#FFFF00>{0}</color></color>"},
                }},
                {"Wins", new Dictionary<string, string>()
                {
                    {"en", "<color=#C0C0C0>You have looted <color=#FFFF00>{0}</color> raid bases! View the ladder using <color=#FFA500>/{1} ladder</color> or <color=#FFA500>/{1} lifetime</color></color>"},
                }},
                {"RaidMessage", new Dictionary<string, string>() {
                    {"en", "Raidable Base {0}m [{1} players]"},
                }},
                {"Ladder", new Dictionary<string, string>()
                {
                    {"en", "<color=#FFFF00>[ Top 10 Raid Hunters (This Wipe) ]</color>:"},
                }},
                {"Ladder Total", new Dictionary<string, string>()
                {
                    {"en", "<color=#FFFF00>[ Top 10 Raid Hunters (Lifetime) ]</color>:"},
                }},
                {"Ladder Insufficient Players", new Dictionary<string, string>()
                {
                    {"en", "<color=#FFFF00>No players are on the ladder yet!</color>"},
                }},
                {"Next Automated Raid", new Dictionary<string, string>() {
                    {"en", "Next automated raid in {0} at {1}"},
                }},
                {"Not Enough Online", new Dictionary<string, string>() {
                    {"en", "Not enough players online ({0} minimum)"},
                }},
                {"Raid Base Distance", new Dictionary<string, string>() {
                    {"en", "<color=#C0C0C0>Raidable Base <color=#FFA500>{0}m</color>"},
                }},
                {"Destroyed Raid", new Dictionary<string, string>() {
                    {"en", "Destroyed a left over raid base at {0}"},
                }},
                {"Indestructible", new Dictionary<string, string>() {
                    {"en", "<color=#FF0000>Treasure chests are indestructible!</color>"},
                }},
                {"View Config", new Dictionary<string, string>() {
                    {"en", "Please view the config if you haven't already."},
                }},
                {"Log Stolen", new Dictionary<string, string>() {
                    {"en", "{0} ({1}) Raids {2}"},
                }},
                {"Log Granted", new Dictionary<string, string>() {
                    {"en", "Granted {0} ({1}) permission {2} for group {3}"},
                }},
                {"Log Saved", new Dictionary<string, string>() {
                    {"en", "Raid Hunters have been logged to: {0}"},
                }},
                {"Prefix", new Dictionary<string, string>() {
                    {"en", "[ <color=#406B35>Raidable Bases</color> ] "},
                }},
                {"RestartDetected", new Dictionary<string, string>()
                {
                    {"en", "Restart detected. Next event in {0} minutes."},
                }},
                {"EconomicsDeposit", new Dictionary<string, string>()
                {
                    {"en", "You have received <color=#FFFF00>${0}</color> for stealing the treasure!"},
                }},
                {"EconomicsWithdraw", new Dictionary<string, string>()
                {
                    {"en", "You have paid <color=#FFFF00>${0}</color> for a raidable base!"},
                }},
                {"EconomicsWithdrawGift", new Dictionary<string, string>()
                {
                    {"en", "{0} has paid <color=#FFFF00>${1}</color> for your raidable base!"},
                }},
                {"EconomicsWithdrawFailed", new Dictionary<string, string>()
                {
                    {"en", "You do not have <color=#FFFF00>${0}</color> for a raidable base!"},
                }},
                {"ServerRewardPoints", new Dictionary<string, string>()
                {
                    {"en", "You have received <color=#FFFF00>{0} RP</color> for stealing the treasure!"},
                }},
                {"ServerRewardPointsTaken", new Dictionary<string, string>()
                {
                    {"en", "You have paid <color=#FFFF00>{0} RP</color> for a raidable base!"},
                }},
                {"ServerRewardPointsGift", new Dictionary<string, string>()
                {
                    {"en", "{0} has paid <color=#FFFF00>{1} RP</color> for your raidable base!"},
                }},
                {"ServerRewardPointsFailed", new Dictionary<string, string>()
                {
                    {"en", "You do not have <color=#FFFF00>{0} RP</color> for a raidable base!"},
                }},
                {"InvalidItem", new Dictionary<string, string>()
                {
                    {"en", "Invalid item shortname: {0}. Use /{1} additem <shortname> <amount> [skin]"},
                }},
                {"AddedItem", new Dictionary<string, string>()
                {
                    {"en", "Added item: {0} amount: {1}, skin: {2}"},
                }},
                {"CustomPositionSet", new Dictionary<string, string>()
                {
                    {"en", "Custom event spawn location set to: {0}"},
                }},
                {"CustomPositionRemoved", new Dictionary<string, string>()
                {
                    {"en", "Custom event spawn location removed."},
                }},
                {"OpenedEvents", new Dictionary<string, string>()
                {
                    {"en", "Opened {0}/{1} events."},
                }},
                {"OnPlayerEntered", new Dictionary<string, string>()
                {
                    {"en", "<color=#FF0000>You have entered a raidable PVP base!</color>"},
                }},
                {"OnPlayerEnteredPVE", new Dictionary<string, string>()
                {
                    {"en", "<color=#FF0000>You have entered a raidable PVE base!</color>"},
                }},
                {"OnFirstPlayerEntered", new Dictionary<string, string>()
                {
                    {"en", "<color=#FFFF00>{0}</color> is the first to enter the raidable base at <color=#FFFF00>{1}</color>"},
                }},
                {"OnChestOpened", new Dictionary<string, string>() {
                    {"en", "<color=#FFFF00>{0}</color> is the first to see the treasures at <color=#FFFF00>{1}</color>!</color>"},
                }},
                {"OnRaidFinished", new Dictionary<string, string>() {
                    {"en", "The raid at <color=#FFFF00>{0}</color> has been unlocked!"},
                }},
                {"CannotBeMounted", new Dictionary<string, string>() {
                    {"en", "You cannot loot the treasure while mounted!"},
                }},
                {"CannotTeleport", new Dictionary<string, string>() {
                    {"en", "You are not allowed to teleport from this event."},
                }},
                {"MustBeAuthorized", new Dictionary<string, string>() {
                    {"en", "You must have building privilege to access this treasure!"},
                }},
                {"OwnerLocked", new Dictionary<string, string>() {
                    {"en", "This treasure belongs to someone else!"},
                }},
                {"CannotFindPosition", new Dictionary<string, string>() {
                    {"en", "Could not find a random position!"},
                }},
                {"PasteOnCooldown", new Dictionary<string, string>() {
                    {"en", "Paste is on cooldown!"},
                }},
                {"BuyableOnCooldown", new Dictionary<string, string>() {
                    {"en", "Try again, a buyable raid was already requested."},
                }},
                {"SpawnOnCooldown", new Dictionary<string, string>() {
                    {"en", "Try again, a manual spawn was already requested."},
                }},
                {"Thief", new Dictionary<string, string>() {
                    {"en", "<color=#FFFF00>The base at <color=#FFFF00>{0}</color> has been raided by <color=#FFFF00>{1}</color>!</color>"},
                }},
                {"BuySyntax", new Dictionary<string, string>() {
                    {"en", "<color=#FFFF00>Syntax: {0} easy|medium|hard {1}</color>"},
                }},
                {"TargetNotFoundId", new Dictionary<string, string>() {
                    {"en", "<color=#FFFF00>Target {0} not found, or not online.</color>"},
                }},
                {"TargetNotFoundNoId", new Dictionary<string, string>() {
                    {"en", "<color=#FFFF00>No steamid provided.</color>"},
                }},
                {"BuyAnotherDifficulty", new Dictionary<string, string>() {
                    {"en", "Difficulty '<color=#FFFF00>{0}</color>' is not available, please try another difficulty."},
                }},
                {"BuyDifficultyNotAvailableToday", new Dictionary<string, string>() {
                    {"en", "Difficulty '<color=#FFFF00>{0}</color>' is not available today, please try another difficulty."},
                }},
                {"BuyPVPRaidsDisabled", new Dictionary<string, string>() {
                    {"en", "<color=#FFFF00>No PVE raids can be bought for this difficulty as buying raids that allow PVP is not allowed.</color>"},
                }},
                {"BuyBaseSpawnedAt", new Dictionary<string, string>() {
                    {"en", "<color=#FFFF00>Your base has been spawned at {0} in {1} !</color>"},
                }},
                {"BuyBaseAnnouncement", new Dictionary<string, string>() {
                    {"en", "<color=#FFFF00>{0} has paid for a base at {1} in {2}!</color>"},
                }},
                {"DestroyingBaseAt", new Dictionary<string, string>() {
                    {"en", "<color=#C0C0C0>Destroying raid base at <color=#FFFF00>{0}</color> in <color=#FFFF00>{1}</color> minutes!</color>"},
                }},
                {"PasteIsBlocked", new Dictionary<string, string>() {
                    {"en", "You cannot start a raid base event there!"},
                }},
                {"LookElsewhere", new Dictionary<string, string>() {
                    {"en", "Unable to find a position; look elsewhere."},
                }},
                {"NoValidBuildingsConfigured", new Dictionary<string, string>() {
                    {"en", "No valid buildings have been configured. Raidable Bases > Building Names in config."},
                }},
                {"DespawnBaseSuccess", new Dictionary<string, string>() {
                    {"en", "<color=#C0C0C0>Despawning the nearest raid base to you!</color>"},
                }},
                {"DespawnedAt", new Dictionary<string, string>() {
                    {"en", "{0} despawned a base manually at {1}"},
                }},
                {"DespawnedAll", new Dictionary<string, string>() {
                    {"en", "{0} despawned all bases manually"},
                }},
                {"ModeLevel", new Dictionary<string, string>() {
                    {"en", "level"},
                }},
                {"ModeEasy", new Dictionary<string, string>() {
                    {"en", "easy"},
                }},
                {"ModeMedium", new Dictionary<string, string>() {
                    {"en", "medium"},
                }},
                {"ModeHard", new Dictionary<string, string>() {
                    {"en", "hard"},
                }},
                {"ModeVeryEasy", new Dictionary<string, string>() {
                    {"en", "very easy"},
                }},
                {"ModeVeryHard", new Dictionary<string, string>() {
                    {"en", "very hard"},
                }},
                {"ModeNightmare", new Dictionary<string, string>() {
                    {"en", "nightmare"},
                }},
                {"DespawnBaseNoneAvailable", new Dictionary<string, string>() {
                    {"en", "<color=#C0C0C0>You must be within 100m of a raid base to despawn it.</color>"},
                }},
                { "GridIsLoading", new Dictionary<string, string>() {
                    {"en", "The grid is loading; please wait until it has finished."},
                }},
                { "GridIsLoadingFormatted", new Dictionary<string, string>() {
                    {"en", "Grid is loading. The process has taken {0} seconds so far with {1} locations added on the grid."},
                }},
                { "TooPowerful", new Dictionary<string, string>() {
                    {"en", "<color=#FF0000>This place is guarded by a powerful spirit. You sheath your wand in fear!</color>"},
                }},
                { "TooPowerfulDrop", new Dictionary<string, string>() {
                    {"en", "<color=#FF0000>This place is guarded by a powerful spirit. You drop your wand in fear!</color>"},
                }},
                { "BuyCooldown", new Dictionary<string, string>() {
                    {"en", "<color=#FF0000>You must wait {0} seconds to use this command!</color>"},
                }},
                { "LoadCopyPaste", new Dictionary<string, string>() {
                    {"en", "CopyPaste is not loaded."},
                }},
                { "DoomAndGloom", new Dictionary<string, string>() {
                    {"en", "<color=#FF0000>You have left a {0} zone and can be attacked for another {1} seconds!</color>"},
                }},
                { "MaintainCoroutineFailedToday", new Dictionary<string, string>() {
                    {"en", "<color=#FF0000>Failed to start maintain coroutine; no difficulties are available today.</color>"},
                }},
                { "ScheduleCoroutineFailedToday", new Dictionary<string, string>() {
                    {"en", "<color=#FF0000>Failed to start scheduled coroutine; no difficulties are available today.</color>"},
                }},
                {"NoConfiguredLoot", new Dictionary<string, string>() {
                    {"en", "Error: No loot found in the config!"},
                }},
                {"NoLootSpawned", new Dictionary<string, string>() {
                    {"en", "Error: No loot was spawned!"},
                }},
                {"NotCompatible", new Dictionary<string, string>() {
                    {"en", "Expansion Mode is not available for your version of Dangerous Treasures ({0}). Please update it to use this feature."},
                }},
                {"LoadedManual", new Dictionary<string, string>() {
                    {"en", "Loaded {0} manual spawns."},
                }},
                {"LoadedBuyable", new Dictionary<string, string>() {
                    {"en", "Loaded {0} buyable spawns."},
                }},
                {"LoadedMaintained", new Dictionary<string, string>() {
                    {"en", "Loaded {0} maintained spawns."},
                }},
                {"LoadedScheduled", new Dictionary<string, string>() {
                    {"en", "Loaded {0} scheduled spawns."},
                }},
                {"InitializedGrid", new Dictionary<string, string>() {
                    {"en", "Grid initialization completed in {0} seconds and {1} milliseconds on a {2} size map. {3} locations are on the grid."},
                }},
                {"EntityCountMax", new Dictionary<string, string>() {
                    {"en", "Command disabled due to entity count being greater than 300k"},
                }},
                {"NotifyPlayerMessageFormat", new Dictionary<string, string>() {
                    {"en", "<color=#ADD8E6>{rank}</color>. <color=#C0C0C0>{name}</color> (<color=#FFFF00>{value}</color>)"},
                }},
                {"ConfigUseFormat", new Dictionary<string, string>() {
                    {"en", "Use: rb.config <{0}> [base] [subset]"},
                }},
                {"ConfigAddBaseSyntax", new Dictionary<string, string>() {
                    {"en", "Use: rb.config add nivex1 nivex4 nivex5 nivex6"},
                }},
                {"FileDoesNotExist", new Dictionary<string, string>() {
                    {"en", " > This file does not exist\n"},
                }},
                {"ListingAll", new Dictionary<string, string>() {
                    {"en", "Listing all primary bases and their subsets:"},
                }},
                {"PrimaryBase", new Dictionary<string, string>() {
                    {"en", "Primary Base: "},
                }},
                {"AdditionalBase", new Dictionary<string, string>() {
                    {"en", "Additional Base: "},
                }},
                {"RaidPVEWarning", new Dictionary<string, string>() {
                    {"en", "Configuration is set to block PVP raids from being bought, and no PVE raids are configured. Therefore players cannot buy raids until you add a PVE raid."},
                }},
                {"NoValidBuilingsWarning", new Dictionary<string, string>() {
                    {"en", "No valid buildings are configured with a valid file that exists. Did you configure valid files and reload the plugin?"},
                }},
                {"Adding", new Dictionary<string, string>() {
                    {"en", "Adding: {0}"},
                }},
                {"AddedPrimaryBase", new Dictionary<string, string>() {
                    {"en", "Added Primary Base: {0}"},
                }},
                {"AddedAdditionalBase", new Dictionary<string, string>() {
                    {"en", "Added Additional Base: {0}"},
                }},
                {"DifficultySetTo", new Dictionary<string, string>() {
                    {"en", "Difficulty set to: {0}"},
                }},
                {"EntryAlreadyExists", new Dictionary<string, string>() {
                    {"en", "That entry already exists."},
                }},
                {"RemoveSyntax", new Dictionary<string, string>() {
                    {"en", "Use: rb.config remove nivex1"},
                }},
                {"RemovingAllBasesFor", new Dictionary<string, string>() {
                    {"en", "\nRemoving all bases for: {0}"},
                }},
                {"RemovedPrimaryBase", new Dictionary<string, string>() {
                    {"en", "Removed primary base: {0}"},
                }},
                {"RemovedAdditionalBase", new Dictionary<string, string>() {
                    {"en", "Removed additional base {0} from primary base {1}"},
                }},
                {"RemovedEntries", new Dictionary<string, string>() {
                    {"en", "Removed {0} entries"},
                }},
            };
        }

        protected override void LoadDefaultMessages()
        {
            var compiledLangs = new Dictionary<string, Dictionary<string, string>>();

            foreach (var line in GetMessages())
            {
                foreach (var translate in line.Value)
                {
                    if (!compiledLangs.ContainsKey(translate.Key))
                        compiledLangs[translate.Key] = new Dictionary<string, string>();

                    compiledLangs[translate.Key][line.Key] = translate.Value;
                }
            }

            foreach (var cLangs in compiledLangs)
                lang.RegisterMessages(cLangs.Value, this, cLangs.Key);
        }

        private static int GetPercentIncreasedAmount(int amount)
        {
            decimal percentLoss = _config.Treasure.PercentLoss;

            if (percentLoss > 0m)
            {
                percentLoss /= 100m;
            }

            if (_config.Treasure.UseDOWL && !_config.Treasure.Increased && percentLoss > 0m)
            {
                return UnityEngine.Random.Range(Convert.ToInt32(amount - (amount * percentLoss)), amount);
            }

            decimal percentIncrease = 0m;

            switch (DateTime.Now.DayOfWeek)
            {
                case DayOfWeek.Monday:
                    {
                        percentIncrease = _config.Treasure.PercentIncreaseOnMonday;
                        break;
                    }
                case DayOfWeek.Tuesday:
                    {
                        percentIncrease = _config.Treasure.PercentIncreaseOnTuesday;
                        break;
                    }
                case DayOfWeek.Wednesday:
                    {
                        percentIncrease = _config.Treasure.PercentIncreaseOnWednesday;
                        break;
                    }
                case DayOfWeek.Thursday:
                    {
                        percentIncrease = _config.Treasure.PercentIncreaseOnThursday;
                        break;
                    }
                case DayOfWeek.Friday:
                    {
                        percentIncrease = _config.Treasure.PercentIncreaseOnFriday;
                        break;
                    }
                case DayOfWeek.Saturday:
                    {
                        percentIncrease = _config.Treasure.PercentIncreaseOnSaturday;
                        break;
                    }
                case DayOfWeek.Sunday:
                    {
                        percentIncrease = _config.Treasure.PercentIncreaseOnSunday;
                        break;
                    }
            }

            if (percentIncrease > 1m)
            {
                percentIncrease /= 100;
            }

            if (percentIncrease > 0m)
            {
                amount = Convert.ToInt32(amount + (amount * percentIncrease));

                if (_config.Treasure.PercentLoss > 0m)
                {
                    amount = UnityEngine.Random.Range(Convert.ToInt32(amount - (amount * _config.Treasure.PercentLoss)), amount);
                }
            }

            return amount;
        }

        #endregion

        #region Configuration

        private static Configuration _config;

        private static BuildingOptions DefaultBuilding
        {
            get
            {
                return new BuildingOptions
                {
                    PasteOptions = DefaultPasteOptions
                };
            }
        }

        private static List<PasteOption> DefaultPasteOptions
        {
            get
            {
                return new List<PasteOption>
                {
                    new PasteOption() { Key = "stability", Value = "false" },
                    new PasteOption() { Key = "autoheight", Value = "false" },
                    new PasteOption() { Key = "height", Value = "1.0" }
                };
            }
        }

        private static Dictionary<string, List<PasteOption>> DefaultPasteOptionsAddition
        {
            get
            {
                return new Dictionary<string, List<PasteOption>>
                {
                    ["nivex4"] = DefaultPasteOptions,
                    ["nivex5"] = DefaultPasteOptions,
                    ["nivex6"] = DefaultPasteOptions
                };
            }
        }

        private static Dictionary<string, BuildingOptions> DefaultBuildingOptions
        {
            get
            {
                return new Dictionary<string, BuildingOptions>()
                {
                    ["nivex1"] = DefaultBuilding,
                    ["nivex2"] = DefaultBuilding,
                    ["nivex3"] = DefaultBuilding,
                };
            }
        }

        private static List<TreasureItem> DefaultLoot
        {
            get
            {
                return new List<TreasureItem>
                {
                    new TreasureItem { shortname = "ammo.pistol", amount = 40, skin = 0, amountMin = 40 },
                    new TreasureItem { shortname = "ammo.pistol.fire", amount = 40, skin = 0, amountMin = 40 },
                    new TreasureItem { shortname = "ammo.pistol.hv", amount = 40, skin = 0, amountMin = 40 },
                    new TreasureItem { shortname = "ammo.rifle", amount = 60, skin = 0, amountMin = 60 },
                    new TreasureItem { shortname = "ammo.rifle.explosive", amount = 60, skin = 0, amountMin = 60 },
                    new TreasureItem { shortname = "ammo.rifle.hv", amount = 60, skin = 0, amountMin = 60 },
                    new TreasureItem { shortname = "ammo.rifle.incendiary", amount = 60, skin = 0, amountMin = 60 },
                    new TreasureItem { shortname = "ammo.shotgun", amount = 24, skin = 0, amountMin = 24 },
                    new TreasureItem { shortname = "ammo.shotgun.slug", amount = 40, skin = 0, amountMin = 40 },
                    new TreasureItem { shortname = "surveycharge", amount = 20, skin = 0, amountMin = 20 },
                    new TreasureItem { shortname = "metal.refined", amount = 150, skin = 0, amountMin = 150 },
                    new TreasureItem { shortname = "bucket.helmet", amount = 1, skin = 0, amountMin = 1 },
                    new TreasureItem { shortname = "cctv.camera", amount = 1, skin = 0, amountMin = 1 },
                    new TreasureItem { shortname = "coffeecan.helmet", amount = 1, skin = 0, amountMin = 1 },
                    new TreasureItem { shortname = "explosive.timed", amount = 1, skin = 0, amountMin = 1 },
                    new TreasureItem { shortname = "metal.facemask", amount = 1, skin = 0, amountMin = 1 },
                    new TreasureItem { shortname = "metal.plate.torso", amount = 1, skin = 0, amountMin = 1 },
                    new TreasureItem { shortname = "mining.quarry", amount = 1, skin = 0, amountMin = 1 },
                    new TreasureItem { shortname = "pistol.m92", amount = 1, skin = 0, amountMin = 1 },
                    new TreasureItem { shortname = "rifle.ak", amount = 1, skin = 0, amountMin = 1 },
                    new TreasureItem { shortname = "rifle.bolt", amount = 1, skin = 0, amountMin = 1 },
                    new TreasureItem { shortname = "rifle.lr300", amount = 1, skin = 0, amountMin = 1 },
                    new TreasureItem { shortname = "smg.2", amount = 1, skin = 0, amountMin = 1 },
                    new TreasureItem { shortname = "smg.mp5", amount = 1, skin = 0, amountMin = 1 },
                    new TreasureItem { shortname = "smg.thompson", amount = 1, skin = 0, amountMin = 1 },
                    new TreasureItem { shortname = "supply.signal", amount = 1, skin = 0, amountMin = 1 },
                    new TreasureItem { shortname = "targeting.computer", amount = 1, skin = 0, amountMin = 1 },
                };
            }
        }

        public class PluginSettingsLimitsDays
        {
            [JsonProperty(PropertyName = "Monday")]
            public bool Monday { get; set; } = true;

            [JsonProperty(PropertyName = "Tuesday")]
            public bool Tuesday { get; set; } = true;

            [JsonProperty(PropertyName = "Wednesday")]
            public bool Wednesday { get; set; } = true;

            [JsonProperty(PropertyName = "Thursday")]
            public bool Thursday { get; set; } = true;

            [JsonProperty(PropertyName = "Friday")]
            public bool Friday { get; set; } = true;

            [JsonProperty(PropertyName = "Saturday")]
            public bool Saturday { get; set; } = true;

            [JsonProperty(PropertyName = "Sunday")]
            public bool Sunday { get; set; } = true;
        }

        public class PluginSettingsBaseManagement
        {
            [JsonProperty(PropertyName = "Easy Raids Can Spawn On")]
            public PluginSettingsLimitsDays Easy { get; set; } = new PluginSettingsLimitsDays();

            [JsonProperty(PropertyName = "Medium Raids Can Spawn On")]
            public PluginSettingsLimitsDays Medium { get; set; } = new PluginSettingsLimitsDays();

            [JsonProperty(PropertyName = "Hard Raids Can Spawn On")]
            public PluginSettingsLimitsDays Hard { get; set; } = new PluginSettingsLimitsDays();

            [JsonProperty(PropertyName = "Allow Teleport")]
            public bool AllowTeleport { get; set; }

            [JsonProperty(PropertyName = "Allow Cupboard Loot To Drop")]
            public bool AllowCupboardLoot { get; set; } = true;

            [JsonProperty(PropertyName = "Allow Player Bags To Be Lootable At PVP Bases")]
            public bool PlayersLootableInPVP { get; set; } = true;

            [JsonProperty(PropertyName = "Allow Player Bags To Be Lootable At PVE Bases")]
            public bool PlayersLootableInPVE { get; set; } = true;

            [JsonProperty(PropertyName = "Block Mounted Damage To Bases And Players")]
            public bool BlockMounts { get; set; }

            [JsonProperty(PropertyName = "Block RestoreUponDeath Plugin For PVP Bases")]
            public bool BlockRestorePVP { get; set; }

            [JsonProperty(PropertyName = "Block RestoreUponDeath Plugin For PVE Bases")]
            public bool BlockRestorePVE { get; set; }

            [JsonProperty(PropertyName = "Boxes Are Invulnerable")]
            public bool Invulnerable { get; set; }

            [JsonProperty(PropertyName = "Bypass Lock Treasure To First Attacker For PVE Bases")]
            public bool BypassUseOwnersForPVE { get; set; }

            [JsonProperty(PropertyName = "Bypass Lock Treasure To First Attacker For PVP Bases")]
            public bool BypassUseOwnersForPVP { get; set; }

            [JsonProperty(PropertyName = "Ignore Containers That Spawn With Loot Already")]
            public bool IgnoreContainedLoot { get; set; }

            [JsonProperty(PropertyName = "Lock Treasure To First Attacker")]
            public bool UseOwners { get; set; }

            [JsonProperty(PropertyName = "Release Lock On Treasure When First Attacker Is X Meters Away")]
            public float MaxOwnerDistance { get; set; } = 300f;

            [JsonProperty(PropertyName = "Lock Treasure Max Inactive Time")]
            public float LockTime { get; set; } = 300f;

            [JsonProperty(PropertyName = "Minutes Until Despawn After Looting (min: 1)")]
            public int DespawnMinutes { get; set; } = 15;

            [JsonProperty(PropertyName = "Minutes Until Despawn After Inactive (0 = disabled)")]
            public int DespawnMinutesInactive { get; set; } = 45;

            [JsonProperty(PropertyName = "PVP Delay Between Zone Hopping")]
            public float PVPDelay { get; set; } = 10f;

            [JsonProperty(PropertyName = "Spawn Bases X Distance Apart")]
            public float Distance { get; set; } = 150f;

            [JsonProperty(PropertyName = "Turn Lights On At Night")]
            public bool Lights { get; set; } = true;

            [JsonProperty(PropertyName = "Turn Lights On Indefinitely")]
            public bool AlwaysLights { get; set; }

            [JsonProperty(PropertyName = "Use Random Codes On Code Locks")]
            public bool RandomCodes { get; set; } = true;
        }

        public class PluginSettingsMapMarkers
        {
            [JsonProperty(PropertyName = "Marker Name")]
            public string MarkerName { get; set; } = "Raidable Base Event";

            [JsonProperty(PropertyName = "Radius")]
            public float Radius { get; set; } = 0.25f;

            [JsonProperty(PropertyName = "Use Vending Map Marker")]
            public bool UseVendingMarker { get; set; } = true;

            [JsonProperty(PropertyName = "Use Explosion Map Marker")]
            public bool UseExplosionMarker { get; set; }
        }

        public class PluginSettings
        {
            [JsonProperty(PropertyName = "Raid Management")]
            public PluginSettingsBaseManagement Management { get; set; } = new PluginSettingsBaseManagement();

            [JsonProperty(PropertyName = "Map Markers")]
            public PluginSettingsMapMarkers Markers { get; set; } = new PluginSettingsMapMarkers();

            [JsonProperty(PropertyName = "Buyable Events")]
            public RaidableBaseSettingsBuyable Buyable { get; set; } = new RaidableBaseSettingsBuyable();

            [JsonProperty(PropertyName = "Maintained Events")]
            public RaidableBaseSettingsMaintainer Maintained { get; set; } = new RaidableBaseSettingsMaintainer();

            [JsonProperty(PropertyName = "Manual Events")]
            public RaidableBaseSettingsManual Manual { get; set; } = new RaidableBaseSettingsManual();

            [JsonProperty(PropertyName = "Scheduled Events")]
            public RaidableBaseSettingsSchedule Schedule { get; set; } = new RaidableBaseSettingsSchedule();

            [JsonProperty(PropertyName = "Economics Buy Raid Costs (0 = disabled)")]
            public RaidableBaseEconomicsOptions Economics { get; set; } = new RaidableBaseEconomicsOptions();

            [JsonProperty(PropertyName = "ServerRewards Buy Raid Costs (0 = disabled)")]
            public RaidableBaseServerRewardsOptions ServerRewards { get; set; } = new RaidableBaseServerRewardsOptions();

            [JsonProperty(PropertyName = "Automatically Teleport Admins To Their Map Marker Positions")]
            public bool TeleportMarker { get; set; }

            [JsonProperty(PropertyName = "Block Wizardry Plugin At Events")]
            public bool NoWizardry { get; set; }

            [JsonProperty(PropertyName = "Chat Steam64ID")]
            public ulong ChatID { get; set; }

            [JsonProperty(PropertyName = "Expansion Mode (Dangerous Treasures)")]
            public bool ExpansionMode { get; set; }

            [JsonProperty(PropertyName = "Remove Admins From Raiders List")]
            public bool RemoveAdminRaiders { get; set; }

            [JsonProperty(PropertyName = "Show X Z Coordinates")]
            public bool ShowXZ { get; set; } = false;

            [JsonProperty(PropertyName = "Buy Raid Command")]
            public string BuyCommand { get; set; } = "buyraid";

            [JsonProperty(PropertyName = "Event Command")]
            public string EventCommand { get; set; } = "rbe";

            [JsonProperty(PropertyName = "Hunter Command")]
            public string HunterCommand { get; set; } = "rb";

            [JsonProperty(PropertyName = "Server Console Command")]
            public string ConsoleCommand { get; set; } = "rbevent";

        }

        public class EventMessageSettings
        {
            [JsonProperty(PropertyName = "Announce Raid Unlocked")]
            public bool AnnounceRaidUnlock { get; set; }

            [JsonProperty(PropertyName = "Announce Buy Base Messages")]
            public bool AnnounceBuy { get; set; }

            [JsonProperty(PropertyName = "Announce Thief Message")]
            public bool AnnounceThief { get; set; } = true;

            [JsonProperty(PropertyName = "Announce PVE/PVP Enter Messages")]
            public bool AnnounceEnterExit { get; set; } = true;

            [JsonProperty(PropertyName = "Show Destroy Warning")]
            public bool ShowWarning { get; set; } = true;

            [JsonProperty(PropertyName = "Show Opened Message")]
            public bool Opened { get; set; } = true;

            [JsonProperty(PropertyName = "Show Prefix")]
            public bool Prefix { get; set; } = true;
        }

        public class GUIAnnouncementSettings
        {
            [JsonProperty(PropertyName = "Enabled")]
            public bool Enabled { get; set; }

            [JsonProperty(PropertyName = "Banner Tint Color")]
            public string TintColor { get; set; } = "Grey";

            [JsonProperty(PropertyName = "Maximum Distance")]
            public float Distance { get; set; } = 300f;

            [JsonProperty(PropertyName = "Text Color")]
            public string TextColor { get; set; } = "White";
        }

        public class LustyMapSettings
        {
            [JsonProperty(PropertyName = "Enabled")]
            public bool Enabled { get; set; }

            [JsonProperty(PropertyName = "Icon File")]
            public string IconFile { get; set; } = "http://i.imgur.com/XoEMTJj.png";

            [JsonProperty(PropertyName = "Icon Name")]
            public string IconName { get; set; } = "rbevent";

            [JsonProperty(PropertyName = "Icon Rotation")]
            public float IconRotation { get; set; }
        }

        public class NpcSettings
        {
            [JsonProperty(PropertyName = "Murderer Items", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> MurdererItems { get; set; } = new List<string> { "metal.facemask", "metal.plate.torso", "pants", "tactical.gloves", "boots.frog", "tshirt", "machete" };

            [JsonProperty(PropertyName = "Scientist Items", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> ScientistItems { get; set; } = new List<string> { "hazmatsuit_scientist", "rifle.ak" };

            [JsonProperty(PropertyName = "Murderer Kits", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> MurdererKits { get; set; } = new List<string> { "murderer_kit_1", "murderer_kit_2" };

            [JsonProperty(PropertyName = "Scientist Kits", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> ScientistKits { get; set; } = new List<string> { "scientist_kit_1", "scientist_kit_2" };

            [JsonProperty(PropertyName = "Random Names", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> RandomNames { get; set; } = new List<string>();

            [JsonProperty(PropertyName = "Amount To Spawn")]
            public int SpawnAmount { get; set; } = 3;

            [JsonProperty(PropertyName = "Aggression Range")]
            public float AggressionRange { get; set; } = 70f;

            [JsonProperty(PropertyName = "Despawn Inventory On Death")]
            public bool DespawnInventory { get; set; } = true;

            [JsonProperty(PropertyName = "Enabled")]
            public bool Enabled { get; set; } = true;

            [JsonProperty(PropertyName = "Health For Murderers (100 min, 5000 max)")]
            public float MurdererHealth { get; set; } = 150f;

            [JsonProperty(PropertyName = "Health For Scientists (100 min, 5000 max)")]
            public float ScientistHealth { get; set; } = 150f;

            [JsonProperty(PropertyName = "Minimum Amount To Spawn")]
            public int SpawnMinAmount { get; set; } = 1;

            [JsonProperty(PropertyName = "Use Dangerous Treasures NPCs")]
            public bool UseExpansionNpcs { get; set; }

            [JsonProperty(PropertyName = "Spawn Murderers And Scientists")]
            public bool SpawnBoth { get; set; } = true;

            [JsonProperty(PropertyName = "Scientist Weapon Accuracy (0 - 100)")]
            public float Accuracy { get; set; } = 75f;

            [JsonProperty(PropertyName = "Spawn Murderers")]
            public bool SpawnMurderers { get; set; }

            [JsonProperty(PropertyName = "Spawn Random Amount")]
            public bool SpawnRandomAmount { get; set; }

            [JsonProperty(PropertyName = "Spawn Scientists Only")]
            public bool SpawnScientistsOnly { get; set; }
        }

        public class PasteOption
        {
            [JsonProperty(PropertyName = "Option")]
            public string Key { get; set; }

            [JsonProperty(PropertyName = "Value")]
            public string Value { get; set; }
        }

        public class BuildingLevelOne
        {
            [JsonProperty(PropertyName = "Amount (0 = disabled)")]
            public int Amount { get; set; }

            [JsonProperty(PropertyName = "Chance To Play")]
            public float Chance { get; set; } = 0.5f;
        }
        
        public class BuildingLevels
        {
            [JsonProperty(PropertyName = "Level 1 - Play With Fire")]
            public BuildingLevelOne Level1 { get; set; } = new BuildingLevelOne();

            [JsonProperty(PropertyName = "Level 2 - Final Death")]
            public bool Level2 { get; set; }
        }

        public class BuildingGradeLevels
        {
            [JsonProperty(PropertyName = "Wooden")]
            public bool Wooden { get; set; }

            [JsonProperty(PropertyName = "Stone")]
            public bool Stone { get; set; }

            [JsonProperty(PropertyName = "Metal")]
            public bool Metal { get; set; }

            [JsonProperty(PropertyName = "HQM")]
            public bool HQM { get; set; }

            public bool Any() => Wooden || Stone || Metal || HQM;
        }

        public class BuildingOptions
        {
            [JsonProperty(PropertyName = "Additional Bases For This Difficulty", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, List<PasteOption>> AdditionalBases { get; set; } = DefaultPasteOptionsAddition;

            [JsonProperty(PropertyName = "Paste Options", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<PasteOption> PasteOptions { get; set; } = new List<PasteOption>();

            [JsonProperty(PropertyName = "Arena Walls")]
            public RaidableBaseWallOptions ArenaWalls { get; set; } = new RaidableBaseWallOptions();

            [JsonProperty(PropertyName = "NPC Levels")]
            public BuildingLevels Levels { get; set; } = new BuildingLevels();

            [JsonProperty(PropertyName = "NPCs")]
            public NpcSettings NPC { get; set; } = new NpcSettings();

            [JsonProperty(PropertyName = "Rewards")]
            public RewardSettings Rewards { get; set; } = new RewardSettings();

            [JsonProperty(PropertyName = "Change Building Material Tier To")]
            public BuildingGradeLevels Tiers { get; set; } = new BuildingGradeLevels();

            [JsonProperty(PropertyName = "Loot (Empty List = Use Treasure Loot)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<TreasureItem> Loot { get; set; } = new List<TreasureItem>();

            [JsonProperty(PropertyName = "Add Code Lock To Unlocked Or KeyLocked Doors")]
            public bool DoorLock { get; set; } = true;

            [JsonProperty(PropertyName = "Allow Base To Float Above Water")]
            public bool Submerged { get; set; }

            [JsonProperty(PropertyName = "Allow Duplicate Items")]
            public bool AllowDuplicates { get; set; }

            [JsonProperty(PropertyName = "Allow Players To Pickup Deployables")]
            public bool AllowPickup { get; set; }

            [JsonProperty(PropertyName = "Allow Players To Deploy A Cupboard")]
            public bool AllowBuildingPriviledges { get; set; } = true;

            [JsonProperty(PropertyName = "Allow PVP")]
            public bool AllowPVP { get; set; } = true;

            [JsonProperty(PropertyName = "Allow Friendly Fire (Teams)")]
            public bool AllowFriendlyFire { get; set; } = true;

            [JsonProperty(PropertyName = "Amount Of Items To Spawn")]
            public int TreasureAmount { get; set; } = 6;

            [JsonProperty(PropertyName = "Block Outside Damage From Players To Players Inside Events")]
            public bool BlockOutsideDamageToPlayers { get; set; }

            [JsonProperty(PropertyName = "Block Outside Damage From Players To Bases Inside Events")]
            public bool BlockOutsideDamageToBase { get; set; }

            [JsonProperty(PropertyName = "Difficulty (0 = easy, 1 = medium, 2 = hard)")]
            public int Difficulty { get; set; }

            [JsonProperty(PropertyName = "Divide Loot Into All Containers")]
            public bool DivideLoot { get; set; }

            [JsonProperty(PropertyName = "Drop Container Loot X Seconds After It Is Looted")]
            public float DropTimeAfterLooting { get; set; }

            [JsonProperty(PropertyName = "Create Dome Around Event Using Spheres (0 = disabled, recommended = 5)")]
            public int SphereAmount { get; set; } = 5;

            [JsonProperty(PropertyName = "Empty All Containers Before Spawning Loot")]
            public bool EmptyAll { get; set; }

            [JsonProperty(PropertyName = "Eject Enemies From Purchased PVE Raids")]
            public bool EjectPurchasedPVE { get; set; }

            [JsonProperty(PropertyName = "Eject Enemies From Purchased PVP Raids")]
            public bool EjectPurchasedPVP { get; set; }

            [JsonProperty(PropertyName = "Eject Enemies From Locked PVE Raids")]
            public bool EjectLockedPVE { get; set; }

            [JsonProperty(PropertyName = "Eject Enemies From Locked PVP Raids")]
            public bool EjectLockedPVP { get; set; }

            [JsonProperty(PropertyName = "Equip Unequipped AutoTurret With")]
            public string AutoTurretShortname { get; set; } = "rifle.ak";

            [JsonProperty(PropertyName = "Explosion Damage Modifier (0-999)")]
            public float ExplosionModifier { get; set; } = 100f;

            [JsonProperty(PropertyName = "Force All Boxes To Have Same Skin")]
            public bool SetSkins { get; set; } = true;

            [JsonProperty(PropertyName = "Maximum Elevation Level")]
            public float Elevation { get; set; } = 2.5f;

            [JsonProperty(PropertyName = "Protection Radius")]
            public float ProtectionRadius { get; set; } = 50f;

            [JsonProperty(PropertyName = "Block Plugins Which Prevent Item Durability Loss")]
            public bool EnforceDurability { get; set; } = false;

            [JsonProperty(PropertyName = "Remove Equipped AutoTurret Weapon")]
            public bool RemoveTurretWeapon { get; set; }

            [JsonProperty(PropertyName = "Require Cupboard Access To Loot")]
            public bool RequiresCupboardAccess { get; set; }

            [JsonProperty(PropertyName = "Respawn Npc X Seconds After Death")]
            public float RespawnRate { get; set; }

            [JsonProperty(PropertyName = "Skip Treasure Loot And Use Loot In Base Only")]
            public bool SkipTreasureLoot { get; set; }

            public static BuildingOptions Clone(BuildingOptions options)
            {
                return options.MemberwiseClone() as BuildingOptions;
            }
        }

        public class RaidableBaseSettingsSchedule
        {
            [JsonProperty(PropertyName = "Enabled")]
            public bool Enabled { get; set; }
            
            [JsonProperty(PropertyName = "Convert PVE To PVP")]
            public bool ConvertPVE { get; set; }

            [JsonProperty(PropertyName = "Every Min Seconds")]
            public float IntervalMin { get; set; } = 3600f;

            [JsonProperty(PropertyName = "Every Max Seconds")]
            public float IntervalMax { get; set; } = 7200f;

            [JsonProperty(PropertyName = "Max Scheduled Events")]
            public int Max { get; set; } = 1;

            [JsonProperty(PropertyName = "Minimum Required Players Online")]
            public int PlayerLimit { get; set; } = 1;

            [JsonProperty(PropertyName = "Spawns Database File (Optional)")]
            public string SpawnsFile { get; set; } = "none";
        }

        public class RaidableBaseSettingsMaintainer
        {
            [JsonProperty(PropertyName = "Always Maintain Max Events")]
            public bool Enabled { get; set; }

            [JsonProperty(PropertyName = "Convert PVE To PVP")]
            public bool ConvertPVE { get; set; }

            [JsonProperty(PropertyName = "Minimum Required Players Online")]
            public int PlayerLimit { get; set; } = 1;

            [JsonProperty(PropertyName = "Max Maintained Events")]
            public int Max { get; set; } = 1;

            [JsonProperty(PropertyName = "Spawns Database File (Optional)")]
            public string SpawnsFile { get; set; } = "none";
        }

        public class RaidableBaseSettingsBuyable
        {
            [JsonProperty(PropertyName = "Allow Players To Buy PVP Raids")]
            public bool BuyPVP { get; set; }

            [JsonProperty(PropertyName = "Cooldown Between Purchases")]
            public float Cooldown { get; set; } = 60f;

            [JsonProperty(PropertyName = "Convert PVE To PVP")]
            public bool ConvertPVE { get; set; }

            [JsonProperty(PropertyName = "Distance To Spawn Bought Raids From Player")]
            public float DistanceToSpawnFrom { get; set; } = 150f;

            [JsonProperty(PropertyName = "Lock Raid To Buyer And Friends")]
            public bool UsePayLock { get; set; } = true;

            [JsonProperty(PropertyName = "Max Buyable Events")]
            public int Max { get; set; } = 1;

            [JsonProperty(PropertyName = "Reset Purchased Owner After X Minutes Offline")]
            public float ResetDuration { get; set; } = 10f;

            [JsonProperty(PropertyName = "Spawns Database File (Optional)")]
            public string SpawnsFile { get; set; } = "none";
        }

        public class RaidableBaseSettingsManual
        {
            [JsonProperty(PropertyName = "Convert PVE To PVP")]
            public bool ConvertPVE { get; set; }

            [JsonProperty(PropertyName = "Max Manual Events")]
            public int Max { get; set; } = 1;

            [JsonProperty(PropertyName = "Spawns Database File (Optional)")]
            public string SpawnsFile { get; set; } = "none";
        }

        public class RaidableBaseSettings
        {
            [JsonProperty(PropertyName = "Buildings", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, BuildingOptions> Buildings { get; set; } = DefaultBuildingOptions;
        }

        public class RaidableBaseWallOptions
        {
            [JsonProperty(PropertyName = "Enabled")]
            public bool Enabled { get; set; } = true;

            [JsonProperty(PropertyName = "Use Stone Walls")]
            public bool Stone { get; set; } = true;

            [JsonProperty(PropertyName = "Use Least Amount Of Walls")]
            public bool LeastAmount { get; set; } = true;

            [JsonProperty(PropertyName = "Use UFO Walls")]
            public bool UseUFOWalls { get; set; }

            [JsonProperty(PropertyName = "Extra Stacks")]
            public int Stacks { get; set; } = 1;
        }

        public class RaidableBaseEconomicsOptions
        {
            [JsonProperty(PropertyName = "Easy")]
            public double Easy { get; set; }

            [JsonProperty(PropertyName = "Medium")]
            public double Medium { get; set; }

            [JsonProperty(PropertyName = "Hard")]
            public double Hard { get; set; }

            [JsonIgnore]
            public bool Any
            {
                get
                {
                    return Easy > 0 || Medium > 0 || Hard > 0;
                }
            }
        }

        public class RaidableBaseServerRewardsOptions
        {
            [JsonProperty(PropertyName = "Easy")]
            public int Easy { get; set; }

            [JsonProperty(PropertyName = "Medium")]
            public int Medium { get; set; }

            [JsonProperty(PropertyName = "Hard")]
            public int Hard { get; set; }

            [JsonIgnore]
            public bool Any
            {
                get
                {
                    return Easy > 0 || Medium > 0 || Hard > 0;
                }
            }
        }

        public class RankedLadderSettings
        {
            [JsonProperty(PropertyName = "Award Top X Players On Wipe")]
            public int Amount { get; set; } = 3;

            [JsonProperty(PropertyName = "Enabled")]
            public bool Enabled { get; set; } = true;
        }

        public class RewardSettings
        {
            [JsonProperty(PropertyName = "Economics Money")]
            public double Money { get; set; }

            [JsonProperty(PropertyName = "ServerRewards Points")]
            public int Points { get; set; }
        }

        public class SkinSettings
        {
            [JsonProperty(PropertyName = "Include Workshop Skins")]
            public bool RandomWorkshopSkins { get; set; } = true;

            [JsonProperty(PropertyName = "Preset Skin")]
            public ulong PresetSkin { get; set; }

            [JsonProperty(PropertyName = "Use Random Skin")]
            public bool RandomSkins { get; set; } = true;
        }

        public class TreasureItem
        {
            public string shortname { get; set; }
            public int amount { get; set; }
            public ulong skin { get; set; }
            public int amountMin { get; set; }
        }

        public class TreasureSettings
        {
            [JsonProperty(PropertyName = "Use Random Skins")]
            public bool RandomSkins { get; set; }

            [JsonProperty(PropertyName = "Include Workshop Skins")]
            public bool RandomWorkshopSkins { get; set; }

            [JsonProperty(PropertyName = "Use Day Of Week Loot")]
            public bool UseDOWL { get; set; }

            [JsonProperty(PropertyName = "Percent Minimum Loss")]
            public decimal PercentLoss { get; set; }

            [JsonProperty(PropertyName = "Percent Increase When Using Day Of Week Loot")]
            public bool Increased { get; set; }

            [JsonProperty(PropertyName = "Day Of Week Loot Monday", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<TreasureItem> DOWL_Monday { get; set; } = new List<TreasureItem>();

            [JsonProperty(PropertyName = "Day Of Week Loot Tuesday", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<TreasureItem> DOWL_Tuesday { get; set; } = new List<TreasureItem>();

            [JsonProperty(PropertyName = "Day Of Week Loot Wednesday", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<TreasureItem> DOWL_Wednesday { get; set; } = new List<TreasureItem>();

            [JsonProperty(PropertyName = "Day Of Week Loot Thursday", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<TreasureItem> DOWL_Thursday { get; set; } = new List<TreasureItem>();

            [JsonProperty(PropertyName = "Day Of Week Loot Friday", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<TreasureItem> DOWL_Friday { get; set; } = new List<TreasureItem>();

            [JsonProperty(PropertyName = "Day Of Week Loot Saturday", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<TreasureItem> DOWL_Saturday { get; set; } = new List<TreasureItem>();

            [JsonProperty(PropertyName = "Day Of Week Loot Sunday", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<TreasureItem> DOWL_Sunday { get; set; } = new List<TreasureItem>();

            [JsonProperty(PropertyName = "Percent Increase On Monday")]
            public decimal PercentIncreaseOnMonday { get; set; }

            [JsonProperty(PropertyName = "Percent Increase On Tuesday")]
            public decimal PercentIncreaseOnTuesday { get; set; }

            [JsonProperty(PropertyName = "Percent Increase On Wednesday")]
            public decimal PercentIncreaseOnWednesday { get; set; }

            [JsonProperty(PropertyName = "Percent Increase On Thursday")]
            public decimal PercentIncreaseOnThursday { get; set; }

            [JsonProperty(PropertyName = "Percent Increase On Friday")]
            public decimal PercentIncreaseOnFriday { get; set; }

            [JsonProperty(PropertyName = "Percent Increase On Saturday")]
            public decimal PercentIncreaseOnSaturday { get; set; }

            [JsonProperty(PropertyName = "Percent Increase On Sunday")]
            public decimal PercentIncreaseOnSunday { get; set; }

            [JsonProperty(PropertyName = "Loot (Easy Difficulty)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<TreasureItem> LootEasy { get; set; } = new List<TreasureItem>();

            [JsonProperty(PropertyName = "Loot (Medium Difficulty)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<TreasureItem> LootMedium { get; set; } = new List<TreasureItem>();

            [JsonProperty(PropertyName = "Loot (Hard Difficulty)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<TreasureItem> LootHard { get; set; } = new List<TreasureItem>();

            [JsonProperty(PropertyName = "Loot", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<TreasureItem> Loot { get; set; } = DefaultLoot;
        }

        public class TruePVESettings
        {
            [JsonProperty(PropertyName = "Allow PVP Server-Wide During Events")]
            public bool ServerWidePVP { get; set; }
        }

        public class WeaponTypeStateSettings
        {
            [JsonProperty(PropertyName = "AutoTurret")]
            public bool AutoTurret { get; set; } = true;

            [JsonProperty(PropertyName = "FlameTurret")]
            public bool FlameTurret { get; set; } = true;

            [JsonProperty(PropertyName = "FogMachine")]
            public bool FogMachine { get; set; } = true;

            [JsonProperty(PropertyName = "GunTrap")]
            public bool GunTrap { get; set; } = true;

            [JsonProperty(PropertyName = "SamSite")]
            public bool SamSite { get; set; } = true;
        }

        public class WeaponTypeAmountSettings
        {
            [JsonProperty(PropertyName = "AutoTurret")]
            public int AutoTurret { get; set; } = 256;

            [JsonProperty(PropertyName = "FlameTurret")]
            public int FlameTurret { get; set; } = 256;

            [JsonProperty(PropertyName = "FogMachine")]
            public int FogMachine { get; set; } = 5;

            [JsonProperty(PropertyName = "GunTrap")]
            public int GunTrap { get; set; } = 128;

            [JsonProperty(PropertyName = "SamSite")]
            public int SamSite { get; set; } = 24;
        }

        public class WeaponSettings
        {
            [JsonProperty(PropertyName = "Infinite Ammo")]
            public WeaponTypeStateSettings InfiniteAmmo { get; set; } = new WeaponTypeStateSettings();

            [JsonProperty(PropertyName = "Ammo")]
            public WeaponTypeAmountSettings Ammo { get; set; } = new WeaponTypeAmountSettings();

            [JsonProperty(PropertyName = "SamSite Repairs Every X Minutes (0.0 = disabled)")]
            public float SamSiteRepair { get; set; } = 5f;
        }

        public class Configuration
        {
            [JsonProperty(PropertyName = "Settings")]
            public PluginSettings Settings = new PluginSettings();

            [JsonProperty(PropertyName = "Event Messages")]
            public EventMessageSettings EventMessages = new EventMessageSettings();

            [JsonProperty(PropertyName = "GUIAnnouncements")]
            public GUIAnnouncementSettings GUIAnnouncement = new GUIAnnouncementSettings();

            [JsonProperty(PropertyName = "Lusty Map")]
            public LustyMapSettings LustyMap = new LustyMapSettings();

            [JsonProperty(PropertyName = "Raidable Bases")]
            public RaidableBaseSettings RaidableBases = new RaidableBaseSettings();

            [JsonProperty(PropertyName = "Ranked Ladder")]
            public RankedLadderSettings RankedLadder = new RankedLadderSettings();

            [JsonProperty(PropertyName = "Skins")]
            public SkinSettings Skins = new SkinSettings();

            [JsonProperty(PropertyName = "Treasure")]
            public TreasureSettings Treasure = new TreasureSettings();

            [JsonProperty(PropertyName = "TruePVE")]
            public TruePVESettings TruePVE = new TruePVESettings();

            [JsonProperty(PropertyName = "Weapons")]
            public WeaponSettings Weapons = new WeaponSettings();
        }

        private bool configLoaded = false;

        protected override void LoadConfig()
        {
            base.LoadConfig();

            try
            {
                _config = Config.ReadObject<Configuration>();
            }
            catch (JsonException ex)
            {
                Puts(ex.Message);
                PrintError("Your configuration file contains a json error, shown above. Please fix this.");
                return;
            }
            catch (Exception ex)
            {
                Puts(ex.Message);
                LoadDefaultConfig();
            }

            if (_config == null)
            {
                LoadDefaultConfig();
            }
            
            configLoaded = true;

            if (string.IsNullOrEmpty(_config.LustyMap.IconFile) || string.IsNullOrEmpty(_config.LustyMap.IconName))
            {
                _config.LustyMap.Enabled = false;
            }

            if (_config.GUIAnnouncement.TintColor.ToLower() == "black")
            {
                _config.GUIAnnouncement.TintColor = "grey";
            }

            SaveConfig();
        }

        private readonly string rankLadderPermission = "raidablebases.th";
        private readonly string rankLadderGroup = "raidhunter";
        private readonly string adminPermission = "raidablebases.allow";

        public static List<TreasureItem> ChestLoot
        {
            get
            {
                if (_config.Treasure.UseDOWL)
                {
                    switch (DateTime.Now.DayOfWeek)
                    {
                        case DayOfWeek.Monday:
                            {
                                if (_config.Treasure.DOWL_Monday.Count > 0)
                                {
                                    return new List<TreasureItem>(_config.Treasure.DOWL_Monday);
                                }
                                break;
                            }
                        case DayOfWeek.Tuesday:
                            {
                                if (_config.Treasure.DOWL_Tuesday.Count > 0)
                                {
                                    return new List<TreasureItem>(_config.Treasure.DOWL_Tuesday);
                                }
                                break;
                            }
                        case DayOfWeek.Wednesday:
                            {
                                if (_config.Treasure.DOWL_Wednesday.Count > 0)
                                {
                                    return new List<TreasureItem>(_config.Treasure.DOWL_Wednesday);
                                }
                                break;
                            }
                        case DayOfWeek.Thursday:
                            {
                                if (_config.Treasure.DOWL_Thursday.Count > 0)
                                {
                                    return new List<TreasureItem>(_config.Treasure.DOWL_Thursday);
                                }
                                break;
                            }
                        case DayOfWeek.Friday:
                            {
                                if (_config.Treasure.DOWL_Friday.Count > 0)
                                {
                                    return new List<TreasureItem>(_config.Treasure.DOWL_Friday);
                                }
                                break;
                            }
                        case DayOfWeek.Saturday:
                            {
                                if (_config.Treasure.DOWL_Saturday.Count > 0)
                                {
                                    return new List<TreasureItem>(_config.Treasure.DOWL_Saturday);
                                }
                                break;
                            }
                        case DayOfWeek.Sunday:
                            {
                                if (_config.Treasure.DOWL_Sunday.Count > 0)
                                {
                                    return new List<TreasureItem>(_config.Treasure.DOWL_Sunday);
                                }
                                break;
                            }
                    }
                }

                return new List<TreasureItem>(_config.Treasure.Loot);
            }
        }

        protected override void SaveConfig() => Config.WriteObject(_config);

        protected override void LoadDefaultConfig()
        {
            _config = new Configuration();
            Puts("Loaded default configuration file");
        }

        #endregion
    }
}