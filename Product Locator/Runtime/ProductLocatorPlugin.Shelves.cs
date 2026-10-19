using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

public sealed partial class ProductLocatorPlugin
{
    private static readonly Vector3 EscalatorTopLanding = new Vector3(134.638f, 9.263f, 233.134f);
    private static readonly Vector3 EscalatorBottomLanding = new Vector3(137.509f, 1.484f, 246.541f);
    // Cached-index application is synchronous on Unity's main thread. The
    // background coroutines keep separate scan state across their yields.
    private readonly HashSet<ProductType> _shelfSelectionTypes = new HashSet<ProductType>();
    private readonly List<ShelfIconTarget> _shelfSelectionTargets = new List<ShelfIconTarget>();
    private readonly Dictionary<ProductType, List<ShelfIconTarget>> _limitedShelfSelectionTargets =
        new Dictionary<ProductType, List<ShelfIconTarget>>();
    private readonly Dictionary<ProductType, List<float>> _limitedShelfSelectionDistances =
        new Dictionary<ProductType, List<float>>();
    private readonly Dictionary<Shelf, int> _shelfSelectionStacks = new Dictionary<Shelf, int>();
    private readonly HashSet<ProductType> _shelfIndexAddedProducts = new HashSet<ProductType>();
    private readonly List<Renderer> _shelfRendererScratch = new List<Renderer>();

    private void RefreshShelfTargets()
    {
        if (_activeTargets.Count == 0)
        {
            if (_shelfRefreshRoutine != null)
                StopCoroutine(_shelfRefreshRoutine);
            _shelfRefreshRoutine = null;
            _shelfTargets.Clear();
            return;
        }

        RefreshShelfTargets(_activeTargets);
    }

    private void RefreshShelfTargets(IList<ProductType> targetTypes, bool forceShelfScan = false)
    {
        if (targetTypes == null || targetTypes.Count == 0)
            return;

        if (DebugLoggingEnabled)
            LogDebug("Refreshing marker targets [" + string.Join(", ", targetTypes) +
                "]; indexReady=" + _shelfIndexReady + "; explicitShelfScan=" + forceShelfScan + ".");

        if (_shelfIndexReady && !forceShelfScan)
        {
            ApplyShelfIndex(targetTypes);
            _nextRefresh = Time.unscaledTime + 1.5f;
            return;
        }

        if (_shelfIndexReady)
            ApplyShelfIndex(targetTypes);

        if (_shelfRefreshRoutine != null)
            StopCoroutine(_shelfRefreshRoutine);
        Dictionary<ProductType, int?> limits = new Dictionary<ProductType, int?>();
        Dictionary<ProductType, Color?> colors = new Dictionary<ProductType, Color?>();
        Dictionary<ProductType, bool> includeEmptyShelves = new Dictionary<ProductType, bool>();
        for (int i = 0; i < targetTypes.Count; i++)
        {
            int? limit;
            if (_activeTargetLimits.TryGetValue(targetTypes[i], out limit))
                limits[targetTypes[i]] = limit;
            else
                limits[targetTypes[i]] = null;

            Color? markerColor;
            colors[targetTypes[i]] = _activeTargetColors.TryGetValue(targetTypes[i], out markerColor)
                ? markerColor : (Color?)null;
            bool includeEmpty;
            includeEmptyShelves[targetTypes[i]] = _activeTargetIncludeEmptyShelves.TryGetValue(targetTypes[i], out includeEmpty) && includeEmpty;
        }
        _shelfRefreshRoutine = StartCoroutine(RefreshShelfTargetsIncrementally(
            new List<ProductType>(targetTypes), limits, colors, includeEmptyShelves));
        _nextRefresh = Time.unscaledTime + 1.5f;
    }

