using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;
using Vintagestory.Client.NoObf;
using ProtoBuf;
using XSkills;
using XLib;  // For XLeveling and Skill types

namespace ValueYourLife
{
    [ProtoContract]
    [Serializable]
    public class PlayerBalanceData
    {
        [ProtoMember(1)]
        public string PlayerName { get; set; }

        [ProtoMember(2)]
        public int Tokens { get; set; }

        // Required for protobuf-net deserialization
        public PlayerBalanceData() { }

        public PlayerBalanceData(string playerName, int tokens)
        {
            PlayerName = playerName;
            Tokens = tokens;
        }
    }

    public class VYLConfig
    {
        public int RustyGearCost { get; set; } = 10;
        public int TemporalGearCost { get; set; } = 1;
        public int StartingTokens { get; set; } = 5;
        public int GracePeriodSeconds { get; set; } = 30;  // NEW: Invuln time post-respawn
        public Dictionary<string, double> SkillsLevelLoss { get; set; } = new Dictionary<string, double>
        {
            { "survival", 0.5 },
            { "farming", 0.5 },
            { "digging", 0.5 },
            { "forestry", 0.5 },
            { "mining", 0.5 },
            { "husbandry", 0.5 },
            { "combat", 0.5 },
            { "metalworking", 0.5 },
            { "pottery", 0.5 },
            { "cooking", 0.5 },
            { "temporaladaptation", 0.5 }
        };
    }

    public class ValueYourLifeModSystem : ModSystem
    {
        private ICoreServerAPI sapi;
        public VYLConfig config;
        internal GuiDialogValueYourLife activeVylDialog;
        private Dictionary<string, PlayerBalanceData> playerTokens;


        public override void Start(ICoreAPI api)
        {
            sapi = api as ICoreServerAPI;
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;  // Make sure sapi is set (needed for logging and SaveAllData)

            config = api.LoadModConfig<VYLConfig>("valueyourlife.json") ?? new VYLConfig();
            api.StoreModConfig(config, "valueyourlife.json");

            sapi.Logger.Notification("[VYL] === SERVER STARTING ===");

            // === TOKEN STORAGE ===
            byte[] data = api.WorldManager.SaveGame.GetData("vyl_player_tokens");
            if (data != null && data.Length > 0)
            {
                playerTokens = SerializerUtil.Deserialize<Dictionary<string, PlayerBalanceData>>(data);
                sapi.Logger.Notification($"[VYL] Loaded {playerTokens.Count} player token entries");
            }
            else
            {
                playerTokens = new Dictionary<string, PlayerBalanceData>();
                sapi.Logger.Notification("[VYL] No existing token data - starting fresh");
            }

            // Save both every world save
            api.Event.GameWorldSave += () => SaveAllData();

            // === EVENT HOOKS ===
            sapi.Event.PlayerDeath += OnPlayerDeath;
            sapi.Event.PlayerRespawn += OnPlayerRespawn;
            sapi.Event.PlayerNowPlaying += OnPlayerNowPlaying;
            sapi.Event.PlayerDisconnect += OnPlayerDisconnect;

            // === FULL CHAT COMMAND REGISTRATION (restored exactly) ===
            sapi.ChatCommands.Create("vyl")
                .WithDescription("Manage your ValueYourLife respawn tokens")
                .RequiresPrivilege("chat")
                .BeginSubCommand("buy")
                    .WithArgs(api.ChatCommands.Parsers.Int("quantity"))
                    .WithDescription("Buy respawn tokens with rusty gears")
                    .HandleWith(OnBuyCommand)
                .EndSubCommand()
                .BeginSubCommand("buyall")
                    .WithDescription("Buy as many respawn tokens as you can afford with rusty gears")
                    .HandleWith(OnBuyAllCommand)
                .EndSubCommand()
                .BeginSubCommand("trade")
                    .WithArgs(api.ChatCommands.Parsers.Int("quantity"))
                    .WithDescription("Trade temporal gears for respawn tokens")
                    .HandleWith(OnTradeCommand)
                .EndSubCommand()
                .BeginSubCommand("tradeall")
                    .WithDescription("Trade all your temporal gears for respawn tokens")
                    .HandleWith(OnTradeAllCommand)
                .EndSubCommand()
                .BeginSubCommand("transfer")
                    .WithArgs(api.ChatCommands.Parsers.Word("player"), api.ChatCommands.Parsers.Int("quantity"))
                    .WithDescription("Transfer respawn tokens to another player")
                    .HandleWith(OnTransferCommand)
                .EndSubCommand()
                .BeginSubCommand("balance")
                    .WithDescription("Check your respawn token count")
                    .HandleWith(OnBalanceCommand)
                .EndSubCommand()
                .BeginSubCommand("cost")
                    .WithDescription("Check the current cost of respawn tokens")
                    .HandleWith(OnCostCommand)
                .EndSubCommand()
                .BeginSubCommand("settokens")
                    .WithArgs(api.ChatCommands.Parsers.Word("player"), api.ChatCommands.Parsers.Int("quantity"))
                    .WithDescription("Set a player's respawn token count (admin only)")
                    .RequiresPrivilege(Privilege.controlserver)
                    .HandleWith(OnSetTokensCommand)
                .EndSubCommand()
                .BeginSubCommand("give")
                    .WithArgs(api.ChatCommands.Parsers.Word("player"), api.ChatCommands.Parsers.Int("quantity"))
                    .WithDescription("Give tokens to a player (admin only)")
                    .RequiresPrivilege(Privilege.controlserver)
                    .HandleWith(OnGiveTokensCommand)
                .EndSubCommand()
                .BeginSubCommand("reset")
                    .WithArgs(api.ChatCommands.Parsers.Word("player"))
                    .WithDescription("Reset a player's respawn tokens to 0 (admin only)")
                    .RequiresPrivilege(Privilege.controlserver)
                    .HandleWith(OnResetTokensCommand)
                .EndSubCommand();
        }

        private void SaveTokens()
        {
            byte[] bytes = SerializerUtil.Serialize<Dictionary<string, PlayerBalanceData>>(playerTokens);
            sapi.WorldManager.SaveGame.StoreData("vyl_player_tokens", bytes);
        }

        public override void StartClientSide(ICoreClientAPI capi)
        {
            // Load client-side config for UI
            config = capi.LoadModConfig<VYLConfig>("valueyourlife.json") ?? new VYLConfig();

            // Register hotkey for UI
            capi.Input.RegisterHotKey("vyl-ui", "Value Your Life UI", GlKeys.L, HotkeyType.GUIOrOtherControls);
            capi.Input.SetHotKeyHandler("vyl-ui", keyCombo =>
            {
                if (capi.World == null) return true;

                OpenValueYourLifeDialog(capi);   // ← now uses the shared method

                return true;
            });
        }

