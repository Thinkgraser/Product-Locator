using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

[BepInPlugin(ProductLocatorApi.PluginGuid, "Product Locator", "1.1.0")]
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

    private sealed class MarkerVisibilityState
    {
        public bool HasClearLineOfSight;
        public bool Initialized;
        public int ClearSamples;
        public int OccludedSamples;
    }

    private sealed class MarkerView
    {
        public ShelfIconTarget Target;
        public bool HasRenderDiagnostic;
        public string LastRenderDiagnostic;
        public RectTransform Root;
        public RawImage Background;
        public RawImage BorderTop;
        public RawImage BorderBottom;
        public RawImage BorderLeft;
        public RawImage BorderRight;
        public RawImage Icon;
        public bool HasAppliedLayout;
        public float AppliedIconSize;
        public float AppliedBorderSize;
        public Vector2 AppliedIconContentSize;
        public bool HasAppliedColors;
        public Color AppliedBackgroundColor;
        public Color AppliedBorderColor;
        public bool HasAppliedIcon;
        public Texture2D AppliedTexture;
        public Rect AppliedUv;
        public string AppliedDistanceText;
        public bool AppliedDistanceVisible;
        public int AppliedDistanceMeters;
        public bool HasAppliedDistanceValue;
        public Text DistanceLabel;
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
    private readonly HashSet<ProductType> _apiDisplayedTargets = new HashSet<ProductType>();
    private readonly List<ProductType> _newTargetRefreshes = new List<ProductType>();
    private readonly Dictionary<ProductType, int?> _activeTargetLimits = new Dictionary<ProductType, int?>();
    private readonly Dictionary<ProductType, int?> _desiredTargetLimits = new Dictionary<ProductType, int?>();
    private readonly Dictionary<ProductType, int?> _apiRequestedLimits = new Dictionary<ProductType, int?>();
    private readonly Dictionary<ProductType, Color?> _activeTargetColors = new Dictionary<ProductType, Color?>();
    private readonly Dictionary<ProductType, Color?> _desiredTargetColors = new Dictionary<ProductType, Color?>();
    private readonly Dictionary<ProductType, Color?> _apiRequestedColors = new Dictionary<ProductType, Color?>();
    private readonly Dictionary<ProductType, Color?> _activeTargetVisibleColors = new Dictionary<ProductType, Color?>();
    private readonly Dictionary<ProductType, Color?> _desiredTargetVisibleColors = new Dictionary<ProductType, Color?>();
    private readonly Dictionary<ProductType, Color?> _apiRequestedVisibleColors = new Dictionary<ProductType, Color?>();
    private readonly Dictionary<ProductType, Color?> _activeTargetVisibleBackgroundColors = new Dictionary<ProductType, Color?>();
    private readonly Dictionary<ProductType, Color?> _desiredTargetVisibleBackgroundColors = new Dictionary<ProductType, Color?>();
    private readonly Dictionary<ProductType, Color?> _apiRequestedVisibleBackgroundColors = new Dictionary<ProductType, Color?>();
    private readonly Dictionary<ProductType, float?> _activeTargetBackgroundOpacities = new Dictionary<ProductType, float?>();
    private readonly Dictionary<ProductType, float?> _desiredTargetBackgroundOpacities = new Dictionary<ProductType, float?>();
    private readonly Dictionary<ProductType, float?> _apiRequestedBackgroundOpacities = new Dictionary<ProductType, float?>();
    private readonly Dictionary<ProductType, Color?> _activeTargetBackgroundColors = new Dictionary<ProductType, Color?>();
    private readonly Dictionary<ProductType, Color?> _desiredTargetBackgroundColors = new Dictionary<ProductType, Color?>();
    private readonly Dictionary<ProductType, Color?> _apiRequestedBackgroundColors = new Dictionary<ProductType, Color?>();
    private readonly Dictionary<ProductType, bool> _activeTargetIncludeEmptyShelves = new Dictionary<ProductType, bool>();
    private readonly Dictionary<ProductType, bool> _desiredTargetIncludeEmptyShelves = new Dictionary<ProductType, bool>();
    private readonly Dictionary<ProductType, bool?> _apiRequestedIncludeEmptyShelves = new Dictionary<ProductType, bool?>();
    private readonly Dictionary<ProductType, bool?> _activeTargetShowDistance = new Dictionary<ProductType, bool?>();
    private readonly Dictionary<ProductType, bool?> _desiredTargetShowDistance = new Dictionary<ProductType, bool?>();
    private readonly Dictionary<ProductType, bool?> _apiRequestedShowDistance = new Dictionary<ProductType, bool?>();
    private readonly List<ShelfIconTarget> _shelfTargets = new List<ShelfIconTarget>();
    private readonly Dictionary<Shelf, Dictionary<ProductType, MarkerVisibilityState>> _markerVisibilityByShelf =
        new Dictionary<Shelf, Dictionary<ProductType, MarkerVisibilityState>>();
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
    private ConfigEntry<Color> _visibleMarkerColor;
    private ConfigEntry<Color> _visibleBackgroundColor;
    private ConfigEntry<int> _iconSize;
    private ConfigEntry<bool> _showMarkerDistance;
    private ConfigEntry<Color> _backgroundColor;
    private ConfigEntry<bool> _includeEmptyLabeledShelves;
    private ConfigEntry<float> _uiScale;
    private ConfigEntry<bool> _debugLogging;

    private readonly List<ProductType> _manualSelections = new List<ProductType>();
    private readonly List<ProductType> _appliedManualSelections = new List<ProductType>();
    private long _apiSnapshotRequestVersion = -1;
    private bool _hasAppliedRequestState;
    private bool _appliedIncludeEmptyShelves;
    private readonly Dictionary<string, bool> _apiSourceExpanded =
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    private bool _manualProductsExpanded = true;
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
    private Vector3 _shelfRankingOrigin;
    private bool _hasShelfRankingOrigin;
    private float _nextShelfCacheRetry;
    private int _pendingShelfCacheRetries;
    private Coroutine _shelfRefreshRoutine;
    private Coroutine _shelfIndexBuildRoutine;
    private Coroutine _shelfWatchRoutine;
    private Coroutine _shelfDiscoveryRoutine;
    private bool _shelfIndexReady;
    private long _apiProductRequestVersion = -1;
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

    private void Awake()
    {
        _toggleKey = Config.Bind("GUI", "Toggle GUI", KeyCode.F6,
            "Key used to open or close the product locator.");
        _markerColor = Config.Bind("Highlight", "Icon Border Color", new Color(1f, 0.72f, 0.12f, 1f), "Border color for product icons shown over matching shelves.");
        _visibleMarkerColor = Config.Bind("Highlight", "Visible Icon Border Color", Color.cyan,
            "Border color for product icons when the matching shelf is in clear view.");
        _visibleBackgroundColor = Config.Bind("Highlight", "Visible Icon Background Color",
            new Color(0.015f, 0.11f, 0.14f, 0.92f),
            "Background color for product icons when the matching shelf is in clear view and within three times interaction range.");
        _iconSize = Config.Bind("Highlight", "Icon Size", 100,
            new ConfigDescription("Displayed product icon card size in pixels.",
                new AcceptableValueRange<int>(50, 150)));
        _showMarkerDistance = Config.Bind("Highlight", "Show Marker Distance", true,
            "Show the distance from the player on each product marker. API requests can override this per product.");
        _backgroundColor = Config.Bind("Highlight", "Icon Background Color",
            new Color(0.035f, 0.045f, 0.065f, 0.92f),
            "Background color and alpha behind product icons.");
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
        if (DebugLoggingEnabled)
            Logger.LogInfo("[Product Locator Debug] " + message);
    }

    private bool DebugLoggingEnabled
    {
        get { return _debugLogging != null && _debugLogging.Value; }
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

        long requestVersion = ProductLocatorApi.GetRequestVersion();
        bool includeEmptyShelves = _includeEmptyLabeledShelves.Value;
        bool apiRequestsChanged = requestVersion != _apiSnapshotRequestVersion;
        bool manualSelectionsChanged = !AreTargetListsEqual(_manualSelections, _appliedManualSelections);
        if (apiRequestsChanged || manualSelectionsChanged || !_hasAppliedRequestState ||
            _appliedIncludeEmptyShelves != includeEmptyShelves)
        {
            if (apiRequestsChanged)
            {
                _apiSnapshotRequestVersion = ProductLocatorApi.GetRequestedProducts(_apiRequestedTargets,
                    _apiRequestedLimits, _apiRequestedColors, _apiRequestedIncludeEmptyShelves,
                    _apiRequestedBackgroundOpacities, _apiRequestedVisibleColors,
                    _apiRequestedVisibleBackgroundColors, _apiRequestedShowDistance,
                    _apiRequestedBackgroundColors);
                UpdateApiDisplayedTargets(_apiSnapshotRequestVersion);
            }

            UpdateDesiredHighlightRequests(includeEmptyShelves);
            _appliedManualSelections.Clear();
            _appliedManualSelections.AddRange(_manualSelections);
            _appliedIncludeEmptyShelves = includeEmptyShelves;
            _hasAppliedRequestState = true;
        }

        if (_activeTarget.HasValue && _shelfRefreshRoutine == null && Time.unscaledTime >= _nextRefresh)
            RefreshShelfTargets();
    }

    private void UpdateDesiredHighlightRequests(bool includeEmptyShelves)
    {
        ProductType? desired = null;
        _desiredTargets.Clear();
        _desiredTargetLimits.Clear();
        _desiredTargetColors.Clear();
        _desiredTargetVisibleColors.Clear();
        _desiredTargetVisibleBackgroundColors.Clear();
        _desiredTargetBackgroundOpacities.Clear();
        _desiredTargetBackgroundColors.Clear();
        _desiredTargetIncludeEmptyShelves.Clear();
        _desiredTargetShowDistance.Clear();
        if (_manualSelections.Count > 0)
        {
            _desiredTargets.AddRange(_manualSelections);
            for (int i = 0; i < _manualSelections.Count; i++)
            {
                _desiredTargetLimits[_manualSelections[i]] = null;
                _desiredTargetColors[_manualSelections[i]] = null;
                _desiredTargetVisibleColors[_manualSelections[i]] = null;
                _desiredTargetVisibleBackgroundColors[_manualSelections[i]] = null;
                _desiredTargetBackgroundOpacities[_manualSelections[i]] = null;
                _desiredTargetBackgroundColors[_manualSelections[i]] = null;
                _desiredTargetIncludeEmptyShelves[_manualSelections[i]] = includeEmptyShelves;
                _desiredTargetShowDistance[_manualSelections[i]] = null;
            }
        }
        for (int i = 0; i < _apiRequestedTargets.Count; i++)
        {
            ProductType productType = _apiRequestedTargets[i];
            if (!_apiDisplayedTargets.Contains(productType))
                continue;
            if (_desiredTargets.Contains(productType))
                continue;
            _desiredTargets.Add(productType);
            int? limit;
            _desiredTargetLimits[productType] = _apiRequestedLimits.TryGetValue(productType, out limit) ? limit : (int?)null;
            Color? markerColor;
            _desiredTargetColors[productType] = _apiRequestedColors.TryGetValue(productType, out markerColor) ? markerColor : (Color?)null;
            Color? visibleMarkerColor;
            _desiredTargetVisibleColors[productType] = _apiRequestedVisibleColors.TryGetValue(productType, out visibleMarkerColor)
                ? visibleMarkerColor : (Color?)null;
            Color? visibleBackgroundColor;
            _desiredTargetVisibleBackgroundColors[productType] =
                _apiRequestedVisibleBackgroundColors.TryGetValue(productType, out visibleBackgroundColor)
                    ? visibleBackgroundColor : (Color?)null;
            float? backgroundOpacity;
            _desiredTargetBackgroundOpacities[productType] = _apiRequestedBackgroundOpacities.TryGetValue(productType, out backgroundOpacity)
                ? backgroundOpacity : (float?)null;
            Color? backgroundColor;
            _desiredTargetBackgroundColors[productType] =
                _apiRequestedBackgroundColors.TryGetValue(productType, out backgroundColor) ? backgroundColor : (Color?)null;
            bool? includeEmpty;
            _desiredTargetIncludeEmptyShelves[productType] =
                _apiRequestedIncludeEmptyShelves.TryGetValue(productType, out includeEmpty) && includeEmpty.HasValue
                    ? includeEmpty.Value : includeEmptyShelves;
            bool? showDistance;
            _desiredTargetShowDistance[productType] =
                _apiRequestedShowDistance.TryGetValue(productType, out showDistance) ? showDistance : (bool?)null;
        }
        if (_desiredTargets.Count > 0)
            desired = _desiredTargets[0];

        bool targetListChanged = !AreTargetListsEqual(_activeTargets, _desiredTargets) ||
            !AreTargetLimitsEqual(_activeTargetLimits, _desiredTargetLimits, _desiredTargets) ||
            !AreTargetColorsEqual(_activeTargetColors, _desiredTargetColors, _desiredTargets) ||
            !AreTargetColorsEqual(_activeTargetVisibleColors, _desiredTargetVisibleColors, _desiredTargets) ||
            !AreTargetColorsEqual(_activeTargetVisibleBackgroundColors,
                _desiredTargetVisibleBackgroundColors, _desiredTargets) ||
            !AreTargetBackgroundOpacitiesEqual(_activeTargetBackgroundOpacities,
                _desiredTargetBackgroundOpacities, _desiredTargets) ||
            !AreTargetColorsEqual(_activeTargetBackgroundColors, _desiredTargetBackgroundColors, _desiredTargets) ||
            !AreTargetEmptyShelfSettingsEqual(_activeTargetIncludeEmptyShelves,
                _desiredTargetIncludeEmptyShelves, _desiredTargets) ||
            !AreNullableBoolSettingsEqual(_activeTargetShowDistance, _desiredTargetShowDistance, _desiredTargets);
        if (desired != _activeTarget || targetListChanged)
        {
            if (DebugLoggingEnabled)
            {
                LogDebug("Locator targets changed to: " + string.Join(", ", _desiredTargets));
                ProductLocatorApi.GetRequestSources(_apiRequestSources);
                LogDebug("API request sources: " + string.Join(", ", _apiRequestSources));
            }
            bool interruptedRefresh = _shelfRefreshRoutine != null;
            _newTargetRefreshes.Clear();
            for (int i = 0; i < _desiredTargets.Count; i++)
            {
                int? activeLimit;
                int? desiredLimit = _desiredTargetLimits[_desiredTargets[i]];
                Color? activeColor;
                Color? desiredColor = _desiredTargetColors[_desiredTargets[i]];
                Color? activeVisibleColor;
                Color? desiredVisibleColor = _desiredTargetVisibleColors[_desiredTargets[i]];
                Color? activeVisibleBackgroundColor;
                Color? desiredVisibleBackgroundColor = _desiredTargetVisibleBackgroundColors[_desiredTargets[i]];
                float? activeBackgroundOpacity;
                float? desiredBackgroundOpacity = _desiredTargetBackgroundOpacities[_desiredTargets[i]];
                Color? activeBackgroundColor;
                Color? desiredBackgroundColor = _desiredTargetBackgroundColors[_desiredTargets[i]];
                bool activeIncludesEmptyShelves;
                bool desiredIncludesEmptyShelves = _desiredTargetIncludeEmptyShelves[_desiredTargets[i]];
                bool? activeShowDistance;
                bool? desiredShowDistance = _desiredTargetShowDistance[_desiredTargets[i]];
                if (!_activeTargets.Contains(_desiredTargets[i]) ||
                    !_activeTargetLimits.TryGetValue(_desiredTargets[i], out activeLimit) || activeLimit != desiredLimit ||
                    !_activeTargetColors.TryGetValue(_desiredTargets[i], out activeColor) || activeColor != desiredColor ||
                    !_activeTargetVisibleColors.TryGetValue(_desiredTargets[i], out activeVisibleColor) ||
                    activeVisibleColor != desiredVisibleColor ||
                    !_activeTargetVisibleBackgroundColors.TryGetValue(_desiredTargets[i], out activeVisibleBackgroundColor) ||
                    activeVisibleBackgroundColor != desiredVisibleBackgroundColor ||
                    !_activeTargetBackgroundOpacities.TryGetValue(_desiredTargets[i], out activeBackgroundOpacity) ||
                    activeBackgroundOpacity != desiredBackgroundOpacity ||
                    !_activeTargetBackgroundColors.TryGetValue(_desiredTargets[i], out activeBackgroundColor) ||
                    activeBackgroundColor != desiredBackgroundColor ||
                    !_activeTargetIncludeEmptyShelves.TryGetValue(_desiredTargets[i], out activeIncludesEmptyShelves) ||
                    activeIncludesEmptyShelves != desiredIncludesEmptyShelves ||
                    !_activeTargetShowDistance.TryGetValue(_desiredTargets[i], out activeShowDistance) ||
                    activeShowDistance != desiredShowDistance)
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
            _activeTargetVisibleColors.Clear();
            foreach (KeyValuePair<ProductType, Color?> item in _desiredTargetVisibleColors)
                _activeTargetVisibleColors[item.Key] = item.Value;
            _activeTargetVisibleBackgroundColors.Clear();
            foreach (KeyValuePair<ProductType, Color?> item in _desiredTargetVisibleBackgroundColors)
                _activeTargetVisibleBackgroundColors[item.Key] = item.Value;
            _activeTargetBackgroundOpacities.Clear();
            foreach (KeyValuePair<ProductType, float?> item in _desiredTargetBackgroundOpacities)
                _activeTargetBackgroundOpacities[item.Key] = item.Value;
            _activeTargetBackgroundColors.Clear();
            foreach (KeyValuePair<ProductType, Color?> item in _desiredTargetBackgroundColors)
                _activeTargetBackgroundColors[item.Key] = item.Value;
            _activeTargetIncludeEmptyShelves.Clear();
            foreach (KeyValuePair<ProductType, bool> item in _desiredTargetIncludeEmptyShelves)
                _activeTargetIncludeEmptyShelves[item.Key] = item.Value;
            _activeTargetShowDistance.Clear();
            foreach (KeyValuePair<ProductType, bool?> item in _desiredTargetShowDistance)
                _activeTargetShowDistance[item.Key] = item.Value;

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
        _markerVisibilityByShelf.Clear();
        if (_markerCanvas != null)
            Destroy(_markerCanvas.gameObject);
        _markerViews.Clear();
        _activeTargets.Clear();
        _shelfBounds.Clear();
    }

}
