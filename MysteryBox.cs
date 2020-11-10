using System.Collections.Generic;
using System.Collections;
using System.Linq;
using Oxide.Game.Rust.Cui;
using Oxide.Core;
using UnityEngine;
using Newtonsoft.Json;
using Random = System.Random;

namespace Oxide.Plugins
{
    [Info("Mystery Box", "Bazz3l", "1.0.4")]
    [Description("Unlock mystery boxes with random loot items.")]
    public class MysteryBox : RustPlugin
    {
        #region Fields
        
        private const string SuccessPrefab = "assets/prefabs/deployable/research table/effects/research-success.prefab";
        private const string StartPrefab = "assets/prefabs/deployable/repair bench/effects/skinchange_spraypaint.prefab";
        private const string PermName = "mysterybox.use";
        
        private readonly List<BoxController> _controllers = new List<BoxController>();
        private readonly List<LootItem> _lootItems = new List<LootItem>();
        private StoredData _stored;
        private PluginConfig _config;
        private static MysteryBox _instance;

        #endregion

        #region Storage
        
        private class StoredData
        {
            public Dictionary<ulong, int> Players = new Dictionary<ulong, int>();
        }

        private void LoadData()
        {
            _stored = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name);
        }

        private void SaveData()
        {
            Interface.Oxide.DataFileSystem.WriteObject(Name, _stored);
        }
        
        private void ClearData()
        {
            _stored.Players.Clear();

            SaveData();
        }
        
        #endregion

        #region Config
        
        protected override void LoadDefaultConfig() => _config = new PluginConfig();

