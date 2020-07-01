using System.Collections.Generic;
using System;
using Oxide.Core;
using Oxide.Core.Plugins;
using System.Linq;
using Oxide.Core.Configuration;
using UnityEngine;
using Newtonsoft.Json;
using Oxide.Core.Libraries.Covalence;
using Oxide.Game.Rust.Cui;
using System.Collections;
using System.Text;
using Facepunch;
using Oxide.Game.Rust;
using System.ComponentModel;
using System.Numerics;
using ConVar;

namespace Oxide.Plugins
{
    [Info("BountyHunter", "Oegge & ashbo", "1.1.1")]
    [Description("provides a bounty system for your server. now with payment on skull delivery")]
    class BountyHunter : RustPlugin
    {
        [PluginReference] private Plugin ServerRewards, Friends, Clans, EventManager;

        #region Config
        private class ConfigFile
        {
            //List of IDs of all items that may be chosen as bounty stations. (You might want to choose stations, players can put items into. Just sayin')
            public Dictionary<string, string> allowedStationItems;
            public static ConfigFile DefaultConfig()
            {
                Dictionary<string, string> items = makeDefaultStations();
                return new ConfigFile
                {
                    allowedStationItems = items
                };
            }

            //generates a list of some basic items that seem qualified to be chosen as bounty stations
            private static Dictionary<string,string>  makeDefaultStations() {
                Dictionary<string, string> stations = new Dictionary<string, string>();
                stations.Add("dropbox.deployed","dropbox") ;
                stations.Add("box.wooden.large","large woodbox");
                stations.Add("mailbox.deployed","mailbox");
                stations.Add("woodbox_deployed","small woodbox");
                stations.Add("wall.frame.shopfront.metal","metal shopfront");
                stations.Add("fridge.deployed","fridge");
                stations.Add("coffinstorage","coffin");
                return stations;
            }
        }

        protected override void LoadDefaultConfig()
        {
            PrintWarning("Generating default configuration file...");
            config = ConfigFile.DefaultConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            config = Config.ReadObject<ConfigFile>();
        }

        protected override void SaveConfig() => Config.WriteObject(config);

        #endregion

        #region Data

        #region Private Classes
        private class Mission
        {
            public ulong victim;
            public ulong client;
            public int reward;

            public Mission(ulong victim, ulong client, int reward)
            {
                this.victim = victim;
                this.client = client;
                this.reward = reward;
            }
        }
        private class BountyData
        {
            public Dictionary<string,string> stations;
            public HashSet<Mission> missions;
            public Dictionary<ulong, HashSet<Mission>> deadpool;
            public Dictionary<ulong, HashSet<ulong>> hunts;

            public BountyData() {
                stations = new Dictionary<string, string>();
                missions = new HashSet<Mission>();
                deadpool = new Dictionary<ulong, HashSet<Mission>>();
                hunts = new Dictionary<ulong, HashSet<ulong>>();
            }


        }
        #endregion

        private DynamicConfigFile bountyData;
        private ConfigFile config;
        private BountyHunter _instance;

        private static string stationPerm = "BountyHunter.BountyStation";
        private static string resetPerm = "BountyHunter.Reset";

        private BountyData data;

        //saves all relevant data in the bountyData file
        private void SaveData()
        {
            SendReply(Player.Find("Oegge"), "deadpool contains " + data.deadpool.Count + " wanted players");
            Dictionary<ulong, BountyData> save = new Dictionary<ulong, BountyData>();
            save.Add(42, data);

            Interface.Oxide.DataFileSystem.WriteObject("bountyData", save);

        }

        //loads all relevant data from the bountyData file. If no such file exists, a new one will be created with default values.
        private void LoadData() {
            try
            {
                bountyData = Interface.Oxide.DataFileSystem.GetDatafile("bountyData");
                Dictionary<ulong, BountyData> loaded = bountyData.ReadObject<Dictionary<ulong, BountyData>>();
                data = loaded[42];
            }
            catch
            {
                PrintWarning("No bounty data found! Creating a new data file");
                data = new BountyData();
                SaveData();
            }
        }


        #endregion

        #region Init

        private void Init()
        {
            _instance = this;
            permission.RegisterPermission(stationPerm, this);
            permission.RegisterPermission(resetPerm, this);
            LoadConfig();
            LoadData();
            if (ServerRewards == null)
                PrintWarning("ServerRewards not loaded. You will not be able to place, remove or fulfill any bounty.");
        }