    private IEnumerator RefreshShelfTargetsIncrementally(List<ProductType> targetTypes,
        Dictionary<ProductType, int?> limits, Dictionary<ProductType, Color?> colors,
        Dictionary<ProductType, bool> includeEmptyShelves)
    {
        float scanStartedAt = Time.unscaledTime;
        // Defer execution so StartCoroutine returns before this routine can finish
        // and its handle can be stored in _shelfRefreshRoutine.
        yield return null;

        List<ShelfIconTarget> refreshedTargets = new List<ShelfIconTarget>();
        Dictionary<ProductType, List<ShelfIconTarget>> refreshedIndex = new Dictionary<ProductType, List<ShelfIconTarget>>();
        for (int i = 0; i < targetTypes.Count; i++)
            refreshedIndex[targetTypes[i]] = new List<ShelfIconTarget>();
        HashSet<ProductType> addedTypes = new HashSet<ProductType>();
        const int shelvesPerFrame = 8;
        HashSet<ProductType> targetSet = new HashSet<ProductType>(targetTypes);
        Dictionary<ProductType, List<ShelfIconTarget>> limitedTargets = new Dictionary<ProductType, List<ShelfIconTarget>>();
        Dictionary<ProductType, List<float>> limitedDistances = new Dictionary<ProductType, List<float>>();
        HashSet<int> scannedShelfInstanceIds = new HashSet<int>();
        Vector3 closestOrigin = GetShelfRankingOrigin();

        for (int i = 0; i < _shelves.Length; i++)
        {
            Shelf shelf = _shelves[i];
            if (shelf == null || shelf.gameObject == null || !shelf.isActiveAndEnabled ||
                !shelf.gameObject.activeInHierarchy || !shelf.gameObject.scene.IsValid())
            {
                if (i % shelvesPerFrame == shelvesPerFrame - 1)
                    yield return null;
                continue;
            }

            if (!scannedShelfInstanceIds.Add(shelf.GetInstanceID()))
            {
                if (DebugLoggingEnabled)
                    LogDebug("Skipping duplicate shelf reference during marker scan: '" + shelf.name +
                        "' (ID " + shelf.ShelfID + ", instance " + shelf.GetInstanceID() + ").");
                continue;
            }

            List<Product> availableProducts = shelf.AvailableProducts();
            bool shelfIsEmpty = availableProducts == null || availableProducts.Count == 0;
            addedTypes.Clear();
            int iconsOnShelf = 0;
            Bounds bounds;
            bool hasBounds = _shelfBounds.TryGetValue(shelf, out bounds);
            if (availableProducts != null)
            {
                for (int productIndex = 0; productIndex < availableProducts.Count; productIndex++)
                {
                    Product product = availableProducts[productIndex];
                    if (product == null || product.Data == null || !targetSet.Contains(product.Data.type))
                        continue;

                    Sprite icon = product.Data.productSprite;
                    if (icon == null || icon.texture == null || !addedTypes.Add(product.Data.type))
                        continue;

                    if (!hasBounds)
                    {
                        bounds = GetShelfBounds(shelf);
                        _shelfBounds[shelf] = bounds;
                        hasBounds = true;
                    }

                    ShelfIconTarget target = CreateShelfIconTarget(shelf, bounds, icon, iconsOnShelf++, product.Data.type, colors);
                    AddToShelfIndex(refreshedIndex, CreateShelfIconTarget(shelf, bounds, icon, 0,
                        product.Data.type, null));
                    AddShelfIconTarget(target, limits, closestOrigin, refreshedTargets, limitedTargets, limitedDistances);
                }
            }

            if (shelfIsEmpty)
            {
                try
                {
                    if (shelf.HasProductLabel())
                    {
                        ProductType assignedType = shelf.ContainedProductType();
                        if (assignedType == ProductType.NONE)
                            assignedType = shelf.PreviousContainedProductType();

                        bool includeEmpty;
                        if (targetSet.Contains(assignedType) &&
                            includeEmptyShelves.TryGetValue(assignedType, out includeEmpty) && includeEmpty)
                        {
                            ProductData data = GetProductData(assignedType);
                            Sprite icon = data != null ? data.productSprite : null;
                            if (icon != null && icon.texture != null)
                            {
                                if (!hasBounds)
                                {
                                    bounds = GetShelfBounds(shelf);
                                    _shelfBounds[shelf] = bounds;
                                }
                                ShelfIconTarget target = CreateShelfIconTarget(shelf, bounds, icon,
                                    iconsOnShelf, assignedType, colors);
                                ShelfIconTarget indexedEmpty = CreateShelfIconTarget(shelf, bounds, icon, 0,
                                    assignedType, null);
                                indexedEmpty.IsEmptyShelf = true;
                                AddToShelfIndex(refreshedIndex, indexedEmpty);
                                AddShelfIconTarget(target, limits, closestOrigin, refreshedTargets,
                                    limitedTargets, limitedDistances);
                            }
                        }
                    }
                }
                catch
                {
                    // Skip shelf labels while the store scene is changing.
                }
            }

            if (i % shelvesPerFrame == shelvesPerFrame - 1)
            {
                yield return null;
            }
        }

        foreach (KeyValuePair<ProductType, List<ShelfIconTarget>> item in limitedTargets)
            refreshedTargets.AddRange(item.Value);

        if (DebugLoggingEnabled)
            LogDebug("Shelf scan selected marker(s): " + DescribeShelfTargets(refreshedTargets) + ".");

        foreach (KeyValuePair<ProductType, List<ShelfIconTarget>> item in refreshedIndex)
            _shelfTargetIndex[item.Key] = item.Value;

        // Replace only the requested product types, leaving other markers untouched.
        for (int i = _shelfTargets.Count - 1; i >= 0; i--)
        {
            if (targetSet.Contains(_shelfTargets[i].ProductType))
                _shelfTargets.RemoveAt(i);
        }
        _shelfTargets.AddRange(refreshedTargets);
        _shelfRefreshRoutine = null;
        _nextRefresh = Time.unscaledTime + 1.5f;
        if (DebugLoggingEnabled)
            LogDebug("Shelf refresh completed for [" + string.Join(", ", targetTypes) + "] with " +
                refreshedTargets.Count + " marker(s) in " + (Time.unscaledTime - scanStartedAt).ToString("0.00") + "s.");
    }

    private void ApplyShelfIndex(IList<ProductType> targetTypes)
    {
        HashSet<ProductType> targetSet = _shelfSelectionTypes;
        targetSet.Clear();
        for (int i = 0; i < targetTypes.Count; i++)
            targetSet.Add(targetTypes[i]);

        List<ShelfIconTarget> selected = _shelfSelectionTargets;
        selected.Clear();
        Dictionary<ProductType, List<ShelfIconTarget>> limited = _limitedShelfSelectionTargets;
        Dictionary<ProductType, List<float>> distances = _limitedShelfSelectionDistances;
        foreach (List<ShelfIconTarget> targets in limited.Values)
            targets.Clear();
        foreach (List<float> productDistances in distances.Values)
            productDistances.Clear();
        Dictionary<Shelf, int> shelfStacks = _shelfSelectionStacks;
        shelfStacks.Clear();
        Vector3 origin = GetShelfRankingOrigin();
        for (int i = 0; i < targetTypes.Count; i++)
        {
            List<ShelfIconTarget> indexed;
            if (!_shelfTargetIndex.TryGetValue(targetTypes[i], out indexed))
                continue;
            for (int j = 0; j < indexed.Count; j++)
            {
                ShelfIconTarget cached = indexed[j];
                if (cached.Shelf == null || !cached.Shelf.isActiveAndEnabled || !cached.Shelf.gameObject.activeInHierarchy)
                    continue;
                bool showEmpty;
                if (cached.IsEmptyShelf &&
                    (!_activeTargetIncludeEmptyShelves.TryGetValue(cached.ProductType, out showEmpty) || !showEmpty))
                    continue;
                int stackIndex;
                shelfStacks.TryGetValue(cached.Shelf, out stackIndex);
                shelfStacks[cached.Shelf] = stackIndex + 1;
                Color? markerColor;
                ShelfIconTarget target = new ShelfIconTarget
                {
                    Shelf = cached.Shelf,
                    Bounds = cached.Bounds,
                    Texture = cached.Texture,
                    Uv = cached.Uv,
                    StackIndex = stackIndex,
                    ProductType = cached.ProductType,
                    MarkerColor = _activeTargetColors.TryGetValue(cached.ProductType, out markerColor)
                        ? markerColor : (Color?)null,
                    IsEmptyShelf = cached.IsEmptyShelf
                };
                AddShelfIconTarget(target, _activeTargetLimits, origin, selected, limited, distances);
            }
        }

        for (int i = 0; i < targetTypes.Count; i++)
        {
            List<ShelfIconTarget> limitedProductTargets;
            if (limited.TryGetValue(targetTypes[i], out limitedProductTargets))
                selected.AddRange(limitedProductTargets);
        }
        if (DebugLoggingEnabled)
            LogDebug("Cached index selected marker(s): " + DescribeShelfTargets(selected) + ".");
        for (int i = _shelfTargets.Count - 1; i >= 0; i--)
        {
            if (targetSet.Contains(_shelfTargets[i].ProductType))
                _shelfTargets.RemoveAt(i);
        }
        _shelfTargets.AddRange(selected);
        // Release references after copying; keep the collection capacities for
        // the next refresh without retaining removed shelf objects.
        selected.Clear();
        shelfStacks.Clear();
        foreach (List<ShelfIconTarget> targets in limited.Values)
            targets.Clear();
    }

