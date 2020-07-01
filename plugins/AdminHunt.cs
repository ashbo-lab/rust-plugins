using System.Collections.Generic;
using System;
using System.Globalization;
using Oxide.Core;
using Oxide.Core.Plugins;
using System.Linq;
using Oxide.Core.Configuration;
using UnityEngine;
using Newtonsoft.Json;
using Oxide.Core.Libraries.Covalence;
using Oxide.Game.Rust.Cui;
using Oxide.Game.Rust.Libraries;

namespace Oxide.Plugins
{
    [Info("AdminHunt", "Oegge & ashbo", "1.0.0")]
    [Description("always places Bounty on admins in the currency of ServerRewards")]
    class AdminHunt : RustPlugin
    {
        [PluginReference] private Plugin ServerRewards, EventManager;

        #region Helper
        private string Lang(string key, string id = null, params object[] args) => string.Format(lang.GetMessage(key, this, id), args);
         static int NumberOfRows = (int)(Mathf.Floor(ConVar.Server.worldsize / 146.3f) - 1);
        #endregion

        #region Config
        private ConfigFile _config;


        #endregion

        #region Constants
        const float calgon = 0.0066666666667f;

        private static string admin = "AdminHunt.admin";
        private static string debug = "AdminHunt.debug";

        #endregion

        #region Fields
        private static AdminHunt _instance;

        private List<BasePlayer> admins;
        private System.Random random = new System.Random();
        private static Timer locationTimer;
        private static Timer messageTimer;

        #endregion

        #region Config
        private class ConfigFile
        {
            public bool eventbypass;
            public int Reward;
            public bool hunt;
            public float timerLocating;
            public float timerMessage;
            public bool debug;


            public static ConfigFile DefaultConfig()
            {


                return new ConfigFile
                {eventbypass = false,
                    debug = false,
                    timerLocating = 240f,
                    timerMessage = 120f,
                    Reward = 1000,
                    hunt = false
                };


            }
        }

        protected override void LoadDefaultConfig()
        {
            PrintWarning("Generating default configuration file...");
            _config = ConfigFile.DefaultConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            _config = Config.ReadObject<ConfigFile>();
        }

        protected override void SaveConfig() => Config.WriteObject(_config);





        #endregion

        #region init

        private void Init()
        {
            _instance = this;
            permission.RegisterPermission(admin, this);
            LoadConfig();

        }




        private void Loaded()
        {
            if (ServerRewards == null)
            {
                PrintWarning("ServerRewards not detected, unloading ServerRewards");
                Interface.Oxide.UnloadPlugin(Name);
                return;
            }
            else
            {
                Console.WriteLine("ServerRewards loaded");
            }
            admins = new List<BasePlayer>();
            if (_config.hunt)
            {

                PrintToChat(string.Format(lang.GetMessage("HuntStart", this), _config.Reward, lang.GetMessage("Currency", this)));
                startAnnouncement();
            }
        }

        #endregion

        #region Helper

        #region AdminfindHelper


        void OnPlayerConnected(BasePlayer player)
        {
            if (player.IsAdmin)
            {
                admins.Add(player);
                return;
            }
            return;

        }

