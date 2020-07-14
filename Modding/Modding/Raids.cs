using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using UnityEngine;
using UnityEngine.Playables;

namespace Oxide.Plugins
{
    [Info("Raids", "Faktura, Oegge", "0.0.5")]
    [Description("Prevents players destroying buildings of those they're not associated with but defers to undestr = true/false when in zone")]

    class Raids : RustPlugin
    {
        #region Declaration

        // Helpers
        private string Lang(string key, string id = null, params object[] args) => string.Format(lang.GetMessage(key, this, id), args);

        // Config, instance, plugin references
        [PluginReference] private Plugin Friends, Clans, ZoneManager;
        private static ConfigFile cFile;
        private static Raids _instance;

        // Variables
        private bool _canRaid;

        // Permissions
        private const string _perm = "raids.admin";

        #endregion

        #region Config

        private class ConfigFile
        {
            public FriendBypass FriendBypass;

            public bool StopAllRaiding;

            public RaidsCommand RaidsCommand;

            public static ConfigFile DefaultConfig()
            {
                return new ConfigFile
                {
                    FriendBypass = new FriendBypass()
                    {
                        Enabled = true,
                        FriendsApi = new FriendsAPI()
                        {
                            Enabled = true
                        },
                        PlayerOwner = new PlayerOwner()
                        {
                            Enabled = true
                        },
                        RustIoClans = new RustIOClans()
                        {
                            Enabled = true
                        }
                    },
                    RaidsCommand = new RaidsCommand()
                    {
                        Enabled = true
                    },
                    StopAllRaiding = true
                };
            }
        }

        private class FriendBypass
        {
            public bool Enabled { get; set; }
            public FriendsAPI FriendsApi { get; set; }
            public RustIOClans RustIoClans { get; set; }
            public PlayerOwner PlayerOwner { get; set; }
        }

        private class FriendsAPI
        {
            public bool Enabled { get; set; }
        }

        private class RustIOClans
        {
            public bool Enabled { get; set; }
        }

        private class PlayerOwner
        {
            public bool Enabled { get; set; }
        }

        private class RaidsCommand
        {
            public bool Enabled { get; set; }
        }

        protected override void LoadDefaultConfig()
        {
            PrintWarning("Generating default configuration file...");
            cFile = ConfigFile.DefaultConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            cFile = Config.ReadObject<ConfigFile>();
        }

        protected override void SaveConfig() => Config.WriteObject(cFile);

        #endregion

        #region Lang

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["Hit"] = "Hit!",
                ["Hit"] = "Hit Trap",
                ["AllowZone"] = "In zone",
                ["Allow"] = "Damage allowed",

                ["CantDamage"] = "No raid!",
                ["CanRaid"] = "Raids are now active",
                ["CantRaid"] = "Raids are now off",
                