    private static string DescribeShelfTargets(IList<ShelfIconTarget> targets)
    {
        if (targets == null || targets.Count == 0)
            return "none";

        List<string> descriptions = new List<string>(targets.Count);
        for (int i = 0; i < targets.Count; i++)
        {
            ShelfIconTarget target = targets[i];
            if (target == null || target.Shelf == null)
                continue;
            descriptions.Add(target.ProductType + " on '" + target.Shelf.name + "' (ID " +
                target.Shelf.ShelfID + ", instance " + target.Shelf.GetInstanceID() + ")" +
                (target.IsEmptyShelf ? " [empty]" : string.Empty));
        }
        return descriptions.Count == 0 ? "none" : string.Join("; ", descriptions);
    }

    private ShelfIconTarget CreateShelfIconTarget(Shelf shelf, Bounds bounds, Sprite icon, int stackIndex,
        ProductType productType, Dictionary<ProductType, Color?> colors)
    {
        Color? markerColor;
        return new ShelfIconTarget
        {
            Shelf = shelf,
            Bounds = bounds,
            Texture = icon.texture,
            Uv = GetSpriteUv(icon),
            StackIndex = stackIndex,
            ProductType = productType,
            MarkerColor = colors != null && colors.TryGetValue(productType, out markerColor)
                ? markerColor : (Color?)null
        };
    }

    private static void AddShelfIconTarget(ShelfIconTarget target, Dictionary<ProductType, int?> limits,
        Vector3 closestOrigin, List<ShelfIconTarget> refreshedTargets,
        Dictionary<ProductType, List<ShelfIconTarget>> limitedTargets,
        Dictionary<ProductType, List<float>> limitedDistances)
    {
        int? limit;
        if (!limits.TryGetValue(target.ProductType, out limit) || !limit.HasValue)
        {
            refreshedTargets.Add(target);
            return;
        }

        float distance = (target.Bounds.center - closestOrigin).sqrMagnitude;
        List<ShelfIconTarget> productTargets;
        List<float> productDistances;
        if (!limitedTargets.TryGetValue(target.ProductType, out productTargets))
        {
            productTargets = new List<ShelfIconTarget>();
            productDistances = new List<float>();
            limitedTargets[target.ProductType] = productTargets;
            limitedDistances[target.ProductType] = productDistances;
        }
        else
            productDistances = limitedDistances[target.ProductType];

        int insertAt = 0;
        while (insertAt < productDistances.Count && productDistances[insertAt] <= distance)
            insertAt++;
        if (insertAt < limit.Value)
        {
            productTargets.Insert(insertAt, target);
            productDistances.Insert(insertAt, distance);
            if (productTargets.Count > limit.Value)
            {
                productTargets.RemoveAt(productTargets.Count - 1);
                productDistances.RemoveAt(productDistances.Count - 1);
            }
        }
    }

    private Vector3 GetShelfRankingOrigin()
    {
        Camera camera = Camera.main;
        Vector3 position = camera != null ? camera.transform.position : Vector3.zero;
        // Camera bob and small look/aim offsets should not make a limited shelf
        // selection oscillate between nearly equidistant shelves. Re-rank after
        // the player has moved a meaningful distance instead.
        const float movementThreshold = 0.75f;
        if (!_hasShelfRankingOrigin || (position - _shelfRankingOrigin).sqrMagnitude >=
            movementThreshold * movementThreshold)
        {
            _shelfRankingOrigin = position;
            _hasShelfRankingOrigin = true;
        }
        return _shelfRankingOrigin;
    }

    private bool CacheShelves()
    {
        Shelf[] foundShelves = FindObjectsByType<Shelf>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        // Keep the scene scan canonical in case the same shelf is exposed twice.
        HashSet<int> uniqueShelfInstanceIds = new HashSet<int>();
        List<Shelf> uniqueResults = new List<Shelf>(foundShelves.Length);
        for (int i = 0; i < foundShelves.Length; i++)
        {
            Shelf shelf = foundShelves[i];
            if (shelf != null && uniqueShelfInstanceIds.Add(shelf.GetInstanceID()))
                uniqueResults.Add(shelf);
        }
        foundShelves = uniqueResults.ToArray();
        bool changed = foundShelves.Length != _shelves.Length;
        if (!changed)
        {
            HashSet<int> currentShelfInstanceIds = new HashSet<int>();
            for (int i = 0; i < _shelves.Length; i++)
            {
                if (_shelves[i] != null)
                    currentShelfInstanceIds.Add(_shelves[i].GetInstanceID());
            }
            for (int i = 0; i < foundShelves.Length; i++)
            {
                if (!currentShelfInstanceIds.Contains(foundShelves[i].GetInstanceID()))
                {
                    changed = true;
                    break;
                }
            }
        }

        if (!changed)
            return false;

        _shelves = foundShelves;
        if (DebugLoggingEnabled)
            LogDebug("Shelf cache changed; now tracking " + foundShelves.Length + " shelf(s).");
        _shelfBounds.Clear();
        _shelfIndexReady = false;
        _shelfTargetIndex.Clear();
        _shelfSnapshots.Clear();
        if (_shelfIndexBuildRoutine != null)
            StopCoroutine(_shelfIndexBuildRoutine);
        _shelfIndexBuildRoutine = StartCoroutine(BuildShelfIndexIncrementally(foundShelves));
        return true;
    }

