using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using XSkills;

namespace ValueYourLife
{
    [Serializable]
    public class PlayerBalanceData
    {
        public string PlayerName { get; set; }
        public int Tokens { get; set; }
    }

    public class VYLConfig
    {
        public int RustyGearCost { get; set; } = 10;
        public int TemporalGearCost { get; set; } = 1;
        public int StartingTokens { get; set; } = 5;
    }

    public class ValueYourLifeModSystem : ModSystem
    {
        private ICoreServerAPI sapi;
        private VYLConfig config;
        private Dictionary<string, PlayerBalanceData> playerTokens;
        private string tokensFilePath;

        public override void Start(ICoreAPI api)
        {
            sapi = api as ICoreServerAPI;
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            config = api.LoadModConfig<VYLConfig>("valueyourlife.json") ?? new VYLConfig();
            api.StoreModConfig(config, "valueyourlife.json");

            tokensFilePath = Path.Combine(api.DataBasePath, "ModData", "valueyourlife_tokens.json");
            playerTokens = api.LoadModConfig<Dictionary<string, PlayerBalanceData>>(tokensFilePath) ?? new Dictionary<string, PlayerBalanceData>();

            sapi.Event.PlayerDeath += OnPlayerDeath;
            sapi.Event.PlayerRespawn += OnPlayerRespawn;
            sapi.Event.PlayerNowPlaying += OnPlayerNowPlaying;
            sapi.Event.PlayerDisconnect += OnPlayerDisconnect;

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

        public override void StartClientSide(ICoreClientAPI api) { }

        private void SaveTokens()
        {
            sapi.StoreModConfig(playerTokens, tokensFilePath);
        }

        private void OnPlayerDeath(IServerPlayer player, DamageSource damageSource)
        {
            player.Entity.WatchedAttributes.SetLong("vylLastDeath", sapi.World.ElapsedMilliseconds);
            int tokens = player.Entity.WatchedAttributes.GetInt("vylTokenCount", 0);
            player.Entity.WatchedAttributes.SetBool("vylCanRespawn", tokens >= 1);

            if (tokens < 1)
            {
                sapi.SendMessage(player, 0, Lang.Get("valueyourlife:respawn-failed-tokens", 1, tokens), EnumChatType.Notification);
                ResetXSkills(player);
            }
        }

        private void ResetXSkills(IServerPlayer player)
        {
            try
            {
                // Check if XSkills is loaded and get the SkillSystem
                var skillSystem = sapi.ModSystems.GetModSystem<SkillSystem>();
                if (skillSystem == null)
                {
                    sapi.SendMessage(player, 0, "XSkills mod not detected. Skills cannot be reset.", EnumChatType.Notification);
                    return;
                }

                // Get the player's SkillPlayer data
                var skillPlayer = skillSystem.GetSkillPlayer(player);
                if (skillPlayer == null)
                {
                    sapi.SendMessage(player, 0, "Failed to access XSkills data. Skills cannot be reset.", EnumChatType.Notification);
                    return;
                }

                // Reset all skills by setting tiers to 0 and refunding points
                foreach (var skill in skillPlayer.Skills.Values)
                {
                    while (skill.Tier > 0)
                    {
                        skill.Unlearn(skillSystem); // Reduces tier by 1, refunds points
                    }
                }

                // Save the updated skill data
                skillPlayer.Save(skillSystem);

                sapi.SendMessage(player, 0, "You ran out of respawn tokens! All XSkills have been reset.", EnumChatType.Notification);
            }
            catch (Exception ex)
            {
                sapi.Logger.Warning($"Failed to reset XSkills for player {player.PlayerName}: {ex.Message}");
            }
        }

        private void OnPlayerRespawn(IServerPlayer player)
        {
            bool canRespawn = player.Entity.WatchedAttributes.GetBool("vylCanRespawn", false);
            int tokens = player.Entity.WatchedAttributes.GetInt("vylTokenCount", 0);

            if (canRespawn && tokens >= 1)
            {
                player.Entity.WatchedAttributes.SetInt("vylTokenCount", tokens - 1);
                playerTokens[player.PlayerUID].Tokens = tokens - 1;
                SaveTokens();
                sapi.SendMessage(player, 0, Lang.Get("valueyourlife:respawn-success-tokens", 1, tokens - 1), EnumChatType.Notification);
            }
            else
            {
                player.Entity.ReceiveDamage(new DamageSource { Source = EnumDamageSource.Internal, Type = EnumDamageType.Gravity }, 100f);
                sapi.SendMessage(player, 0, Lang.Get("valueyourlife:respawn-failed-tokens", 1, tokens), EnumChatType.Notification);
            }
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
            bool isDead = player.Entity.GetBehavior<EntityBehaviorHealth>()?.Health <= 0f;

            player.Entity.WatchedAttributes.SetBool("vylCanRespawn", data.Tokens >= 1);
            if (isDead && data.Tokens < 1)
            {
                player.Entity.ReceiveDamage(new DamageSource { Source = EnumDamageSource.Internal, Type = EnumDamageType.Gravity }, 100f);
                sapi.SendMessage(player, 0, Lang.Get("valueyourlife:respawn-failed-tokens", 1, data.Tokens), EnumChatType.Notification);
            }
        }

        private void OnPlayerDisconnect(IServerPlayer player)
        {
            int tokens = player.Entity.WatchedAttributes.GetInt("vylTokenCount", 0);
            playerTokens[player.PlayerUID] = new PlayerBalanceData { PlayerName = player.PlayerName, Tokens = tokens };
            SaveTokens();
        }

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

            return TextCommandResult.Success(Lang.Get("valueyourlife:buy-success", quantity, totalGearCost, newTokens));
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
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Respawn Token Costs:");
            sb.AppendLine($"- Rusty Gears: {config.RustyGearCost} per token");
            sb.AppendLine($"- Temporal Gears: {config.TemporalGearCost} per token");
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
}