using ExileCore2;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.Elements;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared;
using ExileCore2.Shared.Cache;
using ExileCore2.Shared.Enums;
using ExileCore2.Shared.Helpers;
using ImGuiNET;
using ItemFilterLibrary;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExileCore2.PoEMemory;
using RectangleF = ExileCore2.Shared.RectangleF;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace PickIt;

public partial class PickIt : BaseSettingsPlugin<PickItSettings>
{
    private readonly CachedValue<List<LabelOnGround>> _chestLabels;
    private readonly CachedValue<List<LabelOnGround>> _doorLabels;
    private readonly CachedValue<List<LabelOnGround>> _portalLabels;
    private readonly CachedValue<LabelOnGround> _transitionLabel;
    private readonly CachedValue<List<LabelOnGround>> _corpseLabels;
    private readonly CachedValue<List<LabelOnGround>> _shrineLabels;
    private readonly CachedValue<bool[,]> _inventorySlotsCache;
    private ServerInventory _inventoryItems;
    private SyncTask<bool> _pickUpTask;
    private bool _isCurrentlyPicking;
    private long _lastClick;
    private List<ItemFilter> _itemFilters;
    private bool _pluginBridgeModeOverride;
    private bool[,] InventorySlots => _inventorySlotsCache.Value;
    private readonly Stopwatch _sinceLastClick = Stopwatch.StartNew();
    private Element UIHoverWithFallback => GameController.IngameState.UIHover switch { null or { Address: 0 } => GameController.IngameState.UIHoverElement, var s => s };
    private bool OkayToClick => _sinceLastClick.ElapsedMilliseconds > Settings.PauseBetweenClicks;

    public PickIt()
    {
        _inventorySlotsCache = new FrameCache<bool[,]>(() => GetContainer2DArray(_inventoryItems));
        _chestLabels = new TimeCache<List<LabelOnGround>>(UpdateChestList, 200);
        _doorLabels = new TimeCache<List<LabelOnGround>>(UpdateDoorList, 200);
        _corpseLabels = new TimeCache<List<LabelOnGround>>(UpdateCorpseList, 200);
        _portalLabels = new TimeCache<List<LabelOnGround>>(UpdatePortalList, 200);
        _transitionLabel = new TimeCache<LabelOnGround>(() => GetLabel(@"Metadata/MiscellaneousObjects/AreaTransition_Animate"), 200);
    }

    public override bool Initialise()
    {
        #region Register keys

        Settings.PickUpKey.OnValueChanged += () => Input.RegisterKey(Settings.PickUpKey);
        Settings.ProfilerHotkey.OnValueChanged += () => Input.RegisterKey(Settings.ProfilerHotkey);

        Input.RegisterKey(Settings.PickUpKey);
        Input.RegisterKey(Settings.ProfilerHotkey);
        Input.RegisterKey(Keys.Escape);

        #endregion

        Settings.ReloadFilters.OnPressed = LoadRuleFiles;
        LoadRuleFiles();
        GameController.PluginBridge.SaveMethod("PickIt.ListItems", () => GetItemsToPickup(false).Select(x => x.QueriedItem).ToList());
        GameController.PluginBridge.SaveMethod("PickIt.IsActive", () => _pickUpTask?.GetAwaiter().IsCompleted == false && _isCurrentlyPicking);
        GameController.PluginBridge.SaveMethod("PickIt.SetWorkMode", (bool running) => { _pluginBridgeModeOverride = running; });
        return true;
    }

    private enum WorkMode
    {
        Stop,
        Lazy,
        Manual
    }

    private WorkMode GetWorkMode()
    {
        if (!GameController.Window.IsForeground() ||
            !Settings.Enable ||
            Input.GetKeyState(Keys.Escape))
        {
            _pluginBridgeModeOverride = false;
            return WorkMode.Stop;
        }

        if (Input.GetKeyState(Settings.ProfilerHotkey.Value))
        {
            var sw = Stopwatch.StartNew();
            var looseVar2 = GetItemsToPickup(false).FirstOrDefault();
            sw.Stop();
            LogMessage($"GetItemsToPickup Elapsed Time: {sw.ElapsedTicks} Item: {looseVar2?.BaseName} Distance: {looseVar2?.Distance}");
        }

        if (Input.GetKeyState(Settings.PickUpKey.Value) || _pluginBridgeModeOverride)
        {
            return WorkMode.Manual;
        }

        if (CanLazyLoot())
        {
            return WorkMode.Lazy;
        }

        return WorkMode.Stop;
    }