    private IEnumerator BuildShelfIndexIncrementally(Shelf[] shelves)
    {
        float buildStartedAt = Time.unscaledTime;
        if (DebugLoggingEnabled)
            LogDebug("Starting background shelf index build for " + shelves.Length + " shelf(s).");
        yield return null;
        Dictionary<ProductType, List<ShelfIconTarget>> index =
            new Dictionary<ProductType, List<ShelfIconTarget>>();
        Dictionary<Shelf, ShelfSnapshot> snapshots = new Dictionary<Shelf, ShelfSnapshot>();
        HashSet<ProductType> addedTypes = new HashSet<ProductType>();
        const int shelvesPerFrame = 8;

        for (int i = 0; i < shelves.Length; i++)
        {
            if (!ReferenceEquals(shelves, _shelves))
                yield break;
            Shelf shelf = shelves[i];
            if (shelf != null && shelf.isActiveAndEnabled && shelf.gameObject.activeInHierarchy &&
                shelf.gameObject.scene.IsValid())
            {
                List<Product> available = shelf.AvailableProducts();
                bool empty = available == null || available.Count == 0;
                ShelfSnapshot snapshot = CaptureShelfSnapshot(shelf, available);
                snapshots[shelf] = snapshot;
                Bounds bounds = default(Bounds);
                bool hasBounds = false;
                addedTypes.Clear();
                if (available != null)
                {
                    for (int p = 0; p < available.Count; p++)
                    {
                        Product product = available[p];
                        if (product == null || product.Data == null || !product.isActiveAndEnabled ||
                            !addedTypes.Add(product.Data.type))
                            continue;
                        Sprite icon = product.Data.productSprite;
                        if (icon == null || icon.texture == null)
                            continue;
                        if (!hasBounds)
                        {
                            bounds = GetShelfBounds(shelf);
                            _shelfBounds[shelf] = bounds;
                            hasBounds = true;
                        }
                        AddToShelfIndex(index, CreateShelfIconTarget(shelf, bounds, icon, 0,
                            product.Data.type, null));
                    }
                }

                if (empty)
                {
                    try
                    {
                        if (shelf.HasProductLabel())
                        {
                            ProductType assigned = shelf.ContainedProductType();
                            if (assigned == ProductType.NONE)
                                assigned = shelf.PreviousContainedProductType();
                            ProductData data = GetProductData(assigned);
                            Sprite icon = data != null ? data.productSprite : null;
                            if (assigned != ProductType.NONE && icon != null && icon.texture != null)
                            {
                                if (!hasBounds)
                                {
                                    bounds = GetShelfBounds(shelf);
                                    _shelfBounds[shelf] = bounds;
                                }
                                ShelfIconTarget target = CreateShelfIconTarget(shelf, bounds, icon, 0,
                                    assigned, null);
                                target.IsEmptyShelf = true;
                                AddToShelfIndex(index, target);
                            }
                        }
                    }
                    catch
                    {
                        // Skip labels while the store scene is changing.
                    }
                }
            }

            if (i % shelvesPerFrame == shelvesPerFrame - 1)
                yield return null;
        }

        _shelfTargetIndex.Clear();
        foreach (KeyValuePair<ProductType, List<ShelfIconTarget>> item in index)
            _shelfTargetIndex[item.Key] = item.Value;
        _shelfSnapshots.Clear();
        foreach (KeyValuePair<Shelf, ShelfSnapshot> item in snapshots)
            _shelfSnapshots[item.Key] = item.Value;
        _shelfIndexReady = true;
        _shelfIndexBuildRoutine = null;
        if (DebugLoggingEnabled)
        {
            int indexedShelfEntries = 0;
            foreach (KeyValuePair<ProductType, List<ShelfIconTarget>> item in _shelfTargetIndex)
                indexedShelfEntries += item.Value.Count;
            LogDebug("Shelf index ready: " + _shelfTargetIndex.Count + " product type(s), " +
                indexedShelfEntries + " shelf entry/entries in " +
                (Time.unscaledTime - buildStartedAt).ToString("0.00") + "s.");
        }
        if (_activeTargets.Count > 0)
            RefreshShelfTargets(_activeTargets);
    }

    private static void AddToShelfIndex(Dictionary<ProductType, List<ShelfIconTarget>> index,
        ShelfIconTarget target)
    {
        List<ShelfIconTarget> targets;
        if (!index.TryGetValue(target.ProductType, out targets))
        {
            targets = new List<ShelfIconTarget>();
            index[target.ProductType] = targets;
        }
        for (int i = 0; i < targets.Count; i++)
        {
            ShelfIconTarget existing = targets[i];
            if (existing.Shelf == target.Shelf && existing.IsEmptyShelf == target.IsEmptyShelf)
                return;
        }
        targets.Add(target);
    }

    private ShelfSnapshot CaptureShelfSnapshot(Shelf shelf, List<Product> available)
    {
        ShelfSnapshot snapshot = new ShelfSnapshot();
        PopulateShelfSnapshot(snapshot, shelf, available);
        return snapshot;
    }

    private void PopulateShelfSnapshot(ShelfSnapshot snapshot, Shelf shelf, List<Product> available)
    {
        snapshot.StockedProducts.Clear();
        Transform shelfTransform = shelf.transform;
        snapshot.Position = shelfTransform.position;
        snapshot.Rotation = shelfTransform.rotation;
        snapshot.Scale = shelfTransform.lossyScale;
        snapshot.LabeledProduct = ProductType.NONE;
        snapshot.EmptyLabelProduct = ProductType.NONE;
        if (available != null)
        {
            for (int i = 0; i < available.Count; i++)
            {
                Product product = available[i];
                if (product != null && product.Data != null && product.isActiveAndEnabled)
                    snapshot.StockedProducts.Add(product.Data.type);
            }
        }

        try
        {
            if (shelf.HasProductLabel())
            {
                ProductType assigned = shelf.ContainedProductType();
                if (assigned == ProductType.NONE)
                    assigned = shelf.PreviousContainedProductType();
                snapshot.LabeledProduct = assigned;
                if (snapshot.StockedProducts.Count == 0)
                    snapshot.EmptyLabelProduct = assigned;
            }
        }
        catch
        {
            // Labels can be unavailable while the store scene changes.
        }
    }