        public int GetGearCount(ICoreClientAPI capi, string code)
        {
            var item = capi.World.GetItem(new AssetLocation(code));
            if (item == null) return 0;

            int count = 0;
            var player = capi.World.Player;

            // Hotbar
            var hotbar = player.InventoryManager.GetHotbarInventory();
            foreach (var slot in hotbar)
            {
                if (slot.Itemstack?.Item == item)
                    count += slot.Itemstack.StackSize;
            }

            // Backpack
            var backpack = player.InventoryManager.GetOwnInventory(GlobalConstants.backpackInvClassName);
            if (backpack != null)
            {
                foreach (var slot in backpack)
                {
                    if (slot.Itemstack?.Item == item)
                        count += slot.Itemstack.StackSize;
                }
            }

            return count;
        }

        public void OpenValueYourLifeDialog(ICoreClientAPI capi)
        {
            if (capi.World == null) return;

            // Close any existing dialog first (prevents ghost window + stacking)
            if (activeVylDialog != null && activeVylDialog.IsOpened())
            {
                activeVylDialog.TryClose();
            }

            var player = capi.World.Player;
            int tokens = player.Entity.WatchedAttributes.GetInt("vylTokenCount", 0);

            int rustyCount = GetGearCount(capi, "gear-rusty");
            int temporalCount = GetGearCount(capi, "gear-temporal");

            // Grace status
            long lastRespawn = player.Entity.WatchedAttributes.GetLong("vylLastRespawn", 0);
            long now = capi.World.ElapsedMilliseconds;
            bool inGrace = lastRespawn > 0 && (now - lastRespawn) < config.GracePeriodSeconds * 1000L;
            string graceText = inGrace
                ? $"Grace active: {(config.GracePeriodSeconds - (now - lastRespawn) / 1000)}s left"
                : "No grace active";

            activeVylDialog = new GuiDialogValueYourLife(
                capi,
                tokens,
                config.RustyGearCost,
                config.TemporalGearCost,
                rustyCount,
                temporalCount,
                graceText
            );

            activeVylDialog.TryOpen();
        }

        private void OnPlayerDeath(IServerPlayer player, DamageSource damageSource)
        {
            int tokens = player.Entity.WatchedAttributes.GetInt("vylTokenCount", 0);
            bool canRespawn = tokens >= 1;

            player.Entity.WatchedAttributes.SetBool("vylCanRespawn", canRespawn);

            // DO NOT set vylLastRespawn here — only in OnPlayerRespawn when token is consumed

            if (!canRespawn)
            {
                sapi.SendMessage(player, 0, Lang.Get("valueyourlife:respawn-failed-tokens", 1, tokens), EnumChatType.Notification);
                if (sapi.ModLoader.IsModEnabled("xskills")) ResetXSkills(player);
            }

        }

        private void OnPlayerRespawn(IServerPlayer player)
        {
            int tokens = player.Entity.WatchedAttributes.GetInt("vylTokenCount", 0);
            long lastRespawn = player.Entity.WatchedAttributes.GetLong("vylLastRespawn", 0);
            long now = sapi.World.ElapsedMilliseconds;
            bool inGracePeriod = lastRespawn > 0 && (now - lastRespawn) < config.GracePeriodSeconds * 1000L;

            if (tokens <= 0 && !inGracePeriod)
            {
                player.Entity.ReceiveDamage(new DamageSource { Source = EnumDamageSource.Internal, Type = EnumDamageType.Gravity }, 999f);
                return;
            }

            if (inGracePeriod)
            {
                sapi.SendMessage(player, 0, $"Grace period saved your token! ({(config.GracePeriodSeconds - (now - lastRespawn) / 1000)}s left)", EnumChatType.Notification);
                return;
            }

            // ONLY HERE do we consume a token and start a new grace period
            player.Entity.WatchedAttributes.SetInt("vylTokenCount", tokens - 1);
            playerTokens[player.PlayerUID].Tokens = tokens - 1;
            SaveTokens();

            var tree = player.Entity.WatchedAttributes.GetTreeAttribute("vylTokens") ?? new TreeAttribute();
            tree.SetInt("count", tokens - 1);
            player.Entity.WatchedAttributes.SetAttribute("vylTokens", tree);

            player.Entity.WatchedAttributes.SetLong("vylLastRespawn", now);  // ← ONLY HERE
            player.Entity.WatchedAttributes.MarkPathDirty("vylLastRespawn");

            sapi.SendMessage(player, 0, Lang.Get("valueyourlife:respawn-success-tokens", 1, tokens - 1), EnumChatType.Notification);
        }

        private void OnPlayerNowPlaying(IServerPlayer player)
        {
            string uid = player.PlayerUID;
            string lowerName = player.PlayerName.ToLowerInvariant();
            string pendingKey = "pending_" + lowerName;

            // Apply any pending offline transfer
            if (playerTokens.TryGetValue(pendingKey, out var pendingData))
            {
                if (!playerTokens.TryGetValue(uid, out var data))
                {
                    data = new PlayerBalanceData { PlayerName = player.PlayerName, Tokens = config.StartingTokens };
                    playerTokens[uid] = data;
                }
                else
                {
                    data.PlayerName = player.PlayerName;
                }

                data.Tokens += pendingData.Tokens;
                playerTokens.Remove(pendingKey);

                SaveAllData();

                sapi.SendMessage(player, 0,
                    $"<font color=\"#00ff00\"><strong>You received {pendingData.Tokens} respawn token{(pendingData.Tokens == 1 ? "" : "s")} while offline!</strong></font>",
                    EnumChatType.Notification);
            }

            // === Your original code continues here (unchanged) ===
            if (!playerTokens.TryGetValue(uid, out var data2))
            {
                data2 = new PlayerBalanceData { PlayerName = player.PlayerName, Tokens = config.StartingTokens };
                playerTokens[uid] = data2;
                SaveTokens();
            }
            else
            {
                data2.PlayerName = player.PlayerName;
            }

            player.Entity.WatchedAttributes.SetInt("vylTokenCount", data2.Tokens);
            var tokenTree = player.Entity.WatchedAttributes.GetTreeAttribute("vylTokens");
            if (tokenTree == null)
            {
                tokenTree = new TreeAttribute();
                player.Entity.WatchedAttributes.SetAttribute("vylTokens", tokenTree);
            }
            tokenTree.SetInt("count", data2.Tokens);
            player.Entity.WatchedAttributes.MarkPathDirty("vylTokens");
            player.Entity.WatchedAttributes.SetBool("vylCanRespawn", data2.Tokens >= 1);

            // Force grace period OFF
            player.Entity.WatchedAttributes.RemoveAttribute("vylLastRespawn");
            player.Entity.WatchedAttributes.SetLong("vylLastRespawn", 0);
            player.Entity.WatchedAttributes.MarkPathDirty("vylLastRespawn");

            if (!player.Entity.HasBehavior<EntityBehaviorVYLGrace>())
            {
                player.Entity.AddBehavior(new EntityBehaviorVYLGrace(player.Entity, sapi, config));
            }

            string color = data2.Tokens switch
            {
                0 => "#ff4444",
                <= 3 => "#ffaa00",
                _ => "#ffffff"
            };
            sapi.SendMessage(player, 0,
                $"<font color=\"{color}\"><strong>You have {data2.Tokens} respawn token{(data2.Tokens == 1 ? "" : "s")} remaining.</strong></font>",
                EnumChatType.Notification);
        }