    private DateTime DisableLazyLootingTill { get; set; }

    public override void Tick()
    {
        var playerInvCount = GameController?.Game?.IngameState?.Data?.ServerData?.PlayerInventories?.Count;
        if (playerInvCount is null or 0)
            return;

        if (Settings.AutoClickHoveredLootInRange.Value)
        {
            var hoverItemIcon = UIHoverWithFallback.AsObject<HoverItemIcon>();
            if (hoverItemIcon != null && !GameController.IngameState.IngameUi.InventoryPanel.IsVisible &&
                !Input.IsKeyDown(Keys.LButton))
            {
                if (hoverItemIcon.Item != null && OkayToClick)
                {
                    var groundItem =
                        GameController.IngameState.IngameUi.ItemsOnGroundLabels.FirstOrDefault(e =>
                            e.Label.Address == hoverItemIcon.Address);
                    if (groundItem != null)
                    {
                        var doWePickThis = Settings.PickUpEverything || (_itemFilters?.Any(filter =>
                            filter.Matches(new ItemData(groundItem, GameController))) ?? false);
                        if (doWePickThis && groundItem?.ItemOnGround.DistancePlayer < 20f)
                        {
                            _sinceLastClick.Restart();
                            Input.Click(MouseButtons.Left);
                        }
                    }
                }
            }
        }

        _inventoryItems = GameController.Game.IngameState.Data.ServerData.PlayerInventories[0].Inventory;
        DrawIgnoredCellsSettings();
        if (Input.GetKeyState(Settings.LazyLootingPauseKey)) DisableLazyLootingTill = DateTime.Now.AddSeconds(2);
        
        return;
    }

    public override void Render()
    {
        if (Settings.DebugHighlight)
        {
            foreach (var item in GetItemsToPickup(false))
            {
                Graphics.DrawFrame(item.QueriedItem.ClientRect, Color.Violet, 5);
            }
            foreach (var door in _doorLabels.Value)
            {
                Graphics.DrawFrame(door.Label.GetClientRect(), Color.Violet, 5);
            }
            foreach (var chest in _chestLabels.Value)
            {
                Graphics.DrawFrame(chest.Label.GetClientRect(), Color.Violet, 5);
            }
        }

        if (GetWorkMode() != WorkMode.Stop)
        {
            TaskUtils.RunOrRestart(ref _pickUpTask, RunPickerIterationAsync);
        }
        else
        {
            _pickUpTask = null;
        }

        if (_pickUpTask?.GetAwaiter().IsCompleted != false)
        {
            _isCurrentlyPicking = false;
        }

        if (Settings.FilterTest.Value is { Length: > 0 } &&
            GameController.IngameState.UIHover is { Address: not 0 } h &&
            h.Entity.IsValid)
        {
            var f = ItemFilter.LoadFromString(Settings.FilterTest);
            var matched = f.Matches(new ItemData(h.Entity, GameController));
            DebugWindow.LogMsg($"Debug item match: {matched}");
        }
    }