    private IEnumerator WatchShelvesForChanges()
    {
        const int shelvesPerFrame = 8;
        const float idleSecondsBetweenPasses = 0.75f;
        HashSet<ProductType> affectedProducts = new HashSet<ProductType>();
        List<ProductType> visibleAffected = new List<ProductType>();
        while (true)
        {
            if (!_shelfIndexReady)
            {
                yield return null;
                continue;
            }

            Shelf[] shelves = _shelves;
            affectedProducts.Clear();
            bool productCatalogChanged = false;
            for (int i = 0; i < shelves.Length; i++)
            {
                if (!ReferenceEquals(shelves, _shelves))
                    break;

                Shelf shelf = shelves[i];
                if (shelf == null || !shelf.isActiveAndEnabled || !shelf.gameObject.activeInHierarchy ||
                    !shelf.gameObject.scene.IsValid())
                {
                    if (i % shelvesPerFrame == shelvesPerFrame - 1)
                        yield return null;
                    continue;
                }

                List<Product> available = shelf.AvailableProducts();
                ShelfSnapshot current = _shelfWatchScratchSnapshot;
                PopulateShelfSnapshot(current, shelf, available);
                ShelfSnapshot previous;
                if (!_shelfSnapshots.TryGetValue(shelf, out previous) || !ShelfSnapshotsMatch(previous, current))
                {
                    if (previous != null && previous.LabeledProduct != current.LabeledProduct)
                        productCatalogChanged = true;
                    if (previous != null)
                        foreach (ProductType type in previous.StockedProducts)
                            affectedProducts.Add(type);
                    if (previous != null && previous.LabeledProduct != ProductType.NONE)
                        affectedProducts.Add(previous.LabeledProduct);
                    if (previous != null && previous.EmptyLabelProduct != ProductType.NONE)
                        affectedProducts.Add(previous.EmptyLabelProduct);
                    foreach (ProductType type in current.StockedProducts)
                        affectedProducts.Add(type);
                    if (current.LabeledProduct != ProductType.NONE)
                        affectedProducts.Add(current.LabeledProduct);
                    if (current.EmptyLabelProduct != ProductType.NONE)
                        affectedProducts.Add(current.EmptyLabelProduct);

                    ShelfSnapshot stored = previous ?? new ShelfSnapshot();
                    CopyShelfSnapshot(current, stored);
                    UpdateShelfIndexEntry(shelf, available, stored, affectedProducts);
                    _shelfSnapshots[shelf] = stored;
                    if (DebugLoggingEnabled)
                        LogDebug("Shelf changed: '" + shelf.name + "' (ID " + shelf.ShelfID +
                            "); affected products: " + string.Join(", ", affectedProducts) + ".");
                }

                if (i % shelvesPerFrame == shelvesPerFrame - 1)
                    yield return null;
            }

            if (productCatalogChanged)
            {
                CacheStoreProductTypes();
                CacheProductData();
            }

            if (affectedProducts.Count > 0 && _shelfIndexReady)
            {
                visibleAffected.Clear();
                for (int i = 0; i < _activeTargets.Count; i++)
                {
                    if (affectedProducts.Contains(_activeTargets[i]))
                        visibleAffected.Add(_activeTargets[i]);
                }
                if (visibleAffected.Count > 0)
                    ApplyShelfIndex(visibleAffected);
            }

            yield return new WaitForSecondsRealtime(idleSecondsBetweenPasses);
        }
    }

    private IEnumerator DiscoverShelvesFromRegistry()
    {
        const float discoveryInterval = 2f;
        const int shelvesPerFrame = 8;
        ProductType[] productTypes = (ProductType[])Enum.GetValues(typeof(ProductType));
        HashSet<int> knownShelfInstanceIds = new HashSet<int>();
        List<Shelf> newlyDiscovered = new List<Shelf>();
        HashSet<ProductType> newlyDiscoveredProducts = new HashSet<ProductType>();
        if (_labeledShelfRegistryField == null)
            _labeledShelfRegistryField = typeof(LabeledStockManager).GetField(
                "allLabeledShelvesByProductType", BindingFlags.Instance | BindingFlags.NonPublic);

        while (true)
        {
            yield return new WaitForSecondsRealtime(discoveryInterval);
            LabeledStockManager manager = LabeledStockManager.Instance;
            if (manager == null || _labeledShelfRegistryField == null)
                continue;

            IDictionary registry = _labeledShelfRegistryField.GetValue(manager) as IDictionary;
            if (registry == null)
                continue;

            knownShelfInstanceIds.Clear();
            for (int i = 0; i < _shelves.Length; i++)
            {
                if (_shelves[i] != null)
                    knownShelfInstanceIds.Add(_shelves[i].GetInstanceID());
            }
            newlyDiscovered.Clear();
            newlyDiscoveredProducts.Clear();
            int inspectedShelves = 0;
            for (int productIndex = 0; productIndex < productTypes.Length; productIndex++)
            {
                ProductType productType = productTypes[productIndex];
                if (!registry.Contains(productType))
                    continue;

                IList restockables = registry[productType] as IList;
                if (restockables == null)
                    continue;

                for (int shelfIndex = 0; shelfIndex < restockables.Count; shelfIndex++)
                {
                    Shelf shelf = restockables[shelfIndex] as Shelf;
                    if (shelf != null && shelf.isActiveAndEnabled &&
                        shelf.gameObject.activeInHierarchy && knownShelfInstanceIds.Add(shelf.GetInstanceID()))
                    {
                        newlyDiscovered.Add(shelf);
                        newlyDiscoveredProducts.Add(productType);
                        if (_loggedShelfInstanceIds.Add(shelf.GetInstanceID()))
                        {
                            if (DebugLoggingEnabled)
                                LogDebug("Detected new shelf '" + shelf.name + "' (ID " +
                                    shelf.ShelfID + ") for product " + productType +
                                    "; adding it to the product index.");
                        }
                    }

                    inspectedShelves++;
                    if (inspectedShelves % shelvesPerFrame == 0)
                        yield return null;
                }
            }

            if (DebugLoggingEnabled && newlyDiscovered.Count > 0)
            {
                LogDebug("Shelf registry discovery checked " + inspectedShelves + " labeled shelf reference(s); " +
                    newlyDiscovered.Count + " new shelf(s) found for product(s): " +
                    string.Join(", ", newlyDiscoveredProducts) + ".");
            }
            else if (DebugLoggingEnabled)
            {
                LogDebug("Shelf registry discovery checked " + inspectedShelves +
                    " labeled shelf reference(s); no new shelves found.");
            }

            if (newlyDiscovered.Count > 0)
            {
                List<Shelf> expandedShelves = new List<Shelf>(_shelves.Length + newlyDiscovered.Count);
                HashSet<int> expandedInstanceIds = new HashSet<int>();
                for (int i = 0; i < _shelves.Length; i++)
                {
                    Shelf existingShelf = _shelves[i];
                    if (existingShelf != null && expandedInstanceIds.Add(existingShelf.GetInstanceID()))
                        expandedShelves.Add(existingShelf);
                }
                for (int i = 0; i < newlyDiscovered.Count; i++)
                {
                    Shelf newShelf = newlyDiscovered[i];
                    if (newShelf != null && expandedInstanceIds.Add(newShelf.GetInstanceID()))
                        expandedShelves.Add(newShelf);
                }
                _shelves = expandedShelves.ToArray();
                _shelfBounds.Clear();
                _shelfIndexReady = false;
                _shelfTargetIndex.Clear();
                _shelfSnapshots.Clear();
                if (_shelfIndexBuildRoutine != null)
                    StopCoroutine(_shelfIndexBuildRoutine);
                _shelfIndexBuildRoutine = StartCoroutine(BuildShelfIndexIncrementally(_shelves));
                CacheStoreProductTypes();
                CacheProductData();
            }
        }
    }

