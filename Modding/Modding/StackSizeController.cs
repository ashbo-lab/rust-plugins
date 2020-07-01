using System.Collections.Generic;
using System.Linq;
using System;
using UnityEngine;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Libraries;
using Oxide.Core;

namespace Oxide.Plugins
{
    [Info("Stack Size Controller", "Canopy Sheep", "2.0.1", ResourceId = 2320)]
    [Description("Allows you to set the max stack size of every item.")]
    public class StackSizeController : RustPlugin
    {
        #region Data

        Items items;
        class Items
        {
            public Dictionary<string, int> itemlist = new Dictionary<string, int>();
        }

        private void LoadData()
        {
            var itemsdatafile = Interface.Oxide.DataFileSystem.GetFile("StackSizeController");
            try
            {
                items = itemsdatafile.ReadObject<Items>();
            }
            catch
            {
                Puts("Error: Data file corrupt, regenerating...");
            }
        }

        private void UpdateItems()
        {
            var gameitemList = ItemManager.itemList;
            int stacksize;

            foreach (var item in gameitemList)
            {
                if (!(items.itemlist.ContainsKey(item.displayName.english)))
                {
                    stacksize = DetermineStack(item);
                    items.itemlist.Add(item.displayName.english, stacksize);
                }
            }

            List<string> KeysToRemove = new List<string>();
            bool foundItem = false;

            foreach (KeyValuePair<string, int> item in items.itemlist)
            {
                foreach (var itemingamelist in gameitemList)
                {
                    if (itemingamelist.displayName.english == item.Key)
                    {
                        foundItem = true;
                        break;
                    }
                }
                if (!(foundItem)) { KeysToRemove.Add(item.Key); }
                foundItem = false;
            }

            if (KeysToRemove.Count > 0) { Puts("Cleaning data file..."); }
            foreach (string key in KeysToRemove)
            {
                items.itemlist.Remove(key);
            }

            SaveData();
            LoadStackSizes();
        }

        private int DetermineStack(ItemDefinition item)
        {
            if (item.condition.enabled && item.condition.max > 0 && (!configData.Settings.StackHealthItems))
            {
                return 1;
            }
            else
            {
                if (configData.Settings.DefaultStack != 0 && (!configData.Settings.CategoryDefaultStack.ContainsKey(item.category.ToString())))
                {
                    return configData.Settings.DefaultStack;
                }
                else if (configData.Settings.CategoryDefaultStack.ContainsKey(item.category.ToString()) && configData.Settings.CategoryDefaultStack[item.category.ToString()] != 0)
                {
                    return configData.Settings.CategoryDefaultStack[item.category.ToString()];
                }
                else if (configData.Settings.DefaultStack != 0 && configData.Settings.CategoryDefaultStack[item.category.ToString()] == 0)
                {
                    return configData.Settings.DefaultStack;
                }
                else
                {
                    return item.stackable;
                }
            }
        }

        private void LoadStackSizes()
        {
            var gameitemList = ItemManager.itemList;

            foreach (var item in gameitemList)
            {
                item.stackable = items.itemlist[item.displayName.english];
            }
        }

        private void SaveData()
        {
            Interface.Oxide.DataFileSystem.WriteObject("StackSizeController", items);
        }

        #endregion

        #region Config

        ConfigData configData;
        class ConfigData
        {
            public SettingsData Settings { get; set; }
        }

        class SettingsData
        {
            public int DefaultStack { get; set; }
            public bool StackHealthItems { get; set; }
            public Dictionary<string, int> CategoryDefaultStack { get; set; }
        }

        private void TryConfig()
        {
            try
            {
                configData = Config.ReadObject<ConfigData>();
            }
            catch (Exception)
            {
                Puts("Corrupt config");
                LoadDefaultConfig();
            }
        }

        protected override void LoadDefaultConfig()
        {
            Puts("Generating a new config file...");

            Config.WriteObject(new ConfigData
            {
                Settings = new SettingsData
                {
                    DefaultStack = 0,
                    StackHealthItems = true,
                    CategoryDefaultStack = new Dictionary<string, int>()
                    {
                        { "Ammunition", 0 },
                        { "Attire", 0 },
                        { "Common", 0 },
                        { "Component", 0 },
                        { "Construction", 0 },
						{ "Electrical", 0 },
                        { "Food", 0 },
                        { "Items", 0 },
                        { "Medical", 0 },
                        { "Misc", 0 },
                        { "Resources", 0 },
                        { "Tools", 0 },
                        { "Traps", 0 },
                        { "Weapon", 0 },
                    },
                },
            }, true);
        }
        #endregion

