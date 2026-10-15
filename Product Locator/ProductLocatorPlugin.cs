using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

[BepInPlugin(ProductLocatorApi.PluginGuid, "Product Locator", "1.0.0")]
[BepInProcess("Megastore Simulator.exe")]
public sealed partial class ProductLocatorPlugin : BaseUnityPlugin
{
    private sealed class ShelfIconTarget
    {
        public Shelf Shelf;
        public Bounds Bounds;
        public Texture2D Texture;
        public Rect Uv;
        public int StackIndex;
        public ProductType ProductType;
        public Color? MarkerColor;
        public bool IsEmptyShelf;
    }

    private sealed class MarkerView
    {
        public ShelfIconTarget Target;
        public RectTransform Root;
        public RawImage Background;
        public RawImage BorderTop;
        public RawImage BorderBottom;
        public RawImage BorderLeft;
        public RawImage BorderRight;
        public RawImage Icon;
    }

    private sealed class ShelfSnapshot
    {
        public readonly HashSet<ProductType> StockedProducts = new HashSet<ProductType>();
        public ProductType LabeledProduct;
        public ProductType EmptyLabelProduct;
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Scale;
    }

    private readonly List<ProductType> _productTypes = new List<ProductType>();
    private readonly List<ProductType> _activeTargets = new List<ProductType>();
    private readonly List<ProductType> _desiredTargets = new List<ProductType>();
    private readonly List<ProductType> _apiRequestedTargets = new List<ProductType>();
    private readonly List<string> _apiRequestSources = new List<string>();
    private readonly List<ProductType> _apiRequestProducts = new List<ProductType>();
    private readonly List<ProductType> _newTargetRefreshes = new List<ProductType>();
    private readonly Dictionary<ProductType, int?> _activeTargetLimits = new Dictionary<ProductType, int?>();
    private readonly Dictionary<ProductType, int?> _desiredTargetLimits = new Dictionary<ProductType, int?>();
    private readonly Dictionary<ProductType, int?> _apiRequestedLimits = new Dictionary<ProductType, int?>();
    private readonly Dictionary<ProductType, Color?> _activeTargetColors = new Dictionary<ProductType, Color?>();
    private readonly Dictionary<ProductType, Color?> _desiredTargetColors = new Dictionary<ProductType, Color?>();
    private readonly Dictionary<ProductType, Color?> _apiRequestedColors = new Dictionary<ProductType, Color?>();
    private readonly Dictionary<ProductType, bool> _activeTargetIncludeEmptyShelves = new Dictionary<ProductType, bool>();
    private readonly Dictionary<ProductType, bool> _desiredTargetIncludeEmptyShelves = new Dictionary<ProductType, bool>();
    private readonly Dictionary<ProductType, bool?> _apiRequestedIncludeEmptyShelves = new Dictionary<ProductType, bool?>();
    private readonly List<ShelfIconTarget> _shelfTargets = new List<ShelfIconTarget>();
    private readonly Dictionary<ProductType, List<ShelfIconTarget>> _shelfTargetIndex =
        new Dictionary<ProductType, List<ShelfIconTarget>>();
    private readonly Dictionary<Shelf, ShelfSnapshot> _shelfSnapshots =
        new Dictionary<Shelf, ShelfSnapshot>();
    private readonly HashSet<int> _loggedShelfInstanceIds = new HashSet<int>();
    private readonly ShelfSnapshot _shelfWatchScratchSnapshot = new ShelfSnapshot();
    private readonly Dictionary<ProductType, ProductData> _productData = new Dictionary<ProductType, ProductData>();
    private readonly Dictionary<ProductType, string> _productNames = new Dictionary<ProductType, string>();
    private readonly Dictionary<Shelf, Bounds> _shelfBounds = new Dictionary<Shelf, Bounds>();
    private ProductGroup[] _departmentGroups = new ProductGroup[0];
    private string[] _departmentLabels = new string[0];
    private Shelf[] _shelves = new Shelf[0];
    private GUIStyle _titleStyle;
    private GUIStyle _subtitleStyle;
    private GUIStyle _sectionStyle;
    private GUIStyle _helpStyle;
    private GUIStyle _panelStyle;
    private GUIStyle _tileStyle;
    private GUIStyle _tileNameStyle;
    private GUIStyle _selectedNameStyle;
    private GUIStyle _brandStyle;
    private GUIStyle _departmentTextStyle;
    private GUIStyle _selectedButtonStyle;
    private GUIStyle _buttonStyle;
    private GUIStyle _productButtonNoHoverStyle;
    private GUIStyle _selectedButtonNoHoverStyle;
    private GUIStyle _searchBarStyle;
    private GUIStyle _searchFieldStyle;
    private GUIStyle _centerSectionStyle;
    private GUIStyle _clearButtonStyle;
    private GUIStyle _dropdownButtonStyle;
    private GUIStyle _dropdownMenuStyle;
    private GUIStyle _dropdownOptionStyle;
    private GUIStyle _dropdownOptionHoverStyle;