    private static bool ShelfSnapshotsMatch(ShelfSnapshot first, ShelfSnapshot second)
    {
        const float positionThreshold = 0.05f;
        const float rotationThreshold = 1f;
        if ((first.Position - second.Position).sqrMagnitude > positionThreshold * positionThreshold ||
            Quaternion.Angle(first.Rotation, second.Rotation) > rotationThreshold ||
            (first.Scale - second.Scale).sqrMagnitude > 0.0001f ||
            first.EmptyLabelProduct != second.EmptyLabelProduct ||
            first.LabeledProduct != second.LabeledProduct ||
            first.StockedProducts.Count != second.StockedProducts.Count)
            return false;

        foreach (ProductType type in first.StockedProducts)
        {
            if (!second.StockedProducts.Contains(type))
                return false;
        }
        return true;
    }

    private static void CopyShelfSnapshot(ShelfSnapshot source, ShelfSnapshot destination)
    {
        destination.Position = source.Position;
        destination.Rotation = source.Rotation;
        destination.Scale = source.Scale;
        destination.LabeledProduct = source.LabeledProduct;
        destination.EmptyLabelProduct = source.EmptyLabelProduct;
        destination.StockedProducts.Clear();
        destination.StockedProducts.UnionWith(source.StockedProducts);
    }

    private void UpdateShelfIndexEntry(Shelf shelf, List<Product> available, ShelfSnapshot snapshot,
        HashSet<ProductType> affectedProducts)
    {
        foreach (ProductType type in affectedProducts)
        {
            List<ShelfIconTarget> targets;
            if (!_shelfTargetIndex.TryGetValue(type, out targets))
                continue;
            for (int i = targets.Count - 1; i >= 0; i--)
            {
                if (targets[i].Shelf == shelf)
                    targets.RemoveAt(i);
            }
        }

        Bounds bounds = GetShelfBounds(shelf);
        _shelfBounds[shelf] = bounds;
        HashSet<ProductType> added = _shelfIndexAddedProducts;
        added.Clear();
        if (available != null)
        {
            for (int i = 0; i < available.Count; i++)
            {
                Product product = available[i];
                if (product == null || product.Data == null || !product.isActiveAndEnabled ||
                    !affectedProducts.Contains(product.Data.type) || !added.Add(product.Data.type))
                    continue;
                Sprite icon = product.Data.productSprite;
                if (icon != null && icon.texture != null)
                    AddToShelfIndex(_shelfTargetIndex, CreateShelfIconTarget(shelf, bounds, icon, 0,
                        product.Data.type, null));
            }
        }

        if (snapshot.EmptyLabelProduct != ProductType.NONE &&
            affectedProducts.Contains(snapshot.EmptyLabelProduct))
        {
            ProductData data = GetProductData(snapshot.EmptyLabelProduct);
            Sprite icon = data != null ? data.productSprite : null;
            if (icon != null && icon.texture != null)
            {
                ShelfIconTarget target = CreateShelfIconTarget(shelf, bounds, icon, 0,
                    snapshot.EmptyLabelProduct, null);
                target.IsEmptyShelf = true;
                AddToShelfIndex(_shelfTargetIndex, target);
            }
        }
    }

    private static bool AreTargetListsEqual(List<ProductType> first, List<ProductType> second)
    {
        if (first.Count != second.Count)
            return false;

        for (int i = 0; i < first.Count; i++)
        {
            if (first[i] != second[i])
                return false;
        }

        return true;
    }

    private static bool AreTargetLimitsEqual(
        Dictionary<ProductType, int?> first,
        Dictionary<ProductType, int?> second,
        List<ProductType> products)
    {
        for (int i = 0; i < products.Count; i++)
        {
            int? firstLimit;
            int? secondLimit;
            if (!first.TryGetValue(products[i], out firstLimit) || !second.TryGetValue(products[i], out secondLimit) || firstLimit != secondLimit)
                return false;
        }
        return first.Count == second.Count;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        _markerVisibilityByShelf.Clear();
        _hasShelfRankingOrigin = false;
        CacheComputerUiRaycasters();
        bool shelvesChanged = CacheShelves();
        CacheStoreProductTypes();
        CacheProductData();
        _pendingShelfCacheRetries = 2;
        _nextShelfCacheRetry = Time.unscaledTime + 1.5f;
        if (shelvesChanged && _activeTarget.HasValue)
            RefreshShelfTargets();
    }