        void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player.IsAdmin)
            {
                if (!player.IsAlive())
                {
                    admins.Remove(player);
                }
            }
        }

        private void checkAdmins()
        {
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                if (player.IsAdmin)
                {
                    admins.Add(player);
                }
            }
            foreach (BasePlayer player in BasePlayer.sleepingPlayerList)
            {
                if (player.IsAdmin)
                {
                    admins.Add(player);
                }
            }
        }
        #endregion


        private void startAnnouncement()
        {
            checkAdmins();

            locationTimer = timer.Every(_config.timerLocating, () =>
            {
                int num = random.Next(admins.Count + 1);
                if (num < 1)
                {
                    return;
                }

                BasePlayer player = admins[num - 1];
                String[] grid= getGrid(player.transform.position);             
                float Coordsx = player.transform.position.x;
                float Coordsy = player.transform.position.z;
                PrintToChat(string.Format(lang.GetMessage("AdminSighted", this), grid[0], grid[1], player.displayName));
            });

            messageTimer = timer.Every(_config.timerMessage, () =>
              {
                  PrintToChat(string.Format(lang.GetMessage("HuntStart", this), _config.Reward, lang.GetMessage("Currency", this)));
              });
        }
       
        private void stopAnnouncements()
        {
            locationTimer.Destroy();
            messageTimer.Destroy();

        }



        #region GRidMaker

        string[] getGrid(Vector3 pos)
        {       
            var x = (pos.x + (ConVar.Server.worldsize / 2)) / 146.3f;
            var z = NumberOfRows - Mathf.Floor((pos.z + (ConVar.Server.worldsize / 2)) / 146.3f);
            
            String letter = getXLetter(x, "");
 
            return new string[2] { letter, ""+z};
        }

        private String getXLetter(float num, String current)
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
            String result = GridLetter + current;
        
            
            if (rest != 0)
            {
                result = getXLetter(rest, result);
            }
            return prefix + result;
        }


        #endregion





        #endregion

        #region Hooks

        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            #region check
            if (!_config.hunt)
                return null;

            if (entity == null || info == null)
                return null;

            #endregion
            BasePlayer victim = entity.ToPlayer();
            BasePlayer attacker = info.InitiatorPlayer;


            if (victim != null && !(victim.userID >= 76560000000000000L || victim.userID <= 0L))
            {
                return null;
            }

            if (victim != null && attacker != null && (attacker.userID >= 76560000000000000L))
            {
                if ((bool)EventManager.Call("isPlaying", victim) || (bool)EventManager.Call("isPlaying", attacker))
                    return null;

                // negate all protection
                if (victim.IsAdmin || attacker.IsAdmin)
                {
                    float health = victim._health;
                    float dmg = 0;
                    dmg = info?.damageTypes?.Total() ?? 0f;
                    victim._health -= dmg;
                    if (victim._health <= 0)
                        victim.Die();
                    return true;
                }

            }

            return null;
        }


        object OnPlayerDeath(BasePlayer player, HitInfo info)
        {
           

            //check if hunt is active
            if (!_config.hunt)
                return null;
            bool playing = (bool)EventManager.Call("isPlaying", player);
            if ( playing && !_config.eventbypass)
                return null;
            //check for nullpointer
            if (player == null || info == null)
                return null;

            if (player.IsAdmin)
            {
                if (player.IsSleeping())
                {
                    admins.Remove(player);
                    Console.WriteLine("Sleeping Admin " + player.displayName + " has been killed");
                    PrintToChat("Sleeping Admin " + player.displayName + " has been killed");
                }            
                {
                    BasePlayer attacker = info.InitiatorPlayer;

                    //
                    if (attacker != null && attacker.userID > 76560000000000000L)
                    {
                        PrintToChat("Admin has been killed");
                        PrintToChat(string.Format
                             (lang.GetMessage("AdminKilled", this, attacker.UserIDString), _config.Reward, lang.GetMessage("Currency", this)));
                        ServerRewards.Call("AddPoints", attacker.userID.ToString(), _config.Reward);
                    }
                }
            }

            return null;

        }




        #endregion

        #region Commands


        [ChatCommand("AdminHunt")]
        private void help(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.userID.ToString(), admin))
            {
                SendReply(player, "You don't have permission to use this command.");
                return;
            }

            SendReply(player, Lang("Help", player.UserIDString));
        }

        [ChatCommand("AH.timer")]
        private void setTimer(BasePlayer player, string command, string[] args)
        {

            if (!permission.UserHasPermission(player.userID.ToString(), admin))
            {
                SendReply(player, "You don't have permission to use this command.");
                return;
            }
            float time;
            if (args.Length == 2 && args[0].ToLower().Equals("location"))
            {
                try
                {
                    time = float.Parse(args[1]);
                }
                catch (FormatException)
                {

                    SendReply(player, "Syntax error, to change the timers use /ah.timer Location/Message <time>");
                    return;
                };
                _config.timerLocating = time;
            }

            else if (args.Length == 2 && args[0].ToLower().Equals("message"))
            {
                try
                {
                    time = float.Parse(args[1]);
                }
                catch (FormatException)
                {

                    SendReply(player, "Syntax error, to change the timers use /ah.timer Location/Message <time>");
                    return;
                };
                _config.timerMessage = time;
            }
            else
            {
                SendReply(player, "Syntax error, to change the timers use /ah.timer Location/Message <time>");
                return;
            }
            SendReply(player, "Timer changed");
            stopAnnouncements();
            startAnnouncement();
            SaveConfig();

        }


        [ChatCommand("AH.set")]
        private void setReward(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.userID.ToString(), admin))
            {
                SendReply(player, "You don't have permission to use this command.");
                return;
            }
            if (args.Length == 1)
                try
                {
                    _config.Reward = int.Parse(args[0]);
                    PrintToChat(player, Lang("RewardChanged", player.UserIDString) + _config.Reward);
                }
                catch (Exception)
                {

                    SendReply(player, "Syntax error, to change the bounty on Admins use /ah set amount");
                }

            else
            {
                SendReply(player, "Syntax error, to change the bounty on Admins use /ah set amount");
                return;
            }
            SaveConfig();
        }

        [ChatCommand("AH.Hunt")]
        private void activateHunt(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.userID.ToString(), admin))
            {
                SendReply(player, "You don't have permission to use this command.");
                return;
            }

            if (args.Length == 1 && args[0].Equals("start"))
            {
                if (_config.hunt)
                {
                    SendReply(player, "they are already hunted");
                    return;
                }
                _config.hunt = true;
                startAnnouncement();
                PrintToChat(string.Format(lang.GetMessage("HuntStart", this), _config.Reward, lang.GetMessage("Currency", this)));
                SaveConfig();
            }
            else if (args.Length == 1 && args[0].Equals("end"))
            {
                if (!_config.hunt)
                {
                    SendReply(player, "they never were hunted n the first place");
                    return;
                }

                _config.hunt = false;
                stopAnnouncements();
                PrintToChat(lang.GetMessage("HuntStop", this));
                SaveConfig();

            }
            else
            {
                SendReply(player, "Syntax error, to activate hunt use /AH.hunt start/end");
                return;
            }


        }


        [ChatCommand("AH.event")]
        private void setEvent(BasePlayer player, string command, string[] args)
        {

            if (!permission.UserHasPermission(player.userID.ToString(), admin))
            {
                SendReply(player, "You don't have permission to use this command.");
                return;
            }
        if(args.Length == 1 && args[0].ToLower().Equals(true))
            {
                _config.eventbypass = true;
                SaveConfig();
                SendReply(player, "EventBypass set to true");
            }
        else if (args.Length == 1 && args[0].ToLower().Equals(false))
            {
                _config.eventbypass = false;
                SaveConfig();
                SendReply(player, "EventBypass set to false");
            }
        else
            {
                SendReply(player, "EventBypass is set to" + _config.eventbypass);
            }
    


        }

        #endregion

        #region Lang

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {

                ["Currency"] = "Gold",
                ["AdminKilled"] = "you have succesfully killed an the Admin {2}. Have fun with your Reward: {0} {1} ",
                ["RewardChanged"] = "you have succesfully changed the bounty on the admins to ",
                ["HuntStart"] = "God has forsaken the Admins, hunt them down to get {0} {1}",
                ["HuntStop"] = "God has taken pity on the Admins and starts his protection again",
                ["AdminSighted"] = "{0} has been Sighted at {1}|{2} be fast and Claim the Reward",
                ["Help"]= "Commands are: \n /AH.set to set Reward \n /AH.hunt to start hunt\n /AH.timer to set the timers of the location and info message \n /AH.event true/false to set event bypass for Rewards during an event",
        }, this); ;
        }

        #endregion



    }



}
