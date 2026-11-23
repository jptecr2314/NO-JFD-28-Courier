using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Japensou.NuclearOption.Superheavy
{
    // BG armament only. Native application/persistence belongs to the runtime;
    // opening, dragging or cancelling this panel never changes the real loadout.
    internal static class SuperheavyWeaponsOrganizerMenu
    {
        private const string OrganizerName = "BGWeaponsOrganizer";
        private const float PanelWidth = 960f, PanelHeight = 700f, SnapMeters = .1f;
        private static readonly Color PanelColor = SuperheavyNativeMenuStyle.Panel;
        private static readonly Color DeckColor = SuperheavyNativeMenuStyle.Recess;
        private static readonly Color BayColor = SuperheavyNativeMenuStyle.Control;
        private static readonly Color LabelColor = SuperheavyNativeMenuStyle.Text;
        private static readonly Color ActiveColor = SuperheavyNativeMenuStyle.Selected;
        private static readonly Color ErrorColor = new Color(1f, .65f, .57f, 1f);
        private static readonly Color WarningColor = new Color(.98f, .79f, .45f, 1f);
        private static readonly Regex IndexedIssue = new Regex(@"(?:placement|routing)\[(\d+)(?:,\d+)?\]", RegexOptions.CultureInvariant);
        private static readonly Regex OtherItem = new Regex(@"(?:envelope|array|gun)\[(\d+)\]", RegexOptions.CultureInvariant);

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
                if (___backgroundTransform == null) return;
                var old = ___backgroundTransform.Find(OrganizerName);
                if (old != null)
                {
                    var previousVisibility = old.GetComponent<LegacyVisibility>();
                    if (previousVisibility != null) previousVisibility.Restore();
                    foreach (var pump in old.GetComponentsInChildren<ValidationPump>(true)) pump.Stop();
                    old.gameObject.SetActive(false); old.name += " (closing)";
                    UnityEngine.Object.Destroy(old.gameObject);
                }
                if (GameManager.gameState != GameState.SinglePlayer || ___aircraft == null ||
                    ___aircraft.definition?.jsonKey != "jp_bg280_bomber" || ___liveryDropdown == null) return;
                RectTransform row = null;
                try
                {
                    // Runtime explicitly grants ownership of just the three
                    // former chamber controls. Defense/wing/livery controls stay native.
                    if (!SuperheavySpatialWeaponsRuntime.CanManage(__instance, ___aircraft)) return;
                    row = At(OrganizerName, ___backgroundTransform, 0f, 0f, 180f, 80f);
                    row.SetAsFirstSibling();
                    var sizing = row.gameObject.AddComponent<LayoutElement>(); sizing.minWidth = 160f; sizing.flexibleWidth = 3f;
                    var manage = Button(Rect("ManageArmamentButton", row, Vector2.zero, Vector2.one,
                        new Vector2(4f, 14f), new Vector2(-4f, -14f)), ___liveryDropdown.captionText, "Manage armament");
                    var panel = Rect("WeaponBayPanel", row, Vector2.up, Vector2.up, new Vector2(0f, 8f), new Vector2(PanelWidth, PanelHeight + 8f));
                    var view = new Hold(__instance, ___aircraft, panel, ___liveryDropdown);
                    manage.onClick.AddListener(view.Toggle); panel.gameObject.SetActive(false);
                    var hidden = row.gameObject.AddComponent<LegacyVisibility>();
                    // Preserve native indices for save compatibility, but do
                    // not expose the removed BG ventral stations as cards.
                    hidden.Hide(___weaponSelectors?.Where((s,i) => s != null && (i < 3 || i == 5 || i == 6)).Select(s => s.gameObject));
                }
                catch (Exception error)
                {
                    if (row != null) { row.gameObject.SetActive(false); UnityEngine.Object.Destroy(row.gameObject); }
                    Debug.LogWarning("BG armament organizer could not be displayed: " + error);
                }
            }
        }

        private sealed class LegacyVisibility : MonoBehaviour
        {
            private readonly List<GameObject> controls = new List<GameObject>();
            internal void Hide(IEnumerable<GameObject> cards)
            {
                if (cards == null) return;
                foreach (var card in cards) if (card != null && card.activeSelf) { controls.Add(card); card.SetActive(false); }
            }
            internal void Restore() { foreach (var card in controls) if (card != null) card.SetActive(true); controls.Clear(); }
            private void LateUpdate() { foreach (var card in controls) if (card != null && card.activeSelf) card.SetActive(false); }
            private void OnDestroy() { Restore(); }
        }

        // Unity only polls a pure-data worker. It never renders from a task or
        // calls a native game API there. Disabling/destroying the panel also
        // invalidates its generation before a completed task can publish.
        private sealed class ValidationPump : MonoBehaviour
        {
            internal Hold View;
            private void Update() { View?.PumpValidation(); }
            internal void Stop() { View?.CancelValidation(); }
            private void OnDisable() { Stop(); }
            private void OnDestroy() { Stop(); View = null; }
        }

        private sealed class ValidationProgress
        {
            private int done, total;
            internal void Report(int current, int count) { Volatile.Write(ref done, current); Volatile.Write(ref total, count); }
            internal int Done => Volatile.Read(ref done);
            internal int Total => Volatile.Read(ref total);
        }

        private sealed class ValidationJob
        {
            internal int Generation;
            internal bool Full;
            internal string GoodMessage;
            internal WeaponPlacement[] Items;
            internal WeaponPlanGeometry Geometry;
            internal CancellationTokenSource Cancellation;
            internal ValidationProgress Progress;
            internal Task<WeaponPlanValidation> Task;
        }

        private sealed class ResolveJob
        {
            internal int Generation;
            internal WeaponPlanGeometry Geometry;
            internal CancellationTokenSource Cancellation;
            internal ValidationProgress Progress;
            internal Task<WeaponPlanResolveResult> Task;
        }

        private sealed class Hold
        {
            private readonly LoadoutSelector selector;
            private readonly Aircraft aircraft;
            private readonly RectTransform panel, viewport, content, deck;
            private readonly ScrollRect scroll;
            private readonly TMP_Text style, title, dimensions, catalogDetails, selectionDetails, mass, status, zoomLabel;
            private readonly TMP_Dropdown catalogDropdown, loadedDropdown, heightDropdown;
            private readonly TMP_InputField quantity, across, along, height;
            private readonly Button add, remove, rotate, earlier, later, apply, focus, fillBay, clearBay, resolve;
            private bool batchBusy;
            private ResolveJob batchJob;
            private readonly List<WeaponItemSpec> catalog = new List<WeaponItemSpec>();
            private readonly List<WeaponPlacement> draft = new List<WeaponPlacement>();
            private readonly List<RectTransform> blocks = new List<RectTransform>();
            private readonly List<List<Image>> physicalShapes = new List<List<Image>>();
            private readonly List<TMP_Text> numbers = new List<TMP_Text>();
            private readonly List<float> shownHeights = new List<float>();
            private WeaponPlanGeometry geometry;
            private WeaponPlanValidation validation;
            private ValidationJob validationJob;
            private ResolveJob resolveJob;
            private int validationGeneration;
            private bool fullValidation;
            private float nextProgressUpdate;
            private int selected = -1;
            private bool dirty, updating, dragging;
            private float pixelsPerMeter;
            private Vector2 dragOffset;

            internal Hold(LoadoutSelector selector, Aircraft aircraft, RectTransform panel, TMP_Dropdown template)
            {
                this.selector = selector; this.aircraft = aircraft; this.panel = panel; style = template.captionText;
                geometry = SuperheavySpatialWeaponsRuntime.GetGeometry(aircraft);
                if (!GeometryAvailable(geometry))
                    throw new InvalidOperationException("Measured BG weapons bay geometry is unavailable.");
                Fill(panel, PanelColor).raycastTarget = true;
                title = Label(At("ArmamentTitle", panel, 18f, 654f, 854f, 31f), style, "Armament bay", 27f, TextAlignmentOptions.MidlineLeft);
                Button(At("CloseArmamentButton", panel, 909f, 654f, 33f, 31f), style, "×").onClick.AddListener(Close);
                dimensions = Label(At("BayDimensions", panel, 18f, 622f, 240f, 24f), style, "", 16f, TextAlignmentOptions.MidlineLeft);
                Button(At("ZoomOut", panel, 270f, 617f, 34f, 28f), style, "-").onClick.AddListener(() => Zoom(.75f));
                Button(At("ZoomFit", panel, 309f, 617f, 56f, 28f), style, "Fit").onClick.AddListener(Fit);
                Button(At("ZoomIn", panel, 370f, 617f, 34f, 28f), style, "+").onClick.AddListener(() => Zoom(1.333333f));
                zoomLabel = Label(At("ZoomScale", panel, 18f, 45f, 235f, 23f), style, "", 14f, TextAlignmentOptions.MidlineLeft);
                focus = Button(At("FocusSelected", panel, 267f, 42f, 143f, 29f), style, "Show selected"); focus.onClick.AddListener(FocusSelected);
                var map = At("WeaponsMapScroll", panel, 18f, 78f, 406f, 528f); Fill(map, DeckColor).raycastTarget = true;
                scroll = map.gameObject.AddComponent<ScrollRect>(); scroll.horizontal = true; scroll.vertical = true; scroll.movementType = ScrollRect.MovementType.Clamped; scroll.scrollSensitivity = 28f;
                viewport = Rect("WeaponsMapViewport", map, Vector2.zero, Vector2.one, new Vector2(4f, 4f), new Vector2(-4f, -4f));
                Fill(viewport, DeckColor); viewport.gameObject.AddComponent<RectMask2D>();
                content = Rect("WeaponsMapContent", viewport, Vector2.up, Vector2.up, Vector2.zero, Vector2.zero); content.pivot = Vector2.up;
                deck = Rect("WeaponBayMap", content, Vector2.up, Vector2.up, Vector2.zero, Vector2.zero); deck.pivot = Vector2.up;
                scroll.viewport = viewport; scroll.content = content;
                Label(At("WeaponsLegend", panel, 18f, 13f, 406f, 22f), style, "Top view · Nose up · Numbers set release order", 14f, TextAlignmentOptions.MidlineLeft);

                Label(At("ChooseArmamentLabel", panel, 448f, 610f, 494f, 28f), style, "Choose armament", 20f, TextAlignmentOptions.MidlineLeft);
                catalogDropdown = Dropdown(template, panel, "WeaponsCatalogDropdown", 448f, 566f, 494f, 38f);
                catalogDropdown.onValueChanged.AddListener(_ => RefreshControls());
                catalogDetails = Label(At("ArmamentCatalogDetails", panel, 448f, 532f, 494f, 30f), style, "", 16f, TextAlignmentOptions.TopLeft);
                fillBay = Button(At("FillWeaponBayButton", panel, 528f, 497f, 124f, 28f), style, "Fill available"); fillBay.onClick.AddListener(FillAvailable);
                clearBay = Button(At("ClearWeaponBayButton", panel, 660f, 497f, 112f, 28f), style, "Clear bay"); clearBay.onClick.AddListener(ClearAll);
                resolve = Button(At("ResolveWeaponConflictsButton", panel, 780f, 497f, 162f, 28f), style, "Resolve conflicts"); resolve.onClick.AddListener(ResolveConflicts);
                Label(At("QuantityLabel", panel, 448f, 496f, 65f, 20f), style, "Batch size", 15f, TextAlignmentOptions.MidlineLeft);
                quantity = Input(At("WeaponsQuantity", panel, 448f, 459f, 70f, 34f), style, "1", true);
                add = Button(At("AddWeaponButton", panel, 528f, 459f, 182f, 34f), style, "+ batch"); add.onClick.AddListener(Add);
                remove = Button(At("RemoveWeaponButton", panel, 720f, 459f, 222f, 34f), style, "- batch"); remove.onClick.AddListener(Remove);
                Label(At("LoadedArmamentLabel", panel, 448f, 428f, 494f, 23f), style, "Items in this plan", 17f, TextAlignmentOptions.MidlineLeft);
                loadedDropdown = Dropdown(template, panel, "LoadedWeaponsDropdown", 448f, 390f, 494f, 34f);
                loadedDropdown.onValueChanged.AddListener(value => { if (!updating) Select(value); });
                selectionDetails = Label(At("SelectedWeaponDetails", panel, 448f, 315f, 494f, 68f), style, "", 17f, TextAlignmentOptions.TopLeft);
                Label(At("AcrossLabel", panel, 448f, 291f, 149f, 20f), style, "Across bay (m)", 14f, TextAlignmentOptions.MidlineLeft);
                Label(At("AlongLabel", panel, 615f, 291f, 149f, 20f), style, "From nose (m)", 14f, TextAlignmentOptions.MidlineLeft);
                Label(At("HeightLabel", panel, 782f, 291f, 160f, 20f), style, "Above floor (m)", 14f, TextAlignmentOptions.MidlineLeft);
                across = Input(At("WeaponAcrossInput", panel, 448f, 258f, 153f, 29f), style, "0.0");
                along = Input(At("WeaponAlongInput", panel, 615f, 258f, 153f, 29f), style, "0.0");
                height = Input(At("WeaponHeightInput", panel, 782f, 258f, 160f, 29f), style, "0.0");
                across.onEndEdit.AddListener(_ => EditCoordinates()); along.onEndEdit.AddListener(_ => EditCoordinates()); height.onEndEdit.AddListener(_ => EditCoordinates());
                rotate = Button(At("RotateWeaponButton", panel, 448f, 210f, 154f, 34f), style, "Rotate 90°"); rotate.onClick.AddListener(Rotate);
                earlier = Button(At("EarlierWeaponButton", panel, 618f, 210f, 154f, 34f), style, "Release earlier"); earlier.onClick.AddListener(() => Reorder(-1));
                later = Button(At("LaterWeaponButton", panel, 788f, 210f, 154f, 34f), style, "Release later"); later.onClick.AddListener(() => Reorder(1));
                heightDropdown = Dropdown(template, panel, "WeaponsHeightView", 448f, 164f, 494f, 32f);
                heightDropdown.onValueChanged.AddListener(_ => { if (!updating) RefreshColors(); });
                mass = Label(At("WeaponsMass", panel, 448f, 125f, 494f, 29f), style, "", 21f, TextAlignmentOptions.MidlineLeft);
                status = Label(At("WeaponsPlanStatus", panel, 448f, 63f, 494f, 58f), style, "", 16f, TextAlignmentOptions.TopLeft);
                selectionDetails.enableAutoSizing=true;selectionDetails.fontSizeMin=10f;selectionDetails.fontSizeMax=17f;
                catalogDetails.enableAutoSizing=true;catalogDetails.fontSizeMin=10f;catalogDetails.fontSizeMax=16f;
                apply = Button(At("ApplyWeaponsButton", panel, 448f, 14f, 237f, 39f), style, "Apply to loadout"); apply.onClick.AddListener(Apply);
                Button(At("CancelWeaponsButton", panel, 705f, 14f, 237f, 39f), style, "Cancel").onClick.AddListener(Close);
                panel.gameObject.AddComponent<ValidationPump>().View = this;
                SuperheavyNativeMenuStyle.Apply(panel, template);
                Fit();
            }

            internal void Toggle()
            {
                if (panel.gameObject.activeSelf) { Close(); return; }
                CancelValidation();
                geometry = SuperheavySpatialWeaponsRuntime.GetGeometry(aircraft);
                if (!GeometryAvailable(geometry))
                { Debug.LogWarning("BG armament organizer: measured bay geometry is unavailable."); return; }
                draft.Clear(); draft.AddRange(SuperheavySpatialWeaponsRuntime.GetPlan(aircraft) ?? Array.Empty<WeaponPlacement>());
                catalog.Clear(); catalog.AddRange((SuperheavySpatialWeaponsRuntime.GetCatalog(aircraft) ?? Array.Empty<WeaponItemSpec>()).Where(s => s != null));
                selected = -1; dirty = false; dragging = false;
                catalogDropdown.ClearOptions(); catalogDropdown.AddOptions(catalog.Select(CatalogName).ToList()); catalogDropdown.SetValueWithoutNotify(0); catalogDropdown.RefreshShownValue();
                FitPanel(); panel.gameObject.SetActive(true); RebuildDeck();
                ValidateAndReport("Add an item or drag one to a new position. Apply to use this plan.");
            }
            private void Close()
            {
                CancelValidation();
                dragging = false; catalogDropdown.Hide(); loadedDropdown.Hide(); heightDropdown.Hide(); panel.gameObject.SetActive(false);
            }
            private void FitPanel()
            {
                var canvas = panel.GetComponentInParent<Canvas>(); var root = canvas == null ? null : canvas.rootCanvas.transform as RectTransform;
                float factor = root == null ? 1f : Mathf.Min(1f, (root.rect.width - 32f) / PanelWidth, (root.rect.height - 140f) / PanelHeight);
                panel.localScale = Vector3.one * Mathf.Clamp(factor, .35f, 1f);
            }
            private void Fit()
            {
                if (geometry == null) return;
                SetScale(Mathf.Min(350f / geometry.Width, 466f / geometry.Length));
                scroll.horizontalNormalizedPosition = .5f; scroll.verticalNormalizedPosition = 1f;
            }
            private void Zoom(float factor) { SetScale(pixelsPerMeter * factor); if (selected >= 0) FocusSelected(false); }
            private void SetScale(float value)
            {
                pixelsPerMeter = Mathf.Clamp(value, 8f, 120f);
                float w = geometry.Width * pixelsPerMeter, h = geometry.Length * pixelsPerMeter;
                content.sizeDelta = new Vector2(Mathf.Max(398f, w + 48f), h + 48f);
                deck.sizeDelta = new Vector2(w, h); deck.anchoredPosition = new Vector2((content.sizeDelta.x - w) * .5f, -24f);
                foreach (Transform child in deck.Cast<Transform>().ToArray())
                    if (!child.name.StartsWith("WeaponItem", StringComparison.Ordinal)) { child.gameObject.SetActive(false); UnityEngine.Object.Destroy(child.gameObject); }
                DrawBay();
                if (blocks.Count > 0) RebuildDeck();
                zoomLabel.text = "Drag items · Scroll to pan · Snap 0.1 m";
            }
            private void DrawBay()
            {
                if (!deck.TryGetComponent<Image>(out var image)) image = Fill(deck, BayColor); image.color = BayColor;
                float w = geometry.Width * pixelsPerMeter, h = geometry.Length * pixelsPerMeter;
                for (int m = 1; m < geometry.Length; m++) Fill(Top("LengthGrid", deck, 0, m * pixelsPerMeter, w, m % 5 == 0 ? 1.2f : .55f), new Color(.36f, .48f, .54f, m % 5 == 0 ? .65f : .3f));
                for (int m = 1; m < geometry.Width; m++) Fill(Top("WidthGrid", deck, m * pixelsPerMeter, 0, .6f, h), new Color(.36f, .48f, .54f, .35f));
                foreach (var exit in geometry.ExitZones ?? Array.Empty<WeaponExitZone>())
                {
                    float left = (exit.Left - geometry.LeftX) * pixelsPerMeter, top = (geometry.FrontZ - exit.Front) * pixelsPerMeter;
                    float width = (exit.Right - exit.Left) * pixelsPerMeter, length = (exit.Front - exit.Aft) * pixelsPerMeter;
                    Fill(Top("ReleaseOpening", deck, left, top, width, length), new Color(.10f, .36f, .38f, .38f));
                }
                Fill(Top("BayEdgeTop", deck, 0, 0, w, 2), LabelColor); Fill(Top("BayEdgeAft", deck, 0, h - 2, w, 2), LabelColor);
                Fill(Top("BayEdgePort", deck, 0, 0, 2, h), LabelColor); Fill(Top("BayEdgeStarboard", deck, w - 2, 0, 2, h), LabelColor);
                foreach (var block in blocks) block.SetAsLastSibling();
                dimensions.text = $"{geometry.Length:0.00} × {geometry.Width:0.00} m · top view";
            }
            private void FocusSelected() { FocusSelected(true); }
            private void FocusSelected(bool zoom)
            {
                if (selected < 0 || selected >= draft.Count) return;
                if (zoom && pixelsPerMeter < 45f) SetScale(45f);
                var p = draft[selected]; float x = deck.anchoredPosition.x + (p.X + Width(p) * .5f) * pixelsPerMeter;
                float y = 24f + (p.Z + Length(p) * .5f) * pixelsPerMeter;
                float overflowX = content.sizeDelta.x - viewport.rect.width, overflowY = content.sizeDelta.y - viewport.rect.height;
                scroll.horizontalNormalizedPosition = overflowX <= 0f ? .5f : Mathf.Clamp01((x - viewport.rect.width * .5f) / overflowX);
                scroll.verticalNormalizedPosition = overflowY <= 0f ? 1f : 1f - Mathf.Clamp01((y - viewport.rect.height * .5f) / overflowY);
            }

            private bool BatchSize(out int count)
            {
                if (int.TryParse(quantity.text, NumberStyles.Integer, CultureInfo.InvariantCulture, out count) && count >= 1 && count <= 999) return true;
                Report("Choose a batch size from 1 to 999.", true); return false;
            }
            private void Add()
            {
                if (batchBusy || !BatchSize(out int count)) return;
                StartBatch(count);
            }
            private void FillAvailable() { if (!batchBusy) StartBatch(SuperheavySpatialWeaponsRuntime.MaximumItems - draft.Count); }
            private void StartBatch(int count)
            {
                if (catalog.Count == 0 || catalogDropdown.value >= catalog.Count || count <= 0) return;
                CancelValidation();
                if (!SuperheavySpatialWeaponsRuntime.PrepareValidation(aircraft, draft, out var items, out var snapshot, out var reason))
                { RefreshControls(); Report(Friendly(reason), true); return; }
                var spec = catalog[catalogDropdown.value];
                int requested = Math.Min(count, SuperheavySpatialWeaponsRuntime.MaximumItems - draft.Count);
                if (requested <= 0) return;
                var cancellation = new CancellationTokenSource(); var progress = new ValidationProgress();
                snapshot.ValidationCancellation = cancellation.Token; snapshot.ValidationProgress = progress.Report;
                var work = new ResolveJob { Generation = validationGeneration, Geometry = snapshot, Cancellation = cancellation, Progress = progress };
                // Immutable pure data only. Large autofills do not execute a
                // full 3-D route search from Update/the game render thread.
                work.Task = Task.Run(() => SuperheavySpatialWeaponsPlan.TryFillAvailable(items, spec, snapshot,
                    requested, SuperheavySpatialWeaponsRuntime.MaximumItems, 10000), cancellation.Token);
                batchJob = work; batchBusy = true; nextProgressUpdate = 0f;
                RefreshControls(); Report("Packing the complete draft; reserving gun lifts and ordering lower layers first...");
            }
            private void PumpBatch()
            {
                var work = batchJob; if (!batchBusy || work == null) return;
                if (panel == null || !panel.gameObject.activeInHierarchy || aircraft == null || selector == null) { CancelValidation(); return; }
                if (!work.Task.IsCompleted)
                {
                    if (Time.unscaledTime >= nextProgressUpdate)
                    { nextProgressUpdate = Time.unscaledTime + .12f; Report("Packing safe positions... " + work.Progress.Done + "/" + work.Progress.Total); }
                    return;
                }
                batchJob = null; batchBusy = false; work.Cancellation.Dispose();
                if (work.Generation != validationGeneration) { var ignored = work.Task.Exception; return; }
                if (work.Task.IsCanceled) { RefreshControls(); Report("Packing cancelled; the draft was preserved."); return; }
                if (work.Task.IsFaulted)
                { Debug.LogWarning("BG armament packing failed: " + work.Task.Exception); RefreshControls(); Report("Packing could not finish; no items were changed.", true); return; }
                var result = work.Task.Result;
                if (!result.Success || result.Validation == null || !result.Validation.CanApplyNative)
                { RefreshControls(); Report("Draft preserved. " + result.Reason, true); return; }
                if (!SuperheavySpatialWeaponsRuntime.RecordValidation(aircraft, result.Items, work.Geometry, result.Validation))
                { RefreshControls(); Report("The aircraft changed during packing. Repeat the batch.", true); return; }
                draft.Clear(); draft.AddRange(result.Items); selected = draft.Count - 1; dirty |= result.ItemsAdded > 0;
                validation = result.Validation; fullValidation = true; RebuildDeck();
                ReportValidation("Added " + result.ItemsAdded + ". Complete layout and release sequence checked. Apply to commit.", true);
            }
            private void ClearAll()
            {
                CancelValidation(); draft.Clear(); selected = -1; dirty = true;
                RebuildDeck(); ValidateAndReport("Bay cleared in this draft. Apply to commit, or Cancel to restore.");
            }
            private void Remove()
            {
                if (batchBusy || !BatchSize(out int count)) return;
                var spec = selected >= 0 && selected < draft.Count ? draft[selected].Spec :
                    catalog.Count > 0 ? catalog[Mathf.Clamp(catalogDropdown.value, 0, catalog.Count - 1)] : null;
                if (spec == null) return;
                CancelValidation(); int removed = 0;
                // Prefer the selected item and then the last matching items;
                // leave all other equipment types and their relative order intact.
                if (selected >= 0 && selected < draft.Count && draft[selected].Spec.Id == spec.Id)
                { draft.RemoveAt(selected); removed++; }
                for (int i = draft.Count - 1; i >= 0 && removed < count; i--)
                    if (draft[i].Spec.Id == spec.Id) { draft.RemoveAt(i); removed++; }
                selected = Math.Min(selected, draft.Count - 1); dirty |= removed > 0;
                RebuildDeck(); ValidateAndReport("Removed " + removed + " matching item(s). Repeat - batch for another group.");
            }
            private void Rotate()
            {
                if (selected < 0 || !draft[selected].Spec.AllowRotation90) return;
                var p = draft[selected]; draft[selected] = new WeaponPlacement(p.Spec, p.X, p.Z, p.Y, !p.Rotation90);
                var next = draft[selected]; draft[selected] = p;
                if (RejectGunConflict(next)) return;
                CancelValidation(); draft[selected] = next;
                dirty = true; RebuildDeck(); ValidateAndReport("Orientation changed.");
            }
            private void Reorder(int shift)
            {
                int next = selected + shift; if (selected < 0 || next < 0 || next >= draft.Count) return;
                CancelValidation();
                var p = draft[selected]; draft.RemoveAt(selected); draft.Insert(next, p); selected = next; dirty = true;
                RebuildDeck(); ValidateAndReport("Release/deployment order changed.");
            }
            private void EditCoordinates()
            {
                if (updating || selected < 0 || selected >= draft.Count) return;
                if (!Number(across.text, out float x) || !Number(along.text, out float z) || !Number(height.text, out float y))
                { RefreshControls(); Report("Enter distances in metres.", true); return; }
                var p = draft[selected]; var next = new WeaponPlacement(p.Spec, Snap(x), Snap(z), p.Spec.HeightAdjustable ? Snap(y) : p.Spec.DefaultY, p.Rotation90);
                if (RejectGunConflict(next)) { RefreshControls(); return; }
                CancelValidation(); draft[selected] = next;
                dirty = true; RebuildDeck(); ValidateAndReport("Position changed.");
            }
            private void Apply()
            {
                if (dragging || !dirty || validationJob != null || !fullValidation || validation == null || !validation.CanApplyNative) return;
                // The current completed generation was recorded on the main
                // thread. TryApply verifies that exact proof and does not run
                // the route solver again inside the button callback.
                CancelValidation();
                RefreshControls();
                try
                {
                    if (!SuperheavySpatialWeaponsRuntime.TryApply(selector, aircraft, draft.ToArray(), out var reason))
                    { Report(Friendly(reason), true); return; }
                    dirty = false;
                    if (panel == null || !panel.gameObject.activeInHierarchy) return;
                    draft.Clear(); draft.AddRange(SuperheavySpatialWeaponsRuntime.GetPlan(aircraft) ?? Array.Empty<WeaponPlacement>());
                    selected = Math.Min(selected, draft.Count - 1); dirty = false; RebuildDeck();
                    ValidateAndReport("Armament plan applied to this loadout.");
                }
                catch (Exception error) { Debug.LogWarning("BG armament plan apply failed: " + error); Report("The loadout could not be updated. Reopen this panel and try again.", true); }
            }

            private void RebuildDeck()
            {
                foreach (var b in blocks) { b.gameObject.SetActive(false); b.name += " (closing)"; UnityEngine.Object.Destroy(b.gameObject); }
                blocks.Clear(); physicalShapes.Clear(); numbers.Clear();
                for (int i = 0; i < draft.Count; i++)
                {
                    int index = i; var placement = draft[i]; var b = Top("WeaponItem" + i, deck, 0, 0, 1, 1); PlaceBlock(b, placement);
                    var outline = ItemColor(placement.Spec); outline.a = .10f; Fill(b, outline).raycastTarget = false;
                    var union = SuperheavySpatialWeaponsPlan.Union(SuperheavySpatialWeaponsPlan.ReservedVolumes(placement, geometry));
                    var shapes = new List<Image>(); int shapeNumber = 0;
                    foreach (var volume in SuperheavySpatialWeaponsPlan.ReservedVolumes(placement, geometry))
                    {
                        var rect = Top("PhysicalVolume" + shapeNumber++, b, (volume.Min.X - union.Min.X) * pixelsPerMeter,
                            (union.Max.Z - volume.Max.Z) * pixelsPerMeter, (volume.Max.X - volume.Min.X) * pixelsPerMeter, (volume.Max.Z - volume.Min.Z) * pixelsPerMeter);
                        var image = Fill(rect, ItemColor(placement.Spec)); image.raycastTarget = true; shapes.Add(image);
                    }
                    var number = Label(Rect("WeaponNumber", b, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero), style, (i + 1).ToString(), 12f);
                    number.enableAutoSizing = true; number.fontSizeMin = 7f; number.fontSizeMax = 13f;
                    var e = b.gameObject.AddComponent<EventTrigger>();
                    Bind(e, EventTriggerType.PointerClick, _ => Select(index)); Bind(e, EventTriggerType.BeginDrag, p => BeginDrag(index, p));
                    Bind(e, EventTriggerType.Drag, Drag); Bind(e, EventTriggerType.EndDrag, EndDrag);
                    blocks.Add(b); physicalShapes.Add(shapes); numbers.Add(number);
                }
                RefreshLists(); RefreshControls(); RefreshColors();
            }
            private void RefreshLists()
            {
                updating = true;
                loadedDropdown.ClearOptions(); loadedDropdown.AddOptions(draft.Select((p, i) => (i + 1) + ". " + CatalogName(p.Spec)).ToList());
                loadedDropdown.SetValueWithoutNotify(Math.Max(0, selected)); loadedDropdown.RefreshShownValue();
                float? oldHeight = heightDropdown.value > 0 && heightDropdown.value - 1 < shownHeights.Count ? shownHeights[heightDropdown.value - 1] : (float?)null;
                shownHeights.Clear(); shownHeights.AddRange(draft.Select(p => p.Y).Distinct().OrderBy(y => y));
                heightDropdown.ClearOptions(); heightDropdown.AddOptions(new[] { "Show all heights" }.Concat(shownHeights.Select(y => $"Height {y:+0.0;-0.0;0.0} m above floor")).ToList());
                int h = oldHeight.HasValue ? shownHeights.FindIndex(y => Mathf.Abs(y - oldHeight.Value) < .05f) : -1;
                heightDropdown.SetValueWithoutNotify(h + 1); heightDropdown.RefreshShownValue(); updating = false;
            }
            private void Select(int index)
            {
                if (index < 0 || index >= draft.Count) return; selected = index; loadedDropdown.SetValueWithoutNotify(index); loadedDropdown.RefreshShownValue();
                blocks[index].SetAsLastSibling(); RefreshControls(); RefreshColors();
            }
            private void BeginDrag(int index, PointerEventData pointer)
            {
                if (batchBusy || resolveJob != null || pointer.button != PointerEventData.InputButton.Left || !PointerMeters(pointer, out var p)) return;
                CancelValidation();
                Select(index); var item = draft[index]; dragOffset = p - new Vector2(item.X, item.Z); dragging = true;
            }
            private void Drag(PointerEventData pointer)
            {
                if (!dragging || selected < 0 || !PointerMeters(pointer, out var mouse)) return;
                var p = draft[selected]; var point = mouse - dragOffset;
                var next = new WeaponPlacement(p.Spec, Snap(point.x), Snap(point.y), p.Y, p.Rotation90);
                if (Mathf.Approximately(next.X, p.X) && Mathf.Approximately(next.Z, p.Z)) return;
                if (RejectGunConflict(next)) return;
                draft[selected] = next; dirty = true; PlaceBlock(blocks[selected], next);
                // Avoid running the whole physical path solver every pointer
                // frame. Native validation runs on release before Apply can arm.
                foreach (var shape in physicalShapes[selected]) shape.color = WarningColor;
                Report("Release to check this position.");
                RefreshControls();
            }
            private void EndDrag(PointerEventData pointer)
            {
                if (!dragging) return; Drag(pointer); dragging = false; RefreshLists(); ValidateAndReport("Position changed.");
            }
            private bool PointerMeters(PointerEventData pointer, out Vector2 p)
            {
                p = default; if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(deck, pointer.position, pointer.pressEventCamera, out var local)) return false;
                p = new Vector2(local.x - deck.rect.xMin, deck.rect.yMax - local.y) / pixelsPerMeter; return true;
            }
            private bool RejectGunConflict(WeaponPlacement next)
            {
                if (!SuperheavySpatialWeaponsPlan.ConflictsWithGun(draft, selected, next, geometry)) return false;
                Report("This space is reserved for a complete gun module and its lift. Move the item elsewhere or use Resolve conflicts.", true);
                return true;
            }
            private void PlaceBlock(RectTransform rect, WeaponPlacement p)
            {
                var b = SuperheavySpatialWeaponsPlan.Union(SuperheavySpatialWeaponsPlan.ReservedVolumes(p, geometry));
                float left = (b.Min.X - geometry.LeftX) * pixelsPerMeter, top = (geometry.FrontZ - b.Max.Z) * pixelsPerMeter;
                Place(rect, Vector2.up, Vector2.up, new Vector2(left, -top - (b.Max.Z - b.Min.Z) * pixelsPerMeter),
                    new Vector2(left + (b.Max.X - b.Min.X) * pixelsPerMeter, -top));
            }
            private void ResolveConflicts()
            {
                if (batchBusy || resolveJob != null || dragging || draft.Count == 0) return;
                CancelValidation();
                if (!SuperheavySpatialWeaponsRuntime.PrepareValidation(aircraft, draft, out var items, out var snapshot, out var reason))
                { RefreshControls(); Report(Friendly(reason), true); return; }
                var cancellation = new CancellationTokenSource(); var progress = new ValidationProgress();
                snapshot.ValidationCancellation = cancellation.Token; snapshot.ValidationProgress = progress.Report;
                var work = new ResolveJob { Generation = validationGeneration, Geometry = snapshot, Cancellation = cancellation, Progress = progress };
                work.Task = Task.Run(() => SuperheavySpatialWeaponsPlan.TryResolveConflicts(items, snapshot, 25000, 15000), cancellation.Token);
                resolveJob = work; nextProgressUpdate = 0f; RefreshControls();
                Report("Resolving conflicts; all items and quantities will be preserved...");
            }
            private void PumpResolve()
            {
                var work = resolveJob; if (work == null) return;
                if (panel == null || !panel.gameObject.activeInHierarchy || aircraft == null || selector == null) { CancelValidation(); return; }
                if (!work.Task.IsCompleted)
                {
                    if (Time.unscaledTime >= nextProgressUpdate)
                    { nextProgressUpdate = Time.unscaledTime + .12f; Report("Resolving conflicts... " + work.Progress.Done + "/" + work.Progress.Total); }
                    return;
                }
                resolveJob = null; work.Cancellation.Dispose();
                if (work.Generation != validationGeneration) { var ignored = work.Task.Exception; return; }
                if (work.Task.IsCanceled) { RefreshControls(); Report("Resolution cancelled; the draft was preserved."); return; }
                if (work.Task.IsFaulted)
                { Debug.LogWarning("BG conflict resolution failed: " + work.Task.Exception); RefreshControls(); Report("Unable to resolve this draft; no items were changed.", true); return; }
                var result = work.Task.Result;
                if (!result.Success || result.Validation == null || !result.Validation.CanApplyNative)
                { RefreshControls(); Report("No complete arrangement found. No items were removed. " + result.Reason, true); return; }
                if (!SuperheavySpatialWeaponsRuntime.RecordValidation(aircraft, result.Items, work.Geometry, result.Validation))
                { RefreshControls(); Report("The aircraft changed during resolution. Run Resolve conflicts again.", true); return; }
                draft.Clear(); draft.AddRange(result.Items); selected = Math.Min(selected, draft.Count - 1); dirty = true;
                validation = result.Validation; fullValidation = true; RebuildDeck();
                ReportValidation("Conflicts resolved; " + result.ItemsMoved + " item(s) moved. Apply to use this arrangement.", true);
            }
            private void ValidateAndReport(string goodMessage = null)
            {
                CancelValidation();
                if (panel == null || !panel.gameObject.activeInHierarchy || aircraft == null || selector == null) return;
                // Prepare/canonicalize while Unity is on its main thread. The
                // returned snapshot has only immutable measured arrays/data.
                if (!SuperheavySpatialWeaponsRuntime.PrepareValidation(aircraft, draft, out var items, out var snapshot, out var reason))
                { Report(Friendly(reason), true); RefreshControls(); return; }
                var cancellation = new CancellationTokenSource();
                var progress = new ValidationProgress();
                snapshot.ValidationCancellation = cancellation.Token;
                snapshot.ValidationProgress = progress.Report;
                var work = new ValidationJob { Generation = validationGeneration, Full = dirty, GoodMessage = goodMessage,
                    Items = items, Geometry = snapshot, Cancellation = cancellation, Progress = progress };
                bool full = work.Full;
                // Capture local pure values only. No Hold/Unity object or
                // runtime API is dereferenced by the worker closure.
                work.Task = Task.Run(() => SuperheavySpatialWeaponsPlan.TryValidate(items, snapshot, full), cancellation.Token);
                validationJob = work; nextProgressUpdate = 0f;
                RefreshColors(); RefreshControls(); ShowProgress(work);
            }
            internal void CancelValidation()
            {
                validationGeneration++; batchBusy = false;
                var packing = batchJob; batchJob = null;
                if (packing != null)
                {
                    packing.Cancellation.Cancel();
                    packing.Task.ContinueWith(task => { var ignored = task.Exception; packing.Cancellation.Dispose(); }, TaskScheduler.Default);
                }
                var resolving = resolveJob; resolveJob = null;
                if (resolving != null)
                {
                    resolving.Cancellation.Cancel();
                    resolving.Task.ContinueWith(task => { var ignored = task.Exception; resolving.Cancellation.Dispose(); }, TaskScheduler.Default);
                }
                var work = validationJob; validationJob = null; validation = null; fullValidation = false;
                if (work == null) return;
                work.Cancellation.Cancel();
                // Observe exceptions even after the view is gone. The
                // continuation owns no Unity object and never publishes UI.
                work.Task.ContinueWith(task => { var ignored = task.Exception; work.Cancellation.Dispose(); }, TaskScheduler.Default);
            }
            internal void PumpValidation()
            {
                if (resolveJob != null) { PumpResolve(); return; }
                if (batchBusy) { PumpBatch(); return; }
                var work = validationJob;
                if (work == null) return;
                if (panel == null || !panel.gameObject.activeInHierarchy || aircraft == null || selector == null)
                { CancelValidation(); return; }
                if (!work.Task.IsCompleted)
                {
                    if (Time.unscaledTime >= nextProgressUpdate) { nextProgressUpdate = Time.unscaledTime + .12f; ShowProgress(work); }
                    return;
                }
                validationJob = null;
                work.Cancellation.Dispose();
                if (work.Generation != validationGeneration) { var ignored = work.Task.Exception; return; }
                if (work.Task.IsCanceled) { RefreshControls(); Report("Check cancelled. Move an item to check it again."); return; }
                if (work.Task.IsFaulted)
                {
                    Debug.LogWarning("BG armament validation failed: " + work.Task.Exception);
                    RefreshControls(); Report("The arrangement could not be checked. Change it or reopen the panel.", true); return;
                }
                validation = work.Task.Result; fullValidation = work.Full;
                if (work.Full && validation != null && validation.CanApplyNative &&
                    !SuperheavySpatialWeaponsRuntime.RecordValidation(aircraft, work.Items, work.Geometry, validation))
                { validation = null; fullValidation = false; RefreshControls(); Report("The aircraft changed during the check. Move an item to check the new arrangement.", true); return; }
                RefreshColors(); RefreshControls();
                ReportValidation(work.GoodMessage, work.Full);
            }
            private void ShowProgress(ValidationJob work)
            {
                string text = work.Full ? "Checking fit and release routes..." : "Checking the current bay layout...";
                int total = work.Progress.Total; if (total > 0) text += " " + work.Progress.Done + "/" + total;
                Report(text);
            }
            private void ReportValidation(string goodMessage, bool full)
            {
                if (validation == null) { Report("The bay could not be checked. Reopen this panel.", true); return; }
                if (validation.Issues.Count > 0)
                {
                    string text = string.Join("\n", validation.Issues.Take(2).Select(Friendly));
                    if (validation.Issues.Count > 2) text += "\n+" + (validation.Issues.Count - 2) + " more issues";
                    Report(text, !validation.PlacementValid, full && !validation.CanApplyNative && validation.PlacementValid);
                }
                else if (validation.OverNominalMass)
                    Report((goodMessage == null ? "Plan fits." : goodMessage) + "\nWeapon load is above the nominal takeoff budget.", warning: true);
                else Report(goodMessage ?? (dirty ? "Plan fits. Apply to update the loadout." : "Current armament plan. Move an item to check a changed release route."));
            }
            private void RefreshControls()
            {
                bool busy = batchBusy || resolveJob != null;
                bool have = !busy && selected >= 0 && selected < draft.Count; catalogDropdown.interactable = !busy && catalog.Count > 0; add.interactable = !busy && catalog.Count > 0;
                fillBay.interactable = !busy && catalog.Count > 0; clearBay.interactable = draft.Count > 0 || busy; quantity.interactable = !busy;
                loadedDropdown.interactable = !busy && draft.Count > 0; remove.interactable = !busy && draft.Count > 0; focus.interactable = have;
                across.interactable = along.interactable = have; height.interactable = have && draft[selected].Spec.HeightAdjustable;
                rotate.interactable = have && draft[selected].Spec.AllowRotation90; earlier.interactable = have && selected > 0; later.interactable = have && selected < draft.Count - 1;
                apply.interactable = !busy && dirty && !dragging && validationJob == null && fullValidation && validation != null && validation.CanApplyNative;
                resolve.interactable = !busy && !dragging && draft.Count > 0;
                title.text = "Armament bay" + (dirty ? " *" : "");
                catalogDetails.text = catalog.Count == 0 ? "No measured armament is available." : Details(catalog[Mathf.Clamp(catalogDropdown.value, 0, catalog.Count - 1)]);
                selectionDetails.text = have ? $"{selected + 1}. {draft[selected].Spec.Label}\n{Details(draft[selected].Spec)}\nTop above floor: {draft[selected].Y + draft[selected].Spec.Height:0.00} m" +
                    (draft[selected].Spec.HeightAdjustable ? "" : " · fixed height") : "Select an item on the map or in the list.\nDrag to move it; edit height to use another level.";
                updating = true;
                across.SetTextWithoutNotify(have ? draft[selected].X.ToString("0.0", CultureInfo.CurrentCulture) : "");
                along.SetTextWithoutNotify(have ? draft[selected].Z.ToString("0.0", CultureInfo.CurrentCulture) : "");
                height.SetTextWithoutNotify(have ? draft[selected].Y.ToString("0.0", CultureInfo.CurrentCulture) : ""); updating = false;
                double kg = validation?.TotalMassKg ?? draft.Sum(p => (double)p.Spec.MassKg);
                mass.text = $"Armament mass   {kg / 1000d:0.0} t";
                mass.color = LabelColor;
            }
            private void RefreshColors()
            {
                var invalid = new HashSet<int>(); var route = new HashSet<int>();
                foreach (string issue in validation?.Issues ?? new List<string>())
                {
                    var match = IndexedIssue.Match(issue); if (!match.Success) continue;
                    int index = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                    if (issue.StartsWith("placement", StringComparison.Ordinal)) invalid.Add(index); else route.Add(index);
                }
                float? level = heightDropdown.value > 0 && heightDropdown.value - 1 < shownHeights.Count ? shownHeights[heightDropdown.value - 1] : (float?)null;
                for (int i = 0; i < blocks.Count; i++)
                {
                    var color = invalid.Contains(i) ? new Color(.72f, .26f, .24f, 1f) : route.Contains(i) ? new Color(.67f, .49f, .21f, 1f) : i == selected ? ActiveColor : ItemColor(draft[i].Spec);
                    if (level.HasValue && Mathf.Abs(draft[i].Y - level.Value) > .05f) color.a = .20f;
                    foreach (var image in physicalShapes[i])
                    {
                        image.color = color;
                        image.raycastTarget = !level.HasValue || Mathf.Abs(draft[i].Y - level.Value) <= .05f;
                    }
                    var outline = color; outline.a = i == selected ? .22f : .07f; blocks[i].GetComponent<Image>().color = outline;
                    numbers[i].color = level.HasValue && Mathf.Abs(draft[i].Y - level.Value) > .05f ? new Color(1, 1, 1, .3f) : LabelColor;
                }
            }
            private void Report(string text, bool error = false, bool warning = false)
            { status.text = text; status.color = error ? ErrorColor : warning ? WarningColor : LabelColor; }
        }

        private static string CatalogName(WeaponItemSpec spec) => spec.Label + (spec.MountedRounds > 1 ? spec.Kind == SpatialWeaponKind.Gun ? " · " + spec.MountedRounds + " rounds" :
            Regex.IsMatch(spec.Label ?? "", @"(?:x|×)\s*" + spec.MountedRounds + @"\s*$", RegexOptions.IgnoreCase) ? "" : " x" + spec.MountedRounds : "");
        private static string Details(WeaponItemSpec spec) => $"{spec.Length:0.00} × {spec.Width:0.00} × {spec.Height:0.00} m · {spec.MassKg / 1000f:0.00} t" + (spec.MountedRounds > 1 ? $"\n{spec.MountedRounds} rounds, complete rack/installation" : "");
        private static float Width(WeaponPlacement p) => p.Rotation90 ? p.Spec.Length : p.Spec.Width;
        private static float Length(WeaponPlacement p) => p.Rotation90 ? p.Spec.Width : p.Spec.Length;
        private static float Snap(float value) => Mathf.Round(value / SnapMeters) * SnapMeters;
        private static float CeilSnap(float value) => Mathf.Ceil(value / SnapMeters) * SnapMeters;
        private static bool GeometryAvailable(WeaponPlanGeometry g) => g != null && g.Width > 0f && g.Length > 0f &&
            !float.IsInfinity(g.Width) && !float.IsInfinity(g.Length);
        private static bool Number(string text, out float value) => (float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) || float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) && !float.IsNaN(value) && !float.IsInfinity(value);
        private static Color ItemColor(WeaponItemSpec spec) => spec.Kind == SpatialWeaponKind.Gun ? new Color(.46f, .39f, .24f, 1f) : spec.Kind == SpatialWeaponKind.Missile ? new Color(.21f, .40f, .55f, 1f) : new Color(.24f, .43f, .34f, 1f);
        private static string Friendly(string issue)
        {
            if (string.IsNullOrEmpty(issue)) return "The plan could not be applied. Check the selected item's clearance.";
            var match = IndexedIssue.Match(issue); string item = match.Success ? "Item " + (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) + 1) : "The plan";
            var other = OtherItem.Match(issue); string second = other.Success ? "item " + (int.Parse(other.Groups[1].Value, CultureInfo.InvariantCulture) + 1) : "another item";
            string s = issue.ToLowerInvariant();
            if (s.Contains("mass exceeds") || s.Contains("mass capacity") || s.Contains("mass exceeds bay capacity")) return "The weapons and ammunition exceed the bay's load limit.";
            if (s.Contains("stow height") || s.Contains("gun storage height")) return item + " must keep its mounting height; move it across or along the bay instead.";
            if (s.Contains("outside measured chamber")) return item + " is outside the bay or too tall.";
            if (s.Contains("overlaps complete")) return item + " overlaps " + second + ".";
            if (s.Contains("door opening sweep")) return item + " touches a wall, structure, fuel tank or a bay door's opening path.";
            if (s.Contains("actual wall") || s.Contains("hits structure")) return item + " touches a wall, structure or fuel tank.";
            if (s.Contains("cannot move through")) return item + " needs a clear route past " + second + ".";
            if (s.Contains("retained launcher hardware")) return item + " would cross another item's launcher after release. Change their order or position.";
            if (s.Contains("earlier deployed gun")) return item + " would cross " + second + " after deployment. Change their order or position.";
            if (s.Contains("no measured deployment profile")) return item + " cannot deploy from this position.";
            if (s.Contains("crosses hull")) return item + " must pass through an open bay exit.";
            if (s.Contains("no complete physical clearance")) return item + " does not fully clear the aircraft.";
            if (s.Contains("no complete clearance below hull")) return item + " does not fully clear the underside of the aircraft.";
            if (s.Contains("internal manoeuvre")) return item + " cannot turn inside the bay.";
            if (s.Contains("unsupported rotation")) return item + " cannot use that orientation.";
            if (s.Contains("path is pending") || s.Contains("disconnected")) return item + " has no clear deployment or release route.";
            if (s.Contains("search budget exhausted")) return item + " needs a simpler release route; this arrangement could not be checked completely.";
            if (s.Contains("no retained round") || s.Contains("no continuous route")) return item + " has no clear route to an opening past the remaining weapons, walls or doors. Change its position or release order.";
            if (s.Contains("starts intersecting retained")) return item + " starts against remaining weapons, a wall or an open door.";
            if (s.Contains("missing/invalid measured bay")) return "The bay measurements are unavailable. Reopen the loadout.";
            if (s.Contains("armament unavailable") || s.Contains("native mount") || s.Contains("unavailable mount")) return "A selected weapon is unavailable. Check that its required mod is enabled and reopen the loadout.";
            if (s.Contains("slot capacity")) return "This plan contains more separate items than the loadout supports. Use a rack for groups of stores.";
            if (s.Contains("solo native hangar")) return "Open this aircraft's loadout in a solo mission to manage its armament.";
            if (s.Contains("initialization") || s.Contains("preview did not retain")) return "The aircraft loadout is still being prepared. Reopen it and apply the plan again.";
            if (s.StartsWith("native:", StringComparison.Ordinal) || s.Contains("capability") || s.Contains("fingerprint")) return "This arrangement cannot be applied. Check the release route and supported weapon positions.";
            if (s.Contains("invalid measured item")) return item + " has incomplete dimensions or an unsupported orientation.";
            if (s.StartsWith("routing[", StringComparison.Ordinal) || s.StartsWith("placement[", StringComparison.Ordinal)) return item + " cannot fit or move safely in this arrangement.";
            return issue;
        }

        private static void Bind(EventTrigger trigger, EventTriggerType type, Action<PointerEventData> callback)
        { var entry = new EventTrigger.Entry { eventID = type }; entry.callback.AddListener(data => callback((PointerEventData)data)); trigger.triggers.Add(entry); }
        private static Button Button(RectTransform rect, TMP_Text style, string text)
        {
            var button = rect.gameObject.AddComponent<Button>(); var image = Fill(rect, BayColor); image.raycastTarget = true; button.targetGraphic = image;
            Label(Rect("Label", rect, Vector2.zero, Vector2.one, new Vector2(3, 0), new Vector2(-3, 0)), style, text, 18f); return button;
        }
        private static TMP_InputField Input(RectTransform rect, TMP_Text style, string text, bool integer = false)
        {
            Fill(rect, DeckColor).raycastTarget = true; var input = rect.gameObject.AddComponent<TMP_InputField>();
            var view = Rect("TextViewport", rect, Vector2.zero, Vector2.one, new Vector2(6f, 1f), new Vector2(-6f, -1f)); view.gameObject.AddComponent<RectMask2D>();
            var label = (TextMeshProUGUI)Label(Rect("Text", view, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero), style, text, 18f, TextAlignmentOptions.MidlineLeft);
            input.textViewport = view; input.textComponent = label; input.lineType = TMP_InputField.LineType.SingleLine; input.characterLimit = integer ? 3 : 12;
            input.contentType = integer ? TMP_InputField.ContentType.IntegerNumber : TMP_InputField.ContentType.Standard; input.text = text; input.caretColor = LabelColor; return input;
        }
        private static TMP_Dropdown Dropdown(TMP_Dropdown template, Transform parent, string name, float x, float y, float w, float h)
        {
            var dropdown = UnityEngine.Object.Instantiate(template, parent, false); dropdown.name = name;
            foreach (Transform child in dropdown.transform) if (child.name == "Dropdown List") { child.gameObject.SetActive(false); UnityEngine.Object.Destroy(child.gameObject); }
            dropdown.onValueChanged = new TMP_Dropdown.DropdownEvent(); dropdown.navigation = Navigation.defaultNavigation;
            Place((RectTransform)dropdown.transform, Vector2.zero, Vector2.zero, new Vector2(x, y), new Vector2(x + w, y + h)); dropdown.gameObject.SetActive(true); return dropdown;
        }
        private static TMP_Text Label(RectTransform rect, TMP_Text style, string text, float size, TextAlignmentOptions alignment = TextAlignmentOptions.Midline)
        {
            var label = rect.gameObject.AddComponent<TextMeshProUGUI>(); if (style != null) { label.font = style.font; label.fontSharedMaterial = style.fontSharedMaterial; }
            label.text = text; label.fontSize = size; label.alignment = alignment; label.color = LabelColor; label.raycastTarget = false; return label;
        }
        private static RectTransform At(string name, Transform parent, float x, float y, float w, float h) => Rect(name, parent, Vector2.zero, Vector2.zero, new Vector2(x, y), new Vector2(x + w, y + h));
        private static RectTransform Top(string name, Transform parent, float x, float top, float w, float h) => Rect(name, parent, Vector2.up, Vector2.up, new Vector2(x, -top - h), new Vector2(x + w, -top));
        private static RectTransform Rect(string name, Transform parent, Vector2 min, Vector2 max, Vector2 a, Vector2 b)
        {
            var r = (RectTransform)new GameObject(name, typeof(RectTransform)).transform; r.gameObject.layer = parent.gameObject.layer; r.SetParent(parent, false); Place(r, min, max, a, b); return r;
        }
        private static void Place(RectTransform r, Vector2 min, Vector2 max, Vector2 a, Vector2 b) { r.anchorMin = min; r.anchorMax = max; r.offsetMin = a; r.offsetMax = b; }
        private static Image Fill(RectTransform rect, Color color) { var image = rect.gameObject.AddComponent<Image>(); image.color = color; image.raycastTarget = false; return image; }
    }
}
