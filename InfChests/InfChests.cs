﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Terraria;
using Terraria.IO;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.Hooks;
using System.Linq;

namespace InfChests
{
	[ApiVersion(1,23)]
	public class InfChests : TerrariaPlugin
	{
		#region Plugin Info
		public override string Name { get { return "InfiniteChests"; } }
		public override string Author { get { return "Zaicon"; } }
		public override string Description { get { return "A server-sided chest manager."; } }
		public override Version Version { get { return Assembly.GetExecutingAssembly().GetName().Version; } }

		public InfChests(Main game)
			: base(game)
		{

		}
		#endregion

		#region Init/Dispose
		public override void Initialize()
		{
			ServerApi.Hooks.GameInitialize.Register(this, onInitialize);
			ServerApi.Hooks.GamePostInitialize.Register(this, onWorldLoaded);
			ServerApi.Hooks.NetGetData.Register(this, onGetData);
			ServerApi.Hooks.ServerLeave.Register(this, onLeave);
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				ServerApi.Hooks.GameInitialize.Deregister(this, onInitialize);
				ServerApi.Hooks.GamePostInitialize.Deregister(this, onWorldLoaded);
				ServerApi.Hooks.NetGetData.Deregister(this, onGetData);
				ServerApi.Hooks.ServerLeave.Deregister(this, onLeave);
			}
			base.Dispose(disposing);
		}
		#endregion

		private static Dictionary<int, Data> playerData = new Dictionary<int, Data>();

		#region Hooks
		private void onInitialize(EventArgs args)
		{
			DB.Connect();
			for (int i = 0; i < TShock.Players.Length; i++)
			{
				playerData.Add(i, new Data());
			}

			Commands.ChatCommands.Add(new Command("infchests.chest.use", ChestCMD, "chest"));
		}

		private void onWorldLoaded(EventArgs args)
		{
			int converted = 0;
			for (int i = 0; i < Main.chest.Length; i++)
			{
				Chest chest = Main.chest[i];
				if (chest != null)
				{
					InfChest ichest = new InfChest()
					{
						items = chest.item,
						x = chest.x,
						y = chest.y,
						userid = -1
					};

					DB.addChest(ichest);
					converted++;
				}
				Main.chest[i] = null;
			}
			if (converted > 0)
			{
				TSPlayer.Server.SendInfoMessage("[InfChests] Converted " + converted + " chest(s).");
				WorldFile.saveWorld();
			}
		}