        private void OnPlayerDisconnect(IServerPlayer player)
        {
            int tokens = player.Entity.WatchedAttributes.GetInt("vylTokenCount", 0);
            playerTokens[player.PlayerUID] = new PlayerBalanceData { PlayerName = player.PlayerName, Tokens = tokens };
            SaveTokens();

            // Optional cleanup
            var graceBehavior = player.Entity.GetBehavior<EntityBehaviorVYLGrace>();
            if (graceBehavior != null)
            {
                player.Entity.RemoveBehavior(graceBehavior);
            }
        }

        private void ResetXSkills(IServerPlayer player)
        {
            try
            {
                if (!sapi.ModLoader.IsModEnabled("xskills"))
                {
                    sapi.SendMessage(player, 0, "XSkills mod not detected. Skills cannot be reset.", EnumChatType.Notification);
                    return;
                }

                var xSkills = sapi.ModLoader.GetModSystem<XSkills.XSkills>();
                if (xSkills == null)
                {
                    sapi.SendMessage(player, 0, "XSkills mod not detected. Skills cannot be reset.", EnumChatType.Notification);
                    return;
                }

                sapi.Logger.Warning($"xSkills.XLeveling is {(xSkills.XLeveling == null ? "null" : "not null")}");

                var levelingApi = xSkills.XLeveling as XLib.XLeveling.IXLevelingAPI;
                if (levelingApi == null)
                {
                    sapi.SendMessage(player, 0, "Failed to access XSkills leveling API. Skills cannot be reset.", EnumChatType.Notification);
                    return;
                }

                var skillSet = levelingApi.GetPlayerSkillSet(player);
                if (skillSet == null)
                {
                    sapi.SendMessage(player, 0, "Failed to access XSkills data. Skills cannot be reset.", EnumChatType.Notification);
                    return;
                }

                if (config.SkillsLevelLoss == null || config.SkillsLevelLoss.Count == 0)
                {
                    return;
                }

                foreach (var skillEntry in config.SkillsLevelLoss)
                {
                    var skillName = skillEntry.Key;
                    var lossPercentage = skillEntry.Value;

                    if (!xSkills.Skills.ContainsKey(skillName))
                    {
                        sapi.Logger.Warning($"Skill '{skillName}' not found in XSkills for level adjustment.");
                        continue;
                    }

                    var skill = xSkills.Skills[skillName];
                    var playerSkill = skillSet.PlayerSkills.Find(ps => ps.Skill.Id == skill.Id);
                    if (playerSkill == null)
                    {
                        sapi.Logger.Warning($"PlayerSkill for '{skillName}' not found in player's skill set.");
                        continue;
                    }

                    var currentLevel = playerSkill.Level;
                    if (currentLevel <= 1)
                    {
                        continue;
                    }

                    int levelsToLose = (int)Math.Round(currentLevel * lossPercentage);
                    int newLevel = Math.Max(1, currentLevel - levelsToLose);

                    sapi.Logger.Warning($"Executing /level set {player.PlayerName} {skillName} {newLevel}");
                    sapi.InjectConsole($"/level set {player.PlayerName} {skillName} {newLevel}");
                    sapi.Logger.Warning($"Executing /exp set {player.PlayerName} {skillName} 0");
                    sapi.InjectConsole($"/exp set {player.PlayerName} {skillName} 0");
                }

                sapi.SendMessage(player, 0, $"You ran out of respawn tokens! XSkills levels have been reduced: {string.Join(", ", config.SkillsLevelLoss.Keys)}.", EnumChatType.Notification);
            }
            catch (Exception ex)
            {
                sapi.Logger.Warning($"Failed to reset XSkills for player {player.PlayerName}: {ex.Message}");
            }
        }