        #endregion

        #region Oxide hooks

        //removes a bounty station from the list if it was destroyed
        void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (data.stations.Remove(entity.ToString()))
                SendReply(info.InitiatorPlayer, "You've destroyed a bounty station dude...");
        }

        //reacts if a "skull of xy" is put into a bounty station
        ItemContainer.CanAcceptResult? CanAcceptItem(ItemContainer container, Item item, int targetPos)
        {

            if (item == null || container == null)
                return null;

            if (!(item.info.shortname.Equals("skull.human")))
                return null;

            if (container.entityOwner == null)
                return null;

            if (!data.stations.ContainsKey(container.entityOwner.ToString()))
                return null;
            if (item.name == null) {
                return null;
            }
            String victimName = item.name.Remove(0, 10);
            victimName = victimName.Remove(victimName.Length - 1);

            BasePlayer victim = BasePlayer.Find(victimName);

            if (victim == null)
            {
                return null;
            }

            BasePlayer hunter = item.GetOwnerPlayer();

            //item.Remove();

            if (!data.deadpool.ContainsKey(victim.userID)) {
                SendReply(hunter, "There are no bounties set on " + victimName);
                SendReply(hunter, "But thank you for this nice skull of " + victimName);
                return null;
            }

            bool legitHunt = data.hunts.ContainsKey(hunter.userID) && data.hunts[hunter.userID].Contains(victim.userID);
            if (!legitHunt) {
                SendReply(hunter, "You have not accepted a bounty hunt for " + victim.displayName);
                SendReply(hunter, "But thank you for this nice skull of " + victim.displayName);
                return null;
            }

            hunted(hunter, victim);
            return null;
        }