		private async void onGetData(GetDataEventArgs args)
		{
			using (var reader = new BinaryReader(new MemoryStream(args.Msg.readBuffer, args.Index, args.Length)))
			{
				switch (args.MsgID)
				{
					case PacketTypes.ChestGetContents: //31 GetContents
						short tilex = reader.ReadInt16();
						short tiley = reader.ReadInt16();
						await Task<bool>.Factory.StartNew( () => getChestContents(args.Msg.whoAmI, tilex, tiley));
						args.Handled = true;
						break;
					case PacketTypes.ChestItem: //22 ChestItem
						short chestid = reader.ReadInt16();
						byte itemslot = reader.ReadByte();
						short stack = reader.ReadInt16();
						byte prefix = reader.ReadByte();
						short itemid = reader.ReadInt16();
						Chest chest = (Chest)Main.chest[chestid].Clone();
						chest.item[itemslot] = new Item();
						chest.item[itemslot].SetDefaults(itemid);
						chest.item[itemslot].stack = stack;
						chest.item[itemslot].prefix = prefix;
						if (!DB.setItems(playerData[args.Msg.whoAmI].dbid, chest.item))
							TShock.Log.Error("Error updating items in DB.");
						break;
					case PacketTypes.ChestOpen: //33 SetChestName
						chestid = reader.ReadInt16();
						tilex = reader.ReadInt16();
						tiley = reader.ReadInt16();
						if (Main.tile[tilex, tiley].frameY % 36 != 0)
							tiley--;
						if (Main.tile[tilex, tiley].frameX % 36 != 0)
							tilex--;
						
						if (chestid == -1)
						{
							playerData[args.Msg.whoAmI].dbid = -1;
							Main.chest[playerData[args.Msg.whoAmI].mainid] = null;
							playerData[args.Msg.whoAmI].mainid = -1;
							NetMessage.SendData((int)PacketTypes.SyncPlayerChestIndex, -1, args.Msg.whoAmI, "", args.Msg.whoAmI, -1);
							args.Handled = true;
						}
						else
						{
							args.Handled = true;
							TShock.Log.ConsoleError("Unhandled ChestOpen packet.");
						}
						break;
					case PacketTypes.TileKill:
						byte action = reader.ReadByte(); // 0 placechest, 1 killchest, 2 placedresser, 3 killdresser
						tilex = reader.ReadInt16();
						tiley = reader.ReadInt16();
						short style = reader.ReadInt16();
						int chestnum = -1;
						if (action == 0 || action == 2)
						{
							if (TShock.Regions.CanBuild(tilex, tiley, TShock.Players[args.Msg.whoAmI]))
							{
								chestnum = WorldGen.PlaceChest(tilex, tiley, type: action == 0 ? (ushort)21 : (ushort)88, style: style);
								if (chestnum == -1)
									break;
								NetMessage.SendTileSquare(args.Msg.whoAmI, Main.chest[chestnum].x, Main.chest[chestnum].y, 3);

								DB.addChest(new InfChest()
								{
									items = Main.chest[chestnum].item,
									userid = TShock.Players[args.Msg.whoAmI].HasPermission("infchests.chest.protect") ? TShock.Players[args.Msg.whoAmI].User.ID : -1,
									x = Main.chest[chestnum].x,
									y = Main.chest[chestnum].y
								});
								Main.chest[chestnum] = null;
							}
							args.Handled = true;
						}
						else
						{
							if (TShock.Regions.CanBuild(tilex, tiley, TShock.Players[args.Msg.whoAmI]) && (Main.tile[tilex, tiley].type == 21 || Main.tile[tilex, tiley].type == 88))
							{
								if (Main.tile[tilex, tiley].frameY % 36 != 0)
									tiley--;
								if (Main.tile[tilex, tiley].frameX % 36 != 0)
									tilex--;

								InfChest chest2 = DB.getChest(tilex, tiley);
								TSPlayer player = TShock.Players[args.Msg.whoAmI];
								if (chest2 == null)
								{
									WorldGen.KillTile(tilex, tiley);
								}
								else if (chest2.userid != -1 && !player.HasPermission("infchests.admin.editall") && chest2.userid != player.User.ID)
								{
									player.SendErrorMessage("This chest is protected.");
								}
								else if(chest2.items.Any(p => p.type != 0))
								{
									
								}
								else
								{
									WorldGen.KillTile(tilex, tiley);
									DB.removeChest(chest2.id);
								}
								player.SendTileSquare(tilex, tiley, 3);
								args.Handled = true;
							}
						}
						break;
					case PacketTypes.ChestName:
						args.Handled = true;
						break;
					case PacketTypes.ForceItemIntoNearestChest:
						byte invslot = reader.ReadByte();

						break;
				}
			}
		}

		private void onLeave(LeaveEventArgs args)
		{
			playerData[args.Who] = new Data();
		}
		#endregion