        // ALL YOUR ORIGINAL COMMAND METHODS — 100% UNCHANGED FROM YOUR DOCUMENT
        private TextCommandResult OnBuyCommand(TextCommandCallingArgs args)
        {
            var player = args.Caller.Player as IServerPlayer;
            int quantity;
            try { quantity = (int)args[0]; }
            catch (IndexOutOfRangeException) { return TextCommandResult.Error($"Usage: /vyl buy <quantity> ({config.RustyGearCost} rusty gears per token)"); }

            if (quantity <= 0) return TextCommandResult.Error("Quantity must be positive!");
            int totalGearCost = quantity * config.RustyGearCost;

            ItemStack gearStack = new(sapi.World.GetItem(new AssetLocation("game:gear-rusty")));
            int gearsInInventory = 0;
            var inventories = player.InventoryManager.Inventories;
            foreach (var kvp in inventories)
            {
                if (kvp.Value == null || kvp.Key.Contains("creative") || kvp.Key.Contains("ground") || kvp.Key.Contains("mouse") ||
                    kvp.Key.Contains("crafting") || kvp.Key.Contains("chest")) continue;

                try
                {
                    foreach (var slot in kvp.Value)
                    {
                        if (slot.Itemstack?.Item?.Code == gearStack.Item.Code)
                        {
                            gearsInInventory += slot.Itemstack.StackSize;
                        }
                    }
                }
                catch (Exception ex)
                {
                    sapi.Logger.Warning("Failed to scan inventory '{0}' for player {1}: {2}", kvp.Key, player.PlayerName, ex.Message);
                }
            }

            if (gearsInInventory < totalGearCost)
            {
                return TextCommandResult.Error(Lang.Get("valueyourlife:buy-failed", totalGearCost, gearsInInventory));
            }

            int gearsToRemove = totalGearCost;
            var backpackSlots = new Dictionary<string, Tuple<ItemSlotBackpack, InventoryBase>>();
            var characterInv = inventories.TryGetValue(inventories.Keys.FirstOrDefault(k => k.Contains("character")), out var inv) ? inv : null;
            if (characterInv != null)
            {
                for (int i = 0; i < characterInv.Count; i++)
                {
                    if (characterInv[i] is ItemSlotBackpack backpackSlot &&
                        backpackSlot.Itemstack?.Item?.Code?.ToString().Contains("backpack") == true &&
                        backpackSlot.Inventory is InventoryBase bagInv)
                    {
                        string invId = inventories.FirstOrDefault(x => x.Value == bagInv).Key ?? $"bagslot{i}";
                        backpackSlots[invId] = new(backpackSlot, bagInv);
                    }
                }
            }

            foreach (var kvp in inventories)
            {
                if (kvp.Value == null) continue;

                try
                {
                    foreach (var slot in kvp.Value)
                    {
                        if (gearsToRemove > 0 && slot.Itemstack?.Item?.Code == gearStack.Item.Code)
                        {
                            int amountToTake = Math.Min(gearsToRemove, slot.Itemstack.StackSize);
                            ItemStack takenStack = slot.TakeOut(amountToTake);
                            int taken = takenStack?.StackSize ?? 0;
                            if (taken < amountToTake && slot.Itemstack?.StackSize > 0)
                            {
                                taken += slot.TakeOut(amountToTake - taken).StackSize;
                            }
                            gearsToRemove -= taken;
                            slot.MarkDirty();

                            if (kvp.Key.Contains("bagslot") && backpackSlots.TryGetValue(kvp.Key, out var backpack))
                            {
                                var attributes = backpack.Item1.Itemstack.Attributes ?? new TreeAttribute();
                                backpack.Item2.ToTreeAttributes(attributes);
                                backpack.Item1.Itemstack.Attributes = attributes;
                                backpack.Item1.MarkDirty();
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    sapi.Logger.Warning("Failed to remove gears from inventory '{0}' for player {1}: {2}", kvp.Key, player.PlayerName, ex.Message);
                }
                if (gearsToRemove <= 0) break;
            }

            int currentTokens = player.Entity.WatchedAttributes.GetInt("vylTokenCount", 0);
            int newTokens = currentTokens + quantity;
            player.Entity.WatchedAttributes.SetInt("vylTokenCount", newTokens);
            playerTokens[player.PlayerUID].Tokens = newTokens;
            SaveTokens();

            bool isDead = player.Entity.GetBehavior<EntityBehaviorHealth>()?.Health <= 0f;
            if (isDead && newTokens >= 1)
            {
                player.Entity.WatchedAttributes.SetBool("vylCanRespawn", true);
                sapi.SendMessage(player, 0, "You now have enough tokens to respawn! Click the respawn button.", EnumChatType.Notification);
            }

            return TextCommandResult.Success(Lang.Get("valueyourlife:buy-success", quantity, totalGearCost, newTokens));
        }

        private TextCommandResult OnBuyAllCommand(TextCommandCallingArgs args)
        {
            var player = args.Caller.Player as IServerPlayer;
            ItemStack gearStack = new(sapi.World.GetItem(new AssetLocation("game:gear-rusty")));
            int gearsInInventory = 0;

            var inventories = player.InventoryManager.Inventories;
            foreach (var kvp in inventories)
            {
                if (kvp.Value == null || kvp.Key.Contains("creative") || kvp.Key.Contains("ground") || kvp.Key.Contains("mouse") ||
                    kvp.Key.Contains("crafting") || kvp.Key.Contains("chest")) continue;

                try
                {
                    foreach (var slot in kvp.Value)
                    {
                        if (slot.Itemstack?.Item?.Code == gearStack.Item.Code)
                        {
                            gearsInInventory += slot.Itemstack.StackSize;
                        }
                    }
                }
                catch (Exception ex)
                {
                    sapi.Logger.Warning("Failed to scan inventory '{0}' for player {1}: {2}", kvp.Key, player.PlayerName, ex.Message);
                }
            }

            int quantity = gearsInInventory / config.RustyGearCost;
            if (quantity == 0)
            {
                return TextCommandResult.Error(Lang.Get("valueyourlife:buyall-failed", config.RustyGearCost, gearsInInventory));
            }

            int totalGearCost = quantity * config.RustyGearCost;
            int gearsToRemove = totalGearCost;
            var backpackSlots = new Dictionary<string, Tuple<ItemSlotBackpack, InventoryBase>>();

            var characterInv = inventories.TryGetValue(inventories.Keys.FirstOrDefault(k => k.Contains("character")), out var inv) ? inv : null;
            if (characterInv != null)
            {
                for (int i = 0; i < characterInv.Count; i++)
                {
                    if (characterInv[i] is ItemSlotBackpack backpackSlot &&
                        backpackSlot.Itemstack?.Item?.Code?.ToString().Contains("backpack") == true &&
                        backpackSlot.Inventory is InventoryBase bagInv)
                    {
                        string invId = inventories.FirstOrDefault(x => x.Value == bagInv).Key ?? $"bagslot{i}";
                        backpackSlots[invId] = new(backpackSlot, bagInv);
                    }
                }
            }

            foreach (var kvp in inventories)
            {
                if (kvp.Value == null) continue;

                try
                {
                    foreach (var slot in kvp.Value)
                    {
                        if (gearsToRemove > 0 && slot.Itemstack?.Item?.Code == gearStack.Item.Code)
                        {
                            ItemStack takenStack = slot.TakeOut(Math.Min(gearsToRemove, slot.Itemstack.StackSize));
                            int taken = takenStack?.StackSize ?? 0;
                            gearsToRemove -= taken;
                            slot.MarkDirty();

                            if (kvp.Key.Contains("bagslot") && backpackSlots.TryGetValue(kvp.Key, out var backpack))
                            {
                                var attributes = backpack.Item1.Itemstack.Attributes ?? new TreeAttribute();
                                backpack.Item2.ToTreeAttributes(attributes);
                                backpack.Item1.Itemstack.Attributes = attributes;
                                backpack.Item1.MarkDirty();
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    sapi.Logger.Warning("Failed to remove gears from inventory '{0}' for player {1}: {2}", kvp.Key, player.PlayerName, ex.Message);
                }
                if (gearsToRemove <= 0) break;
            }

            int currentTokens = player.Entity.WatchedAttributes.GetInt("vylTokenCount", 0);
            int newTokens = currentTokens + quantity;
            player.Entity.WatchedAttributes.SetInt("vylTokenCount", newTokens);
            playerTokens[player.PlayerUID].Tokens = newTokens;
            SaveTokens();

            bool isDead = player.Entity.GetBehavior<EntityBehaviorHealth>()?.Health <= 0f;
            if (isDead && newTokens >= 1)
            {
                player.Entity.WatchedAttributes.SetBool("vylCanRespawn", true);
                sapi.SendMessage(player, 0, "You now have enough tokens to respawn! Click the respawn button.", EnumChatType.Notification);
            }

            return TextCommandResult.Success(Lang.Get("valueyourlife:buyall-success", totalGearCost, quantity, newTokens));
        }

        private TextCommandResult OnTradeCommand(TextCommandCallingArgs args)
        {
            var player = args.Caller.Player as IServerPlayer;
            int quantity;
            try { quantity = (int)args[0]; }
            catch (IndexOutOfRangeException) { return TextCommandResult.Error($"Usage: /vyl trade <quantity> ({config.TemporalGearCost} temporal gears per token)"); }

            if (quantity <= 0) return TextCommandResult.Error("Quantity must be positive!");
            int totalGearCost = quantity * config.TemporalGearCost;

            ItemStack tempGearStack = new(sapi.World.GetItem(new AssetLocation("game:gear-temporal")));
            int tempGearsInInventory = 0;
            var inventories = player.InventoryManager.Inventories;
            foreach (var kvp in inventories)
            {
                if (kvp.Value == null || kvp.Key.Contains("creative") || kvp.Key.Contains("ground") || kvp.Key.Contains("mouse") ||
                    kvp.Key.Contains("crafting") || kvp.Key.Contains("chest")) continue;

                try
                {
                    foreach (var slot in kvp.Value)
                    {
                        if (slot.Itemstack?.Item?.Code == tempGearStack.Item.Code)
                        {
                            tempGearsInInventory += slot.Itemstack.StackSize;
                        }
                    }
                }
                catch (Exception ex)
                {
                    sapi.Logger.Warning("Failed to scan inventory '{0}' for player {1}: {2}", kvp.Key, player.PlayerName, ex.Message);
                }
            }

            if (tempGearsInInventory < totalGearCost)
            {
                return TextCommandResult.Error(Lang.Get("valueyourlife:trade-failed", totalGearCost, tempGearsInInventory));
            }

            int tempGearsToRemove = totalGearCost;
            var backpackSlots = new Dictionary<string, Tuple<ItemSlotBackpack, InventoryBase>>();

            var characterInv = inventories.TryGetValue(inventories.Keys.FirstOrDefault(k => k.Contains("character")), out var inv) ? inv : null;
            if (characterInv != null)
            {
                for (int i = 0; i < characterInv.Count; i++)
                {
                    if (characterInv[i] is ItemSlotBackpack backpackSlot &&
                        backpackSlot.Itemstack?.Item?.Code?.ToString().Contains("backpack") == true &&
                        backpackSlot.Inventory is InventoryBase bagInv)
                    {
                        string invId = inventories.FirstOrDefault(x => x.Value == bagInv).Key ?? $"bagslot{i}";
                        backpackSlots[invId] = new(backpackSlot, bagInv);
                    }
                }
            }

            foreach (var kvp in inventories)
            {
                if (kvp.Value == null) continue;

                try
                {
                    foreach (var slot in kvp.Value)
                    {
                        if (tempGearsToRemove > 0 && slot.Itemstack?.Item?.Code == tempGearStack.Item.Code)
                        {
                            ItemStack takenStack = slot.TakeOut(Math.Min(tempGearsToRemove, slot.Itemstack.StackSize));
                            int taken = takenStack?.StackSize ?? 0;
                            tempGearsToRemove -= taken;
                            slot.MarkDirty();

                            if (kvp.Key.Contains("bagslot") && backpackSlots.TryGetValue(kvp.Key, out var backpack))
                            {
                                var attributes = backpack.Item1.Itemstack.Attributes ?? new TreeAttribute();
                                backpack.Item2.ToTreeAttributes(attributes);
                                backpack.Item1.Itemstack.Attributes = attributes;
                                backpack.Item1.MarkDirty();
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    sapi.Logger.Warning("Failed to remove gears from inventory '{0}' for player {1}: {2}", kvp.Key, player.PlayerName, ex.Message);
                }
                if (tempGearsToRemove <= 0) break;
            }

            int currentTokens = player.Entity.WatchedAttributes.GetInt("vylTokenCount", 0);
            int newTokens = currentTokens + quantity;
            player.Entity.WatchedAttributes.SetInt("vylTokenCount", newTokens);
            playerTokens[player.PlayerUID].Tokens = newTokens;
            SaveTokens();

            bool isDead = player.Entity.GetBehavior<EntityBehaviorHealth>()?.Health <= 0f;
            if (isDead && newTokens >= 1)
            {
                player.Entity.WatchedAttributes.SetBool("vylCanRespawn", true);
                sapi.SendMessage(player, 0, "You now have enough tokens to respawn! Click the respawn button.", EnumChatType.Notification);
            }

            return TextCommandResult.Success(Lang.Get("valueyourlife:trade-success", totalGearCost, quantity, newTokens));
        }

        private TextCommandResult OnTradeAllCommand(TextCommandCallingArgs args)
        {
            var player = args.Caller.Player as IServerPlayer;
            ItemStack tempGearStack = new(sapi.World.GetItem(new AssetLocation("game:gear-temporal")));
            int tempGearsInInventory = 0;

            var inventories = player.InventoryManager.Inventories;
            foreach (var kvp in inventories)
            {
                if (kvp.Value == null || kvp.Key.Contains("creative") || kvp.Key.Contains("ground") || kvp.Key.Contains("mouse") ||
                    kvp.Key.Contains("crafting") || kvp.Key.Contains("chest")) continue;

                try
                {
                    foreach (var slot in kvp.Value)
                    {
                        if (slot.Itemstack?.Item?.Code == tempGearStack.Item.Code)
                        {
                            tempGearsInInventory += slot.Itemstack.StackSize;
                        }
                    }
                }
                catch (Exception ex)
                {
                    sapi.Logger.Warning("Failed to scan inventory '{0}' for player {1}: {2}", kvp.Key, player.PlayerName, ex.Message);
                }
            }

            int quantity = tempGearsInInventory / config.TemporalGearCost;
            if (quantity == 0)
            {
                return TextCommandResult.Error(Lang.Get("valueyourlife:tradeall-failed"));
            }

            int totalGearsTraded = quantity * config.TemporalGearCost;
            int gearsToRemove = totalGearsTraded;
            var backpackSlots = new Dictionary<string, Tuple<ItemSlotBackpack, InventoryBase>>();

            var characterInv = inventories.TryGetValue(inventories.Keys.FirstOrDefault(k => k.Contains("character")), out var inv) ? inv : null;
            if (characterInv != null)
            {
                for (int i = 0; i < characterInv.Count; i++)
                {
                    if (characterInv[i] is ItemSlotBackpack backpackSlot &&
                        backpackSlot.Itemstack?.Item?.Code?.ToString().Contains("backpack") == true &&
                        backpackSlot.Inventory is InventoryBase bagInv)
                    {
                        string invId = inventories.FirstOrDefault(x => x.Value == bagInv).Key ?? $"bagslot{i}";
                        backpackSlots[invId] = new(backpackSlot, bagInv);
                    }
                }
            }

            foreach (var kvp in inventories)
            {
                if (kvp.Value == null) continue;

                try
                {
                    foreach (var slot in kvp.Value)
                    {
                        if (gearsToRemove > 0 && slot.Itemstack?.Item?.Code == tempGearStack.Item.Code)
                        {
                            ItemStack takenStack = slot.TakeOut(Math.Min(gearsToRemove, slot.Itemstack.StackSize));
                            int taken = takenStack?.StackSize ?? 0;
                            gearsToRemove -= taken;
                            slot.MarkDirty();

                            if (kvp.Key.Contains("bagslot") && backpackSlots.TryGetValue(kvp.Key, out var backpack))
                            {
                                var attributes = backpack.Item1.Itemstack.Attributes ?? new TreeAttribute();
                                backpack.Item2.ToTreeAttributes(attributes);
                                backpack.Item1.Itemstack.Attributes = attributes;
                                backpack.Item1.MarkDirty();
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    sapi.Logger.Warning("Failed to remove gears from inventory '{0}' for player {1}: {2}", kvp.Key, player.PlayerName, ex.Message);
                }
                if (gearsToRemove <= 0) break;
            }

            int currentTokens = player.Entity.WatchedAttributes.GetInt("vylTokenCount", 0);
            int newTokens = currentTokens + quantity;
            player.Entity.WatchedAttributes.SetInt("vylTokenCount", newTokens);
            playerTokens[player.PlayerUID].Tokens = newTokens;
            SaveTokens();

            bool isDead = player.Entity.GetBehavior<EntityBehaviorHealth>()?.Health <= 0f;
            if (isDead && newTokens >= 1)
            {
                player.Entity.WatchedAttributes.SetBool("vylCanRespawn", true);
                sapi.SendMessage(player, 0, "You now have enough tokens to respawn! Click the respawn button.", EnumChatType.Notification);
            }

            return TextCommandResult.Success(Lang.Get("valueyourlife:tradeall-success", totalGearsTraded, quantity, newTokens));
        }

        private TextCommandResult OnTransferCommand(TextCommandCallingArgs args)
        {
            var sender = args.Caller.Player as IServerPlayer;
            string targetPlayerName = ((string)args[0]).Trim();
            int quantity = (int)args[1];

            if (quantity <= 0) return TextCommandResult.Error("Quantity must be positive!");
            if (sender.PlayerName.Equals(targetPlayerName, StringComparison.OrdinalIgnoreCase))
                return TextCommandResult.Error(Lang.Get("valueyourlife:transfer-self"));

            int senderTokens = playerTokens[sender.PlayerUID].Tokens;
            if (quantity > senderTokens)
                return TextCommandResult.Error(Lang.Get("valueyourlife:transfer-insufficient", senderTokens));

            senderTokens -= quantity;
            playerTokens[sender.PlayerUID].Tokens = senderTokens;
            sender.Entity.WatchedAttributes.SetInt("vylTokenCount", senderTokens);

            string lowerTarget = targetPlayerName.ToLowerInvariant();
            string pendingKey = "pending_" + lowerTarget;

            // Check if target already has an entry (online or previously seen)
            var targetEntry = playerTokens.FirstOrDefault(e =>
                e.Value.PlayerName.Equals(targetPlayerName, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrEmpty(targetEntry.Key))
            {
                playerTokens[targetEntry.Key].Tokens += quantity;

                var targetPlayer = sapi.Server.Players.FirstOrDefault(p => p?.PlayerUID == targetEntry.Key) as IServerPlayer;
                if (targetPlayer != null)
                {
                    targetPlayer.Entity.WatchedAttributes.SetInt("vylTokenCount", playerTokens[targetEntry.Key].Tokens);
                    sapi.SendMessage(targetPlayer, 0, Lang.Get("valueyourlife:transfer-received", sender.PlayerName, quantity, playerTokens[targetEntry.Key].Tokens), EnumChatType.Notification);
                }

                SaveAllData();
                sapi.SendMessage(sender, 0, Lang.Get("valueyourlife:transfer-success", quantity, targetPlayerName, senderTokens), EnumChatType.Notification);
                return TextCommandResult.Success();
            }
            else
            {
                // Store as pending (will be applied on first login)
                playerTokens[pendingKey] = new PlayerBalanceData { PlayerName = targetPlayerName, Tokens = quantity };

                SaveAllData();

                sapi.SendMessage(sender, 0, Lang.Get("valueyourlife:transfer-success-offline", quantity, targetPlayerName, senderTokens), EnumChatType.Notification);
                return TextCommandResult.Success();
            }
        }

        private TextCommandResult OnBalanceCommand(TextCommandCallingArgs args)
        {
            var player = args.Caller.Player as IServerPlayer;
            int tokens = player.Entity.WatchedAttributes.GetInt("vylTokenCount", 0);
            return TextCommandResult.Success(Lang.Get("valueyourlife:token-balance", tokens));
        }

        private TextCommandResult OnCostCommand(TextCommandCallingArgs args)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Respawn Token Costs:");
            sb.AppendLine($"• Buy with Rusty Gears   → {config.RustyGearCost} gears per token  (/vyl buy)");
            sb.AppendLine($"• Trade Temporal Gears  → {config.TemporalGearCost} gears per token  (/vyl trade)");
            return TextCommandResult.Success(sb.ToString());
        }

        private TextCommandResult OnSetTokensCommand(TextCommandCallingArgs args)
        {
            string targetPlayerName = (string)args[0];
            int quantity = (int)args[1];
            if (quantity < 0) return TextCommandResult.Error("Quantity cannot be negative!");

            var targetPlayer = Array.Find(sapi.Server.Players, p => p?.PlayerName?.Equals(targetPlayerName, StringComparison.OrdinalIgnoreCase) == true);
            if (targetPlayer != null) // Online
            {
                targetPlayer.Entity.WatchedAttributes.SetInt("vylTokenCount", quantity);
                playerTokens[targetPlayer.PlayerUID] = new PlayerBalanceData { PlayerName = targetPlayerName, Tokens = quantity };
                SaveTokens();

                bool isDead = targetPlayer.Entity.GetBehavior<EntityBehaviorHealth>()?.Health <= 0f;
                if (isDead)
                {
                    targetPlayer.Entity.WatchedAttributes.SetBool("vylCanRespawn", quantity >= 1);
                    sapi.SendMessage(targetPlayer, 0, quantity >= 1 ? "Your token count has been set. You can now respawn!" : $"Your token count is now {quantity}, but you need 1 to respawn.", EnumChatType.Notification);
                }
                return TextCommandResult.Success($"Set {targetPlayerName}'s respawn token count to {quantity}.");
            }

            // Offline or unknown
            string targetUid = playerTokens.FirstOrDefault(e => e.Value.PlayerName.Equals(targetPlayerName, StringComparison.OrdinalIgnoreCase)).Key;
            if (targetUid != null)
            {
                playerTokens[targetUid].Tokens = quantity;
                SaveTokens();
                return TextCommandResult.Success($"Set offline player {targetPlayerName}'s respawn token count to {quantity}. It will apply when they join.");
            }

            var offlinePlayer = Array.Find(sapi.World.AllPlayers, p => p?.PlayerName?.Equals(targetPlayerName, StringComparison.OrdinalIgnoreCase) == true);
            targetUid = offlinePlayer?.PlayerUID ?? "pending_" + targetPlayerName.ToLowerInvariant();
            playerTokens[targetUid] = new PlayerBalanceData { PlayerName = targetPlayerName, Tokens = quantity };
            SaveTokens();
            return TextCommandResult.Success($"Set {targetPlayerName}'s respawn token count to {quantity}. " + (offlinePlayer == null ? "They haven't joined yet; tokens will sync on first join." : "It will apply when they join."));
        }

        private TextCommandResult OnGiveTokensCommand(TextCommandCallingArgs args)
        {
            string targetPlayerName = (string)args[0];
            int quantity = (int)args[1];
            if (quantity <= 0) return TextCommandResult.Error("Quantity must be positive!");

            var targetPlayer = Array.Find(sapi.Server.Players, p => p?.PlayerName?.Equals(targetPlayerName, StringComparison.OrdinalIgnoreCase) == true);
            if (targetPlayer != null) // Online
            {
                int newTokens = targetPlayer.Entity.WatchedAttributes.GetInt("vylTokenCount", 0) + quantity;
                targetPlayer.Entity.WatchedAttributes.SetInt("vylTokenCount", newTokens);
                playerTokens[targetPlayer.PlayerUID].Tokens = newTokens;
                SaveTokens();

                bool isDead = targetPlayer.Entity.GetBehavior<EntityBehaviorHealth>()?.Health <= 0f;
                if (isDead && newTokens >= 1)
                {
                    targetPlayer.Entity.WatchedAttributes.SetBool("vylCanRespawn", true);
                    sapi.SendMessage(targetPlayer, 0, "An admin has given you tokens. You can now respawn!", EnumChatType.Notification);
                }
                return TextCommandResult.Success(Lang.Get("valueyourlife:give-success", quantity, targetPlayerName, newTokens));
            }

            // Offline or unknown
            string targetUid = playerTokens.FirstOrDefault(e => e.Value.PlayerName.Equals(targetPlayerName, StringComparison.OrdinalIgnoreCase)).Key;
            if (targetUid != null)
            {
                int newTokens = playerTokens[targetUid].Tokens + quantity;
                playerTokens[targetUid].Tokens = newTokens;
                SaveTokens();
                return TextCommandResult.Success(Lang.Get("valueyourlife:give-success", quantity, targetPlayerName, newTokens));
            }

            var offlinePlayer = Array.Find(sapi.World.AllPlayers, p => p?.PlayerName?.Equals(targetPlayerName, StringComparison.OrdinalIgnoreCase) == true);
            targetUid = offlinePlayer?.PlayerUID ?? "pending_" + targetPlayerName.ToLowerInvariant();
            playerTokens[targetUid] = new PlayerBalanceData { PlayerName = targetPlayerName, Tokens = quantity };
            SaveTokens();
            return TextCommandResult.Success(Lang.Get("valueyourlife:give-success", quantity, targetPlayerName, quantity));
        }

        private TextCommandResult OnResetTokensCommand(TextCommandCallingArgs args)
        {
            string targetPlayerName = (string)args[0];
            var targetPlayer = Array.Find(sapi.Server.Players, p => p?.PlayerName?.Equals(targetPlayerName, StringComparison.OrdinalIgnoreCase) == true);

            if (targetPlayer != null) // Online
            {
                targetPlayer.Entity.WatchedAttributes.SetInt("vylTokenCount", 0);
                playerTokens[targetPlayer.PlayerUID].Tokens = 0;
                SaveTokens();

                bool isDead = targetPlayer.Entity.GetBehavior<EntityBehaviorHealth>()?.Health <= 0f;
                if (isDead)
                {
                    targetPlayer.Entity.WatchedAttributes.SetBool("vylCanRespawn", false);
                    sapi.SendMessage(targetPlayer, 0, "An admin has reset your tokens. You need 1 to respawn.", EnumChatType.Notification);
                }
                return TextCommandResult.Success(Lang.Get("valueyourlife:reset-success", targetPlayerName));
            }

            // Offline or unknown
            string targetUid = playerTokens.FirstOrDefault(e => e.Value.PlayerName.Equals(targetPlayerName, StringComparison.OrdinalIgnoreCase)).Key;
            if (targetUid == null) return TextCommandResult.Error(Lang.Get("valueyourlife:player-not-found", targetPlayerName));

            playerTokens[targetUid].Tokens = 0;
            SaveTokens();
            return TextCommandResult.Success(Lang.Get("valueyourlife:reset-success", targetPlayerName));
        }

        private void SaveAllData()
        {
            byte[] bytes = SerializerUtil.Serialize<Dictionary<string, PlayerBalanceData>>(playerTokens);
            sapi.WorldManager.SaveGame.StoreData("vyl_player_tokens", bytes);
        }
    }

    // NEW: Inline EntityBehavior class (lives in the namespace)
    public class EntityBehaviorVYLGrace : EntityBehavior
    {
        private ICoreServerAPI sapi;
        private ValueYourLife.VYLConfig config;
        private long graceEndTime = 0;

        public EntityBehaviorVYLGrace(Entity entity, ICoreServerAPI sapi, ValueYourLife.VYLConfig config) : base(entity)
        {
            this.sapi = sapi;
            this.config = config;
        }

        // Runs the moment the player entity spawns after respawn — earliest possible hook
        public override void OnEntitySpawn()
        {
            base.OnEntitySpawn();

            graceEndTime = sapi.World.ElapsedMilliseconds + (config.GracePeriodSeconds * 1000L);

            // Notify the player
            if (entity is EntityPlayer playerEntity)
            {
                var player = sapi.World.PlayerByUid(playerEntity.Player?.PlayerUID);
                if (player != null)
                {
                    sapi.SendMessage(player, GlobalConstants.GeneralChatGroup,
                        $"Grace period started: {config.GracePeriodSeconds} seconds of invulnerability!",
                        EnumChatType.OwnMessage);
                }
            }
        }

        public override void OnEntityReceiveDamage(DamageSource damageSource, ref float damage)
        {
            if (sapi.World.ElapsedMilliseconds < graceEndTime)
            {
                damage = 0f;

                // Countdown every 5 seconds
                long remaining = (graceEndTime - sapi.World.ElapsedMilliseconds) / 1000;
                if (remaining > 0 && remaining % 5 == 0)
                {
                    if (entity is EntityPlayer playerEntity)
                    {
                        var player = sapi.World.PlayerByUid(playerEntity.Player?.PlayerUID);
                        if (player != null)
                        {
                            sapi.SendMessage(player, GlobalConstants.GeneralChatGroup,
                                $"Grace period: {remaining} seconds left",
                                EnumChatType.OwnMessage);
                        }
                    }
                }
            }
        }

        public override string PropertyName() => "vylGrace";
        public override void Initialize(EntityProperties properties, JsonObject attributes) { }
        public override void OnEntityDespawn(EntityDespawnData despawn) { }
    }


    public class GuiDialogValueYourLife : GuiDialog
    {
        private ICoreClientAPI capi;

        public GuiDialogValueYourLife(ICoreClientAPI capi, int tokens, int rustyCost, int temporalCost, int rustyCount, int temporalCount, string graceText) : base(capi)
        {
            this.capi = capi;
            this.tokens = tokens;
            this.rustyCost = rustyCost;
            this.temporalCost = temporalCost;
            this.rustyCount = rustyCount;
            this.temporalCount = temporalCount;
            this.graceText = graceText;
            ComposeDialog();
        }

        private int tokens, rustyCost, temporalCost, rustyCount, temporalCount;
        private string graceText;

        private void ComposeDialog()
        {
            ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.CenterMiddle)
                .WithFixedSize(430, 520);

            ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;

            var titleFont = CairoFont.WhiteMediumText();
            var normalFont = CairoFont.WhiteSmallText();
            var buttonFont = CairoFont.WhiteSmallText().WithFontSize(20);

            ElementBounds titleBounds = ElementBounds.Fixed(0, 25, 430, 40).WithAlignment(EnumDialogArea.CenterTop);
            ElementBounds tokenBounds = ElementBounds.Fixed(30, 72, 370, 30);
            ElementBounds rustyBounds = ElementBounds.Fixed(30, 102, 370, 25);
            ElementBounds temporalBounds = ElementBounds.Fixed(30, 127, 370, 25);
            ElementBounds graceBounds = ElementBounds.Fixed(30, 155, 370, 25);

            ElementBounds buy1Bounds = ElementBounds.Fixed(0, 190, 210, 30);
            ElementBounds trade1Bounds = ElementBounds.Fixed(0, 225, 210, 30);
            ElementBounds buyAllBounds = ElementBounds.Fixed(220, 190, 210, 30);
            ElementBounds tradeAllBounds = ElementBounds.Fixed(220, 225, 210, 30);

            // Transfer section
            ElementBounds recipientLabelBounds = ElementBounds.Fixed(30, 275, 150, 25);
            ElementBounds recipientInputBounds = ElementBounds.Fixed(180, 272, 220, 30);
            ElementBounds amountLabelBounds = ElementBounds.Fixed(30, 315, 150, 25);
            ElementBounds amountInputBounds = ElementBounds.Fixed(180, 312, 80, 30);
            ElementBounds transferButtonBounds = ElementBounds.Fixed(270, 312, 130, 30);

            ElementBounds closeBounds = ElementBounds.Fixed(395, 15, 30, 30);

            SingleComposer = capi.Gui.CreateCompo("vyltokenui", dialogBounds)
                .AddShadedDialogBG(bgBounds, true)
                .BeginChildElements(bgBounds)
                    .AddStaticText("Value Your Life", titleFont, titleBounds)
                    .AddStaticText($"Tokens remaining: {tokens}", normalFont, tokenBounds)
                    .AddStaticText($"Rusty Gears: {rustyCount}", normalFont, rustyBounds)
                    .AddStaticText($"Temporal Gears: {temporalCount}", normalFont, temporalBounds)
                    .AddStaticText(graceText, normalFont, graceBounds)

                    .AddButton($" Buy 1 (-{rustyCost} Rusty) ", OnBuyOne, buy1Bounds, buttonFont)
                    .AddButton($" Trade 1 (-{temporalCost} Temporal) ", OnTradeOne, trade1Bounds, buttonFont)
                    .AddButton(" Buy All Possible ", OnBuyAll, buyAllBounds, buttonFont)
                    .AddButton(" Trade All Possible ", OnTradeAll, tradeAllBounds, buttonFont)

                    // Transfer section
                    .AddStaticText("Recipient name:", normalFont, recipientLabelBounds)
                    .AddTextInput(recipientInputBounds, _ => { }, buttonFont, "vyl-recipient")
                    .AddStaticText("Amount:", normalFont, amountLabelBounds)
                    .AddNumberInput(amountInputBounds, _ => { }, buttonFont, "vyl-amount")
                    .AddButton(" Transfer ", OnTransfer, transferButtonBounds, buttonFont)

                    .AddButton("X", () => TryClose(), closeBounds)
                .EndChildElements()
                .Compose();
        }

        private bool OnBuyOne() { capi.SendChatMessage("/vyl buy 1"); Refresh(); return true; }
        private bool OnTradeOne() { capi.SendChatMessage("/vyl trade 1"); Refresh(); return true; }
        private bool OnBuyAll() { capi.SendChatMessage("/vyl buyall"); Refresh(); return true; }
        private bool OnTradeAll() { capi.SendChatMessage("/vyl tradeall"); Refresh(); return true; }

        private bool OnTransfer()
        {
            var nameInput = SingleComposer.GetTextInput("vyl-recipient");
            var amountInput = SingleComposer.GetNumberInput("vyl-amount");

            string recipient = nameInput.GetText().Trim();
            string amountStr = amountInput.GetText();

            if (string.IsNullOrEmpty(recipient))
            {
                capi.TriggerChatMessage("Please enter a recipient name.");
                return true;
            }

            if (!int.TryParse(amountStr, out int amount) || amount < 1)
            {
                capi.TriggerChatMessage("Please enter a valid amount (1 or more).");
                return true;
            }

            capi.SendChatMessage($"/vyl transfer {recipient} {amount}");

            // Clear fields
            nameInput.SetValue("");
            amountInput.SetValue("1");

            Refresh();   // auto-refresh numbers
            return true;
        }

        private void Refresh()
        {
            capi.Event.RegisterCallback(_ => ReopenWithFreshData(), 80);
        }

        private void ReopenWithFreshData()
        {
            if (IsOpened()) TryClose();
            var mod = capi.ModLoader.GetModSystem<ValueYourLifeModSystem>();
            mod.OpenValueYourLifeDialog(capi);
        }

        public override string ToggleKeyCombinationCode => "vyl-ui";
    }


}