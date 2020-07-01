using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Collections;

using UnityEngine;

using Oxide.Core;
using Oxide.Game.Rust.Cui;
using Oxide.Core.Plugins;
using Oxide.Core.Configuration;
using Newtonsoft.Json;
using Oxide.Core.Libraries.Covalence;
using Rust.Ai.HTN.Bear.Reasoners;

namespace Oxide.Plugins
{
    [Info("NoAdminLoot", "ashbo", "1.0.3")]
    [Description("Makes Admins unlootable and deletes loot of deceased, offline admins")]

    class NoAdminLoot : RustPlugin
    {
        object OnPlayerLanded(BasePlayer player, float num) {
            Item item = player.GetActiveItem();
            Console.WriteLine(player.displayName + " dropped " + item.GetOwnerPlayer() + "test");
            SendReply(player, item.name);
            return null;
        }

        // disallows looting of sleeping or wounded admins
        object CanLootPlayer(BasePlayer target, BasePlayer looter)
        {
            if (target != null && looter != null) {
                if (looter.IsAdmin)
                    return null;
                if (target.IsAdmin)
                {
                    SendReply(looter, "How dare you try to loot an admin, scum!");
                    return false;
                }
            }
            
            return null;
        }

        // empties inventory of dead admins
        void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            
            if (entity != null && info!=null&&!entity.IsNpc)
            {
                BasePlayer player = null;
                try
                {
                    player = entity.ToPlayer();
                }
                catch (Exception e)
                {
                }

                if (player!=null&&(player.userID > 76560000000000000L))
                {
                    if (!player.IsConnected)
                    {
                        if (player.IsAdmin)
                        {
                            foreach (var item in player.inventory.containerBelt.itemList)
                            {
                                item.Remove();
                            }
                            foreach (var item in player.inventory.containerMain.itemList)
                            {
                                item.Remove();
                            }
                            foreach (var item in player.inventory.containerWear.itemList)
                            {
                                item.Remove();
                            }
                        }
                    }
                }
            }
        }

        // disallows looting of dead admins
        object CanLootEntity(BasePlayer player, LootableCorpse corpse)
        {
            BasePlayer dead = BasePlayer.FindByID(corpse.playerSteamID);
            if (dead != null && player!=null) {
                if (player.IsAdmin)
                    return null;
                if (dead.IsAdmin)
                {
                    SendReply(player, "You are not qualified to loot an admin, scum!");
                    return false; }
            }
            
            return null;
        }

        // disallows looting of whatever remains of an admin
        object CanLootEntity(BasePlayer player, DroppedItemContainer container)
        {
            BasePlayer dead = BasePlayer.FindByID(container.playerSteamID);
            if (dead != null && player != null)
            {
                if (player.IsAdmin)
                    return null;
                if (dead.IsAdmin)
                {
                    SendReply(player, "You are not qualified to loot an admin, scum!");
                    return false;
                }
            }
            return null;
        }
    }
}
