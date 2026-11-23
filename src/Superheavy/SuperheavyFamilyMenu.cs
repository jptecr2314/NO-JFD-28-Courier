using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using NuclearOption.MissionEditorScripts.Buttons;
using NuclearOption.Networking;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Japensou.NuclearOption.Superheavy
{
    // One family tile opens the existing native Courier and Bastion definitions.
    public sealed class SuperheavyFamilyMenu : MonoBehaviour
    {
        // Keep native ownership and availability specific to each definition.
        private AircraftSelectionButton family;
        private Player player;
        private readonly List<AircraftSelectionButton> models = new List<AircraftSelectionButton>();
        private readonly List<Row> rows = new List<Row>();
        private AircraftInventoryMenu inventory;
        private AircraftSelectionMenu selection;
        private RectTransform popup, panel, canvasRect;
        private GameObject backend, previousFocus;
        private AircraftDefinition selected;
        private bool expanded;
        private GameObject nativeTooltip;
        private Canvas tooltipCanvas;
        private bool createdTooltipCanvas, tooltipRaised, oldOverride;
        private int oldOrder, oldLayer;
        private float refreshAt;
        private static readonly System.Reflection.FieldInfo Label = AccessTools.Field(typeof(AircraftSelectionButton), "label");
        private static readonly System.Reflection.FieldInfo Icon = AccessTools.Field(typeof(AircraftSelectionButton), "image");
        private static readonly System.Reflection.FieldInfo Selection = AccessTools.Field(typeof(AircraftInventoryMenu), "selectionMenu");
        private static readonly System.Reflection.FieldInfo OwnedValue = AccessTools.Field(typeof(AircraftSelectionButton), "ownedValue");
        private sealed class Row
        {
            internal AircraftSelectionButton Model;
            internal Button Button;
            internal AircraftSelectionButton View;
        }

        internal static void Install(Harmony harmony)
        {
            harmony.CreateClassProcessor(typeof(InitializePatch)).Patch();
            harmony.CreateClassProcessor(typeof(RefreshPatch)).Patch();
        }

        internal void Rebuild(AircraftInventoryMenu menu, List<AircraftSelectionButton> buttons, Player localPlayer, HoverText hover)
        {
            Close();
            models.Clear();
            rows.Clear();
            var nextTooltip = hover == null ? null : (GameObject)AccessTools.Field(typeof(HoverText), "hover").GetValue(hover);
            if (nextTooltip != nativeTooltip)
            {
                ReleaseTooltip();
                nativeTooltip = nextTooltip;
            }

            foreach (string key in new[] { "jp_superheavy_ground_test", "jp_bg280_bomber" })
            {
                var model = buttons.FirstOrDefault(b => b.definition?.jsonKey == key);
                if (model != null)
                    models.Add(model);
            }

            family = models.FirstOrDefault(b => b.definition.jsonKey == "jp_superheavy_ground_test");
            if (family == null || models.Count < 2)
                return;
            var canvas = menu.GetComponentInParent<Canvas>(true);
            if (canvas == null)
                return;
            canvas = canvas.rootCanvas;
            canvasRect = (RectTransform)canvas.transform;
            inventory = menu;
            player = localPlayer;
            selection = (AircraftSelectionMenu)Selection.GetValue(menu);
            // Detach reused buttons before deferred destruction of their old container.
            foreach (var model in models)
                if ((backend != null && model.transform.IsChildOf(backend.transform)) || (popup != null && model.transform.IsChildOf(popup)))
                    model.transform.SetParent(menu.buttonsContainer, false);
            if (backend != null)
                Destroy(backend);
            if (popup != null)
                Destroy(popup.gameObject);
            backend = new GameObject("Courier / Bastion native selection backends");
            backend.transform.SetParent(menu.transform, false);
            backend.SetActive(false);
            foreach (var model in models)
                if (model != family)
                    model.transform.SetParent(backend.transform, false);
            ((Text)Label.GetValue(family)).text = "JFD/BG";
            var toggle = family.GetComponent<Button>();
            toggle.onClick = new Button.ButtonClickedEvent();
            toggle.onClick.AddListener(Toggle);
            // The native per-model Update would replace the family total with
            // Courier's count. Keep the Button active and own this badge here.
            family.enabled = false;
            family.GetComponent<ShowHoverText>().SetText("Courier / Bastion\nSelect variant");
            popup = Rect("Courier / Bastion variant overlay", canvas.transform);
            popup.pivot = canvasRect.pivot;
            Stretch(popup);
            // Root-canvas portal, outside grid layouts and clipping masks.
            var layer = popup.gameObject.AddComponent<Canvas>();
            layer.overrideSorting = true;
            layer.sortingLayerID = canvas.sortingLayerID;
            layer.sortingOrder = canvas.sortingOrder + 50;
            layer.worldCamera = canvas.worldCamera;
            popup.gameObject.AddComponent<GraphicRaycaster>();
            var shade = popup.gameObject.AddComponent<Image>();
            shade.color = Color.clear;
            var outside = popup.gameObject.AddComponent<Button>();
            outside.targetGraphic = shade;
            outside.transition = Selectable.Transition.None;
            outside.onClick.AddListener(Close);
            outside.navigation = new Navigation
            {
                mode = Navigation.Mode.None
            };
            panel = Rect("Courier / Bastion native variant tiles", popup);
            panel.anchorMin = panel.anchorMax = canvasRect.pivot;
            panel.pivot = new Vector2(0, 1);
            var background = panel.gameObject.AddComponent<Image>();
            // Native tile sprites remain translucent at alpha=1; use an opaque quad.
            background.sprite = null;
            background.type = Image.Type.Simple;
            background.material = null;
            background.color = Color.black;
            var blocker = panel.gameObject.AddComponent<Button>();
            blocker.targetGraphic = background;
            blocker.transition = Selectable.Transition.None;
            blocker.navigation = new Navigation
            {
                mode = Navigation.Mode.None
            };
            foreach (var model in models)
            {
                // Keep Courier as the launcher; reuse the native Bastion button.
                var view = model == family ? Instantiate(model, panel, false) : model;
                view.transform.SetParent(panel, false);
                view.Initialize(menu, player, hover);
                view.enabled = true;
                var button = view.GetComponent<Button>();
                button.onClick = new Button.ButtonClickedEvent();
                var captured = model;
                button.onClick.AddListener(() => Choose(captured));
                AddCancel(view.gameObject);
                rows.Add(new Row { Model = model, View = view, Button = button });
            }

            for (int i = 0; i < rows.Count; i++)
                rows[i].Button.navigation = new Navigation
                {
                    mode = Navigation.Mode.Explicit,
                    selectOnLeft = rows[(i + rows.Count - 1) % rows.Count].Button,
                    selectOnRight = rows[(i + 1) % rows.Count].Button,
                    selectOnUp = rows[(i + rows.Count - 1) % rows.Count].Button,
                    selectOnDown = rows[(i + 1) % rows.Count].Button
                };
            popup.gameObject.SetActive(false);
            expanded = false;
        }

        private bool Available(AircraftSelectionButton model) => selection != null && selection.CanFlyAircraft(model.definition);
        private void Choose(AircraftSelectionButton model)
        {
            inventory.OnAircraftButtonClick(model);
            Close();
        }

        private void Toggle()
        {
            if (expanded)
                Close();
            else
                Open();
        }

        private void Open()
        {
            if (popup == null || family == null || canvasRect == null)
                return;
            previousFocus = EventSystem.current == null ? null : EventSystem.current.currentSelectedGameObject;
            var corners = new Vector3[4];
            ((RectTransform)family.transform).GetWorldCorners(corners);
            var local = corners.Select(p => canvasRect.InverseTransformPoint(p)).ToArray();
            var b = canvasRect.rect;
            var layout = SuperheavyVariantOverlayLayout.Fit(b.xMin, b.yMin, b.xMax, b.yMax, local.Min(p => p.x), local.Max(p => p.x), local.Max(p => p.y), rows.Count, local.Max(p => p.x) - local.Min(p => p.x), local.Max(p => p.y) - local.Min(p => p.y));
            panel.anchoredPosition = new Vector2(layout.X, layout.Y);
            panel.sizeDelta = new Vector2(layout.Width, layout.Height);
            float tile = (layout.Width - 12 - 6 * (rows.Count - 1)) / rows.Count;
            for (int i = 0; i < rows.Count; i++)
                TopLeft((RectTransform)rows[i].View.transform, 6 + i * (tile + 6), 6, tile, layout.Height - 12);
            expanded = true;
            popup.gameObject.SetActive(true);
            RefreshRows();
            RaiseTooltip();
            if (rows.Count > 0)
                rows[0].Button.Select();
        }

        internal void Close()
        {
            RestoreTooltip();
            expanded = false;
            if (popup != null)
                popup.gameObject.SetActive(false);
            if (previousFocus != null && previousFocus.activeInHierarchy && EventSystem.current != null)
                EventSystem.current.SetSelectedGameObject(previousFocus);
            previousFocus = null;
        }

        internal void Refresh(AircraftDefinition selectionType)
        {
            bool changed = selected != selectionType;
            selected = selectionType;
            if (family == null || player == null || models.Count < 2)
                return;
            bool selectedFamily = models.Any(m => selected == m.definition);
            family.Setup(models.Any(m => player.OwnsAirframe(m.definition, true)), models.Any(Available));
            family.SetActive(selectedFamily);
            int owned = models.Sum(m => player.OwnedAirframeTypeCount(m.definition, true));
            ((Text)OwnedValue.GetValue(family)).text = owned > 0 ? owned.ToString() : "";
            if (changed && !selectedFamily && expanded)
                Close();
            if (expanded)
                RefreshRows();
        }

        private void RefreshRows()
        {
            if (player == null)
                return;
            foreach (var row in rows)
            {
                row.View.Setup(player.OwnsAirframe(row.Model.definition, true), Available(row.Model));
                row.View.SetActive(selected == row.Model.definition);
            }
        }

        private void Update()
        {
            if (inventory == null || family == null || !family.gameObject.activeInHierarchy)
            {
                Close();
                return;
            }

            if (Time.unscaledTime >= refreshAt)
            {
                refreshAt = Time.unscaledTime + .25f;
                Refresh(selected);
            }
        }

        private void OnDisable()
        {
            Close();
        }

        private void RaiseTooltip()
        {
            if (nativeTooltip == null || popup == null)
                return;
            tooltipCanvas = nativeTooltip.GetComponent<Canvas>();
            if (tooltipCanvas == null)
            {
                tooltipCanvas = nativeTooltip.AddComponent<Canvas>();
                createdTooltipCanvas = true;
            }

            oldOverride = tooltipCanvas.overrideSorting;
            oldOrder = tooltipCanvas.sortingOrder;
            oldLayer = tooltipCanvas.sortingLayerID;
            var overlay = popup.GetComponent<Canvas>();
            tooltipCanvas.overrideSorting = true;
            tooltipCanvas.sortingLayerID = overlay.sortingLayerID;
            tooltipCanvas.sortingOrder = overlay.sortingOrder + 1;
            tooltipRaised = true;
        }

        private void RestoreTooltip()
        {
            if (!tooltipRaised || tooltipCanvas == null)
                return;
            tooltipCanvas.overrideSorting = oldOverride;
            tooltipCanvas.sortingOrder = oldOrder;
            tooltipCanvas.sortingLayerID = oldLayer;
            tooltipRaised = false;
        }

        private void ReleaseTooltip()
        {
            RestoreTooltip();
            if (createdTooltipCanvas && tooltipCanvas != null)
                Destroy(tooltipCanvas);
            tooltipCanvas = null;
            createdTooltipCanvas = false;
            nativeTooltip = null;
        }

        private void OnDestroy()
        {
            ReleaseTooltip();
            if (popup != null)
                Destroy(popup.gameObject);
            if (backend != null)
                Destroy(backend);
        }

        private void AddCancel(GameObject target)
        {
            (target.GetComponent<SuperheavyOverlayCancel>() ?? target.AddComponent<SuperheavyOverlayCancel>()).Owner = this;
        }

        private static RectTransform Rect(string name, Transform parent)
        {
            var r = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            r.SetParent(parent, false);
            r.gameObject.layer = parent.gameObject.layer;
            return r;
        }

        private static void Stretch(RectTransform r)
        {
            r.anchorMin = Vector2.zero;
            r.anchorMax = Vector2.one;
            r.offsetMin = r.offsetMax = Vector2.zero;
        }

        private static void TopLeft(RectTransform r, float x, float y, float width, float height)
        {
            r.anchorMin = r.anchorMax = new Vector2(0, 1);
            r.pivot = new Vector2(0, 1);
            r.anchoredPosition = new Vector2(x, -y);
            r.sizeDelta = new Vector2(width, height);
        }

        [HarmonyPatch(typeof(AircraftInventoryMenu), "Initialize")]
        internal static class InitializePatch
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(AircraftInventoryMenu __instance, List<AircraftSelectionButton> ___listAircraftButtons, Player ___localPlayer, HoverText ___hoverText, AircraftDefinition ___selectedType)
            {
                var group = __instance.GetComponent<SuperheavyFamilyMenu>() ?? __instance.gameObject.AddComponent<SuperheavyFamilyMenu>();
                group.Rebuild(__instance, ___listAircraftButtons, ___localPlayer, ___hoverText);
                group.Refresh(___selectedType);
            }
        }

        [HarmonyPatch(typeof(AircraftInventoryMenu), "Refresh")]
        internal static class RefreshPatch
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(AircraftInventoryMenu __instance, AircraftDefinition ___selectedType)
            {
                __instance.GetComponent<SuperheavyFamilyMenu>()?.Refresh(___selectedType);
            }
        }
    }

    public sealed class SuperheavyOverlayCancel : MonoBehaviour, ICancelHandler
    {
        internal SuperheavyFamilyMenu Owner;
        public void OnCancel(BaseEventData eventData)
        {
            if (Owner != null)
                Owner.Close();
            eventData.Use();
        }
    }
}
