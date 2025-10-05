using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

public class MissionPromptController : MonoBehaviour
{
    private UIDocument doc;

    // Root / bars
    private VisualElement appRoot, topbar, content;
    private VisualElement editorBar;
    private Button btnMissionDesigner, btnLayoutEditor, btnModuleEditor, btnEnvironmentEditor;

    // Modal
    private VisualElement overlay;
    private Button btnCreate, btnCancel;
    private DropdownField missionTypeField;
    private DropdownField missionDurationField; // <-- NUEVO
    private Button btnAddCrew, btnRemoveCrew;
    private Label crewLabel;
    private VisualElement crewIcons;
    private int crewCount = 0;

    // Panels
    private VisualElement layoutPanel, modulePanel;

    // Layout Editor (Clean / Dirty)
    private VisualElement cleanGrid, dirtyGrid;
    private readonly HashSet<VisualElement> selectedClean = new HashSet<VisualElement>();
    private readonly HashSet<VisualElement> selectedDirty = new HashSet<VisualElement>();

    // Module fields
    private IntegerField heightField, widthField, depthField;
    private Label volumeLabel;

    // Module Editor tiles
    private VisualElement objectGrid, zoneGrid;
    private readonly HashSet<VisualElement> selectedObjects = new HashSet<VisualElement>();
    private VisualElement selectedZone;

    void Awake()
    {
        doc = GetComponent<UIDocument>();
        var root = doc.rootVisualElement;

        // Root / bars
        appRoot = root.Q<VisualElement>("app");
        topbar  = root.Q<VisualElement>("topbar");
        content = root.Q<VisualElement>("content");

        // === LOGO ===
        var appLogo = root.Q<Image>("appLogo");
        var logoTex = Resources.Load<Texture2D>("logo_shelter");
        if (appLogo != null && logoTex != null) appLogo.image = logoTex;

        // Topbar / editor bar
        btnMissionDesigner   = root.Q<Button>("btnMissionDesigner");
        editorBar            = root.Q<VisualElement>("editorBar");
        btnLayoutEditor      = root.Q<Button>("btnLayoutEditor");
        btnModuleEditor      = root.Q<Button>("btnModuleEditor");
        btnEnvironmentEditor = root.Q<Button>("btnEnvironmentEditor");

        if (btnMissionDesigner != null)
            btnMissionDesigner.clicked += () => overlay.RemoveFromClassList("hidden");
        if (btnLayoutEditor != null)
            btnLayoutEditor.clicked += () => ShowPanel(layoutPanel);
        if (btnModuleEditor != null)
            btnModuleEditor.clicked += () => ShowPanel(modulePanel);
        if (btnEnvironmentEditor != null)
            btnEnvironmentEditor.clicked += () => Debug.Log("Environment Editor (todo)");

        // Modal
        overlay              = root.Q<VisualElement>("mdOverlay");
        btnCreate            = root.Q<Button>("btnCreate");
        btnCancel            = root.Q<Button>("btnCancel");
        missionTypeField     = root.Q<DropdownField>("missionType");
        missionDurationField = root.Q<DropdownField>("missionDuration");
        btnAddCrew           = root.Q<Button>("btnAddCrew");
        btnRemoveCrew        = root.Q<Button>("btnRemoveCrew");
        crewLabel            = root.Q<Label>("crewLabel");
        crewIcons            = root.Q<VisualElement>("crewIcons");

        if (btnCancel != null)     btnCancel.clicked     += () => overlay.AddToClassList("hidden");
        if (btnCreate != null)     btnCreate.clicked     += OnMissionCreated;
        if (btnAddCrew != null)    btnAddCrew.clicked    += () => UpdateCrew(+1);
        if (btnRemoveCrew != null) btnRemoveCrew.clicked += () => UpdateCrew(-1);

        // === Opciones de los Dropdown ===
        if (missionTypeField != null)
        {
            missionTypeField.choices = new List<string>
            {
                "Spacial",
                "Planetary Surface",
            };
            missionTypeField.value = missionTypeField.choices[0];
        }

        if (missionDurationField != null)
        {
            missionDurationField.choices = new List<string>
            {
                "<30",
                "<180",
                ">180"
            };
            missionDurationField.value = missionDurationField.choices[0];
        }

        // Panels
        layoutPanel = root.Q<VisualElement>("layoutPanel");
        modulePanel = root.Q<VisualElement>("modulePanel");

        // Layout Editor subgrids
        cleanGrid = layoutPanel?.Q<VisualElement>("cleanGrid");
        dirtyGrid = layoutPanel?.Q<VisualElement>("dirtyGrid");
        WireLayoutZoneTiles(cleanGrid, selectedClean);
        WireLayoutZoneTiles(dirtyGrid, selectedDirty);

        // Module fields
        heightField = root.Q<IntegerField>("heightField");
        widthField  = root.Q<IntegerField>("widthField");
        depthField  = root.Q<IntegerField>("depthField");
        volumeLabel = root.Q<Label>("volumeLabel");

        heightField?.RegisterValueChangedCallback(_ => UpdateVolume());
        widthField ?.RegisterValueChangedCallback(_ => UpdateVolume());
        depthField ?.RegisterValueChangedCallback(_ => UpdateVolume());

        // Module Editor tiles
        objectGrid = root.Q<VisualElement>("objectGrid");
        zoneGrid   = root.Q<VisualElement>("zoneGrid");
        WireModuleTiles(objectGrid, isZone:false);
        WireModuleTiles(zoneGrid,   isZone:true);

        // Cancel panel buttons
        var cancelLayoutBtn = root.Q<Button>("btnCancelLayout");
        if (cancelLayoutBtn != null) cancelLayoutBtn.clicked += () => layoutPanel.AddToClassList("hidden");
        var cancelModuleBtn = root.Q<Button>("btnCancelModule");
        if (cancelModuleBtn != null) cancelModuleBtn.clicked += () => modulePanel.AddToClassList("hidden");

        // Estado inicial
        overlay.AddToClassList("hidden");
        HideAllPanels();
        UpdateCrew(0);
        UpdateVolume();
        ShowBackground("spacebackground");
    }