        //ensures that bounty hunters can always damage their victims and vice versa.
        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || info == null)
                return null;

            BasePlayer victim = entity.ToPlayer();
            BasePlayer attacker = info.InitiatorPlayer;

            if (victim != null && attacker != null && (attacker.userID >= 76560000000000000L) && (victim.userID >= 76560000000000000L)) {

                bool legitHunt = data.hunts.ContainsKey(attacker.userID) && data.hunts[attacker.userID].Contains(victim.userID);
                if (!legitHunt)
                    return null;

                if ((bool)EventManager.Call("isPlaying", victim) || (bool)EventManager.Call("isPlaying", attacker))
                    return null;


                float health = victim._health;
                float dmg = 0;
                dmg = info?.damageTypes?.Total() ?? 0f;
                victim._health -= dmg;
                if (victim._health <= 0) {
                    victim.Die();
                    if (data.stations.Count == 0) {
                        hunted(attacker, victim);
                    }
                }
                return true;

            }
            return null;
        }

        #endregion

        #region ConsoleCommands

        // place a reward of "reward" Gold on the death of the given "player"
        // [bounty.place "player" "reward"]
        [ConsoleCommand("bounty.place")]
        private void cmdPlaceBounty(ConsoleSystem.Arg arg) {
            BasePlayer player = arg.Connection.player as BasePlayer;
            string[] args = arg.Args;
            if (args.Length != 2)
            {
                SendReply(player, "Invalid Command");
                return;
            }

            int reward = 0;
            if (!Int32.TryParse(args[1], out reward))
            {
                SendReply(player, "Invalid reward");
                return;
            }

            BasePlayer victim = BasePlayer.Find(args[0]);
            if (victim == null)
            {
                SendReply(player, "Invalid player name");
                return;
            }

            if (!canDoBountyThings(player, victim))
            {
                SendReply(player, "You would stab your own ally's back?! :O");
                SendReply(player, "What a dick move...");
                SendReply(player, "but ok. Your choice.");
                SendReply(player, "*whispers* what an asshole...");
            }

            if (!takeGold(player, reward))
            {
                SendReply(player, "You don't have enough gold to pay for this mission");
                return;
            }
            if (reward <= 0)
            {
                SendReply(player, "nice try");
                if (takeGold(player, 1))
                    SendReply(player, "1 Gold has been taken from your account for stealing Server attention.");
                return;
            }
            Mission mission = new Mission(victim.userID, player.userID, reward);
            data.missions.Add(mission);
            HashSet<Mission> all = new HashSet<Mission>();
            if (data.deadpool.Keys.Contains(victim.userID))
            {
                all = data.deadpool[victim.userID];
            }
            all.Add(mission);
            data.deadpool.Remove(victim.userID);
            data.deadpool.Add(victim.userID, all);
            SendReply(player, "Your have succesfulls placed a bounty of " + reward + " gold on " + victim._name);

            SaveData();
        }


        // removes a bounty you set onto another player and returns your gold
        //[bounty.remove "player"]
        [ConsoleCommand("bounty.remove")]
        private void cmdRemoveBounty(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Connection.player as BasePlayer;
            string[] args = arg.Args;
            if (args.Length != 1)
            {
                SendReply(player, "Invalid Command");
                return;
            }

            BasePlayer victim = BasePlayer.Find(args[0]);
            if (victim == null)
            {
                SendReply(player, "Invalid player name");
                return;
            }

            if (!data.deadpool.Keys.Contains(victim.userID))
            {
                SendReply(player, "There is no bounty on " + victim._name);
                return;
            }

            bool ok = false;
            HashSet<Mission> removeM = new HashSet<Mission>();
            foreach (Mission mission in data.missions)
            {
                if (mission.client == player.userID && mission.victim == victim.userID)
                {
                    ok = true;
                    removeM.Add(mission);

                    data.deadpool[victim.userID].Remove(mission);
                    if (data.deadpool[victim.userID].Count == 0)
                    {
                        data.deadpool.Remove(victim.userID);
                        HashSet<ulong> removeH = new HashSet<ulong>();
                        foreach (ulong hunterID in data.hunts.Keys)
                        {
                            BasePlayer hunter = Player.FindById(hunterID);
                            data.hunts[hunter.userID].Remove(victim.userID);
                            if (data.hunts[hunter.userID].Count == 0)
                            {
                                removeH.Add(hunter.userID);
                            }
                        }
                        foreach (ulong hunter in removeH)
                        {
                            data.hunts.Remove(hunter);
                        }
                    }


                }
            }
            foreach (Mission mission in removeM)
            {
                data.missions.Remove(mission);
            }
            if (!ok)
            {
                SendReply(player, "There is no active bounty placed on " + victim._name + " by you");
            }
            else
            {
                SendReply(player, "All bounties you had set on " + victim._name + " have been removed succesfully");
            }
            SaveData();
        }

        // accepts all bounty requests for player "player"
        // [bounty.accept "player"]
        [ConsoleCommand("bounty.accept")]
        private void cmdAcceptBounty(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Connection.player as BasePlayer;
            string[] args = arg.Args;
            if (args.Length != 1)
            {
                SendReply(player, "Invalid Command");
                return;
            }

            BasePlayer victim = BasePlayer.Find(args[0]);
            if (victim == null)
            {
                SendReply(player, "Invalid player name");
                return;
            }

            if (!data.deadpool.ContainsKey(victim.userID))
            {
                SendReply(player, "There are no bounties set on " + victim.displayName);
                return;
            }

            /*
             * foreach (Mission mission in data.deadpool[victim.userID]) {
                if (mission.client == player.userID) { 
                    SendReply(player, "You can't accept a bounty hunt you're set yourself");
                    return;
                }
            }
            */

            if (!canDoBountyThings(player, victim))
            {
                SendReply(player, "You would stab your own ally's back?! :O");
                SendReply(player, "I'm sorry, we don't do that here.");
                // return;
            }

            if (!data.hunts.Keys.Contains(player.userID))
            {
                data.hunts.Add(player.userID, new HashSet<ulong>());
            }
            if (data.hunts[player.userID].Contains(victim.userID))
            {
                SendReply(player, "You already hunt this player");
                return;
            }
            data.hunts[player.userID].Add(victim.userID);
            SendReply(player, "You are on the hunt for " + victim.displayName);
            SendReply(player, "You may now attack " + victim.displayName + " on the whole map. Be aware that " + victim.displayName + " will be allowed to defend");
            SaveData();
        }

        //Sends a list of all active bounty stations with their grid coords
        //[bounty.station.list]
        [ConsoleCommand("bounty.station.list")]
        private void cmdListStation(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Connection.player as BasePlayer;
            string[] args = arg.Args;
            String list = listStations();
            SendReply(player, list);
            if (list.Equals(""))
                SendReply(player, "There are no bounty stations set. Rewards will be claimed automatically on a hunted victim's death.");
        }

        //Sends a list of all players with bounties on them and the total reward for their death
        //[bounty.list]
        [ConsoleCommand("bounty.list")]
        private void cmdListBounty(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Connection.player as BasePlayer;
            string[] args = arg.Args;
            String list = listDeadpool();
            SendReply(player, list);
            if (list.Equals(""))
                SendReply(player, "There are no active bounties at the moment");
        }

        #endregion

        #region ChatCommands


        // sets the container in you line of sight as a bounty station.
        //Players will receive their reward by placing the skull of the wanted player in one of your set Stations.
        //If no station is set, players receive their reward on death of the victim.
        // [bounty.station.set]
        [ChatCommand("bounty.station.set")]
        private void cmdBountyStation(BasePlayer player, String command, string[] args)
        {
            if (args.Length != 0)
            {
                SendReply(player, "Invalid Command");
                return;
            }

            if (!permission.UserHasPermission(player.UserIDString, stationPerm)) {
                SendReply(player, "You don't have permission to set a bounty station");
                return;
            }
            BaseEntity entity = GetTargetEntity(player);
            if (entity == null)
            {
                SendReply(player, "There is no Item in you line of sight");
                return;
            }
//sysout            
            SendReply(player, "you see " + entity.ShortPrefabName);
            
            if (!config.allowedStationItems.ContainsKey(entity.ShortPrefabName)) { 
                SendReply(player, "The item in your line of sight may not be chosen as Bounty station");
                return;
            }

            if (!data.stations.ContainsKey(entity.ToString()))
            {
                data.stations.Add(entity.ToString(),getEntityPos(entity));
                SendReply(player, "you have succesfully set this " + entity.ShortPrefabName + " as a bounty station");
                SaveData();
                return;
            }

            SendReply(player, "this item is a bounty station already, duh!");
            
        }

        //removes the container in your line of sight from the list of bounty stations.
        //If no more station is set, players receive their reward on death of the victim.
        // [bounty.station.remove]
        [ChatCommand("bounty.station.remove")]
        private void cmdRemoveStation(BasePlayer player, String command, string[] args) {
            if (args.Length != 0)
            {
                SendReply(player, "Invalid Command");
                return;
            }

            if (!permission.UserHasPermission(player.UserIDString, stationPerm))
            {
                SendReply(player, "You don't have permission to remove a bounty station");
                return;
            }
            BaseEntity entity = GetTargetEntity(player);
            if (entity == null)
            {
                SendReply(player, "There is no Item in you line of sight");
                return;
            }
            //sysout            
            SendReply(player, "you try to remove " + entity.ShortPrefabName);

            if (data.stations.ContainsKey(entity.ToString()))
            {
                data.stations.Remove(entity.ToString());
                SendReply(player, "you have successfully removed this " + entity.ShortPrefabName + " from the list of bounty stations");
                SaveData();
                return;
            }

            SendReply(player, "this item is was not a bounty station, duh!");
        }



        //deletes all bounties and stations.
        //returns the already paid gold for active bounties to the respective client
        [ChatCommand("bounty.reset")]
        private void cmdReset(BasePlayer player, String command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, resetPerm))
            {
                SendReply(player, "You don't have permission to reset the bounty data");
                return;
            }

            foreach (Mission mission in data.missions)
            {
                giveGold(Player.FindById(mission.client), mission.reward);
            }
            data.deadpool = new Dictionary<ulong, HashSet<Mission>>();
            data.missions = new HashSet<Mission>();
            data.hunts = new Dictionary<ulong, HashSet<ulong>>();
            data.stations = new Dictionary<string, string>();
            SaveData();
            SendReply(player, "You have succesfully reset the Bounty Plugin to default state");
        }

        #endregion

        #region Helper

        //rewards the succesful hunter and deletes all mission related data.
        private void hunted(BasePlayer hunter, BasePlayer victim) {
            int gold = getTotalReward(victim);
            giveGold(hunter, gold);
            SendReply(hunter, "You have succesfully hunted down " + victim.displayName + ". Enjoy your reward: "+gold+" gold!");

            HashSet<Mission> ended = data.deadpool[victim.userID];
            foreach (Mission mission in ended)
            {
                data.missions.Remove(mission);
            }
            data.deadpool.Remove(victim.userID);
            HashSet<ulong> remove = new HashSet<ulong>();
            foreach (ulong playerID in data.hunts.Keys)
            {
                
                data.hunts[playerID].Remove(victim.userID);
                if (data.hunts[playerID].Count == 0)
                    remove.Add(playerID);
            }
            foreach (ulong id in remove)
            {
                data.hunts.Remove(id);
            }
            SaveData();
        }

        // takes gold from the player if he has enough gold. Returns true on success.
        private bool takeGold(BasePlayer player, int gold) {
            int credit = (int) (ServerRewards.Call("CheckPoints", player.userID));
            if (credit < gold)
                return false;
            ServerRewards.Call("TakePoints", player, gold);
            return true;
        }

        //gives the player gold
        private void giveGold(BasePlayer player, int gold) {
            ServerRewards.Call("AddPoints", player, gold);
        }

        //generates a String, listing all wanted players with the total gold on their head
        private String listDeadpool() {
            String list = "";
            foreach (ulong playerID in data.deadpool.Keys) {
                list += (Player.FindById(playerID)._name + ": " + getTotalReward(Player.FindById(playerID)) + " gold" + "\n");
            }
            return list;
        }

        #region ListStations

        private string listStations() {
            string list = "";
            foreach (KeyValuePair<string,string> entry in data.stations)
            {
                list += niceName(entry.Key) + ": " + entry.Value + "\n";
            }
            return list;
        }

        private string niceName(String entityToString) {
            string[] subs = entityToString.Split('[');

            return config.allowedStationItems[subs[0]];
        }

        private String getEntityPos(BaseEntity entity) {
            return getGrid(entity.transform.position);
        }

        #region GridMaker

        string getGrid(UnityEngine.Vector3 pos)
        {
            var x = (pos.x + (ConVar.Server.worldsize / 2)) / 146.3f;
            int NumberOfRows = (int)(Mathf.Floor(ConVar.Server.worldsize / 146.3f) - 1);
            var z = NumberOfRows - Mathf.Floor((pos.z + (ConVar.Server.worldsize / 2)) / 146.3f);

            String letter = getXLetter(x);

            return letter + z;
        }

        private String getXLetter(float num)
        {
            String prefix = "";
            if (num < 0)
            {
                num = num * (-1);
                prefix = "-";
            }
            int numI = (int)num;
            int mod = numI % 26;
            int remain = numI - mod;
            int rest = remain / 26;

            char GridLetter = (char)(((int)'A') + mod);
            String result = GridLetter + "";


            if (rest != 0)
            {
                result = XHelper(rest, result);
            }
            return prefix + result;
        }

        private String XHelper(float num, String current)
        {

            String prefix = "";
            if (num < 0)
            {
                num = num * (-1);
                prefix = "-";
            }
            int numI = (int)num;
            int mod = numI % 26;
            int remain = numI - mod;
            int rest = remain / 26;

            char GridLetter = (char)(((int)'A') + mod - 1);
            String result = GridLetter + current;


            if (rest != 0)
            {
                result = XHelper(rest, result);
            }
            return prefix + result;
        }


        #endregion

        #endregion

        // counts how much gold is placed on the given player's head in total
        private int getTotalReward(BasePlayer player)
        {
            int reward = 0;
            HashSet<Mission> missions;
            if (data.deadpool.TryGetValue(player.userID, out missions))
            {
                foreach (Mission mission in missions)
                {
                    reward += mission.reward;
                }
            }
            return reward;
        }

        //decided whether the player is allowed to accept a bounty on the victim
        private bool canDoBountyThings(BasePlayer player, BasePlayer victim) {
            if (Friends != null)
            {
                bool friends = (bool)Friends.Call("AreFriends", player.userID, victim.userID);
                if (friends)
                    return false;
            }
            if (player.Team != null&& victim.Team!=null)
            {
                if (player.Team.Equals(victim.Team))
                    return false;
            }
            if (Clans != null)
            {
                string clanP = (string)Clans.Call("GetClanOf", player);
                string clanV = (string)Clans.Call("GetClanOf", victim);
                if (clanP != null && clanV != null)
                {
                    if (clanP.Equals(clanV))
                        return false;
                }
            }
            return true;
        }

        //returns the entity in your line of sight
        private BaseEntity GetTargetEntity(BasePlayer player)
        {
            BaseEntity targetEntity;
            RaycastHit hit;

            bool flag = UnityEngine.Physics.Raycast(player.eyes.HeadRay(), out hit, 50);
            targetEntity = flag ? hit.GetEntity() : null;
            return targetEntity;
        }

        #region UIHelper

        private HashSet<Mission> myBounties(ulong myId) {
            HashSet<Mission> missions = new HashSet<Mission>();
            foreach (Mission mission in data.missions)
            {
                if (mission.client == myId)
                    missions.Add(mission);
            }
            return missions;
        }

        #endregion

        #endregion

    }
}