                ["Cmd_Permission"] = "You don't have permission to use that command",
                ["Cmd_InvalidArgs"] = "Invalid arguments. Usage: </color=orange>/raids</color> <color=silver><start/stop></color>",
            }, this);
        }

        #endregion

        #region Methods

        
        private void StartRaid()
        {
            _canRaid = true;
            PrintToChat(Lang("CanRaid"));
            cFile.StopAllRaiding = false;
            SaveConfig();
        }

        private void StopRaid()
        {
            _canRaid = false;
            PrintToChat(Lang("CantRaid"));
            cFile.StopAllRaiding = true;
            SaveConfig();
        }

        private bool getRaid() {
            return _canRaid;
        }

        #endregion

        #region Hooks

        private void Init()
        {
           
            _instance = this;
            permission.RegisterPermission(_perm, this);
            SaveConfig();
        }

        private void OnServerInitialized()
        {
            LoadConfig();
            if (!Clans && cFile.FriendBypass.RustIoClans.Enabled)
            {
                cFile.FriendBypass.RustIoClans.Enabled = false;
                PrintWarning("RustIO Clans not detected, disabling RustIO Clans integration");
            }
            if (!Friends && cFile.FriendBypass.FriendsApi.Enabled)
            {
                cFile.FriendBypass.FriendsApi.Enabled = false;
                PrintWarning("FriendsAPI not detected, disabling FriendsAPI integration");
            }

            _canRaid = !cFile.StopAllRaiding;
        }

        private void Unload()
        {
           
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {

        }
        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
            
        {
         
            if(BasePlayer.Find( entity.OwnerID.ToString() ) == null || covalence.Players.FindPlayer(entity.OwnerID.ToString()).IsAdmin) {
              
                return null;
            }
           //System.Console.WriteLine(covalence.Players.FindPlayer(entity.OwnerID.ToString()));

            if (entity is BuildingBlock || entity.name.Contains("deploy") || entity.name.Contains("building"))
            {

                var player = info?.Initiator?.ToPlayer();

                if (!player/* || !entity.OwnerID.IsSteamId()*/)
                {
                    return null;
                }
				
                //allow damage to turrets, traps, sam site (also fishtrap but shussh)
                if(entity.name.Contains("turret") || entity.name.Contains("trap") || entity.name.Contains("landmine")){
                    return null;
				}
				
                //allow all damage in zones without the id 6660 or 6661
                if(ZoneManager != null)
                {
                    var zoneIDs = (string[]) ZoneManager.Call("GetEntityZoneIDs", entity);
					int zl = zoneIDs.Count();
					if(zl > 0){
						if(zl == 1 && (zoneIDs[0] == "6660" || zoneIDs[0] == "6661")){
							//PrintToChat("only zone is 6660");
						}else{
							return null;
						}
					}             
                }

                //friend checks
                if (cFile.FriendBypass.Enabled)
                {
                    // if owner
                    if (cFile.FriendBypass.PlayerOwner.Enabled && player.userID == entity.OwnerID)
                    {
                        //return null;
                    }

                    //if friends with owner
                    if (Friends)
                    {
                        var hasFriend = Friends?.Call("HasFriend", entity.OwnerID.ToString(), player.UserIDString) ?? false;
                        if (cFile.FriendBypass.FriendsApi.Enabled && (bool)hasFriend)
                        {
                            return null;
                        }
                    }

                    //if in clan with owner
                    if (Clans)
                    {
                        var targetClan = (string)Clans?.Call("GetClanOf", entity.OwnerID.ToString());
                        var playerClan = (string)Clans?.Call("GetClanOf", player.UserIDString);
                        if (cFile.FriendBypass.RustIoClans.Enabled && playerClan != null && targetClan != null && targetClan == playerClan)
                        {
                            return null;
                        }
                    }
                }

                // Prevents player from damaging after friendbypass checks
                if (!_canRaid)
                {
                    PrintToChat(player, Lang("CantDamage", player.UserIDString));
                    return true;
                }
            }
            return null;
        }


        [ChatCommand("raids")]
        private void RaidsCmd(BasePlayer player, string command, string[] args)
        {
            if (!cFile.RaidsCommand.Enabled)
                return;

            if (!permission.UserHasPermission(player.UserIDString, _perm))
            {
                PrintToChat(player, Lang("Cmd_Permission", player.UserIDString));
                return;
            }
            if (args.Length == 0)
            {
                PrintToChat(player, Lang("Cmd_InvalidArgs", player.UserIDString));
                return;
            }
            switch (args[0].ToLower())
            {
                case "start":
                    {
                        StartRaid();
                        return;
                    }
                case "stop":
                    {
                        StopRaid();
                        return;
                    }
            }
        }

        [ConsoleCommand("raids.start")]
        private void ConsoleStartCommand(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null && !permission.UserHasPermission(arg.Connection.userid.ToString(), _perm))
            {
                arg.ReplyWith(Lang("Cmd_Permission", arg.Connection.userid.ToString()));
                return;
            }
            arg.ReplyWith($"Raids are allowed");
            StartRaid();
        }

        [ConsoleCommand("raids.stop")]
        private void ConsoleStopCommand(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null && !permission.UserHasPermission(arg.Connection.userid.ToString(), _perm))
            {
                arg.ReplyWith(Lang("Cmd_Permission", arg.Connection.userid.ToString()));
                return;
            }
            StopRaid();
            arg.ReplyWith("Raids are off");
        }

        #endregion
    }
}