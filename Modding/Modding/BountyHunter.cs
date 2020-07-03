﻿using System.Collections.Generic;
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
    [Info("BountyHunter", "Oegge & ashbo", "1.3.0")]
    [Description("provides a bounty system for your server. now with payment on skull delivery")]
    class BountyHunter : CovalencePlugin
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
                players.FindPlayer(info.InitiatorPlayer.UserIDString).Reply("You've destroyed a bounty station dude...");
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
                players.FindPlayer(hunter.UserIDString).Reply("There are no bounties set on " + victimName);
                players.FindPlayer(hunter.UserIDString).Reply("But thank you for this nice skull of " + victimName);
                return null;
            }

            bool legitHunt = data.hunts.ContainsKey(hunter.userID) && data.hunts[hunter.userID].Contains(victim.userID);
            if (!legitHunt) {
                players.FindPlayer(hunter.UserIDString).Reply("You have not accepted a bounty hunt for " + victim.displayName);
                players.FindPlayer(hunter.UserIDString).Reply("But thank you for this nice skull of " + victim.displayName);
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

        #region RawGUICommands

        // place a reward of "reward" Gold on the death of the given "player"
        // [bounty.place "player" "reward"]
        // former [ConsoleCommand("bounty.place")]
        private void cmdPlaceBounty(IPlayer iclient, IPlayer ivictim, int reward) {

            
            BasePlayer player = BasePlayer.FindByID(ulong.Parse(iclient.Id));
            IPlayer victim = ivictim;
            if (!canDoBountyThings(player, victim))
            {

                players.FindPlayer(player.UserIDString).Reply("You would stab your own ally's back?! :O");
                players.FindPlayer(player.UserIDString).Reply("What a dick move...");
                players.FindPlayer(player.UserIDString).Reply("but ok. Your choice.");
                players.FindPlayer(player.UserIDString).Reply("*whispers* what an asshole...");
            }

            if (!takeGold(player, reward))
            {
                players.FindPlayer(player.UserIDString).Reply("You don't have enough gold to pay for this mission");
                return;
            }
            if (reward <= 0)
            {
                players.FindPlayer(player.UserIDString).Reply("nice try");
                if (takeGold(player, 1))
                    players.FindPlayer(player.UserIDString).Reply("1 Gold has been taken from your account for stealing Server attention.");
                return;
            }
            Mission mission = new Mission(ulong.Parse(victim.Id), player.userID, reward);
            data.missions.Add(mission);
            HashSet<Mission> all = new HashSet<Mission>();
            if (data.deadpool.Keys.Contains(ulong.Parse(victim.Id)))
            {
                all = data.deadpool[ulong.Parse(victim.Id)];
            }
            all.Add(mission);
            data.deadpool.Remove(ulong.Parse(victim.Id));
            data.deadpool.Add(ulong.Parse(victim.Id), all);
            players.FindPlayer(player.UserIDString).Reply("Your have succesfulls placed a bounty of " + reward + " gold on " + victim.Name);

            SaveData();
        }


        // removes a bounty you set onto another player and returns your gold
        //[bounty.remove "player"]
        [Command("removeBounty")]
        private void removeBountyCallBack(IPlayer iplayer, String command, string[] args)
        {
            var victim =covalence.Players.FindPlayerById(args[0]);
            System.Console.WriteLine(victim == null);
            cmdRemoveBounty(iplayer, victim);

            myBountyPage(iplayer, null, args);
        }


            private void cmdRemoveBounty(IPlayer iclient, IPlayer victim)
        {
            BasePlayer player = BasePlayer.FindByID(ulong.Parse(iclient.Id));

            if (!data.deadpool.Keys.Contains(ulong.Parse(victim.Id)))
            {
                players.FindPlayer(player.UserIDString).Reply("There is no bounty on " + victim.Name);
                return;
            }

            bool ok = false;
            HashSet<Mission> removeM = new HashSet<Mission>();
            foreach (Mission mission in data.missions)
            {
                if (mission.client == player.userID && mission.victim == victim.Id)
                {
                    ok = true;
                    removeM.Add(mission);

                    data.deadpool[victim.Id].Remove(mission);
                    if (data.deadpool[victim.Id].Count == 0)
                    {
                        data.deadpool.Remove(victim.userID);
                        HashSet<ulong> removeH = new HashSet<ulong>();
                        foreach (ulong hunterID in data.hunts.Keys)
                        {
                            BasePlayer hunter = BasePlayer.FindByID(hunterID);
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
                players.FindPlayer(player.UserIDString).Reply("There is no active bounty placed on " + victim._name + " by you");
            }
            else
            {
                players.FindPlayer(player.UserIDString).Reply("All bounties you had set on " + victim._name + " have been removed succesfully");
            }
            SaveData();
        }

        // accepts all bounty requests for player "player"
        // [bounty.accept "player"]
        // former [ConsoleCommand("bounty.accept")]
        private void cmdAcceptBounty(IPlayer iclient, IPlayer ivictim)
        {
            BasePlayer player = BasePlayer.FindByID(ulong.Parse(iclient.Id));
            IPlayer victim = ivictim;

            if (victim == null)
            {
                players.FindPlayer(player.UserIDString).Reply("Invalid player name");
                return;
            }

            if (!data.deadpool.ContainsKey(ulong.Parse(victim.Id)))
            {
                players.FindPlayer(player.UserIDString).Reply("There are no bounties set on " + victim.Name);
                return;
            }

             foreach (Mission mission in data.deadpool[ulong.Parse(victim.Id)]) {
                if (mission.client == player.userID) { 
                    players.FindPlayer(player.UserIDString).Reply("You can't accept a bounty hunt you've set yourself");
                    return;
                }
            }

            if (!canDoBountyThings(player, victim))
            {
                players.FindPlayer(player.UserIDString).Reply("You would stab your own ally's back?! :O");
                players.FindPlayer(player.UserIDString).Reply("I'm sorry, we don't do that here.");
                // return;
            }

            if (!data.hunts.Keys.Contains(player.userID))
            {
                data.hunts.Add(player.userID, new HashSet<ulong>());
            }
            if (data.hunts[player.userID].Contains(ulong.Parse(victim.Id)))
            {
                players.FindPlayer(player.UserIDString).Reply("You already hunt this player");
                return;
            }
            data.hunts[player.userID].Add(ulong.Parse(victim.Id));
            players.FindPlayer(player.UserIDString).Reply("You are on the hunt for " + victim.Name);
            players.FindPlayer(player.UserIDString).Reply("You may now attack " + victim.Name + " on the whole map. Be aware that " + victim.Name + " will be allowed to defend");
            SaveData();
        }

        //Sends a list of all active bounty stations with their grid coords
        //[bounty.station.list]
        // former [ConsoleCommand("bounty.station.list")]
        private void cmdListStation(IPlayer iclient)
        {
            BasePlayer player = BasePlayer.FindByID(ulong.Parse(iclient.Id));
            
            String list = listStations();
            players.FindPlayer(player.UserIDString).Reply(list);
            if (list.Equals(""))
                players.FindPlayer(player.UserIDString).Reply("There are no bounty stations set. Rewards will be claimed automatically on a hunted victim's death.");
        }

        //Sends a list of all players with bounties on them and the total reward for their death
        //[bounty.list]
        [Command("bounty.list")]
        private void cmdListBounty(IPlayer iclient)
        {
            BasePlayer player = BasePlayer.FindByID(ulong.Parse(iclient.Id));   
            String list = listDeadpool();
            players.FindPlayer(player.UserIDString).Reply(list);
            if (list.Equals(""))
                players.FindPlayer(player.UserIDString).Reply("There are no active bounties at the moment");
        }

        #endregion

        #region ChatCommands


        // sets the container in you line of sight as a bounty station.
        //Players will receive their reward by placing the skull of the wanted player in one of your set Stations.
        //If no station is set, players receive their reward on death of the victim.
        // [bounty.station.set]
        [Command("bounty.station.set")]
        private void cmdBountyStation(IPlayer iplayer, String command, string[] args)
        {
            BasePlayer player = BasePlayer.FindByID(ulong.Parse(iplayer.Id));
            if (args.Length != 0)
            {
                players.FindPlayer(player.UserIDString).Reply("Invalid Command");
                return;
            }

            if (!permission.UserHasPermission(player.UserIDString, stationPerm)) {
                players.FindPlayer(player.UserIDString).Reply("You don't have permission to set a bounty station");
                return;
            }
            BaseEntity entity = GetTargetEntity(player);
            if (entity == null)
            {
                players.FindPlayer(player.UserIDString).Reply("There is no Item in you line of sight");
                return;
            }
            
            if (!config.allowedStationItems.ContainsKey(entity.ShortPrefabName)) { 
                players.FindPlayer(player.UserIDString).Reply("The item in your line of sight may not be chosen as Bounty station");
                return;
            }

            if (!data.stations.ContainsKey(entity.ToString()))
            {
                data.stations.Add(entity.ToString(),getEntityPos(entity));
                players.FindPlayer(player.UserIDString).Reply("you have succesfully set this " + entity.ShortPrefabName + " as a bounty station");
                SaveData();
                return;
            }

            players.FindPlayer(player.UserIDString).Reply("this item is a bounty station already, duh!");
            
        }

        //removes the container in your line of sight from the list of bounty stations.
        //If no more station is set, players receive their reward on death of the victim.
        // [bounty.station.remove]
        [Command("bounty.station.remove")]
        private void cmdRemoveStation(IPlayer iplayer, String command, string[] args) {
            BasePlayer player = BasePlayer.FindByID(ulong.Parse(iplayer.Id));
            if (args.Length != 0)
            {
                players.FindPlayer(player.UserIDString).Reply("Invalid Command");
                return;
            }

            if (!permission.UserHasPermission(player.UserIDString, stationPerm))
            {
                players.FindPlayer(player.UserIDString).Reply("You don't have permission to remove a bounty station");
                return;
            }
            BaseEntity entity = GetTargetEntity(player);
            if (entity == null)
            {
                players.FindPlayer(player.UserIDString).Reply("There is no Item in you line of sight");
                return;
            }

            if (data.stations.ContainsKey(entity.ToString()))
            {
                data.stations.Remove(entity.ToString());
                players.FindPlayer(player.UserIDString).Reply("you have successfully removed this " + entity.ShortPrefabName + " from the list of bounty stations");
                SaveData();
                return;
            }

            players.FindPlayer(player.UserIDString).Reply("this item is was not a bounty station, duh!");
        }



        //deletes all bounties and stations.
        //returns the already paid gold for active bounties to the respective client
        [Command("bounty.reset")]
        private void cmdReset(IPlayer iplayer, String command, string[] args)
        {
            BasePlayer player = BasePlayer.FindByID(ulong.Parse(iplayer.Id));
            if (!permission.UserHasPermission(player.UserIDString, resetPerm))
            {
                players.FindPlayer(player.UserIDString).Reply("You don't have permission to reset the bounty data");
                return;
            }

            foreach (Mission mission in data.missions)
            {
                giveGold(BasePlayer.FindByID(mission.client), mission.reward);
            }
            data.deadpool = new Dictionary<ulong, HashSet<Mission>>();
            data.missions = new HashSet<Mission>();
            data.hunts = new Dictionary<ulong, HashSet<ulong>>();
            data.stations = new Dictionary<string, string>();
            SaveData();
            players.FindPlayer(player.UserIDString).Reply("You have succesfully reset the Bounty Plugin to default state");
        }

        #endregion

        #region Helper

        //rewards the succesful hunter and deletes all mission related data.
        private void hunted(BasePlayer hunter, BasePlayer victim) {
            int gold = getTotalReward(victim);
            giveGold(hunter, gold);
            players.FindPlayer(hunter.UserIDString).Message("You have succesfully hunted down " + victim.displayName + ". Enjoy your reward: "+gold+" gold!");

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
                list += (BasePlayer.FindByID(playerID).displayName + ": " + getTotalReward(BasePlayer.FindByID(playerID)) + " gold" + "\n");
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
        private bool canDoBountyThings(BasePlayer player, IPlayer victim) {

           
           if (Friends != null)
            {
                bool friends = (bool)Friends.Call("AreFriends", player.userID, ulong.Parse(victim.Id));
                if (friends)
                    return false;
            }

            if (player.Team != null && player.Team.members.Contains(ulong.Parse(victim.Id)))
            { 
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



        #region GUIHelper
        private Dictionary<string, string[]> UserInputBounty = new Dictionary<string, string[]>(); // Format: <userId, text>



        float AnchorMin1 = 0.01f;
        float AnchorMin2start = 0.86f;
        float AnchorMax2Start = 0.9f;
        float AnchorMax1 = 0.7f;

        private void createPlayerlistUI(List<BasePlayer> players, CustomCuiElementContainer container, String mainName, int page = 0)
        {

            #region header


            container.Add(new CuiPanel
            {
                Image = { Color = ButtonColour },
                RectTransform = { AnchorMin = "0.01 0.92", AnchorMax = "0.9 0.99" },
                CursorEnabled = true
            }, mainName);

            container.Add(new CuiLabel
            {

                RectTransform = { AnchorMin = "0.1 0.92", AnchorMax = "0.9 0.99" },
                Text = { Text = "Available players", FontSize = 25, Align = TextAnchor.MiddleCenter }
            }, mainName);
            #endregion

            #region Fields
            float step = 0f;
            List<List<BasePlayer>> lists = new List<List<BasePlayer>>();
            int Pages = (int)Math.Ceiling((double)players.Count / 13);
            int start = 0;
            int last = players.Count;


            for (int i = 0; i < Pages; i++)
            {
                int dist = (start + 13 <= last) ? 13 : (last - start);
                lists.Add(players.GetRange(start, dist));
                start += 13;
            }
            #endregion


            foreach (BasePlayer player in lists[page])
            {
                container.Add(new CuiPanel
                {
                    Image = { Color = ButtonColour
},
                    RectTransform = {
                        AnchorMin = $"0.01 {AnchorMin2start - step}",
                        AnchorMax = $"0.5 {AnchorMax2Start - step}" },
                    CursorEnabled = true

                }, mainName, page.ToString());

                #region Inputfield

                container.Add(new CuiPanel
                {
                    Image = { Color = "0 0 0 0.5"
},
                    RectTransform = {
                        AnchorMin = $"0.55 {AnchorMin2start - step}",
                        AnchorMax = $"0.7 {AnchorMax2Start - step}" },
                    CursorEnabled = true

                }, mainName, page.ToString());

                addingInput(container, step, mainName, player);

                #endregion

                container.Add(new CuiLabel
                {

                    RectTransform = { AnchorMin = $"0.01 {AnchorMin2start - step}", AnchorMax = $"0.5 {AnchorMax2Start - step}" },
                    Text = { Text = player.displayName, FontSize = 16, Align = TextAnchor.MiddleCenter }
                }, mainName, page.ToString());



                container.Add(new CuiButton
                {
                    Button = { Command = $"addBounty {player.userID}", Color = ButtonColour },
                    RectTransform = { AnchorMin = $"0.75 {0.86 - step}", AnchorMax = $"0.9 {0.9 - step}" },
                    Text = { Text = "Add Bounty", FontSize = 14, Align = TextAnchor.MiddleCenter }
                }, mainName);

                step += 0.055f;

            }


            //Next Button

            if (!lists.Last().Equals(lists[page]))
                container.Add(new CuiButton
                {
                    Button = { Command = $"availablePlayers {"PlayerList"} {page + 1}", Color = ButtonColour },
                    RectTransform = { AnchorMin = "0.8 0.12", AnchorMax = "0.9 0.15" },
                    Text = { Text = "Next", FontSize = 15, Align = TextAnchor.MiddleCenter }
                }, mainName);

            //Next Button

            if (page > 0)
                container.Add(new CuiButton
                {
                    Button = { Command = $"availablePlayers {"PlayerList"} {page - 1}", Color = ButtonColour },
                    RectTransform = { AnchorMin = "0.1 0.12", AnchorMax = "0.2 0.15" },
                    Text = { Text = "previous", FontSize = 15, Align = TextAnchor.MiddleCenter }
                }, mainName);

        }

        private void createBountyList(int page, IPlayer iplayer, CustomCuiElementContainer elements, List<Mission> missions, String mainName)
        {
            #region header


            elements.Add(new CuiPanel
            {
                Image = { Color = ButtonColour },
                RectTransform = { AnchorMin = "0.01 0.92", AnchorMax = "0.99 0.99" },
                CursorEnabled = true
            }, mainName);

            elements.Add(new CuiLabel
            {

                RectTransform = { AnchorMin = "0.01 0.92", AnchorMax = "0.99 0.99" },
                Text = { Text = "My Bounties", FontSize = 25, Align = TextAnchor.MiddleCenter }
            }, mainName);
            #endregion

            #region Fields
            float step = 0f;
            List<List<Mission>> lists = new List<List<Mission>>();
            int Pages = (int)Math.Ceiling((double)missions.Count / 13);
            int start = 0;
            int last = missions.Count;

           // missions.Sort();
            for (int i = 0; i < Pages; i++)
            {
                int dist = (start + 13 <= last) ? 13 : (last - start);
                lists.Add(missions.GetRange(start, dist));
                start += 13;
            }
            #endregion


            foreach (Mission mission in lists[page])
            {
                {
                    elements.Add(new CuiPanel
                    {
                        Image = { Color = ButtonColour
},
                        RectTransform = {
                        AnchorMin = $"0.01 {AnchorMin2start - step}",
                        AnchorMax = $"0.5 {AnchorMax2Start - step}" },
                        CursorEnabled = true

                    }, mainName, page.ToString());

                    elements.Add(new CuiPanel
                    {
                        Image = { Color = "0 0 0 0.5"
},
                        RectTransform = {
                        AnchorMin = $"0.55 {AnchorMin2start - step}",
                        AnchorMax = $"0.7 {AnchorMax2Start - step}" },
                        CursorEnabled = true

                    }, mainName, page.ToString());
                    elements.Add(new CuiLabel
                    {
                        Text = {
                            Text = mission.reward.ToString(),
                            FontSize = 16,
                            Align = TextAnchor.MiddleCenter },

                        RectTransform = {
                        AnchorMin = $"0.55 {AnchorMin2start - step}",
                        AnchorMax = $"0.7 {AnchorMax2Start - step}" }
                    },mainName, page.ToString());

                    elements.Add(new CuiLabel
                    {

                        RectTransform = {
                            AnchorMin = $"0.01 {AnchorMin2start - step}"
                            , AnchorMax = $"0.5 {AnchorMax2Start - step}"
                        },
                        Text = { 
                            Text = covalence.Players.FindPlayerById(mission.victim.ToString()).Name, 
                            FontSize = 16, 
                            Align = TextAnchor.MiddleCenter }
                    }, mainName, page.ToString());




                    elements.Add(new CuiButton
                    {
                        Button = { Command = $"removeBounty {mission.victim}", Color = ButtonColour },
                        RectTransform = { AnchorMin = $"0.75 {0.86 - step}", AnchorMax = $"0.9 {0.9 - step}" },
                        Text = { Text = "remove Bounty", FontSize = 14, Align = TextAnchor.MiddleCenter }
                    }, mainName);

                    step += 0.055f;

                }

                //Next Button

                if (!lists.Last().Equals(lists[page]))
                    elements.Add(new CuiButton
                    {
                        Button = { Command = $"MyBounty {"BountyList"} {page + 1}", Color = ButtonColour },
                        RectTransform = { AnchorMin = "0.8 0.12", AnchorMax = "0.9 0.15" },
                        Text = { Text = "Next", FontSize = 15, Align = TextAnchor.MiddleCenter }
                    }, mainName);

                //Next Button

                if (page > 0)
                    elements.Add(new CuiButton
                    {
                        Button = { Command = $"MyBounty {"BountyList"} {page - 1}", Color = ButtonColour },
                        RectTransform = { AnchorMin = "0.1 0.12", AnchorMax = "0.2 0.15" },
                        Text = { Text = "previous", FontSize = 15, Align = TextAnchor.MiddleCenter }
                    }, mainName);

            }

        }




        private void addingInput(CustomCuiElementContainer container, float step, String parent, BasePlayer player)
        {
            var input = new CuiElement
            {
                Name = "TestNameInput",
                Parent = parent,
                Components =
                {
                    new CuiInputFieldComponent
                    {
                        Text = "Bounty",
                        CharsLimit = 10,
                        Color = "1 1 1 1",
                        IsPassword = false,
                        Command = "inputfieldControll "+ player.userID,
                        FontSize =16,
                        Align = TextAnchor.MiddleLeft
                    },
                    new CuiRectTransformComponent
                    {
                        AnchorMin = $"0.55 {AnchorMin2start - step}",
                        AnchorMax = $"0.7 {AnchorMax2Start - step}"
                    }
                }
            };
            container.Add(input);
        }

        #endregion


        #region Custom CUI components

        /// <summary>
        /// Input field object
        /// </summary>
        private class CuiInputField
        {
            public CuiInputFieldComponent InputField { get; } = new CuiInputFieldComponent();
            public CuiRectTransformComponent RectTransform { get; } = new CuiRectTransformComponent();
            public float FadeOut { get; set; }
        }

        /// <summary>
        /// Custom version of the CuiElementContainer to add InputFields
        /// </summary>
        private class CustomCuiElementContainer : CuiElementContainer
        {
            public string Add(CuiInputField aInputField, string aParent = "Hud", string aName = "")
            {
                if (string.IsNullOrEmpty(aName))
                    aName = CuiHelper.GetGuid();

                if (aInputField == null)
                {
                    return string.Empty;
                }

                Add(new CuiElement
                {
                    Name = aName,
                    Parent = aParent,
                    FadeOut = aInputField.FadeOut,
                    Components = {
                aInputField.InputField,
                aInputField.RectTransform
            }
                });
                return aName;
            }
        }

        #endregion

        #region GUI 

       
      private  String ButtonColour = "0.7 0.32 0.17 1";
        private String guiString = String.Format("0 0 0 {0}", 0.5);
        private CuiElementContainer elements;

        #region Commands

        [Command("GUI")]
        private void Gui(IPlayer iplayer, String command, string[] args)
        {
            BasePlayer player = BasePlayer.FindByID(ulong.Parse(iplayer.Id));
            MainUI(player);
            String parent = args.Length > 0 ? args[0] : String.Empty;
            CuiHelper.DestroyUi(player, parent);
            CuiHelper.AddUi(player, elements);

        }

        void MainUI(BasePlayer player)
        {
            int page = 1;
            double LeftAMin1 = 0.1, AMin2 = 0.89, LeftAMax1 = 0.3, AMax2 = 0.91, CenterAMin1 = 0.4, CenterAMax1 = 0.6, RightAMin1 = 0.7, RightAMax1 = 0.9;
            int plugsTotal = 0, pos1 = (60 - (page * 60)), next = (page + 1), previous = (page - 1);


            elements = new CuiElementContainer();
            var mainName = elements.Add(new CuiPanel
            {
                Image = { Color = guiString },
                RectTransform = { AnchorMin = "0.3 0.3", AnchorMax = "0.7 0.6" },
                CursorEnabled = true
            }, "Overlay", "MenuGUI");


            //playerlistButton
            elements.Add(new CuiButton
            {
                Button = { Command = $"availablePlayers {"MenuGUI"}"
                , Color = ButtonColour },
                RectTransform = { AnchorMin = "0.3 0.8", AnchorMax = "0.7 0.95" },
                Text = { Text = "New Bounty", FontSize = 14, Align = TextAnchor.MiddleCenter }
            }, mainName);
          
            elements.Add(new CuiButton
            {
                Button = { Command = $"MyBounty {"MenuGUI"}", Color = ButtonColour },
                RectTransform = { AnchorMin = "0.3 0.6", AnchorMax = "0.7 0.75" },
                Text = { Text = "My Bounties", FontSize = 14, Align = TextAnchor.MiddleCenter }
            }, mainName);

            elements.Add(new CuiButton
            {
                Button = { Command = $"ClosePM {"MenuGUI"}", Color = ButtonColour },
                RectTransform = { AnchorMin = "0.3 0.4", AnchorMax = "0.7 0.55" },
                Text = { Text = "All Bounties", FontSize = 14, Align = TextAnchor.MiddleCenter }
            }, mainName);
            //closeButton
            elements.Add(new CuiButton
            {
                Button = { Command = $"ClosePM {"MenuGUI"}", Color = ButtonColour },
                RectTransform = { AnchorMin = "0.3 0.02", AnchorMax = "0.7 0.1" },
                Text = { Text = "Close", FontSize = 14, Align = TextAnchor.MiddleCenter }
            }, mainName);





        }


        [Command("MyBounty")]
        private void myBountyPage(IPlayer iplayer, String command, string[] args)
        {
            BasePlayer player = BasePlayer.FindByID(ulong.Parse(iplayer.Id));
            #region fields
            page = args.Length > 1 ? int.Parse(args[1]) : 0;
            List<Mission> missions= myBounties(player.userID).ToList();
  
            #endregion

            #region UIStuff
            var elements = new CustomCuiElementContainer();

          
            var mainName = elements.Add(new CuiPanel
            {
                Image = { Color = guiString },
                RectTransform = { AnchorMin = "0.3 0.11", AnchorMax = "0.7 0.97" },
                CursorEnabled = true
            }, "Overlay", "BountyList");

            createBountyList(page, iplayer, elements, missions, mainName);

            //CloseButton
            elements.Add(new CuiButton
            {
                Button = { Command = $"ClosePM {"BountyList"}", Color = ButtonColour },
                RectTransform = { AnchorMin = "0.6 0.02", AnchorMax = "0.9 0.1" },
                Text = { Text = "Close", FontSize = 25, Align = TextAnchor.MiddleCenter }
            }, mainName);

            //back button
            elements.Add(new CuiButton
            {
                Button = { Command = $"GUI {"BountyList"}", Color = ButtonColour },
                RectTransform = { AnchorMin = "0.1 0.02", AnchorMax = "0.4 0.1" },
                Text = { Text = "<<< Back", FontSize = 25, Align = TextAnchor.MiddleCenter }
            }, mainName);


            #endregion

            CuiHelper.DestroyUi(player, args[0]);
            CuiHelper.AddUi(player, elements);


            return;

        }

        //closes the UiElement with the name in args[0]
        [Command("ClosePM")]
        private void closepm(IPlayer iplayer, String command, string[] args)
        {
            BasePlayer player = BasePlayer.FindByID(ulong.Parse(iplayer.Id));

            if (player == null)
                return;
            if (args[0] == null)
                return;
            CuiHelper.DestroyUi(player, args[0]);
            return;
        }


        #region AddBounty

       
        int page;
        //open a new UIpanel with a list of available players from wich you can selct one to place a bounty
        [Command("availablePlayers")]
        private void availablePlayers(IPlayer iplayer, String command, string[] args)
        {
            BasePlayer player = BasePlayer.FindByID(ulong.Parse(iplayer.Id));

            #region fields

            page = args.Length > 1 ? int.Parse(args[1]) : 0;
            List<BasePlayer> all = BasePlayer.activePlayerList.ToList();   
            all.AddRange(BasePlayer.sleepingPlayerList.ToList());
            
            #endregion

            #region UIStuff
            var elements = new CustomCuiElementContainer();

            var mainName = elements.Add(new CuiPanel
            {
                Image = { Color = guiString },
                RectTransform = { AnchorMin = "0.3 0.11", AnchorMax = "0.7 0.97" },
                CursorEnabled = true
            }, "Overlay", "PlayerList");


            createPlayerlistUI(all, elements, mainName, page);

            //CloseButton
            elements.Add(new CuiButton
            {
                Button = { Command = $"ClosePM {"PlayerList"}", Color = ButtonColour },
                RectTransform = { AnchorMin = "0.6 0.02", AnchorMax = "0.9 0.1" },
                Text = { Text = "Close", FontSize = 25, Align = TextAnchor.MiddleCenter }
            }, mainName);

            //back button
            elements.Add(new CuiButton
            {
                Button = { Command = $"GUI {"PlayerList"}", Color = ButtonColour },
                RectTransform = { AnchorMin = "0.1 0.02", AnchorMax = "0.4 0.1" },
                Text = { Text = "<<< Back", FontSize = 25, Align = TextAnchor.MiddleCenter }
            }, mainName);


            #endregion
           
            CuiHelper.DestroyUi(player, args[0]);
            CuiHelper.AddUi(player, elements);


            return;
        }


        [Command("inputfieldControll")]
        private void inputfieldControll(IPlayer iPlayer, String command, string[] args)
        {
            BasePlayer player = BasePlayer.Find(iPlayer.Id);

          
            if (iPlayer.IsServer || args.Count() <= 1)
            {
                if (UserInputBounty.ContainsKey(iPlayer.Id))
                    UserInputBounty.Remove(iPlayer.Id);

                return;
            }
       
            if (UserInputBounty.ContainsKey(iPlayer.Id))
            {

                UserInputBounty[iPlayer.Id] = args;
            }
            else
            {
                UserInputBounty.Add(iPlayer.Id, args);
            }
        }




        [Command("addBounty")]
        private void addBounty(IPlayer iplayer, String command, string[] args)
        {
            if (!UserInputBounty.ContainsKey(iplayer.Id))
                return;
            var bounty = UserInputBounty[iplayer.Id][1];
            var victimId = UserInputBounty[iplayer.Id][0];
            
            // check if the bounty is in the right line
            IPlayer p = covalence.Players.FindPlayerById(args[0]);         
            if (victimId != p.Id)
                return;
            

            try
            {    
                cmdPlaceBounty(iplayer, p, int.Parse(bounty));
            }
            catch (FormatException)
            {

                System.Console.WriteLine("format exception");
            }  
            UserInputBounty.Remove(iplayer.Id);
            availablePlayers(iplayer, "", new string[] { "PlayerList", page.ToString() });
        }
        #endregion


        #endregion
        #endregion


    }


}
