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
    [Info("ExpensiveBackpacks", "ashbo", "0.0.0")]
    [Description("Enables you to charge ServerRewards units for acessing backpacks")]
    class ExpensiveBackpacks : Backpacks
    {
        [PluginReference] private Plugin ServerRewards, NoEscape, Backpacks;

        #region Config
        private class ConfigFile
        {
            //List of IDs of all items that may be chosen as bounty stations. (You might want to choose stations, players can put items into. Just sayin')
            public Dictionary<string, string> situationalCosts;
            public static ConfigFile DefaultConfig()
            {
                Dictionary<string, string> costs = makeDefaultCosts();
                return new ConfigFile
                {
                    situationalCosts = costs
                };
            }

            //generates a list of some basic items that seem qualified to be chosen as bounty stations
            private static Dictionary<string, string> makeDefaultCosts()
            {
                Dictionary<string, string> prices = new Dictionary<string, string>();
                prices.Add("baseCost", "0");
                prices.Add("noRaidAdditionalCosts", "100");
                prices.Add("purgeAdditionalCost", "50");
                return prices;
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

        private ConfigFile config;
        private ExpensiveBackpacks _instance;

        private static string freeAccessPerm = "ExpensiveBackpacks.Free";

        #endregion

        #region Init

        private void Init()
        {
            _instance = this;
            permission.RegisterPermission(freeAccessPerm, this);
            LoadConfig();
            if (Backpacks == null)
            {
                PrintWarning("Backpacks is not loaded. ExpensiveBackpacks will not be loaded either. Make sure Backpacks gets loaded and try reloading.");
                //TODO: somehow unload this plugin

            }
        }

        #endregion
    }
}