    //TODO: Make function pretty
    private void DrawIgnoredCellsSettings()
    {
        if (!Settings.ShowInventoryView.Value)
            return;

        var opened = true;

        const ImGuiWindowFlags nonMoveableFlag = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoBackground |
                                                 ImGuiWindowFlags.NoTitleBar |
                                                 ImGuiWindowFlags.NoInputs |
                                                 ImGuiWindowFlags.NoFocusOnAppearing;

        ImGui.SetNextWindowPos(Settings.InventoryPos.Value);
        if (ImGui.Begin($"{Name}##InventoryCellMap", ref opened,nonMoveableFlag))
        {
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1);
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0,0));

            var numb = 0;
            for (var i = 0; i < 5; i++)
            for (var j = 0; j < 12; j++)
            {
                var toggled = Convert.ToBoolean(InventorySlots[i, j]);
                if (ImGui.Checkbox($"##{numb}IgnoredCells", ref toggled)) InventorySlots[i, j] = toggled;

                if (j != 11) ImGui.SameLine();

                numb += 1;
            }

            ImGui.PopStyleVar(2);

            ImGui.End();
        }
    }

    private bool IsItSafeToPickit()
    {
        if (Settings.NoLootingWhileEnemyClose && GameController.EntityListWrapper.ValidEntitiesByType[EntityType.Monster]
                    .Any(x => x?.GetComponent<Monster>() != null && x.IsValid && x.IsHostile && x.IsAlive
                              && !x.IsHidden && !x.Path.Contains("ElementalSummoned")
                              && Vector3.Distance(GameController.Player.Pos, x.GetComponent<Render>().Pos) <= Settings.ItemPickitRange))
            return false;
        else
            return true;
    }

    private bool DoWePickThis(PickItItemData item)
    {
        if (!IsItSafeToPickit())
            return false;
        else
            return Settings.PickUpEverything || (_itemFilters?.Any(filter => filter.Matches(item)) ?? false);
    }

    private List<LabelOnGround> UpdateChestList()
    {
        bool IsFittingEntity(Entity entity)
        {
            return entity?.Path is { } path &&
                   (path.StartsWith("Metadata/Chests", StringComparison.Ordinal) ||
                   path.Contains("CampsiteChest", StringComparison.Ordinal)) &&
                   entity.HasComponent<Chest>();
        }

        if (!IsItSafeToPickit())
            return [];

        if (GameController.EntityListWrapper.OnlyValidEntities.Any(IsFittingEntity))
        {
            return GameController?.Game?.IngameState?.IngameUi?.ItemsOnGroundLabelsVisible
                .Where(x => x.Address != 0 &&
                            x.IsVisible &&
                            IsFittingEntity(x.ItemOnGround))
                .OrderBy(x => x.ItemOnGround.DistancePlayer)
                .ToList() ?? [];
        }

        return [];
    }

    private List<LabelOnGround> UpdateDoorList()
    {
        bool IsFittingEntity(Entity entity)
        {
            return entity?.Path is { } path && (
                    path.Contains("DoorRandom", StringComparison.Ordinal) ||
                    path.Contains("Door", StringComparison.Ordinal) ||
                    path.Contains("Endgame/TowerCompletion", StringComparison.Ordinal) ||
                    path.Contains("WaterLevelLever", StringComparison.Ordinal));
        }

        if (!IsItSafeToPickit())
            return [];

        if (GameController.EntityListWrapper.OnlyValidEntities.Any(IsFittingEntity))
        {
            return GameController?.Game?.IngameState?.IngameUi?.ItemsOnGroundLabelsVisible
                .Where(x => x.Address != 0 &&
                            x.IsVisible &&
                            IsFittingEntity(x.ItemOnGround))
                .OrderBy(x => x.ItemOnGround.DistancePlayer)
                .ToList() ?? [];
        }

        return [];
    }

    private List<LabelOnGround> UpdateCorpseList()
    {
        bool IsFittingEntity(Entity entity)
        {
            return entity?.Path is "Metadata/Terrain/Leagues/Necropolis/Objects/NecropolisCorpseMarker";
        }

        if (!IsItSafeToPickit())
            return [];

        if (GameController.EntityListWrapper.OnlyValidEntities.Any(IsFittingEntity))
        {
            return GameController?.Game?.IngameState?.IngameUi?.ItemsOnGroundLabelsVisible
                .Where(x => x.Address != 0 &&
                            x.IsVisible &&
                            IsFittingEntity(x.ItemOnGround))
                .OrderBy(x => x.ItemOnGround.DistancePlayer)
                .ToList() ?? [];
        }

        return [];
    }

    private List<LabelOnGround> UpdatePortalList()
    {
        bool IsFittingEntity(Entity entity)
        {
            return entity?.Path is { } path &&
                   path.StartsWith("Metadata/MiscellaneousObjects/Portal", StringComparison.Ordinal) ||
                   entity.HasComponent<Portal>();
        }

        if (!IsItSafeToPickit())
            return [];


        if (GameController.EntityListWrapper.OnlyValidEntities.Any(IsFittingEntity))
        {
            return GameController?.Game?.IngameState?.IngameUi?.ItemsOnGroundLabelsVisible
                .Where(x => x.Address != 0 &&
                            x.IsVisible &&
                            IsFittingEntity(x.ItemOnGround))
                .OrderBy(x => x.ItemOnGround.DistancePlayer)
                .ToList() ?? [];
        }
        return [];
    }

    private bool CanLazyLoot()
    {
        if (!Settings.LazyLooting) return false;
        if (DisableLazyLootingTill > DateTime.Now) return false;
        try
        {
            if (Settings.NoLazyLootingWhileEnemyClose && GameController.EntityListWrapper.ValidEntitiesByType[EntityType.Monster]
                    .Any(x => x?.GetComponent<Monster>() != null && x.IsValid && x.IsHostile && x.IsAlive
                              && !x.IsHidden && !x.Path.Contains("ElementalSummoned")
                              && Vector3.Distance(GameController.Player.Pos, x.GetComponent<Render>().Pos) < Settings.ItemPickitRange)) 
                return false;
        }
        catch (NullReferenceException)
        {
        }

        return true;
    }

    private bool ShouldLazyLoot(PickItItemData item)
    {
        if (!Settings.LazyLooting)
            return false;

        if (Settings.LazyLooting && Settings.MiscPickit && Settings.ClickDoors && _doorLabels != null)
        {
            foreach (var door in _doorLabels?.Value)
            {
                if (door == null)
                    continue;
                //LogMessage($"Checking door label: {door.Label.Address}, Distance: {door.ItemOnGround.DistancePlayer}");
                var doorLabel = _doorLabels?.Value.FirstOrDefault(x =>
                    x.ItemOnGround.DistancePlayer <= Settings.MiscPickitRange &&
                    IsLabelClickable(x.Label, null));

                if (doorLabel != null)
                {
                    //LogMessage($"Door label found: {doorLabel.Label.Address}, Distance: {doorLabel.ItemOnGround.DistancePlayer}");
                    return true;
                }
            }
        }

        if (item == null)
            return false;

        var itemPos = item.QueriedItem.Entity.Pos;
        var playerPos = GameController.Player.Pos;
        return Math.Abs(itemPos.Z - playerPos.Z) <= 50 &&
               itemPos.Xy().DistanceSquared(playerPos.Xy()) <= 275 * 275;
    }

    private bool ShouldLazyLootMisc(LabelOnGround label)
    {
        if (!Settings.LazyLooting && !Settings.MiscPickit)
            return false;

        if (label == null)
            return false;

        var itemPos = label.ItemOnGround.Pos;
        var playerPos = GameController.Player.Pos;
        return Math.Abs(itemPos.Z - playerPos.Z) <= 50 &&
               itemPos.Xy().DistanceSquared(playerPos.Xy()) <= 275 * 275;
    }

    private bool IsLabelClickable(Element element, RectangleF? customRect)
    {
        if (element is not { IsValid: true, IsVisible: true, IndexInParent: not null })
        {
            return false;
        }

        var center = (customRect ?? element.GetClientRect()).Center;

        var gameWindowRect = GameController.Window.GetWindowRectangleTimeCache with { Location = Vector2.Zero };
        gameWindowRect.Inflate(-36, -36);
        return gameWindowRect.Contains(center.X, center.Y);
    }

    private LabelOnGround GetLabel(string id)
    {
        var labels = GameController?.Game?.IngameState?.IngameUi?.ItemsOnGroundLabels;
        if (labels == null)
        {
            return null;
        }

        var regex = new Regex(id);
        var labelQuery =
            from labelOnGround in labels
            where labelOnGround?.Label is { IsValid: true, Address: > 0, IsVisible: true }
            let itemOnGround = labelOnGround.ItemOnGround
            where itemOnGround?.Metadata is { } metadata && regex.IsMatch(metadata)
            let dist = GameController?.Player?.GridPos.DistanceSquared(itemOnGround.GridPos)
            orderby dist
            select labelOnGround;

        return labelQuery.FirstOrDefault();
    }

    #region (Re)Loading Rules

    private void LoadRuleFiles()
    {
        var pickitConfigFileDirectory = ConfigDirectory;
        var existingRules = Settings.PickitRules;

        if (!string.IsNullOrEmpty(Settings.CustomConfigDir))
        {
            var customConfigFileDirectory = Path.Combine(Path.GetDirectoryName(ConfigDirectory), Settings.CustomConfigDir);

            if (Directory.Exists(customConfigFileDirectory))
            {
                pickitConfigFileDirectory = customConfigFileDirectory;
            }
            else
            {
                DebugWindow.LogError("[Pickit] custom config folder does not exist.", 15);
            }
        }

        try
        {
            var newRules = new DirectoryInfo(pickitConfigFileDirectory).GetFiles("*.ifl")
                .Select(x => new PickitRule(x.Name, Path.GetRelativePath(pickitConfigFileDirectory, x.FullName), false))
                .ExceptBy(existingRules.Select(x => x.Location), x => x.Location)
                .ToList();
            foreach (var groundRule in existingRules)
            {
                var fullPath = Path.Combine(pickitConfigFileDirectory, groundRule.Location);
                if (File.Exists(fullPath))
                {
                    newRules.Add(groundRule);
                }
                else
                {
                    LogError($"File '{groundRule.Name}' not found.");
                }
            }

            _itemFilters = newRules
                .Where(rule => rule.Enabled)
                .Select(rule => ItemFilter.LoadFromPath(Path.Combine(pickitConfigFileDirectory, rule.Location)))
                .ToList();

            Settings.PickitRules = newRules;
        }
        catch (Exception ex)
        {
            LogError($"[Pickit] Error loading filters: {ex}.", 15);
        }
    }

    private async SyncTask<bool> RunPickerIterationAsync()
    {
        LogMessage("RunPickerIterationAsync");
        if (!GameController.Window.IsForeground()) return true;

        var pickUpThisItem = GetItemsToPickup(true).FirstOrDefault();
        var workMode = GetWorkMode();
        if (workMode == WorkMode.Manual || workMode == WorkMode.Lazy && (ShouldLazyLoot(pickUpThisItem) ||
            ShouldLazyLootMisc(_portalLabels?.Value?.FirstOrDefault()) ||
            ShouldLazyLootMisc(_transitionLabel?.Value) ||
            ShouldLazyLootMisc(_shrineLabels?.Value?.FirstOrDefault()) ||
            ShouldLazyLootMisc(_chestLabels?.Value?.FirstOrDefault())))
        {
            if (Settings.ClickCorpses && Settings.MiscPickit)
            {
                if (GameController.Area.CurrentArea.IsHideout || GameController.Area.CurrentArea.IsTown)
                    return false;

                var corpseLabel = _corpseLabels?.Value?.FirstOrDefault(x =>
                    x.ItemOnGround.DistancePlayer <= Settings.MiscPickitRange &&
                    IsLabelClickable(x.Label, null));

                if (corpseLabel != null)
                {
                    await PickAsync(corpseLabel.ItemOnGround, corpseLabel.Label?.GetChildFromIndices(0, 2, 1), null, _corpseLabels.ForceUpdate);
                    return true;
                }
            }

            if (Settings.ClickDoors && Settings.MiscPickit)
            {
                if (GameController.Area.CurrentArea.IsHideout || GameController.Area.CurrentArea.IsTown)
                    return false;

                var doorLabel = _doorLabels?.Value?.FirstOrDefault(x =>
                    x.ItemOnGround.DistancePlayer <= Settings.MiscPickitRange &&
                    IsLabelClickable(x.Label, null));

                if (doorLabel != null && (pickUpThisItem == null || pickUpThisItem.Distance >= doorLabel.ItemOnGround.DistancePlayer))
                {
                    await PickAsync(doorLabel.ItemOnGround, doorLabel.Label, null, _doorLabels.ForceUpdate);
                    return true;
                }
            }

            if (Settings.ClickChests && Settings.MiscPickit)
            {
                if (GameController.Area.CurrentArea.IsHideout || GameController.Area.CurrentArea.IsTown)
                    return false;

                var chestLabel = _chestLabels?.Value?.FirstOrDefault(x =>
                    x.ItemOnGround.DistancePlayer <= Settings.MiscPickitRange &&
                    IsLabelClickable(x.Label, null));

                if (chestLabel != null && (pickUpThisItem == null || pickUpThisItem.Distance >= chestLabel.ItemOnGround.DistancePlayer))
                {
                    await PickAsync(chestLabel.ItemOnGround, chestLabel.Label, null, _chestLabels.ForceUpdate);
                    return true;
                }
            }

            if (Settings.ClickPortals && Settings.MiscPickit)
            {
                if (GameController.Area.CurrentArea.IsHideout || GameController.Area.CurrentArea.IsTown)
                    return false;

                var portalLabel = _portalLabels?.Value?.FirstOrDefault(x =>
                    x.ItemOnGround.DistancePlayer <= Settings.MiscPickitRange &&
                    IsLabelClickable(x.Label, null));

                if (portalLabel != null && (pickUpThisItem == null || pickUpThisItem.Distance >= portalLabel.ItemOnGround.DistancePlayer))
                {
                    if (_sinceLastClick.ElapsedMilliseconds < Settings.MiscClickDelay)
                    {
                        return false;
                    }
                    await PickAsync(portalLabel.ItemOnGround, portalLabel.Label, null, _portalLabels.ForceUpdate);
                    return true;
                }
            }

            if (Settings.ClickTransitions && Settings.MiscPickit)
            {
                if (GameController.Area.CurrentArea.IsHideout || GameController.Area.CurrentArea.IsTown)
                    return false;

                var transitionLabel = _transitionLabel?.Value;

                if (transitionLabel != null && (pickUpThisItem == null || pickUpThisItem.Distance >= transitionLabel.ItemOnGround.DistancePlayer))
                {
                    if (_sinceLastClick.ElapsedMilliseconds < Settings.MiscClickDelay)
                    {
                        return false;
                    }
                    await PickAsync(transitionLabel.ItemOnGround, transitionLabel.Label, null, _transitionLabel.ForceUpdate);
                    return true;
                }
            }

            if (pickUpThisItem == null)
            {
                return true;
            }

            pickUpThisItem.AttemptedPickups++;
            await PickAsync(pickUpThisItem.QueriedItem.Entity, pickUpThisItem.QueriedItem.Label, pickUpThisItem.QueriedItem.ClientRect, () => { });
        }

        return true;
    }

    private IEnumerable<PickItItemData> GetItemsToPickup(bool filterAttempts)
    {
        var labels = GameController.Game.IngameState.IngameUi.ItemsOnGroundLabelElement.VisibleGroundItemLabels?
            .Where(x=> x.Entity?.DistancePlayer is {} distance && distance < Settings.ItemPickitRange)
            .OrderBy(x => x.Entity?.DistancePlayer ?? int.MaxValue);

        return labels?
            .Where(x => x.Entity?.Path != null && IsLabelClickable(x.Label, x.ClientRect))
            .Select(x => new PickItItemData(x, GameController))
            .Where(x => x.Entity != null
                        && (!filterAttempts || x.AttemptedPickups == 0)
                        && DoWePickThis(x)
                        && (Settings.PickUpWhenInventoryIsFull || CanFitInventory(x))) ?? [];
    }

    private async SyncTask<bool> PickAsync(Entity item, Element label, RectangleF? customRect, Action onNonClickable)
    {
        _isCurrentlyPicking = true;
        try
        {
            var tryCount = 0;
            while (tryCount < 3)
            {
                if (!IsLabelClickable(label, customRect))
                {
                    onNonClickable();
                    return true;
                }

                if (Settings.IgnoreMoving && GameController.Player.GetComponent<Actor>().isMoving)
                {
                    if (item.DistancePlayer > Settings.ItemDistanceToIgnoreMoving.Value)
                    {
                        await TaskUtils.NextFrame();
                        continue;
                    }
                }

                var position = label.GetClientRect().ClickRandom(5, 3) + GameController.Window.GetWindowRectangleTimeCache.TopLeft;
                if (OkayToClick)
                {
                    if (!IsTargeted(item, label))
                    {
                        await SetCursorPositionAsync(position, item, label);
                    }
                    else
                    {
                        if (!IsTargeted(item, label))
                        {
                            await TaskUtils.NextFrame();
                            continue;
                        }

                        Input.Click(MouseButtons.Left);
                        _sinceLastClick.Restart();
                        tryCount++;
                    }
                }

                await TaskUtils.NextFrame();
            }

            return true;
        }
        finally
        {
            _isCurrentlyPicking = false;
        }
    }

    private static bool IsTargeted(Entity item, Element label)
    {
        if (item == null) return false;
        if (item.GetComponent<Targetable>()?.isTargeted is { } isTargeted)
        {
            return isTargeted;
        }

        return label is { HasShinyHighlight: true };
    }

    private static async SyncTask<bool> SetCursorPositionAsync(Vector2 position, Entity item, Element label)
    {
        DebugWindow.LogMsg($"Set cursor pos: {position}");
        Input.SetCursorPos(position);
        return await TaskUtils.CheckEveryFrame(() => IsTargeted(item, label), new CancellationTokenSource(60).Token);
    }

    #endregion
}