        #region Hooks
        private void OnServerInitialized()
        {
            TryConfig();
            LoadData();
            UpdateItems();

            permission.RegisterPermission("stacksizecontroller.canChangeStackSize", this);
        }

        private bool hasPermission(BasePlayer player, string perm)
        {
            if (player.net.connection.authLevel > 1)
            {
                return true;
            }
            return permission.UserHasPermission(player.userID.ToString(), perm);
        }

        #endregion

        #region Commands
        [ChatCommand("stack")]
        private void StackCommand(BasePlayer player, string command, string[] args)
        {
            int stackAmount = 0;

            if (!hasPermission(player, "stacksizecontroller.canChangeStackSize"))
            {
                SendReply(player, "You don't have permission to use this command.");
                return;
            }

            if (args.Length <= 1)
            {
                SendReply(player, "Syntax Error: Requires 2 arguments. Syntax Example: /stack ammo.rocket.hv 64 (Use shortname)");
                return;
            }

            List<ItemDefinition> gameitems = ItemManager.itemList.FindAll(x => x.shortname.Equals(args[0]));

            if (gameitems.Count == 0)
            {
                SendReply(player, "Syntax Error: That is an incorrect item name. Please use a valid shortname.");
                return;
            }

            string replymessage = "";
            switch (args[1].ToLower())
            {
                case "default":
                {
                    stackAmount = DetermineStack(gameitems[0]);
                    replymessage = "Updated Stack Size for " + gameitems[0].displayName.english + " (" + gameitems[0].shortname + ") to " + stackAmount + " (Default value based on config).";
                    break;
                }
                default:
                {
                    if (int.TryParse(args[1], out stackAmount) == false)
                    {
                        SendReply(player, "Syntax Error: Stack Amount is not a number. Syntax Example: /stack ammo.rocket.hv 64 (Use shortname)");
                        return;
                    }
                    replymessage = "Updated Stack Size for " + gameitems[0].displayName.english + " (" + gameitems[0].shortname + ") to " + stackAmount + ".";
                    break;
                }
            }

            if (gameitems[0].condition.enabled && gameitems[0].condition.max > 0)
            {
                if (!(configData.Settings.StackHealthItems))
                {
                    SendReply(player, "Error: Stacking health items is disabled in the config.");
                    return;
                }
            }

            items.itemlist[gameitems[0].displayName.english] = Convert.ToInt32(stackAmount);
                
            gameitems[0].stackable = Convert.ToInt32(stackAmount);

            SaveData();

            SendReply(player, replymessage);
        }

        [ChatCommand("stackall")]
        private void StackAllCommand(BasePlayer player, string command, string[] args)
        {
            if (!hasPermission(player, "stacksizecontroller.canChangeStackSize"))
            {
                SendReply(player, "You don't have permission to use this command.");

                return;
            }

            if (args.Length == 0)
            {
                SendReply(player, "Syntax Error: Requires 1 argument. Syntax Example: /stackall 65000");

                return;
            }

            int stackAmount = 0;
            string replymessage = "";

            var itemList = ItemManager.itemList;

            foreach (var gameitem in itemList)
            {
                switch (args[0].ToLower())
                {
                    case "default":
                    {
                        stackAmount = DetermineStack(gameitem);
                        replymessage = "The Stack Size of all stackable items has been set to their default values (specified in config).";
                        break;
                    }
                    default:
                    {
                        if (int.TryParse(args[0], out stackAmount) == false)
                        {
                            SendReply(player, "Syntax Error: Stack Amount is not a number. Syntax Example: /stackall 65000");
                            return;
                        }
                        replymessage = "The Stack Size of all stackable items has been set to " + stackAmount.ToString() + ".";
                        break;
                    }
                }

                if (gameitem.condition.enabled && gameitem.condition.max > 0 && !(configData.Settings.StackHealthItems)) { continue; }
                if (gameitem.displayName.english.ToString() == "Salt Water" || gameitem.displayName.english.ToString() == "Water") { continue; }

                items.itemlist[gameitem.displayName.english] = Convert.ToInt32(stackAmount);
                gameitem.stackable = Convert.ToInt32(stackAmount);
            }

            SaveData();

            SendReply(player, replymessage);
        }