    private void ShowBackground(string resourceKey)
    {
        var host = appRoot != null ? appRoot : content;
        if (host == null) return;

        var oldBg  = host.Q<Image>("__homeBg");
        if (oldBg != null) host.Remove(oldBg);
        var oldDim = host.Q<VisualElement>("__homeBgDim");
        if (oldDim != null) host.Remove(oldDim);

        var tex = Resources.Load<Texture2D>(resourceKey);
        if (tex == null)
        {
            Debug.LogWarning($"❌ No se encontró Assets/Resources/{resourceKey}.(png/jpg)");
            return;
        }

        float topOffset = topbar != null ? topbar.resolvedStyle.height : 60f;
        if (topOffset <= 0) topOffset = 60f;

        var bg = new Image { name = "__homeBg", image = tex, scaleMode = ScaleMode.ScaleAndCrop, pickingMode = PickingMode.Ignore };
        bg.style.position = Position.Absolute; bg.style.left = 0; bg.style.right = 0; bg.style.top = topOffset; bg.style.bottom = 0;

        var dim = new VisualElement { name = "__homeBgDim", pickingMode = PickingMode.Ignore };
        dim.style.position = Position.Absolute; dim.style.left = 0; dim.style.right = 0; dim.style.top = topOffset; dim.style.bottom = 0;
        dim.style.backgroundColor = new Color(0f, 0f, 0f, 0.20f);

        host.Insert(0, bg);
        host.Insert(1, dim);
    }

    private void OnMissionCreated()
    {
        overlay.AddToClassList("hidden");
        editorBar?.RemoveFromClassList("hidden");

        string selected = missionTypeField?.value ?? "Spatial";

        if (selected.Contains("Planetary Surface"))
            ShowBackground("");
        else
            ShowBackground("");

        Debug.Log($"Mission created. Type: {selected}, Duration: {missionDurationField?.value ?? "(n/a)"}");
    }

    private void UpdateCrew(int delta)
    {
        crewCount = Mathf.Clamp(crewCount + delta, 0, 16);
        if (crewLabel != null) crewLabel.text = $"Crew ({crewCount})";

        if (crewIcons == null) return;
        crewIcons.Clear();
        for (int i = 0; i < crewCount; i++)
        {
            var icon = new Label("👤");
            icon.AddToClassList("avatar");
            crewIcons.Add(icon);
        }
    }

    private void UpdateVolume()
    {
        int h = Mathf.Max(0, heightField?.value ?? 0);
        int w = Mathf.Max(0, widthField ?.value ?? 0);
        int d = Mathf.Max(0, depthField ?.value ?? 0);
        long vol = (long)w * h * d;
        if (volumeLabel != null) volumeLabel.text = $"Volume: {vol} m³";
    }

    private void ShowPanel(VisualElement panel)
    {
        HideAllPanels();
        panel?.RemoveFromClassList("hidden");
    }

    private void HideAllPanels()
    {
        layoutPanel?.AddToClassList("hidden");
        modulePanel?.AddToClassList("hidden");
    }

    private void WireLayoutZoneTiles(VisualElement grid, HashSet<VisualElement> set)
    {
        if (grid == null) return;
        foreach (var tile in grid.Children())
        {
            var t = tile;
            t.pickingMode = PickingMode.Position;
            t.focusable = true;

            t.RegisterCallback<ClickEvent>(_ => ToggleLayoutTile(t, set));
            t.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.Space)
                {
                    ToggleLayoutTile(t, set);
                    e.StopPropagation();
                }
            });
        }
    }

    private void ToggleLayoutTile(VisualElement tile, HashSet<VisualElement> set)
    {
        if (set.Contains(tile))
        {
            tile.RemoveFromClassList("selected");
            set.Remove(tile);
        }
        else
        {
            tile.AddToClassList("selected");
            set.Add(tile);
        }
    }

    private void WireModuleTiles(VisualElement grid, bool isZone)
    {
        if (grid == null) return;
        foreach (var tile in grid.Children())
        {
            tile.pickingMode = PickingMode.Position;
            tile.focusable = true;
            tile.RegisterCallback<ClickEvent>(_ => OnModuleTileClicked(tile, isZone));
        }
    }

    private void OnModuleTileClicked(VisualElement tile, bool isZone)
    {
        if (tile == null) return;

        if (isZone)
        {
            if (selectedZone == tile)
            {
                selectedZone.RemoveFromClassList("selected");
                selectedZone = null;
                return;
            }
            selectedZone?.RemoveFromClassList("selected");
            selectedZone = tile;
            selectedZone.AddToClassList("selected");
        }
        else
        {
            if (selectedObjects.Contains(tile))
            {
                tile.RemoveFromClassList("selected");
                selectedObjects.Remove(tile);
            }
            else
            {
                tile.AddToClassList("selected");
                selectedObjects.Add(tile);
            }
        }
    }
}