        protected override void LoadConfig()
        {
            base.LoadConfig();

            try
            {
                _config = Config.ReadObject<PluginConfig>();

                if (_config == null)
                {
                    throw new JsonException();
                }

                foreach (ItemDefinition item in ItemManager.itemList)
                {
                    if (_config.RewardItems.Find(x => x.Shortname == item.shortname) != null) continue;
                    
                    switch (item.category)
                    {
                        case ItemCategory.Weapon:
                            _config.RewardItems.Add(new LootItem
                            {
                                Shortname = item.shortname, 
                                MinAmount = 1, 
                                MaxAmount = 1
                            });
                            break;
                        case ItemCategory.Ammunition:
                            
                            if (item.shortname.Contains("rocket"))
                                _config.RewardItems.Add(new LootItem
                                {
                                    Shortname = item.shortname, 
                                    MinAmount = 2, 
                                    MaxAmount = 3
                                });
                            else
                                _config.RewardItems.Add(new LootItem
                                {
                                    Shortname = item.shortname,
                                    MinAmount = 60, 
                                    MaxAmount = 128
                                });

                            break;
                    }
                }

                PrintToConsole($"New config created {Name}.json.");
            }
            catch
            {
                LoadDefaultConfig();

                PrintError("The configuration file contains an error and has been replaced with a default config.\n" + "The error configuration file was saved in the .jsonError extension");
            }
            
            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(_config);

        private class PluginConfig
        {
            public string ImageURL = "https://i.imgur.com/fCJrUYL.png";
            public bool WipeOnNewSave = true;
            public readonly List<LootItem> RewardItems = new List<LootItem>();
        }

        private class LootItem
        {
            public string Shortname;
            public int MinAmount;
            public int MaxAmount;
            public bool Hide;
        }
        
        #endregion

        #region Oxide
        
        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string> {
                { "InvalidSyntax", "Invalid syntax: give.mysterybox <steamid> <amount>" },
                { "NoInventory", "No inventory space." },
                { "NoPermission", "No permission." },
                { "NoBoxes", "You have 0 boxes left." },
                { "NoPlayer", "No player found." },
                { "Wiped", "Data cleared." },
                { "Award", "Mystery boxes awarded." },
                { "Reward", "You have been rewarded {0} mystery boxes, /mb" },
                { "Give", "You have received a mystery box" },
                { "Given", "{0} was given {1} boxes." },
                { "Error", "Something went wrong." }
            }, this);
        }

        private void OnServerInitialized()
        {
            permission.RegisterPermission(PermName, this);

            if (!_config.WipeOnNewSave)
            {
                Unsubscribe("OnNewSave");
            }

            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                OnPlayerConnected(player);
            }

            foreach (LootItem lootItem in _config.RewardItems)
            {
                if (lootItem.Hide) continue;
                
                _lootItems.Add(lootItem);
            }
        }

        private void Init()
        {
            _instance = this;

            ItemManager.Initialize();
            LoadConfig();
            LoadData();
        }

        private void Unload()
        {
            for (int i = 0; i < _controllers.Count; i++)
            {
                _controllers[i]?.Destroy();
            }

            SaveData();

            _instance = null;
        }

        private void OnNewSave() => ClearData();

        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null)
            {
                return;
            }

            if (player.IsReceivingSnapshot)
            {
                timer.Once(3f, () => OnPlayerConnected(player));
                return;
            }

            BoxController controller = FindController(player);
            if (controller != null)
            {
                return;
            }

            _controllers.Add(new BoxController(player));
        }

        private void OnPlayerDisconnected(BasePlayer player)
        {
            BoxController controller = FindController(player);
            if (controller == null)
            {
                return;
            }

            controller.Destroy();

            _controllers.Remove(controller);
        }
        
        private object OnItemAction(Item item, string action, BasePlayer player)
        {
            if (item.skin != 879416808 || action != "open")
            {
                return null;
            }

            BoxController container = FindController(player);
            if (container == null || !container.CanShow() || container.IsOpened)
            {
                return null;
            }

            item.RemoveFromContainer();
            item.Remove();

            container.Show();

            return false;
        }

        private void OnItemRemovedFromContainer(ItemContainer itemContainer, Item item)
        {
            if (item.parentItem != null)
            {
                return;
            }

            BoxController controller = FindController(itemContainer);
            BasePlayer player = itemContainer.GetOwnerPlayer();
            if (controller == null || player == null)
            {
                return;
            }

            controller.Clear();
        }

        private void OnPlayerLootEnd(PlayerLoot loot)
        {
            BasePlayer player = loot.gameObject.GetComponent<BasePlayer>();
            if (player != loot.entitySource)
            {
                return;
            }

            FindController(player)?.Close();
        }

        private object CanLootPlayer(BasePlayer looter, UnityEngine.Object target)
        {
            if (looter != target)
            {
                return null;
            }

            BoxController controller = FindController(looter);
            if (controller == null || !controller.IsOpened)
            {
                return null;
            }

            return true;
        }

        private object CanMoveItem(Item item, PlayerInventory playerInventory, uint containerId, int slot, int amount)
        {
            BoxController controllerFrom = FindController(item.parent);
            BoxController controllerTo = FindController(containerId);
            if ((controllerFrom ?? controllerTo) == null)
            {
                return null;
            }

            if (item.parent?.uid == containerId)
            {
                return false;
            }

            return CanMoveItemTo(controllerTo, item, slot, amount);
        }

        private object CanMoveItemTo(BoxController controller, Item item, int slot, int amount)
        {
            Item targetItem = controller?.Container?.GetSlot(slot);
            controller?.GiveItem(targetItem);
            controller?.Clear();

            return null;
        }
        
        #endregion

        #region Core
        
        private class BoxController
        {
            private const int MAX_TICKS = 50;
            public readonly ItemContainer Container;
            public BasePlayer Player;
            public bool IsOpened;
            private bool IsReady;
            private int _ticks;
            private Coroutine _coroutine;

            public BoxController(BasePlayer player)
            {
                Player = player;

                Container = new ItemContainer
                {
                    entityOwner = Player,
                    capacity = 1,
                    isServer = true,
                    allowedContents = ItemContainer.ContentsType.Generic
                };

                Container.GiveUID();
            }

            public void Show()
            {
                IsOpened = true;

                ResetTick();

                Container.SetLocked(true);

                PlayerLoot loot = Player.inventory.loot;
                loot.Clear();
                loot.PositionChecks = false;
                loot.entitySource = Player;
                loot.itemSource = null;
                loot.AddContainer(Container);
                loot.SendImmediate();

                Player.ClientRPCPlayer(null, Player, "RPC_OpenLootPanel", "generic");

                StartCoroutine();
            }

            public void Close()
            {
                if (IsOpened && IsReady && _instance._stored.Players.ContainsKey(Player.userID))
                {
                    GiveItem();
                }
                
                if (IsOpened && !IsReady)
                {
                    _instance.GivePlayerBox(Player);
                }

                Clear();

                IsOpened = false;
                IsReady = false;

                StopCoroutine();
            }

            public void Destroy()
            {
                Close();

                Container.Kill();
            }
            
            public void Clear()
            {
                for (int i = 0; i < Container.itemList.Count; i++)
                {
                    Item item = Container.itemList[i];
                    
                    if (item == null) continue;
                    
                    RemoveItem(item);
                }
            }
            
            private void StartCoroutine()
            {
                PlayEffect(StartPrefab);

                _coroutine = CommunityEntity.ServerInstance.StartCoroutine(LootSpinTick());
            }
            
            private void StopCoroutine()
            {
                if (_coroutine == null)
                {
                    return;
                }
                
                CommunityEntity.ServerInstance.StopCoroutine(_coroutine);
                
                _coroutine = null;
            }

            private void ResetTick()
            {
                _ticks = 0;
            }

            private void LootTick()
            {
                _ticks++;
                
                RandomItem();
            }

            private IEnumerator LootSpinTick()
            {
                while (_ticks < MAX_TICKS)
                {
                    LootTick();

                    yield return new WaitForSeconds(0.1f);
                }

                LootSpinEnd();
            }
            
            private void LootSpinEnd()
            {
                IsReady = true;

                Container.SetLocked(false);

                PlayEffect(SuccessPrefab);
            }

            private void RandomItem()
            {
                Clear();

                LootItem rewardItem = _instance._lootItems.GetRandom();
                if (rewardItem == null)
                {
                    return;
                }

                ItemManager.CreateByName(rewardItem.Shortname, UnityEngine.Random.Range(rewardItem.MinAmount, rewardItem.MaxAmount))?.MoveToContainer(Container);
            }
            
            public void GiveItem(Item itemDefault = null)
            {
                if (!ValidContainer())
                {
                    return;
                }

                Item item = itemDefault ?? Container.GetSlot(0);
                if (item == null)
                {
                    return;
                }

                Player.GiveItem(item);
            }

            private void RemoveItem(Item item)
            {
                item.RemoveFromContainer();
                item.Remove();
            }
            
            public bool CanShow() => CanShow(Player);

            private bool CanShow(BasePlayer player) => player != null && !player.IsDead();

            private bool ValidContainer() => Player == null || Container?.itemList != null;

            private void PlayEffect(string prefab) => EffectNetwork.Send(new Effect(prefab, Player.transform.position, Vector3.zero), Player.net.connection);
        }

        private int GetPlayerBoxes(BasePlayer player)
        {
            EnsurePlayerBoxesKey(player.userID);

            return _stored.Players[player.userID];
        }

        private void SetPlayerBoxes(BasePlayer player, int giveAmount)
        {
            EnsurePlayerBoxesKey(player.userID);

            _stored.Players[player.userID] += giveAmount;

            player.ChatMessage(Lang("Reward", player.UserIDString, giveAmount));
        }

        private void EnsurePlayerBoxesKey(ulong userID, int defaultValue = 0)
        {
            if (!_stored.Players.ContainsKey(userID))
            {
                _stored.Players[userID] = defaultValue;
            }
        }
        
        private void GivePlayerBox(BasePlayer player)
        {
            Item item = ItemManager.CreateByName("wrappedgift", 1, 879416808);
            if (item == null)
            {
                return;
            }

            item.name = "Mystery Box";
            item.MarkDirty();

            player.GiveItem(item);
        }

        private bool LimitReached(BasePlayer player) => GetPlayerBoxes(player) <= 0;
        
        private BoxController FindController(ItemContainer container) => _controllers.Find(x => x.Container != null && x.Container == container);
        private BoxController FindController(BasePlayer player) => _controllers.Find(x => player != null && player == x.Player);
        private BoxController FindController(uint id) => _controllers.Find(x => x.Container != null && x.Container.uid == id);
        
        #endregion

        #region Commands
        
        [ChatCommand("mb")]
        private void MysteryCommand(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player))
            {
                player.ChatMessage(Lang("NoPermission", player.UserIDString));
                return;
            }

            if (LimitReached(player))
            {
                player.ChatMessage(Lang("NoBoxes", player.UserIDString));
                return;
            }

            if (player.inventory.containerBelt.IsFull() || player.inventory.containerMain.IsFull())
            {
                player.ChatMessage(Lang("NoInventory", player.UserIDString));
                return;
            }

            GivePlayerBox(player);
            
            _stored.Players[player.userID]--;
            
            SaveData();

            player.ChatMessage(Lang("Give", player.UserIDString));
        }

        [ConsoleCommand("mysterybox.give")]
        private void GiveBox(ConsoleSystem.Arg arg)
        {
            if (!arg.IsRcon)
            {
                arg.ReplyWith(Lang("NoPermission"));
                return;
            }

            if (!arg.HasArgs(2))
            {
                arg.ReplyWith(Lang("InvalidSyntax"));
                return;
            }

            BasePlayer target = BasePlayer.Find(arg.GetString(0));
            if (target == null)
            {
                arg.ReplyWith(Lang("NoPlayer"));
                return;
            }

            int amount = arg.GetInt(1);

            SetPlayerBoxes(target, amount);
            
            SaveData();

            arg.ReplyWith(Lang("Given", target.UserIDString, target.userID, amount));
        }
        
        [ConsoleCommand("mysterybox.award")]
        private void AwardBoxes(ConsoleSystem.Arg arg)
        {
            if (!arg.IsRcon)
            {
                arg.ReplyWith(Lang("NoPermission"));
                return;
            }

            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                SetPlayerBoxes(player, 1);
            }
            
            SaveData();
            
            arg.ReplyWith(Lang("Award"));
        }

        [ConsoleCommand("mysterybox.wipe")]
        private void WipeBoxes(ConsoleSystem.Arg arg)
        {
            if (!arg.IsRcon)
            {
                arg.ReplyWith(Lang("NoPermission"));
                return;
            }

            ClearData();

            arg.ReplyWith(Lang("Wiped"));
        }
        
        #endregion

        #region Helpers
        
        private string Lang(string key, string id = null, params object[] args) => string.Format(lang.GetMessage(key, this, id), args);

        private bool HasPermission(BasePlayer player) => permission.UserHasPermission(player.UserIDString, PermName);

        #endregion
    }
}