    private ConfigEntry<KeyCode> _toggleKey;
    private ConfigEntry<Color> _markerColor;
    private ConfigEntry<int> _iconSize;
    private ConfigEntry<float> _backgroundOpacity;
    private ConfigEntry<bool> _includeEmptyLabeledShelves;
    private ConfigEntry<float> _uiScale;
    private ConfigEntry<bool> _debugLogging;

    private readonly List<ProductType> _manualSelections = new List<ProductType>();
    private ProductType? _activeTarget;
    private string _search = string.Empty;
    private Vector2 _selectedScroll;
    private Vector2 _tileScroll;
    private Vector2 _departmentMenuScroll;
    private int _departmentFilterIndex;
    private bool _departmentDropdownOpen;
    private bool _departmentMenuPointerCaptured;
    private bool _cursorOverDepartmentDropdown;
    private Rect _departmentButtonRect;
    private Rect _window = new Rect(30f, 80f, 320f, 450f);
    private bool _windowOpen;
    private float _nextRefresh;
    private float _nextShelfCacheRetry;
    private int _pendingShelfCacheRetries;
    private Coroutine _shelfRefreshRoutine;
    private Coroutine _shelfIndexBuildRoutine;
    private Coroutine _shelfWatchRoutine;
    private Coroutine _shelfDiscoveryRoutine;
    private bool _shelfIndexReady;
    private Canvas _markerCanvas;
    private readonly List<MarkerView> _markerViews = new List<MarkerView>();
    private GraphicRaycaster[] _computerRaycasters = new GraphicRaycaster[0];
    private FieldInfo _computerRaycasterField;
    private FieldInfo _labeledShelfRegistryField;
    private InputManager _blockedInputManager;
    private PlayerMove _blockedPlayerMove;
    private PlayerLook _blockedPlayerLook;
    private CursorLockMode _previousCursorLock;
    private bool _previousCursorVisible;
    private bool _previousMovementLocked;
    private bool _previousRotationLocked;
    private float _styledUiScale = -1f;

    private void Awake()
    {
        _toggleKey = Config.Bind("GUI", "Toggle GUI", KeyCode.F6,
            "Key used to open or close the product locator.");
        _markerColor = Config.Bind("Highlight", "Icon Border Color", new Color(1f, 0.72f, 0.12f, 1f), "Border color for product icons shown over matching shelves.");
        _iconSize = Config.Bind("Highlight", "Icon Size", 100,
            new ConfigDescription("Displayed product icon card size in pixels.",
                new AcceptableValueRange<int>(50, 150)));
        _backgroundOpacity = Config.Bind("Highlight", "Background Opacity", 0.92f, "Opacity of the dark background behind each product icon.");
        _includeEmptyLabeledShelves = Config.Bind("Highlight", "Include Empty Labeled Shelves", true,
            "Show product markers on empty shelves that still have a product label assigned.");
        _uiScale = Config.Bind("Interface", "UI Scale", 1f,
            new ConfigDescription("Scale of the fullscreen product picker.", new AcceptableValueRange<float>(0.5f, 2f)));
        _debugLogging = Config.Bind("Debug", "Enable Debug Logs", false,
            "Write detailed Product Locator diagnostics to the BepInEx log.");

        CreateMarkerCanvas();

        CacheShelves();
        _shelfWatchRoutine = StartCoroutine(WatchShelvesForChanges());
        _shelfDiscoveryRoutine = StartCoroutine(DiscoverShelvesFromRegistry());
        CacheComputerUiRaycasters();
        CacheStoreProductTypes();
        CacheProductData();
        _pendingShelfCacheRetries = 3;
        _nextShelfCacheRetry = Time.unscaledTime + 1f;
        SceneManager.sceneLoaded += OnSceneLoaded;
        ProductLocatorApi.RegisterGameServices(FindClosestAvailableProduct,
            HasAvailableShelfStock, RefreshHighlightedProduct);

        Logger.LogInfo("Product Locator loaded. Press " + _toggleKey.Value + " to choose a product.");
    }