        [ConsoleCommand("stack")]
        private void StackConsoleCommand(ConsoleSystem.Arg arg)
        {
            int stackAmount = 0;

            if (arg.IsAdmin != true) { return; }

            if (arg.Args == null)
            {
                Puts("Syntax Error: Requires 2 arguments. Syntax Example: stack ammo.rocket.hv 64 (Use shortname)");

                return;
            }

            if (arg.Args.Length <= 1)
            {
                Puts("Syntax Error: Requires 2 arguments. Syntax Example: stack ammo.rocket.hv 64 (Use shortname)");

                return;
            }

            List<ItemDefinition> gameitems = ItemManager.itemList.FindAll(x => x.shortname.Equals(arg.Args[0]));

            if (gameitems.Count == 0)
            {
                Puts("Syntax Error: That is an incorrect item name. Please use a valid shortname.");
                return;
            }

            string replymessage = "";
            switch (arg.Args[1].ToLower())
            {
                case "default":
                {
                    stackAmount = DetermineStack(gameitems[0]);
                    replymessage = "Updated Stack Size for " + gameitems[0].displayName.english + " (" + gameitems[0].shortname + ") to " + stackAmount + " (Default value based on config).";
                    break;
                }
                default:
                {
                    if (int.TryParse(arg.Args[1], out stackAmount) == false)
                    {
                        Puts("Syntax Error: Stack Amount is not a number. Syntax Example: /stack ammo.rocket.hv 64 (Use shortname)");
                        return;
                    }
                    replymessage = "Updated Stack Size for " + gameitems[0].displayName.english + " (" + gameitems[0].shortname + ") to " + stackAmount + ".";
                    break;
                }
            }

            if (gameitems[0].condition.enabled && gameitems[0].condition.max > 0)
            {
                if (!(configData.Settings.StackHealthItems))
                {
                    Puts("Error: Stacking health items is disabled in the config.");
                    return;
                }
            }

            items.itemlist[gameitems[0].displayName.english] = Convert.ToInt32(stackAmount);

            gameitems[0].stackable = Convert.ToInt32(stackAmount);

            SaveData();

            Puts(replymessage);
        }

        [ConsoleCommand("stackall")]
        private void StackAllConsoleCommand(ConsoleSystem.Arg arg)
        {
            if (arg.IsAdmin != true) { return; }

            if (arg.Args.Length == 0)
            {
                Puts("Syntax Error: Requires 1 argument. Syntax Example: stackall 65000");

                return;
            }

            int stackAmount = 0;
            string replymessage = "";

            var itemList = ItemManager.itemList;

            foreach (var gameitem in itemList)
            {
                if (gameitem.condition.enabled && gameitem.condition.max > 0 && (!(configData.Settings.StackHealthItems))) { continue; }
                if (gameitem.displayName.english.ToString() == "Salt Water" ||
                gameitem.displayName.english.ToString() == "Water") { continue; }

                switch (arg.Args[0].ToLower())
                {
                    case "default":
                    {
                        stackAmount = DetermineStack(gameitem);
                        replymessage = "The Stack Size of all stackable items has been set to their default values (specified in config).";
                        break;
                    }
                    default:
                    {
                        if (int.TryParse(arg.Args[0], out stackAmount) == false)
                        {
                            Puts("Syntax Error: Stack Amount is not a number. Syntax Example: /stackall 65000");
                            return;
                        }
                        replymessage = "The Stack Size of all stackable items has been set to " + stackAmount.ToString() + ".";
                        break;
                    }
                }

                items.itemlist[gameitem.displayName.english] = Convert.ToInt32(stackAmount);
                gameitem.stackable = Convert.ToInt32(stackAmount);
            }

            SaveData();

            Puts(replymessage);
        }
        #endregion

        #region Plugin References
        [PluginReference("FurnaceSplitter")]
        private Plugin FurnaceSplitter;
        #endregion
    }
}