		#region ChestActions
		private bool getChestContents(int index, short tilex, short tiley)
		{
			InfChest chest = DB.getChest(tilex, (short)(tiley));
			TSPlayer player = TShock.Players[index];

			if (chest == null)
			{
				WorldGen.KillTile(tilex, tiley);
				TSPlayer.All.SendData(PacketTypes.Tile, "", 0, tilex, tiley + 1);
				player.SendWarningMessage("This chest was corrupted.");
				playerData[index].action = chestAction.none;
				return true;
			}

			if (playerData.Values.Any(p => p.dbid == chest.id))
			{
				player.SendErrorMessage("This chest is in use.");
				playerData[index].action = chestAction.none;
                return true;
            }

			switch (playerData[index].action)
			{
				case chestAction.info:
					player.SendInfoMessage($"X: {chest.x} | Y: {chest.y}");
					string owner = chest.userid == -1 ? "(None)" : TShock.Users.GetUserByID(chest.userid).Name;
					player.SendInfoMessage($"Chest Owner: {owner}");
					break;
				case chestAction.setPassword:
					if (chest.userid != player.User.ID && !player.HasPermission("infchests.admin.editall"))
					{
						player.SendErrorMessage("This chest is not yours.");
					}
					else
					{
						if (DB.setPassword(chest.id, playerData[index].password))
						{
							player.SendSuccessMessage("Set chest password to `" + playerData[index].password + "`.");
						}
						else
						{
							player.SendErrorMessage("An error occured.");
							TShock.Log.Error("Error setting chest password.");
						}
					}
					break;
				case chestAction.protect:
					if (chest.userid == player.User.ID)
						player.SendErrorMessage("This chest is already claimed by you!");
					else if (chest.userid != -1 && !player.HasPermission("infchests.admin.editall"))
						player.SendErrorMessage("This chest is already claimed by someone else!");
					else
					{
						if (DB.setUserID(chest.id, player.User.ID))
							player.SendSuccessMessage("This chest is now claimed by you!");
						else
						{
							player.SendErrorMessage("An error occured.");
							TShock.Log.Error("Error setting chest protection.");
						}
					}
					break;
				case chestAction.unProtect:
					if (chest.userid != player.User.ID && !player.HasPermission("infchests.admin.editall"))
						player.SendErrorMessage("This chest is not yours!");
					else if (chest.userid == -1)
						player.SendErrorMessage("This chest is not claimed!");
					else
					{
						if (DB.setUserID(chest.id, -1))
							player.SendSuccessMessage("This chest is no longer claimed.");
						else
						{
							player.SendErrorMessage("An error occured.");
							TShock.Log.Error("Error setting chest un-protection.");
						}
					}
					break;
				case chestAction.none:
					if (chest.userid != -1 && !player.IsLoggedIn)
						player.SendErrorMessage("You must be logged in to use this chest.");
					else if (chest.userid != -1 && !player.HasPermission("infchests.admin.editall") && chest.userid != player.User.ID && chest.password != playerData[index].password)
					{
						if (chest.password != string.Empty)
							player.SendErrorMessage("This chest is password protected.");
						else
							player.SendErrorMessage("This chest is protected.");
					}
					else
					{
						int chestindex = Chest.FindEmptyChest(tilex, tiley);
						if (chestindex == -1)
							throw new Exception("No empty chests!");

						Main.chest[chestindex] = new Chest()
						{
							item = chest.items,
							x = chest.x,
							y = chest.y
						};
						playerData[index].dbid = chest.id;
						playerData[index].mainid = chestindex;

						for (int i = 0; i < Main.chest[chestindex].item.Length; i++)
						{
							player.SendData(PacketTypes.ChestItem, "", chestindex, i, Main.chest[chestindex].item[i].stack, Main.chest[chestindex].item[i].prefix, Main.chest[chestindex].item[i].type);
						}
						player.SendData(PacketTypes.ChestOpen, "", chestindex, Main.chest[chestindex].x, Main.chest[chestindex].y, Main.chest[chestindex].name.Length);
						NetMessage.SendData((int)PacketTypes.SyncPlayerChestIndex, -1, index, "", index, chestindex);
					}
					break;
			}
			playerData[index].action = chestAction.none;
			
			return true;
		}
		#endregion

		#region Chest Command
		private void ChestCMD(CommandArgs args)
		{
			if (args.Parameters.Count == 0 || args.Parameters[0].ToLower() == "help")
			{
				args.Player.SendErrorMessage("Invalid syntax:");
				args.Player.SendErrorMessage("/chest <claim/unclaim>");
				args.Player.SendErrorMessage("/chest info");
				args.Player.SendErrorMessage("/chest password <password>");
				args.Player.SendErrorMessage("/chest unlock <password>");
				args.Player.SendErrorMessage("/chest cancel");
				return;
			}

			switch (args.Parameters[0].ToLower())
			{
				case "claim":
					args.Player.SendInfoMessage("Open a chest to claim it.");
					playerData[args.Player.Index].action = chestAction.protect;
					break;
				case "unclaim":
					args.Player.SendInfoMessage("Open a chest to unclaim it.");
					playerData[args.Player.Index].action = chestAction.unProtect;
					break;
				case "info":
					args.Player.SendInfoMessage("Open a chest to get information about it.");
					playerData[args.Player.Index].action = chestAction.info;
					break;
				case "password":
					if (args.Parameters.Count == 1)
					{
						args.Player.SendInfoMessage("Open a chest to remove its password.");
						playerData[args.Player.Index].password = "";
					}
					else
					{
						string psw = string.Join(" ", args.Parameters.GetRange(1, args.Parameters.Count - 1));
						args.Player.SendInfoMessage("Open a chest to set its password.");
						playerData[args.Player.Index].password = psw;
					}
					playerData[args.Player.Index].action = chestAction.setPassword;
					break;
				case "unlock":
					if (args.Parameters.Count == 1)
						args.Player.SendErrorMessage("Invalid syntax: /chest unlock <password>");
					else
					{
						playerData[args.Player.Index].password = string.Join(" ", args.Parameters.GetRange(1, args.Parameters.Count - 1));
						args.Player.SendSuccessMessage("You can now unlock chests that use this password.");
					}
					break;
				case "cancel":
					playerData[args.Player.Index].action = chestAction.none;
					args.Player.SendInfoMessage("Canceled chest action.");
					break;
			}
		}
		#endregion
	}

	public enum chestAction
	{
		none,
		info,
		protect,
		unProtect,
		//togglePublic,
		//setRefill
		setPassword
	}
}