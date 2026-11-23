using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Japensou.NuclearOption.Superheavy
{
    internal static class SuperheavyCargoOrganizerMenu
    {
        private const string OrganizerName = "CourierCargoOrganizer";
        private const string CargoGate = "SHGT_CargoReleaseGate";
        private const float DeckPixelsPerMeter = 16f;
        private const float SnapMeters = .1f;
        private static readonly Color PanelColor = SuperheavyNativeMenuStyle.Panel;
        private static readonly Color BayColor = SuperheavyNativeMenuStyle.Control;
        private static readonly Color DeckColor = SuperheavyNativeMenuStyle.Recess;
        private static readonly Color LabelColor = SuperheavyNativeMenuStyle.Text;
        private static readonly Color ActiveColor = SuperheavyNativeMenuStyle.Selected;
        private static readonly Color ErrorColor = new Color(1f, .65f, .58f, 1f);
        private static readonly Color InvalidBlockColor = new Color(.66f, .30f, .28f, 1f);
        private static readonly AccessTools.FieldRef<WeaponSelector, List<WeaponMount>> NativeOptions =
            AccessTools.FieldRefAccess<WeaponSelector, List<WeaponMount>>("getCache");

        internal static void Install(Harmony harmony)
        {
            harmony.CreateClassProcessor(typeof(NativeLoadoutPatch)).Patch();
        }

        [HarmonyPatch(typeof(LoadoutSelector), nameof(LoadoutSelector.AssignAircraft))]
        private static class NativeLoadoutPatch
        {
            private static void Postfix(LoadoutSelector __instance, Aircraft ___aircraft, Transform ___backgroundTransform,
                List<WeaponSelector> ___weaponSelectors, TMP_Dropdown ___liveryDropdown)
            {
                if (___backgroundTransform == null)
                    return;

                var previous = ___backgroundTransform.Find(OrganizerName);
                if (previous != null)
                {
                    previous.gameObject.SetActive(false);
                    previous.name = OrganizerName + " (closing)";
                    UnityEngine.Object.Destroy(previous.gameObject);
                }

                if (GameManager.gameState != GameState.SinglePlayer || ___aircraft == null ||
                    ___aircraft.definition == null ||
                    ___aircraft.definition.jsonKey != SuperheavyGroundPhysicsPlugin.AircraftKey)
                    return;

                try
                {
                    var cargo = FindCargoSelectors(___aircraft.weaponManager, ___weaponSelectors);
                    if (cargo.Count <= 3)
                        throw new InvalidOperationException("The spatial cargo mounts are missing from this aircraft package.");

                    BuildOrganizer(__instance, ___aircraft, ___backgroundTransform, cargo, ___liveryDropdown);
                }
                catch (Exception error)
                {
                    Debug.LogWarning($"Courier cargo organizer could not be displayed: {error.Message}");
                }
            }
        }

        private static List<WeaponSelector> FindCargoSelectors(WeaponManager manager, List<WeaponSelector> selectors)
        {
            var cargo = new List<WeaponSelector>();
            var sets = manager == null ? null : manager.hardpointSets;
            if (sets == null || selectors.Count != sets.Length)
                return cargo;

            for (int i = 0; i < sets.Length; i++)
            {
                var points = sets[i]?.hardpoints;
                if (points == null || points.Count != 1 || points[0] == null)
                    continue;
                var point = points[0];
                if (point.transform != null && point.transform.name.StartsWith("SHGT_CargoMount_", StringComparison.Ordinal) &&
                    point.bayDoors != null && point.GetCargoDoor()?.name == CargoGate)
                    cargo.Add(selectors[i]);
            }
            return cargo;
        }

        private static void BuildOrganizer(LoadoutSelector selector, Aircraft preview, Transform strip,
            List<WeaponSelector> cargo, TMP_Dropdown dropdownStyle)
        {
            if (dropdownStyle == null)
                throw new InvalidOperationException("The stock dropdown template is missing.");

            var row = At(OrganizerName, strip, 0f, 0f, 180f, 80f);
            try
            {
                row.SetAsFirstSibling();
                var sizing = row.gameObject.AddComponent<LayoutElement>();
                sizing.minWidth = 140f;
                sizing.flexibleWidth = 3f;
                var style = dropdownStyle.captionText;
                var manage = MakeButton(Rect("ManageCargoButton", row, Vector2.zero, Vector2.one,
                    new Vector2(4f, 14f), new Vector2(-4f, -14f)), style, "Manage Cargo");
                var panel = Rect("CargoHoldPanel", row, Vector2.up, Vector2.up,
                    new Vector2(0f, 8f), new Vector2(615f, 645f));
                var hold = new Hold(selector, preview, panel, cargo, dropdownStyle);
                manage.onClick.AddListener(hold.Toggle);
                SuperheavyNativeMenuStyle.Apply(panel, dropdownStyle);
                panel.gameObject.SetActive(false);

                // The original native selector objects stay alive and preserve their indices, events, and cost checks.
                var storage = new GameObject("NativeCargoSelectors");
                storage.transform.SetParent(row, false);
                storage.SetActive(false);
                foreach (var card in cargo)
                    card.transform.SetParent(storage.transform, false);
            }
            catch
            {
                row.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(row.gameObject);
                throw;
            }
        }

        private sealed class CatalogEntry
        {
            internal WeaponMount Mount;
            internal CargoItem Item;
        }

        private sealed class Hold
        {
            private readonly LoadoutSelector selector;
            private readonly Aircraft preview;
            private readonly RectTransform panel;
            private readonly RectTransform deck;
            private readonly TMP_Text style;
            private readonly TMP_Dropdown vehicle;
            private readonly Button add;
            private readonly Button remove;
            private readonly Button apply;
            private readonly TMP_Text catalogDetails;
            private readonly TMP_Text selectionDetails;
            private readonly TMP_Text mass;
            private readonly TMP_Text status;
            private readonly List<WeaponSelector> native;
            private readonly List<CatalogEntry> catalog = new List<CatalogEntry>();
            private readonly List<SpatialCargoLoad> draft = new List<SpatialCargoLoad>();
            private readonly List<RectTransform> blocks = new List<RectTransform>();
            private int selected = -1;
            private bool dirty;
            private bool legacy;
            private bool dragging;
            private bool dragValid;
            private string dragReason;
            private Vector2 dragOffset;
            private Vector2 dragPosition;

            internal Hold(LoadoutSelector selector, Aircraft preview, RectTransform panel,
                List<WeaponSelector> native, TMP_Dropdown dropdownStyle)
            {
                this.selector = selector;
                this.preview = preview;
                this.panel = panel;
                this.native = native;
                style = dropdownStyle.captionText;

                Fill(panel, PanelColor).raycastTarget = true;
                Label(At("CargoTitle", panel, 16f, 593f, 510f, 32f), style, "Cargo Organizer", 27f,
                    TextAlignmentOptions.MidlineLeft);
                var close = MakeButton(At("CloseCargoButton", panel, 561f, 594f, 38f, 30f), style, "×");
                close.onClick.AddListener(Close);
                float deckWidth = SuperheavyCargoPacking.HoldWidth * DeckPixelsPerMeter;
                float deckLength = SuperheavyCargoPacking.HoldLength * DeckPixelsPerMeter;
                Label(At("Nose", panel, 36f, 556f, deckWidth, 23f), style, "Nose", 17f);

                deck = At("CargoDeck", panel, 36f, 40f, deckWidth, deckLength);
                Fill(deck, BayColor);
                float laneStart = SuperheavySpatialCargoPlan.LaneLeft * DeckPixelsPerMeter;
                float laneWidth = (SuperheavySpatialCargoPlan.LaneRight - SuperheavySpatialCargoPlan.LaneLeft) * DeckPixelsPerMeter;
                float endMargin = SuperheavyCargoPacking.EndClearance * DeckPixelsPerMeter;
                Fill(At("StraightRampLane", deck, laneStart, endMargin, laneWidth, deckLength - 2f * endMargin), DeckColor);
                for (int mark = 0; mark <= 30; mark += 5)
                {
                    float y = (SuperheavyCargoPacking.HoldLength - mark) * DeckPixelsPerMeter;
                    Fill(At("DeckMark", deck, laneStart, y, laneWidth, 1f), BayColor);
                    if (mark > 0)
                        Label(At("MeterMark", panel, 3f, 40f + y - 8f, 30f, 16f), style, mark.ToString(), 13f,
                            TextAlignmentOptions.MidlineRight);
                }
                Label(At("Ramp", panel, 36f, 12f, deckWidth, 23f), style, "Ramp", 17f);

                Label(At("VehicleHeading", panel, 158f, 546f, 438f, 28f), style, "Choose vehicle", 20f,
                    TextAlignmentOptions.MidlineLeft);
                vehicle = UnityEngine.Object.Instantiate(dropdownStyle, panel, false);
                vehicle.name = "CargoVehicleDropdown";
                foreach (Transform child in vehicle.transform)
                {
                    if (child.name != "Dropdown List")
                        continue;
                    child.gameObject.SetActive(false);
                    UnityEngine.Object.Destroy(child.gameObject);
                }
                vehicle.onValueChanged = new TMP_Dropdown.DropdownEvent();
                vehicle.navigation = Navigation.defaultNavigation;
                Place((RectTransform)vehicle.transform, Vector2.zero, Vector2.zero,
                    new Vector2(158f, 501f), new Vector2(599f, 543f));
                vehicle.gameObject.SetActive(true);
                vehicle.onValueChanged.AddListener(_ => RefreshControls());

                catalogDetails = Label(At("CargoDimensions", panel, 158f, 465f, 438f, 29f), style, "", 16f,
                    TextAlignmentOptions.MidlineLeft);
                add = MakeButton(At("AddCargoButton", panel, 158f, 416f, 214f, 38f), style, "Add");
                remove = MakeButton(At("RemoveCargoButton", panel, 381f, 416f, 218f, 38f), style, "Remove selected");
                add.onClick.AddListener(Add);
                remove.onClick.AddListener(Remove);
                Label(At("PlanInstructions", panel, 158f, 348f, 438f, 57f), style,
                    "Drag cargo to move it. Side loads need room to turn into the dark ramp lane.\nDimensions are length × width × height.", 16f,
                    TextAlignmentOptions.TopLeft);
                selectionDetails = Label(At("CargoSelected", panel, 158f, 253f, 438f, 77f), style, "", 18f,
                    TextAlignmentOptions.TopLeft);
                mass = Label(At("CargoWeight", panel, 158f, 207f, 438f, 36f), style, "", 20f,
                    TextAlignmentOptions.MidlineLeft);
                status = Label(At("CargoStatus", panel, 158f, 87f, 438f, 105f), style, "", 17f,
                    TextAlignmentOptions.TopLeft);
                apply = MakeButton(At("ApplyCargoButton", panel, 158f, 24f, 214f, 42f), style, "Apply");
                var cancel = MakeButton(At("CancelCargoButton", panel, 381f, 24f, 218f, 42f), style, "Cancel");
                apply.onClick.AddListener(Apply);
                cancel.onClick.AddListener(Close);
                SuperheavyNativeMenuStyle.Apply(panel, dropdownStyle);
            }

            internal void Toggle()
            {
                if (panel.gameObject.activeSelf)
                {
                    Close();
                    return;
                }

                draft.Clear();
                draft.AddRange(SuperheavySpatialCargoRuntime.GetPlan(preview));
                selected = -1;
                dirty = false;
                dragging = false;
                legacy = draft.Count == 0 && native.Any(card => card.GetValue() != null);
                PopulateCatalog();
                panel.gameObject.SetActive(true);
                RebuildDeck();
                Report(legacy
                    ? "This loadout uses the original cargo mount positions. Applying a new deck plan replaces its cargo."
                    : "Add a vehicle and drag it to a new position. Apply to use the plan in this loadout.");
            }

            private void Close()
            {
                dragging = false;
                vehicle.Hide();
                panel.gameObject.SetActive(false);
            }

            private void PopulateCatalog()
            {
                catalog.Clear();
                var seen = new HashSet<WeaponMount>();
                foreach (var card in native)
                foreach (var mount in NativeOptions(card))
                {
                    if (mount == null || !mount.Cargo || !seen.Add(mount) ||
                        !SuperheavySpatialCargoPlan.TryMeasure(mount, out var item))
                        continue;
                    catalog.Add(new CatalogEntry { Mount = mount, Item = item });
                }
                catalog.Sort((left, right) => StringComparer.CurrentCultureIgnoreCase.Compare(left.Item.Label, right.Item.Label));
                vehicle.ClearOptions();
                vehicle.AddOptions(catalog.Select(entry => entry.Item.Label).ToList());
                vehicle.SetValueWithoutNotify(0);
                vehicle.RefreshShownValue();
            }

            private void Add()
            {
                if (!add.interactable)
                    return;
                var entry = catalog[vehicle.value];
                float centered = SuperheavySpatialCargoPlan.CenterlineX(entry.Mount);
                var alone = new SpatialCargoLoad(entry.Mount, entry.Item, centered, SuperheavyCargoPacking.EndClearance);
                if (!SuperheavySpatialCargoPlan.TryValidate(new[] { alone }, out var reason))
                {
                    Report(reason, true);
                    return;
                }
                if (!SuperheavySpatialCargoPlan.TryFindSpot(draft, entry.Mount, entry.Item, out var placement))
                {
                    Report("No usable position was found for this vehicle. Move or remove another load, then try again.", true);
                    return;
                }
                draft.Add(placement);
                selected = draft.Count - 1;
                dirty = true;
                RebuildDeck();
                Report(SuperheavySpatialCargoPlan.RequiresCenterline(entry.Mount, entry.Item)
                    ? "Vehicle added on the centerline. Drag it forward or back to reposition it."
                    : "Vehicle added to the plan. Drag it if you want to change its position.");
            }

            private void Remove()
            {
                if (selected < 0)
                    return;
                draft.RemoveAt(selected);
                selected = -1;
                dirty = true;
                RebuildDeck();
                Report("Vehicle removed from the plan.");
            }

            private void Apply()
            {
                if (!apply.interactable)
                    return;
                if (!SuperheavySpatialCargoRuntime.TryApply(selector, preview, draft, out var reason))
                {
                    Report(reason, true);
                    return;
                }
                dirty = false;
                legacy = false;
                RefreshControls();
                Report("Cargo plan applied to this loadout.");
            }

            private void RebuildDeck()
            {
                foreach (var block in blocks)
                {
                    block.gameObject.SetActive(false);
                    UnityEngine.Object.Destroy(block.gameObject);
                }
                blocks.Clear();
                for (int i = 0; i < draft.Count; i++)
                    CreateBlock(i);
                RefreshControls();
            }

            private void CreateBlock(int index)
            {
                var cargo = draft[index];
                var block = At("CargoItem" + index, deck, 0f, 0f, 1f, 1f);
                PlaceBlock(block, cargo.Item, cargo.X, cargo.Y);
                var background = Fill(block, index == selected ? ActiveColor : BayColor);
                background.raycastTarget = true;
                var icon = Rect("CargoSilhouette", block, Vector2.zero, Vector2.one,
                    new Vector2(2f, 14f), new Vector2(-2f, -2f));
                var image = Fill(icon, Color.white);
                image.preserveAspect = true;
                image.sprite = cargo.Mount.info != null ? cargo.Mount.info.weaponIcon : null;
                image.enabled = image.sprite != null;
                Label(Rect("CargoNumber", block, Vector2.zero, Vector2.right, Vector2.zero, new Vector2(0f, 16f)),
                    style, (index + 1).ToString(), 13f);

                var events = block.gameObject.AddComponent<EventTrigger>();
                Bind(events, EventTriggerType.PointerClick, _ => Select(index));
                Bind(events, EventTriggerType.BeginDrag, pointer => BeginDrag(index, pointer));
                Bind(events, EventTriggerType.Drag, MoveDrag);
                Bind(events, EventTriggerType.EndDrag, EndDrag);
                blocks.Add(block);
            }

            private void Select(int index)
            {
                selected = index;
                for (int i = 0; i < blocks.Count; i++)
                    blocks[i].GetComponent<Image>().color = i == selected ? ActiveColor : BayColor;
                RefreshControls();
            }

            private void BeginDrag(int index, PointerEventData pointer)
            {
                if (pointer.button != PointerEventData.InputButton.Left || !PointerMeters(pointer, out var position))
                    return;
                Select(index);
                var cargo = draft[index];
                dragPosition = new Vector2(cargo.X, cargo.Y);
                dragOffset = position - dragPosition;
                dragValid = true;
                dragReason = null;
                dragging = true;
            }

            private void MoveDrag(PointerEventData pointer)
            {
                if (!dragging || !PointerMeters(pointer, out var position))
                    return;
                var load = draft[selected];
                var item = load.Item;
                var shift = position - dragOffset - new Vector2(load.X, load.Y);
                float nextX = SuperheavySpatialCargoPlan.RequiresCenterline(load.Mount, item)
                    ? SuperheavySpatialCargoPlan.CenterlineX(load.Mount)
                    : Mathf.Clamp(load.X + Snap(shift.x), SuperheavySpatialCargoPlan.StowLeft,
                        SuperheavySpatialCargoPlan.StowRight - item.Width);
                var nextPosition = new Vector2(nextX,
                    Mathf.Clamp(load.Y + Snap(shift.y), SuperheavyCargoPacking.EndClearance,
                        SuperheavyCargoPacking.HoldLength - SuperheavyCargoPacking.EndClearance - item.Length));
                if (nextPosition == dragPosition)
                    return;
                dragPosition = nextPosition;
                var candidate = new SpatialCargoLoad(load.Mount, item, dragPosition.x, dragPosition.y);
                var next = new List<SpatialCargoLoad>(draft) { [selected] = candidate };
                dragValid = SuperheavySpatialCargoPlan.TryValidate(next, out dragReason);
                var block = blocks[selected];
                block.GetComponent<Image>().color = dragValid ? ActiveColor : InvalidBlockColor;
                PlaceBlock(block, item, dragPosition.x, dragPosition.y);
            }

            private static float Snap(float value) => Mathf.Round(value / SnapMeters) * SnapMeters;

            private void EndDrag(PointerEventData pointer)
            {
                if (!dragging)
                    return;
                MoveDrag(pointer);
                dragging = false;
                var cargo = draft[selected];
                if (Mathf.Approximately(dragPosition.x, cargo.X) && Mathf.Approximately(dragPosition.y, cargo.Y))
                    return;
                if (dragValid)
                {
                    draft[selected] = new SpatialCargoLoad(cargo.Mount, cargo.Item, dragPosition.x, dragPosition.y);
                    dirty = true;
                    Report("Position updated.");
                }
                else
                {
                    var block = blocks[selected];
                    block.GetComponent<Image>().color = ActiveColor;
                    PlaceBlock(block, cargo.Item, cargo.X, cargo.Y);
                    Report(dragReason, true);
                }
                RefreshControls();
            }

            private bool PointerMeters(PointerEventData pointer, out Vector2 position)
            {
                position = default;
                if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(deck, pointer.position,
                    pointer.pressEventCamera, out var local))
                    return false;
                position = new Vector2(local.x - deck.rect.xMin, deck.rect.yMax - local.y) / DeckPixelsPerMeter;
                return true;
            }

            private static void PlaceBlock(RectTransform block, CargoItem item, float x, float y)
            {
                float left = x * DeckPixelsPerMeter;
                float top = -y * DeckPixelsPerMeter;
                Place(block, Vector2.up, Vector2.up,
                    new Vector2(left, top - item.Length * DeckPixelsPerMeter),
                    new Vector2(left + item.Width * DeckPixelsPerMeter, top));
            }

            private void RefreshControls()
            {
                vehicle.interactable = catalog.Count > 0;
                add.interactable = vehicle.interactable && draft.Count < native.Count;
                remove.interactable = selected >= 0;
                apply.interactable = dirty;
                catalogDetails.text = vehicle.interactable ? Dimensions(catalog[vehicle.value].Item) : "No measured vehicles available.";
                selectionDetails.text = selected < 0 ? "Click a loaded vehicle to see its details." :
                    $"{selected + 1}. {draft[selected].Item.Label}\n{Dimensions(draft[selected].Item)}";
                mass.text = $"Cargo mass   {draft.Sum(cargo => cargo.Item.Tonnes):0.0} t";
            }

            private static string Dimensions(CargoItem item) =>
                $"{item.Length:0.00} × {item.Width:0.00} × {item.Height:0.00} m   ·   {item.Tonnes:0.0} t";

            private void Report(string message, bool error = false)
            {
                status.text = message;
                status.color = error ? ErrorColor : LabelColor;
            }
        }

        private static void Bind(EventTrigger trigger, EventTriggerType type, Action<PointerEventData> callback)
        {
            var entry = new EventTrigger.Entry { eventID = type };
            entry.callback.AddListener(data => callback((PointerEventData)data));
            trigger.triggers.Add(entry);
        }

        private static Button MakeButton(RectTransform rect, TMP_Text style, string caption)
        {
            var button = rect.gameObject.AddComponent<Button>();
            var graphic = Fill(rect, BayColor);
            graphic.raycastTarget = true;
            button.targetGraphic = graphic;
            Label(Rect("Label", rect, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero), style, caption, 19f);
            return button;
        }

        private static TMP_Text Label(RectTransform rect, TMP_Text style, string text, float size,
            TextAlignmentOptions alignment = TextAlignmentOptions.Midline)
        {
            var label = rect.gameObject.AddComponent<TextMeshProUGUI>();
            if (style != null)
            {
                label.font = style.font;
                label.fontSharedMaterial = style.fontSharedMaterial;
            }
            label.text = text;
            label.fontSize = size;
            label.alignment = alignment;
            label.color = LabelColor;
            label.raycastTarget = false;
            return label;
        }

        private static RectTransform At(string name, Transform parent, float x, float y, float width, float height)
        {
            return Rect(name, parent, Vector2.zero, Vector2.zero, new Vector2(x, y), new Vector2(x + width, y + height));
        }

        private static RectTransform Rect(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax,
            Vector2 offsetMin, Vector2 offsetMax)
        {
            var rect = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
            rect.gameObject.layer = parent.gameObject.layer;
            rect.SetParent(parent, false);
            Place(rect, anchorMin, anchorMax, offsetMin, offsetMax);
            return rect;
        }

        private static void Place(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax,
            Vector2 offsetMin, Vector2 offsetMax)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
        }

        private static Image Fill(RectTransform rect, Color color)
        {
            var image = rect.gameObject.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return image;
        }
    }
}

