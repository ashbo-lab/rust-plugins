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
using Oxide.Plugins;
using System.Security;
using System.Timers;
using ConVar;

namespace Oxide.Plugins
{
    [Info("ServerTimer", "Oegge", "0.0.1")]
    [Description("timer for next purge")]
    class ServerTimer : RustPlugin
    {

        #region Config
        private ConfigFile _config;


        #endregion


        #region Constants
        [PluginReference] private Plugin Raids;

        private static string admin = "ServerTimer.admin";
        private static int DaySeconds = 86400;
        private static int HourstoSecond = 3600;
        private static int MinutesToSeconds = 60;
        #endregion

        private static ServerTimer _instance;
        CuiElementContainer elements;
        private static Timer DaysTimer;
        private static Timer HoursTimer;
        private static Timer MinutesTimer;
        private static Timer SecondsTimer;

        #region Config
        private class ConfigFile
        {
            public DateTime date;
            public bool timerActive;
            public static ConfigFile DefaultConfig()
            {
                return new ConfigFile
                {
                    timerActive = true,
                    date = DateTime.Now.AddMinutes(1)
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

        #region Init
        private void Init()
        {
            _instance = this;
            permission.RegisterPermission(admin, this);
            SaveConfig();
            if (_config.timerActive)
                startTimer();



        }

        void OnPlayerSleepEnded(BasePlayer player)
        {
            String[] args = new string[1];
            gui(player);
        }



        #endregion

        #region ChatCommands
        [ChatCommand("purge")]
        private void purgeTimerGet(BasePlayer player, String command, string[] args)
        {

            Puts("Hello world!");
            int days = _config.date.Subtract(DateTime.Today).Days;
            int hours = _config.date.Subtract(DateTime.Now).Hours;
            int minutes = _config.date.Subtract(DateTime.Now).Minutes;


            SendReply(player, $"Purge starts at {_config.date.Day}.{_config.date.Month}.{_config.date.Year} that makes {days} days, {hours} hours, {minutes} minutes till Purge");




            // CuiHelper.DestroyUi(player,"Timer");



        }



        [ChatCommand("purge.setTimer")]
        private void purgeTimerSet(BasePlayer player, String command, string[] args)
        {
            if (!permission.UserHasPermission(player.userID.ToString(), admin))
            {
                SendReply(player, "you don't have permission to set the purge timer");
                return;
            }

            String date = "";
            if (args.Length > 1)
                date = args[0] + " " + args[1];
            else
            {
                SendReply(player, "you have to enter a date with the format <yyyy/mm/dd hh:mm:ss>" +
                       "\nexampel /purge.settimer 2009/02/26 18:37:58");
                return;
            }

            DateTime purgeDate = DateTime.Now;


            try
            {
                purgeDate = DateTime.Parse(date);
                _config.date = purgeDate;
            }
            catch (FormatException)
            {
                SendReply(player, "you have to enter a date with the format <yyyy/mm/dd hh:mm:ss>" +
                     "\nexampel /purge.settimer 2009/02/26 18:37:58");
                return;
            }

            SendReply(player, $"the purge has ben set to {_config.date}");

            SaveConfig();

            foreach (BasePlayer basePlayer in BasePlayer.activePlayerList)
                gui(basePlayer);


        }


        [ChatCommand("purge.Show")]
        private void showGui(BasePlayer player, String command, string[] args)
        {
            if (!permission.UserHasPermission(player.userID.ToString(), admin))
            {
                SendReply(player, "you don't have permission to activate the Gui");
                return;
            }

            foreach (BasePlayer basePlayer in BasePlayer.activePlayerList)
            {
               
                gui(basePlayer);
            }
        }

        [ChatCommand("purge.close")]
        private void c(BasePlayer player, String command, string[] args)
        {
            CuiHelper.DestroyUi(player, "Timer");
        }
        #endregion


        #region Timers

        public void startTimer()
        {

            int days = _config.date.Subtract(DateTime.Today).Days;
            int hours = _config.date.Subtract(DateTime.Now).Hours;
            int minutes = _config.date.Subtract(DateTime.Now).Minutes;

            if (days > 1)
            {
                startDaysTimer();
                return;
            }
            else if (hours > 1)
            {
                startHoursTimer();
                return;

            }
            else if (minutes > 1)
            {

                startminutesTimer(minutes);
                return;
            }

            else if (_config.date.Subtract(DateTime.Now).Seconds > 0)
            {
                startCountdown();
                return;
            }

        }



        void startCountdown()
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                SendReply(player, "only seconds left till purge!");
            }
            int send = 0;
            if (MinutesTimer != null)
                MinutesTimer.Destroy();
            SecondsTimer = timer.Every(0.1f, () =>
            {
               
                send += 1;
                if (send % 10 == 0)
                {
                    
                    foreach (var player in BasePlayer.activePlayerList)
                    {
                        SendReply(player, $"only {_config.date.Subtract(DateTime.Now).Seconds} seconds left till purge!");
                    }
                }
                if (_config.date.Subtract(DateTime.Now).Seconds < 1)
                {
                    foreach (var player in BasePlayer.activePlayerList)
                    {
                        SendReply(player, $"Purge Starts now");
                    }
                   
                    Raids.Call("StartRaid");
                    rust.RunServerCommand("oxide.unload", new object[] { "TequilaSafezone" });

                    SecondsTimer.Destroy();
                    return;
                }



            });
        }

