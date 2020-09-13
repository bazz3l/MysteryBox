using System.Collections.Generic;
using System.Collections;
using System.Linq;
using Oxide.Game.Rust.Cui;
using Oxide.Core;
using UnityEngine;
using Newtonsoft.Json;

namespace Oxide.Plugins
{
    [Info("Mystery Box", "Bazz3l", "1.0.3")]
    [Description("Mystery box, unlock boxes with random loot items inside.")]
    class MysteryBox : RustPlugin
    {
        #region Fields
        private const string _successPrefab = "assets/prefabs/deployable/research table/effects/research-success.prefab";
        private const string _startPrefab = "assets/prefabs/deployable/repair bench/effects/skinchange_spraypaint.prefab";
        private const string _panelName = "mysterybox_panel";
        private const string _permName = "mysterybox.use";
        private List<BoxController> _controllers = new List<BoxController>();
        private CuiElementContainer _element = new CuiElementContainer();
        private CuiTextComponent _textComponent;
        private StoredData stored;
        private PluginConfig config;
        private static MysteryBox Instance;
        #endregion

        #region Storage
        class StoredData
        {
            public Dictionary<ulong, int> Players = new Dictionary<ulong, int>();
        }

        void LoadData()
        {
            stored = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name);
        }

        void SaveData()
        {
            Interface.Oxide.DataFileSystem.WriteObject(Name, stored);
        }
        #endregion

        #region Config
        protected override void LoadDefaultConfig() => config = new PluginConfig();

        protected override void LoadConfig()
        {
            base.LoadConfig();

            try
            {
                config = Config.ReadObject<PluginConfig>();

                if (config == null)
                {
                    throw new JsonException();
                }

                foreach (ItemDefinition item in ItemManager.itemList)
                {
                    if (config.Items.Find(x => x.Shortname == item.shortname) != null) continue;

                    config.Items.Add(new RewardItem {
                        Shortname = item.shortname,
                        Amount = 1
                    });
                }

                SaveConfig();

                PrintToConsole($"New config created {Name}.json.");
            }
            catch
            {
                LoadDefaultConfig();

                PrintToConsole($"Invalid config {Name}.json, using default config.");
            }
        }

        protected override void SaveConfig() => Config.WriteObject(config, true);

        class PluginConfig
        {
            public string ImageURL = "https://i.imgur.com/fCJrUYL.png";
            public bool WipeOnNewSave = true;
            public List<RewardItem> Items = new List<RewardItem>();
        }

        class RewardItem
        {
            public string Shortname;
            public int Amount;
        }
        #endregion

