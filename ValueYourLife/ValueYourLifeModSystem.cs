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
        private VYLConfig config;
        private Dictionary<string, PlayerBalanceData> playerTokens;

        public override void Start(ICoreAPI api)
        {
            sapi = api as ICoreServerAPI;
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            config = api.LoadModConfig<VYLConfig>("valueyourlife.json") ?? new VYLConfig();
            api.StoreModConfig(config, "valueyourlife.json");

            // === 100% WORKING PER-WORLD TOKEN STORAGE ===
            byte[] data = api.WorldManager.SaveGame.GetData("vyl_player_tokens");
            if (data != null && data.Length > 0)
            {
                playerTokens = SerializerUtil.Deserialize<Dictionary<string, PlayerBalanceData>>(data);
            }
            else
            {
                playerTokens = new Dictionary<string, PlayerBalanceData>();
            }

            // Save tokens every time the world saves
            api.Event.GameWorldSave += () =>
            {
                byte[] bytes = SerializerUtil.Serialize<Dictionary<string, PlayerBalanceData>>(playerTokens);
                api.WorldManager.SaveGame.StoreData("vyl_player_tokens", bytes);
            };

            // === EVENT HOOKS ===
            sapi.Event.PlayerDeath += OnPlayerDeath;
            sapi.Event.PlayerRespawn += OnPlayerRespawn;
            sapi.Event.PlayerNowPlaying += OnPlayerNowPlaying;
            sapi.Event.PlayerDisconnect += OnPlayerDisconnect;

            // === CHAT COMMANDS ===
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

                var player = capi.World.Player;
                int tokens = player.Entity.WatchedAttributes.GetInt("vylTokenCount", 0);

                // Get grace status
                long lastRespawn = player.Entity.WatchedAttributes.GetLong("vylLastRespawn", 0);
                long now = capi.World.ElapsedMilliseconds;
                bool inGrace = lastRespawn > 0 && (now - lastRespawn) < config.GracePeriodSeconds * 1000L;
                string graceText = inGrace ? $"Grace active: {(config.GracePeriodSeconds - (now - lastRespawn) / 1000)}s left" : "No grace active";

                new GuiDialogTokenUI(capi, tokens, config.RustyGearCost, config.TemporalGearCost, graceText).TryOpen();
                return true;
            });
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

            if (!playerTokens.TryGetValue(uid, out var data))
            {
                data = new PlayerBalanceData { PlayerName = player.PlayerName, Tokens = config.StartingTokens };
                playerTokens[uid] = data;
                SaveTokens();
            }
            else
            {
                data.PlayerName = player.PlayerName;
            }

            player.Entity.WatchedAttributes.SetInt("vylTokenCount", data.Tokens);

            var tokenTree = player.Entity.WatchedAttributes.GetTreeAttribute("vylTokens");
            if (tokenTree == null)
            {
                tokenTree = new TreeAttribute();
                player.Entity.WatchedAttributes.SetAttribute("vylTokens", tokenTree);
            }
            tokenTree.SetInt("count", data.Tokens);
            player.Entity.WatchedAttributes.MarkPathDirty("vylTokens");

            player.Entity.WatchedAttributes.SetBool("vylCanRespawn", data.Tokens >= 1);

            // Force grace period OFF on first join
            player.Entity.WatchedAttributes.RemoveAttribute("vylLastRespawn");
            player.Entity.WatchedAttributes.SetLong("vylLastRespawn", 0);
            player.Entity.WatchedAttributes.MarkPathDirty("vylLastRespawn");

            // Optional: attach grace behavior (if you still use it for messages)
            if (!player.Entity.HasBehavior<EntityBehaviorVYLGrace>())
            {
                player.Entity.AddBehavior(new EntityBehaviorVYLGrace(player.Entity, sapi, config));
            }
            string color = data.Tokens switch
            {
                0 => "#ff4444",
                <= 3 => "#ffaa00",
                _ => "#ffffff"
            };

            sapi.SendMessage(player, 0,
                $"<font color=\"{color}\"><strong>You have {data.Tokens} respawn token{(data.Tokens == 1 ? "" : "s")} remaining.</strong></font>",
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
            string targetPlayerName = (string)args[0];
            int quantity = (int)args[1];

            if (quantity <= 0) return TextCommandResult.Error("Quantity must be positive!");
            if (sender.PlayerName.Equals(targetPlayerName, StringComparison.OrdinalIgnoreCase))
                return TextCommandResult.Error(Lang.Get("valueyourlife:transfer-self"));

            int senderTokens = playerTokens[sender.PlayerUID].Tokens;
            if (quantity > senderTokens)
                return TextCommandResult.Error(Lang.Get("valueyourlife:transfer-insufficient", senderTokens));

            var targetPlayer = Array.Find(sapi.Server.Players, p => p?.PlayerName?.Equals(targetPlayerName, StringComparison.OrdinalIgnoreCase) == true);
            if (targetPlayer != null) // Online
            {
                senderTokens -= quantity;
                int targetTokens = playerTokens[targetPlayer.PlayerUID].Tokens + quantity;
                playerTokens[sender.PlayerUID].Tokens = senderTokens;
                playerTokens[targetPlayer.PlayerUID].Tokens = targetTokens;
                sender.Entity.WatchedAttributes.SetInt("vylTokenCount", senderTokens);
                targetPlayer.Entity.WatchedAttributes.SetInt("vylTokenCount", targetTokens);
                SaveTokens();

                sapi.SendMessage(sender, 0, Lang.Get("valueyourlife:transfer-success", quantity, targetPlayerName, senderTokens), EnumChatType.Notification);
                sapi.SendMessage(targetPlayer, 0, Lang.Get("valueyourlife:transfer-received", sender.PlayerName, quantity, targetTokens), EnumChatType.Notification);
                return TextCommandResult.Success();
            }

            // Offline or unknown
            string targetUid = playerTokens.FirstOrDefault(e => e.Value.PlayerName.Equals(targetPlayerName, StringComparison.OrdinalIgnoreCase)).Key;
            if (targetUid != null)
            {
                senderTokens -= quantity;
                playerTokens[sender.PlayerUID].Tokens = senderTokens;
                playerTokens[targetUid].Tokens += quantity;
                sender.Entity.WatchedAttributes.SetInt("vylTokenCount", senderTokens);
                SaveTokens();
                return TextCommandResult.Success(Lang.Get("valueyourlife:transfer-success-offline", quantity, targetPlayerName, senderTokens));
            }

            var offlinePlayer = Array.Find(sapi.World.AllPlayers, p => p?.PlayerName?.Equals(targetPlayerName, StringComparison.OrdinalIgnoreCase) == true);
            targetUid = offlinePlayer?.PlayerUID ?? "pending_" + targetPlayerName.ToLowerInvariant();
            senderTokens -= quantity;
            playerTokens[sender.PlayerUID].Tokens = senderTokens;
            playerTokens[targetUid] = new PlayerBalanceData { PlayerName = targetPlayerName, Tokens = quantity };
            sender.Entity.WatchedAttributes.SetInt("vylTokenCount", senderTokens);
            SaveTokens();
            return TextCommandResult.Success(Lang.Get(offlinePlayer != null ? "valueyourlife:transfer-success-offline" : "valueyourlife:transfer-success-new", quantity, targetPlayerName, senderTokens));
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

    public class GuiDialogTokenUI : GuiDialog
    {
        public GuiDialogTokenUI(ICoreClientAPI capi, int tokens, int rustyCost, int temporalCost, string graceText) : base(capi)
        {
            // Dialog bounds (unchanged)
            ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);

            // Background (unchanged)
            ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;

            // Title bounds (down 10px, left 5px)
            ElementBounds titleBounds = ElementBounds.Fixed(5, 25, 300, 30);

            // Token bounds (down 10px, left 5px, plain for now)
            ElementBounds tokenBounds = ElementBounds.Fixed(5, 65, 300, 30);
            string tokenText = $"Tokens remaining: {tokens}";

            // Grace bounds (down 10px, left 5px)
            ElementBounds graceBounds = ElementBounds.Fixed(5, 100, 300, 30);

            // Cost bounds (down 10px, left 5px)
            ElementBounds costBounds = ElementBounds.Fixed(5, 135, 300, 60);
            string costText = $"Buy: {rustyCost} rusty gears (/vyl buy)\nTrade: {temporalCost} temporal gears (/vyl trade)";

            // Close button bounds (wider square 40x40, top-right)
            ElementBounds closeBounds = ElementBounds.Fixed(250, 170, 40, 40);

            // Compose the dialog
            SingleComposer = capi.Gui.CreateCompo("vyltokenui", dialogBounds)
                .AddShadedDialogBG(bgBounds, true, 5.0, 0.75f)
                .BeginChildElements(bgBounds)
                    .AddStaticText("Value Your Life - Tokens", CairoFont.WhiteSmallText(), titleBounds)
                    .AddStaticText(tokenText, CairoFont.WhiteSmallText(), tokenBounds)
                    .AddStaticText(graceText, CairoFont.WhiteSmallText(), graceBounds)
                    .AddStaticText(costText, CairoFont.WhiteSmallText(), costBounds)
                    .AddButton("X", () => TryClose(), closeBounds)
                .EndChildElements()
                .Compose();
        }

        public override string ToggleKeyCombinationCode => "vyl-ui";
    }

}