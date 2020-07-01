using Oxide.Core;
using UnityEngine;
using System;

namespace Oxide.Plugins
{
    [Info("Tequila Safezone", "Faktura", "0.0.1")]
    [Description("Prevents damage to/from players < 0 y position on map")]

    class TequilaSafezone : RustPlugin
    {
        private const string AdminPermission = "TequilaSafezone.Ignore";
        private void Init()
        {
		
        }
        private static TequilaSafezone _instance;

        private void Loaded()
        {
            _instance = this;

            permission.RegisterPermission(AdminPermission, this);
        }

        private object OnEntityTakeDamage(BasePlayer victim, HitInfo info)
        {
          
            if (victim is BasePlayer){
				if(info?.Initiator is BasePlayer){
                 // Console.WriteLine(victim.userID);
					var attacker = info?.Initiator?.ToPlayer();
                    if (permission.UserHasPermission(attacker.UserIDString, AdminPermission) || permission.UserHasPermission(victim.UserIDString, AdminPermission))
                    {
                        return null; }


                    if (attacker && attacker.userID != victim.userID && attacker.userID >= 76560000000000000L && victim.userID >= 76560000000000000L && !victim.IsSleeping() &&
                        (victim.transform.position.z > 0 || attacker.transform.position.z > 0)){
						PrintToChat(attacker, "Attacking players is not permitted in the South");
                        PrintToChat(victim, "you are safe in the south");
                        return true;
					}
				}
			}
            //
            return null;
        }
    }
}