        void startminutesTimer(int minutes)
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                SendReply(player, $"{_config.date.Subtract(DateTime.Now).Minutes}:{_config.date.Subtract(DateTime.Now).Seconds}");

            }
            int send = 0;
            MinutesTimer = timer.Every(10, () =>
            {
                minutes = _config.date.Subtract(DateTime.Now).Minutes;

                //less than 1min left
                if (minutes <= 1)
                {
                    foreach (var player in BasePlayer.activePlayerList)
                    {
                        SendReply(player, $"{minutes}:{_config.date.Subtract(DateTime.Now).Seconds} minute till Purge!");
                    }
                    startCountdown();
                }

                // 1 to 10 min left

                else if (_config.date.Subtract(DateTime.Now).Minutes < 10 && send % 6 == 0)
                {

                    foreach (var player in BasePlayer.activePlayerList)
                    {
                        SendReply(player, $"{minutes}:{_config.date.Subtract(DateTime.Now).Seconds} minutes till Purge!");

                    }
                }
                // more than 10min left
                else if (_config.date.Subtract(DateTime.Now).Minutes % 10 == 1)
                {
                    foreach (var player in BasePlayer.activePlayerList)
                    {
                        SendReply(player, $"{minutes}:{_config.date.Subtract(DateTime.Now).Seconds} minutes till Purge!");
                    }
                }
                send++;
            });



        }

        void startDaysTimer()
        {
            //for plugin loaded
            foreach (var player in BasePlayer.activePlayerList)
            {
                String s = _config.date.Subtract(DateTime.Now).Days <= 1 ? "" : "s";
                SendReply(player, $"{_config.date.Subtract(DateTime.Now).Days} day{s} and {_config.date.Subtract(DateTime.Now).Hours}:{_config.date.Subtract(DateTime.Now).Minutes} h till Purge!");
            }
            int send = 0;
            DaysTimer = timer.Every(DaySeconds / 2, () =>
              {

                  int days = _config.date.Subtract(DateTime.Today).Days;
                  int hours = _config.date.Subtract(DateTime.Now).Hours;

                  if (send % 2 == 0)
                      foreach (var player in BasePlayer.activePlayerList)
                      {
                          SendReply(player, $"{days} days till Purge left");
                      }



                  if (days <= 1)
                  {
                      foreach (var player in BasePlayer.activePlayerList)
                      {
                          SendReply(player, $"{days} day and {hours}h till Purge left");
                      }
                      startHoursTimer();

                  }
                  send++;

              });
        }

        private void startHoursTimer()
        {
            DaysTimer.Destroy();
            int send = 0;
            HoursTimer = timer.Every(HourstoSecond / 2, () =>
              {
                  if (send % 2 == 0)
                      foreach (var player in BasePlayer.activePlayerList)
                      {
                          SendReply(player, $"{_config.date.Subtract(DateTime.Now).Hours} days till Purge left");
                      }
                  send++;
              });

        }
        #endregion

        [ChatCommand("g")]
        private void gui(BasePlayer player)
        {
            
            CuiHelper.DestroyUi(player, "Timer");
            #region fields

            String ButtonColour = "0.7 0.32 0.17 0.5";
            String guiString = String.Format("0.5 1 1 {0}", 0.5);
            #endregion


            elements = new CuiElementContainer();
            var mainName = elements.Add(new CuiPanel
            {
                Image = { Color = ButtonColour },
                RectTransform = { AnchorMin = "0.28 0.022", AnchorMax = "0.33 0.07" },
                CursorEnabled = false
            }
          , "Overlay", "Timer");

            elements.Add(new CuiLabel
            {

                RectTransform = { AnchorMin = "-0.2 -0.2", AnchorMax = "1.2 1.2" },
                Text = { Text = $"Purge:\n {_config.date.Day}.{_config.date.Month}.{_config.date.Year}", FontSize = 15, Align = TextAnchor.MiddleCenter }
            }, mainName);
            CuiHelper.AddUi(player, elements);


        }


    }
}
