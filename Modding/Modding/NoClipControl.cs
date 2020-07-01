using System;
using System.IO;
using System.Collections.Generic;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;
using ProtoBuf;
using Facepunch;
using System.Windows.Forms;

namespace Oxide.Plugins
{
    [Info("NoClipControl", "Oegge", "0.0.1")]
    [Description("Give people some leeway when they first join the game.")]
    public class NoClipControl : CovalencePlugin
    {
        [PluginReference]
        Plugin  NoEscape;

      
        
        #region Config
        private ConfigFile config;

        private class ConfigFile
        {
            //List of IDs of all items that may be chosen as bounty stations. (You might want to choose stations, players can put items into. Just sayin')
            public Dictionary<string, string> allowedStationItems;
            public static ConfigFile DefaultConfig()
            {

                return new ConfigFile
                {

                };
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

        public void noClip()
        {
            BuildingManager m = null;
            var a=m.GetBuilding(1);
            var b=a.ID;
            var c=a.buildingPrivileges;

        }

    }
}