    private ProductType? FindClosestAvailableProduct(IList<ProductType> candidates, bool preferCurrentFloor)
    {
        if (candidates == null || candidates.Count == 0)
            return null;

        HashSet<ProductType> candidateSet = new HashSet<ProductType>(candidates);
        Camera camera = Camera.main;
        Vector3 origin = camera != null ? camera.transform.position : Vector3.zero;
        const float sameFloorHeightTolerance = 2.5f;
        float bestSameFloorDistance = float.PositiveInfinity;
        float bestAnyFloorDistance = float.PositiveInfinity;
        ProductType? nearestSameFloor = null;
        ProductType? nearestAnyFloor = null;

        for (int shelfIndex = 0; shelfIndex < _shelves.Length; shelfIndex++)
        {
            Shelf shelf = _shelves[shelfIndex];
            if (shelf == null || !shelf.isActiveAndEnabled || !shelf.gameObject.activeInHierarchy ||
                !shelf.gameObject.scene.IsValid())
                continue;

            List<Product> available = shelf.AvailableProducts();
            if (available == null)
                continue;

            for (int productIndex = 0; productIndex < available.Count; productIndex++)
            {
                Product product = available[productIndex];
                if (product == null || !product.isActiveAndEnabled || product.Data == null ||
                    !candidateSet.Contains(product.Data.type))
                    continue;

                Vector3 productPosition = GetProductPosition(product);
                bool sameFloor = Mathf.Abs(productPosition.y - origin.y) <= sameFloorHeightTolerance;
                Vector3 distanceOrigin = sameFloor ? origin : GetFloorLanding(productPosition);
                float distance = (productPosition - distanceOrigin).sqrMagnitude;
                if (distance < bestAnyFloorDistance)
                {
                    bestAnyFloorDistance = distance;
                    nearestAnyFloor = product.Data.type;
                }

                if (sameFloor && distance < bestSameFloorDistance)
                {
                    bestSameFloorDistance = distance;
                    nearestSameFloor = product.Data.type;
                }
            }
        }

        return preferCurrentFloor ? nearestSameFloor ?? nearestAnyFloor : nearestAnyFloor;
    }

    private void UpdateApiDisplayedTargets(long requestVersion)
    {
        if (requestVersion == _apiProductRequestVersion)
            return;

        _apiDisplayedTargets.Clear();
        _apiRequestSources.Clear();
        _apiRequestProducts.Clear();
        ProductLocatorApi.GetRequestSources(_apiRequestSources);
        for (int sourceIndex = 0; sourceIndex < _apiRequestSources.Count; sourceIndex++)
        {
            string source = _apiRequestSources[sourceIndex];
            ProductLocatorApi.GetRequestedProductsForSource(source, _apiRequestProducts);
            int? productLimit = ProductLocatorApi.GetMaxProductsPerSource(source);
            if (!productLimit.HasValue)
            {
                for (int i = 0; i < _apiRequestProducts.Count; i++)
                    _apiDisplayedTargets.Add(_apiRequestProducts[i]);
                continue;
            }

            List<ProductType> closestProducts = FindClosestAvailableProducts(
                _apiRequestProducts, productLimit.Value, true);
            for (int i = 0; i < closestProducts.Count; i++)
                _apiDisplayedTargets.Add(closestProducts[i]);
        }

        _apiProductRequestVersion = requestVersion;
    }

