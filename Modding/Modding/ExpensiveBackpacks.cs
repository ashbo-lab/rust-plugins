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
using System.Transactions;
using System.ComponentModel.Design;

namespace Oxide.Plugins
{
    [Info("ExpensiveBackpacks", "ashbo", "1.0.0")]
    [Description("Enables you to charge ServerRewards units for acessing backpacks")]
    class ExpensiveBackpacks : CovalencePlugin
    {
        [PluginReference] private Plugin ServerRewards, NoEscape, Backpacks, Raids;

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
                prices.Add("raidBlockedAdditionalCosts", "100");
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
                PrintWarning("Backpacks is not loaded. ExpensiveBackpacks will not do anything.");
            }
        }

        #endregion

        #region Hooks
        //calls Hook in Backpacks. Only lets you open your backpack if you can pay for it.
        private string CanOpenBackpack(BasePlayer player, ulong backpackOwnerID) {
            if(player.userID!= backpackOwnerID) return null;

            IPlayer iplayer = players.FindPlayerById(backpackOwnerID.ToString());
            int price = calculatePrice(iplayer);

            if (takeGold(iplayer, price))
            {
                iplayer.Reply(price + " gold have been deducted from your account for accessing your backpack.");
                return null;
            }

            return "Opening your backpack now would cost " + price + " gold. Unfortunately, you're not that rich.";
        }


        #endregion

        #region Helper

        private int calculatePrice(IPlayer player) {
            int price = Int32.Parse(config.situationalCosts["baseCost"]);
            if (NoEscape!=null&&(bool)NoEscape.Call("IsRaidBlocked", player.Id)) { 
                price += Int32.Parse(config.situationalCosts["raidBlockAdditionalCost"]);
            }
            if (Raids != null && (bool)Raids.Call("getRaid")) { 
                price += Int32.Parse(config.situationalCosts["purgeAdditionalCost"]);
            }
            return price;
        }

        // takes gold from the player if he has enough gold. Returns true on success.
        private bool takeGold(IPlayer player, int gold)
        {
            int credit = (int)ServerRewards.Call("CheckPoints", player);
            if (credit < gold)
                return false;
            ServerRewards.Call("TakePoints", player, gold);
            return true;
        }

        #endregion
        
    }
}