        #region Oxide
        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string> {
                { "InvalidSyntax", "Invalid syntax: give.mysterybox <steamid> <amount>" },
                { "NoPermission", "No permission." },
                { "NoBoxes", "You have 0 boxes left." },
                { "NoPlayer", "No player found." },
                { "Wiped", "Data cleared." },
                { "Reward", "You have been rewarded {0} mystery boxes, /mystery" },
                { "Given", "{0} was given {1} boxes." },
                { "Error", "Something went wrong." }
            }, this);
        }

        private void OnServerInitialized()
        {
            permission.RegisterPermission(_permName, this);

            LoadUI();

            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                OnPlayerConnected(player);
            }
        }

        private void Init()
        {
            Instance = this;

            ItemManager.Initialize();
            LoadConfig();
            LoadData();
        }

        private void Unload()
        {
            BasePlayer.activePlayerList.ToList().ForEach(x => DestroyUI(x));

            for (int i = 0; i < _controllers.Count; i++)
            {
                _controllers[i]?.Destroy();
            }

            SaveData();
        }

        private void OnNewSave()
        {
            if (!config.WipeOnNewSave)
            {
                return;
            }

            WipePlayerBoxes();
        }

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

            BoxController controller = BoxController.Find(player);
            if (controller != null)
            {
                return;
            }

            _controllers.Add(new BoxController(player));
        }

        private void OnPlayerDisconnected(BasePlayer player)
        {
            BoxController controller = BoxController.Find(player);
            if (controller == null)
            {
                return;
            }

            controller.Destroy();

            _controllers.Remove(controller);
        }

        private void OnItemRemovedFromContainer(ItemContainer itemContainer, Item item)
        {
            if (item.parentItem != null)
            {
                return;
            }

            BoxController controller = BoxController.Find(itemContainer);
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

            BoxController.Find(player)?.Close();
        }

        private object CanLootPlayer(BasePlayer looter, UnityEngine.Object target)
        {
            if (looter != target)
            {
                return null;
            }

            BoxController controller = BoxController.Find(looter);
            if (controller == null || !controller.IsOpened)
            {
                return null;
            }

            return true;
        }

        private object CanMoveItem(Item item, PlayerInventory playerInventory, uint containerId, int slot, int amount)
        {
            BoxController controllerFrom = BoxController.Find(item.parent);
            BoxController controllerTo   = BoxController.Find(containerId);
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
            if (targetItem != null)
            {
                controller.GiveItem(targetItem);
                controller.Clear();
            }

            return null;
        }
        #endregion

        #region Controller
        class BoxController
        {
            public ItemContainer Container;
            public BasePlayer Player;
            public bool IsOpened;
            public bool IsReady;
            private int _maxTicks = 20;
            private int _ticks;
            private Coroutine _coroutine;

            public static BoxController Find(ItemContainer container) => Instance._controllers.Find(x => x.Container != null && x.Container == container);
            public static BoxController Find(BasePlayer player) => Instance._controllers.Find(x => x.Player != null && x.Player == player);
            public static BoxController Find(uint id) => Instance._controllers.Find(x => x.Container != null && x.Container.uid == id);

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
                Instance.DestroyUI(Player);

                IsOpened = true;

                _ticks = 0;

                Container.SetLocked(true);

                PlayerLoot loot = Player.inventory.loot;
                loot.Clear();
                loot.PositionChecks = false;
                loot.entitySource = Player;
                loot.itemSource = null;
                loot.AddContainer(Container);
                loot.SendImmediate();

                Player.ClientRPCPlayer(null, Player, "RPC_OpenLootPanel", "generic");

                LootSpin();
            }

            public void Close()
            {
                if (IsOpened && IsReady && Instance.stored.Players.ContainsKey(Player.userID))
                {
                    GiveItem();

                    Instance.stored.Players[Player.userID]--;
                    Instance.SaveData();
                }

                Clear();

                IsOpened = false;
                IsReady  = false;

                StopCoroutine();
            }

            private void StopCoroutine()
            {
                if (_coroutine != null)
                {
                    CommunityEntity.ServerInstance.StopCoroutine(_coroutine);
                }
            }

            public void LootSpin()
            {
                PlayEffect(_startPrefab);

                _coroutine = CommunityEntity.ServerInstance.StartCoroutine(LootTick());
            }

            public void LootReady()
            {
                _coroutine = null;

                IsReady = true;

                Container.SetLocked(false);

                PlayEffect(_successPrefab);
            }

            public IEnumerator LootTick()
            {
                yield return new WaitForSeconds(0.2f);

                while (_ticks < _maxTicks)
                {
                    _ticks++;

                    RandomItem();

                    yield return new WaitForSeconds(0.2f);
                }

                LootReady();

                yield break;
            }

            public void RandomItem()
            {
                Container.itemList.Clear();
                Container.MarkDirty();

                RewardItem rewardItem = Instance.config.Items.GetRandom();
                if (rewardItem == null)
                {
                    return;
                }

                Item item = ItemManager.CreateByName(rewardItem.Shortname, rewardItem.Amount);
                if (item == null)
                {
                    return;
                }

                if (item.MoveToContainer(Container, -1, true))
                {
                    return;
                }

                item.Remove(0.0f);
            }

            public bool CanShow() => CanShow(Player);

            bool CanShow(BasePlayer player)
            {
                return player != null && !player.IsDead();
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

                MoveItemToContainer(item, Player.inventory.containerMain);
            }

            public void Clear()
            {
                for (int i = 0; i < Container.itemList.Count; i++)
                {
                    RemoveItem(Container.itemList[i]);
                }

                Container.itemList.Clear();
                Container.MarkDirty();
            }

            public void Destroy()
            {
                Close();

                Container.Kill();
            }

            public bool ValidContainer() => Player == null || Container?.itemList != null;

            private void MoveItemToContainer(Item item, ItemContainer container, int slot = 0)
            {
                while (container.SlotTaken(item, slot) && container.capacity > slot)
                {
                    slot++;
                }

                if (container.IsFull() || container.SlotTaken(item, slot))
                {
                    item.Drop(Player.transform.position, Vector3.up);
                    return;
                }

                item.parent?.itemList?.Remove(item);
                item.RemoveFromWorld();
                item.position = slot;
                item.parent   = container;

                container.itemList.Add(item);
                item.MarkDirty();

                for (int i = 0; i < item.info.itemMods.Length; i++)
                {
                    item.info.itemMods[i].OnParentChanged(item);
                }
            }

            private void RemoveItem(Item item)
            {
                if (Network.Net.sv != null && item.uid > 0U)
                {
                    Network.Net.sv.ReturnUID(item.uid);

                    item.uid = 0U;
                }

                if (item.contents != null)
                {
                    for (int i = 0; i < item.contents.itemList.Count; i++)
                    {
                        RemoveItem(item.contents.itemList[i]);
                    }

                    item.contents = null;
                }

                item.RemoveFromWorld();

                item.parent?.itemList?.Remove(item);
                item.parent = null;

                BaseEntity entity = item.GetHeldEntity();
                if (entity != null && entity.IsValid() && !entity.IsDestroyed)
                {
                    entity.Kill();
                }
            }

            private void PlayEffect(string prefab) => EffectNetwork.Send(new Effect(prefab, Player.transform.position, Vector3.zero), Player.net.connection);
        }

        private int GetPlayerBoxes(BasePlayer player)
        {
            int currentAmount;
            if (!stored.Players.TryGetValue(player.userID, out currentAmount))
            {
                stored.Players[player.userID] = currentAmount =  0;
                SaveData();
            }

            return currentAmount;
        }

        private void SetPlayerBoxes(BasePlayer player, int giveAmount)
        {
            if (!stored.Players.ContainsKey(player.userID))
                stored.Players[player.userID] = giveAmount;
            else
                stored.Players[player.userID] += giveAmount;

            SaveData();
        }

        private void WipePlayerBoxes()
        {
            stored.Players.Clear();

            SaveData();
        }

        private bool HasReachedLimit(BasePlayer player) => GetPlayerBoxes(player) <= 0;
        #endregion

        #region UI
        private void LoadUI()
        {
            string panel = _element.Add(new CuiPanel {
                CursorEnabled = true,

                Image = {
                    Color    = "0 0 0 0.65",
                    Material = "assets/content/ui/uibackgroundblur-ingamemenu.mat"
                },
                RectTransform = {
                    AnchorMin = "0 0",
                    AnchorMax = "1 1"
                }
            }, "Overlay", _panelName);

            CuiLabel label = new CuiLabel {
                Text = {
                    Font = "robotocondensed-regular.ttf",
                    FontSize  = 14,
                    Text      = "",
                    Align     = TextAnchor.MiddleCenter,
                },
                RectTransform = {
                    AnchorMin = "0.349 0.749",
                    AnchorMax = "0.623 0.98"
                }
            };

            _textComponent = label.Text;

            _element.Add(label, panel);

            _element.Add(new CuiElement
            {
                Parent     = panel,
                Components = {
                    new CuiRawImageComponent
                    {
                        Url = config.ImageURL
                    },
                    new CuiRectTransformComponent
                    {
                        AnchorMin = "0.394 0.501",
                        AnchorMax = "0.589 0.84"
                    }
                }
            });

            _element.Add(new CuiButton {
                Button =
                {
                    Color   = "0.6 0.6 0.6 0.24",
                    Material = "assets/icons/greyout.mat",
                    Command = "open.mysterybox"
                },
                Text =
                {
                    Font = "robotocondensed-regular.ttf",
                    FontSize = 12,
                    Text     = "OPEN BOX",
                    Align    = TextAnchor.MiddleCenter
                },
                RectTransform =
                {
                    AnchorMin = "0.494 0.42",
                    AnchorMax = "0.689 0.46"
                }
            }, panel);

            _element.Add(new CuiButton {
                Button =
                {
                    Color   = "0.6 0.6 0.6 0.24",
                    Material = "assets/icons/greyout.mat",
                    Command = "close.mysterybox"
                },
                Text =
                {
                    Font = "robotocondensed-regular.ttf",
                    FontSize = 12,
                    Text     = "CLOSE",
                    Align    = TextAnchor.MiddleCenter
                },
                RectTransform =
                {
                    AnchorMin = "0.292 0.42",
                    AnchorMax = "0.488 0.46"
                }
            }, panel);
        }

        private void CreateIU(BasePlayer player)
        {
            int amount = GetPlayerBoxes(player);

            _textComponent.Text = $"You have ({amount}) unopened boxes";

            CuiHelper.AddUi(player, _element);
        }

        private void DestroyUI(BasePlayer player) => CuiHelper.DestroyUi(player, _panelName);
        #endregion

        #region Commands
        [ChatCommand("mystery")]
        private void MysteryCommand(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player))
            {
                player.ChatMessage(Lang("NoPermission", player.UserIDString));
                return;
            }

            if (HasReachedLimit(player))
            {
                player.ChatMessage(Lang("NoBoxes", player.UserIDString));
                return;
            }

            BoxController controller = BoxController.Find(player);
            if (controller == null || !controller.CanShow() || controller.IsOpened)
            {
                player.ChatMessage(Lang("Error", player.UserIDString));
                return;
            }

            CreateIU(player);
        }

        [ConsoleCommand("open.mysterybox")]
        private void OpenMysteryBox(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null)
            {
                return;
            }

            if (!HasPermission(player))
            {
                player.ChatMessage(Lang("NoPermission", player.UserIDString));
                return;
            }

            if (HasReachedLimit(player))
            {
                player.ChatMessage(Lang("NoBoxes", player.UserIDString));
                return;
            }

            BoxController container = BoxController.Find(player);
            if (container == null || !container.CanShow() || container.IsOpened)
            {
                return;
            }

            NextTick(() => container.Show());
        }

        [ConsoleCommand("close.mysterybox")]
        private void CloseMysteryBox(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null)
            {
                return;
            }

            DestroyUI(player);
        }

        [ConsoleCommand("give.mysterybox")]
        private void GiveMysteryBox(ConsoleSystem.Arg arg)
        {
            if (arg.Player() != null)
            {
                arg.ReplyWith(Lang("NoPermission"));
                return;
            }

            if (arg.Args.Length != 2)
            {
                arg.ReplyWith(Lang("InvalidSyntax"));
                return;
            }

            BasePlayer target = BasePlayer.Find(arg.Args[0]);
            if (target == null)
            {
                arg.ReplyWith(Lang("NoPlayer"));
                return;
            }

            int amount;

            if (!int.TryParse(arg.Args[1], out amount))
            {
                arg.ReplyWith(Lang("InvalidSyntax"));
                return;
            }

            SetPlayerBoxes(target, amount);

            target.ChatMessage(Lang("Reward", target.UserIDString, amount));

            arg.ReplyWith(Lang("Given", target.UserIDString, target.userID, amount));
        }

        [ConsoleCommand("wipe.mysterybox")]
        private void WipeMysteryBox(ConsoleSystem.Arg arg)
        {
            if (arg.Player() != null)
            {
                arg.ReplyWith(Lang("NoPermission"));
                return;
            }

            WipePlayerBoxes();

            arg.ReplyWith(Lang("Wiped"));
        }
        #endregion

        #region Helpers
        private bool HasPermission(BasePlayer player) => permission.UserHasPermission(player.UserIDString, _permName);

        private string Lang(string key, string id = null, params object[] args) => string.Format(lang.GetMessage(key, this, id), args);
        #endregion
    }
}