    private List<ProductType> FindClosestAvailableProducts(IList<ProductType> candidates,
        int maxProducts, bool preferCurrentFloor)
    {
        List<ProductType> result = new List<ProductType>();
        if (candidates == null || candidates.Count == 0 || maxProducts < 1)
            return result;

        HashSet<ProductType> candidateSet = new HashSet<ProductType>(candidates);
        Dictionary<ProductType, float> anyFloorDistances = new Dictionary<ProductType, float>();
        Dictionary<ProductType, float> sameFloorDistances = new Dictionary<ProductType, float>();
        Camera camera = Camera.main;
        Vector3 origin = camera != null ? camera.transform.position : Vector3.zero;
        const float sameFloorHeightTolerance = 2.5f;

        if (_shelfIndexReady)
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                ProductType type = candidates[i];
                List<ShelfIconTarget> indexed;
                if (!_shelfTargetIndex.TryGetValue(type, out indexed))
                    continue;
                for (int targetIndex = 0; targetIndex < indexed.Count; targetIndex++)
                {
                    ShelfIconTarget target = indexed[targetIndex];
                    if (target.IsEmptyShelf || target.Shelf == null || !target.Shelf.isActiveAndEnabled ||
                        !target.Shelf.gameObject.activeInHierarchy)
                        continue;
                    RecordProductDistance(type, target.Bounds.center, origin, sameFloorHeightTolerance,
                        anyFloorDistances, sameFloorDistances);
                }
            }
        }
        else
        {
            for (int shelfIndex = 0; shelfIndex < _shelves.Length; shelfIndex++)
            {
                Shelf shelf = _shelves[shelfIndex];
                if (shelf == null || !shelf.isActiveAndEnabled || !shelf.gameObject.activeInHierarchy ||
                    !shelf.gameObject.scene.IsValid())
                    continue;

                List<Product> available = shelf.AvailableProducts();
                if (available == null)
                    continue;
                for (int productIndex = 0; productIndex < available.Count; productIndex++)
                {
                    Product product = available[productIndex];
                    if (product == null || !product.isActiveAndEnabled || product.Data == null ||
                        !candidateSet.Contains(product.Data.type))
                        continue;
                    RecordProductDistance(product.Data.type, GetProductPosition(product), origin,
                        sameFloorHeightTolerance, anyFloorDistances, sameFloorDistances);
                }
            }
        }

        List<ProductType> ranked = new List<ProductType>(anyFloorDistances.Keys);
        ranked.Sort((first, second) =>
        {
            float firstDistance;
            float secondDistance;
            int firstFloor = preferCurrentFloor && sameFloorDistances.ContainsKey(first) ? 0 : 1;
            int secondFloor = preferCurrentFloor && sameFloorDistances.ContainsKey(second) ? 0 : 1;
            if (firstFloor != secondFloor)
                return firstFloor.CompareTo(secondFloor);
            Dictionary<ProductType, float> distances = firstFloor == 0 ? sameFloorDistances : anyFloorDistances;
            distances.TryGetValue(first, out firstDistance);
            distances.TryGetValue(second, out secondDistance);
            int distanceOrder = firstDistance.CompareTo(secondDistance);
            return distanceOrder != 0 ? distanceOrder : first.CompareTo(second);
        });

        int resultCount = Mathf.Min(maxProducts, ranked.Count);
        for (int i = 0; i < resultCount; i++)
            result.Add(ranked[i]);
        return result;
    }

    private static void RecordProductDistance(ProductType type, Vector3 position, Vector3 origin,
        float sameFloorHeightTolerance, Dictionary<ProductType, float> anyFloorDistances,
        Dictionary<ProductType, float> sameFloorDistances)
    {
        bool sameFloor = Mathf.Abs(position.y - origin.y) <= sameFloorHeightTolerance;
        Vector3 distanceOrigin = sameFloor ? origin : GetFloorLanding(position);
        float distance = (position - distanceOrigin).sqrMagnitude;
        float previousDistance;
        if (!anyFloorDistances.TryGetValue(type, out previousDistance) || distance < previousDistance)
            anyFloorDistances[type] = distance;
        if (sameFloor && (!sameFloorDistances.TryGetValue(type, out previousDistance) || distance < previousDistance))
            sameFloorDistances[type] = distance;
    }

    private static Vector3 GetFloorLanding(Vector3 position)
    {
        float topFloorDifference = Mathf.Abs(position.y - EscalatorTopLanding.y);
        float bottomFloorDifference = Mathf.Abs(position.y - EscalatorBottomLanding.y);
        return topFloorDifference <= bottomFloorDifference ? EscalatorTopLanding : EscalatorBottomLanding;
    }

    private bool HasAvailableShelfStock(ProductType productType)
    {
        for (int shelfIndex = 0; shelfIndex < _shelves.Length; shelfIndex++)
        {
            Shelf shelf = _shelves[shelfIndex];
            if (shelf == null || !shelf.isActiveAndEnabled || !shelf.gameObject.activeInHierarchy ||
                !shelf.gameObject.scene.IsValid())
                continue;

            List<Product> available = shelf.AvailableProducts();
            if (available == null)
                continue;

            for (int productIndex = 0; productIndex < available.Count; productIndex++)
            {
                Product product = available[productIndex];
                if (product != null && product.isActiveAndEnabled && product.Data != null &&
                    product.Data.type == productType)
                    return true;
            }
        }

        return false;
    }

    private bool RefreshHighlightedProduct(ProductType productType)
    {
        if (!_activeTargets.Contains(productType))
            return false;

        RefreshShelfTargets(new[] { productType }, true);
        return true;
    }

    private static bool AreTargetColorsEqual(
        Dictionary<ProductType, Color?> first,
        Dictionary<ProductType, Color?> second,
        IList<ProductType> productTypes)
    {
        for (int i = 0; i < productTypes.Count; i++)
        {
            Color? firstColor;
            Color? secondColor;
            if (!first.TryGetValue(productTypes[i], out firstColor) ||
                !second.TryGetValue(productTypes[i], out secondColor) || firstColor != secondColor)
                return false;
        }

        return true;
    }

    private static bool AreTargetBackgroundOpacitiesEqual(
        Dictionary<ProductType, float?> first,
        Dictionary<ProductType, float?> second,
        IList<ProductType> productTypes)
    {
        for (int i = 0; i < productTypes.Count; i++)
        {
            float? firstOpacity;
            float? secondOpacity;
            if (!first.TryGetValue(productTypes[i], out firstOpacity) ||
                !second.TryGetValue(productTypes[i], out secondOpacity) || firstOpacity != secondOpacity)
                return false;
        }

        return true;
    }

    private static bool AreTargetEmptyShelfSettingsEqual(
        Dictionary<ProductType, bool> first,
        Dictionary<ProductType, bool> second,
        IList<ProductType> productTypes)
    {
        for (int i = 0; i < productTypes.Count; i++)
        {
            bool firstValue;
            bool secondValue;
            if (!first.TryGetValue(productTypes[i], out firstValue) ||
                !second.TryGetValue(productTypes[i], out secondValue) || firstValue != secondValue)
                return false;
        }

        return true;
    }

    private static bool AreNullableBoolSettingsEqual(
        Dictionary<ProductType, bool?> first,
        Dictionary<ProductType, bool?> second,
        IList<ProductType> productTypes)
    {
        for (int i = 0; i < productTypes.Count; i++)
        {
            bool? firstValue;
            bool? secondValue;
            if (!first.TryGetValue(productTypes[i], out firstValue) ||
                !second.TryGetValue(productTypes[i], out secondValue) || firstValue != secondValue)
                return false;
        }

        return true;
    }

    private Bounds GetShelfBounds(Shelf shelf)
    {
        List<Renderer> renderers = _shelfRendererScratch;
        renderers.Clear();
        shelf.GetComponentsInChildren<Renderer>(false, renderers);
        bool found = false;
        Bounds bounds = new Bounds(shelf.transform.position, Vector3.zero);
        for (int i = 0; i < renderers.Count; i++)
        {
            Renderer renderer = renderers[i];
            if (renderer == null || !renderer.gameObject.activeInHierarchy)
                continue;

            if (!found)
            {
                bounds = renderer.bounds;
                found = true;
            }
            else
                bounds.Encapsulate(renderer.bounds);
        }

        renderers.Clear();
        return found ? bounds : new Bounds(shelf.transform.position, Vector3.one);
    }

    private static Rect GetSpriteUv(Sprite sprite)
    {
        try
        {
            Rect textureRect = sprite.textureRect;
            Texture texture = sprite.texture;
            return new Rect(textureRect.x / texture.width, textureRect.y / texture.height,
                textureRect.width / texture.width, textureRect.height / texture.height);
        }
        catch
        {
            return new Rect(0f, 0f, 1f, 1f);
        }
    }

    private static Vector3 GetProductPosition(Product product)
    {
        Renderer renderer = product.PrimaryRenderer;
        return renderer != null ? renderer.bounds.center : product.transform.position;
    }


}