    private void LogDebug(string message)
    {
        if (_debugLogging != null && _debugLogging.Value)
            Logger.LogInfo("[Product Locator Debug] " + message);
    }

    private void Update()
    {
        if (_windowOpen && Input.GetKeyDown(KeyCode.Escape))
            SetWindowOpen(false);
        else if (Input.GetKeyDown(_toggleKey.Value))
            SetWindowOpen(!_windowOpen);

        // Some store scenes create shelves shortly after sceneLoaded. Retry the
        // cache briefly so the locator does not get stuck with an empty/partial list.
        if (_pendingShelfCacheRetries > 0 && Time.unscaledTime >= _nextShelfCacheRetry)
        {
            bool shelvesChanged = CacheShelves();
            CacheComputerUiRaycasters();
            CacheStoreProductTypes();
            CacheProductData();
            _pendingShelfCacheRetries--;
            _nextShelfCacheRetry = Time.unscaledTime + 1.5f;
            if (shelvesChanged && _activeTarget.HasValue)
                RefreshShelfTargets();
        }

        ProductType? desired = null;
        _desiredTargets.Clear();
        _desiredTargetLimits.Clear();
        _desiredTargetColors.Clear();
        _desiredTargetIncludeEmptyShelves.Clear();
        ProductLocatorApi.GetRequestedProducts(_apiRequestedTargets, _apiRequestedLimits,
            _apiRequestedColors, _apiRequestedIncludeEmptyShelves);
        if (_manualSelections.Count > 0)
        {
            _desiredTargets.AddRange(_manualSelections);
            for (int i = 0; i < _manualSelections.Count; i++)
            {
                _desiredTargetLimits[_manualSelections[i]] = null;
                _desiredTargetColors[_manualSelections[i]] = null;
                _desiredTargetIncludeEmptyShelves[_manualSelections[i]] = _includeEmptyLabeledShelves.Value;
            }
        }
        for (int i = 0; i < _apiRequestedTargets.Count; i++)
        {
            ProductType productType = _apiRequestedTargets[i];
            if (_desiredTargets.Contains(productType))
                continue;
            _desiredTargets.Add(productType);
            int? limit;
            _desiredTargetLimits[productType] = _apiRequestedLimits.TryGetValue(productType, out limit) ? limit : (int?)null;
            Color? markerColor;
            _desiredTargetColors[productType] = _apiRequestedColors.TryGetValue(productType, out markerColor) ? markerColor : (Color?)null;
            bool? includeEmpty;
            _desiredTargetIncludeEmptyShelves[productType] =
                _apiRequestedIncludeEmptyShelves.TryGetValue(productType, out includeEmpty) && includeEmpty.HasValue
                    ? includeEmpty.Value : _includeEmptyLabeledShelves.Value;
        }
        if (_desiredTargets.Count > 0)
            desired = _desiredTargets[0];

        bool targetListChanged = !AreTargetListsEqual(_activeTargets, _desiredTargets) ||
            !AreTargetLimitsEqual(_activeTargetLimits, _desiredTargetLimits, _desiredTargets) ||
            !AreTargetColorsEqual(_activeTargetColors, _desiredTargetColors, _desiredTargets) ||
            !AreTargetEmptyShelfSettingsEqual(_activeTargetIncludeEmptyShelves,
                _desiredTargetIncludeEmptyShelves, _desiredTargets);
        if (desired != _activeTarget || targetListChanged)
        {
            LogDebug("Locator targets changed to: " + string.Join(", ", _desiredTargets));
            ProductLocatorApi.GetRequestSources(_apiRequestSources);
            LogDebug("API request sources: " + string.Join(", ", _apiRequestSources));
            bool interruptedRefresh = _shelfRefreshRoutine != null;
            _newTargetRefreshes.Clear();
            for (int i = 0; i < _desiredTargets.Count; i++)
            {
                int? activeLimit;
                int? desiredLimit = _desiredTargetLimits[_desiredTargets[i]];
                Color? activeColor;
                Color? desiredColor = _desiredTargetColors[_desiredTargets[i]];
                bool activeIncludesEmptyShelves;
                bool desiredIncludesEmptyShelves = _desiredTargetIncludeEmptyShelves[_desiredTargets[i]];
                if (!_activeTargets.Contains(_desiredTargets[i]) ||
                    !_activeTargetLimits.TryGetValue(_desiredTargets[i], out activeLimit) || activeLimit != desiredLimit ||
                    !_activeTargetColors.TryGetValue(_desiredTargets[i], out activeColor) || activeColor != desiredColor ||
                    !_activeTargetIncludeEmptyShelves.TryGetValue(_desiredTargets[i], out activeIncludesEmptyShelves) ||
                    activeIncludesEmptyShelves != desiredIncludesEmptyShelves)
                    _newTargetRefreshes.Add(_desiredTargets[i]);
            }

            if (_shelfRefreshRoutine != null)
                StopCoroutine(_shelfRefreshRoutine);
            _shelfRefreshRoutine = null;
            _activeTarget = desired;
            _activeTargets.Clear();
            _activeTargets.AddRange(_desiredTargets);
            _activeTargetLimits.Clear();
            foreach (KeyValuePair<ProductType, int?> item in _desiredTargetLimits)
                _activeTargetLimits[item.Key] = item.Value;
            _activeTargetColors.Clear();
            foreach (KeyValuePair<ProductType, Color?> item in _desiredTargetColors)
                _activeTargetColors[item.Key] = item.Value;
            _activeTargetIncludeEmptyShelves.Clear();
            foreach (KeyValuePair<ProductType, bool> item in _desiredTargetIncludeEmptyShelves)
                _activeTargetIncludeEmptyShelves[item.Key] = item.Value;

            for (int i = _shelfTargets.Count - 1; i >= 0; i--)
            {
                if (!_activeTargets.Contains(_shelfTargets[i].ProductType))
                    _shelfTargets.RemoveAt(i);
            }

            _nextRefresh = Time.unscaledTime + 1.5f;
            if (_activeTargets.Count == 0)
                _shelfTargets.Clear();
            else if (_newTargetRefreshes.Count > 0)
                RefreshShelfTargets(_activeTargets, true);
            else if (interruptedRefresh)
                RefreshShelfTargets();
        }
        else if (_activeTarget.HasValue && _shelfRefreshRoutine == null && Time.unscaledTime >= _nextRefresh)
        {
            RefreshShelfTargets();
        }
    }

    private void OnDestroy()
    {
        if (_windowOpen)
        {
            _windowOpen = false;
            RestoreGameplayInput();
        }
        SceneManager.sceneLoaded -= OnSceneLoaded;
        ProductLocatorApi.UnregisterGameServices();
        if (_shelfRefreshRoutine != null)
            StopCoroutine(_shelfRefreshRoutine);
        if (_shelfIndexBuildRoutine != null)
            StopCoroutine(_shelfIndexBuildRoutine);
        if (_shelfWatchRoutine != null)
            StopCoroutine(_shelfWatchRoutine);
        if (_shelfDiscoveryRoutine != null)
            StopCoroutine(_shelfDiscoveryRoutine);
        _shelfTargets.Clear();
        if (_markerCanvas != null)
            Destroy(_markerCanvas.gameObject);
        _markerViews.Clear();
        _activeTargets.Clear();
        _shelfBounds.Clear();
    }

}
