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
        Dictionary<ProductType, Color?> indexColors = new Dictionary<ProductType, Color?>();
        for (int i = 0; i < targetTypes.Count; i++)
            refreshedIndex[targetTypes[i]] = new List<ShelfIconTarget>();
        HashSet<ProductType> addedTypes = new HashSet<ProductType>();
        const int shelvesPerFrame = 8;
        HashSet<ProductType> targetSet = new HashSet<ProductType>(targetTypes);
        Dictionary<ProductType, List<ShelfIconTarget>> limitedTargets = new Dictionary<ProductType, List<ShelfIconTarget>>();
        Dictionary<ProductType, List<float>> limitedDistances = new Dictionary<ProductType, List<float>>();
        Camera camera = Camera.main;
        Vector3 closestOrigin = camera != null ? camera.transform.position : Vector3.zero;

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
                    refreshedIndex[product.Data.type].Add(CreateShelfIconTarget(shelf, bounds, icon, 0,
                        product.Data.type, indexColors));
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
                                    assignedType, indexColors);
                                indexedEmpty.IsEmptyShelf = true;
                                refreshedIndex[assignedType].Add(indexedEmpty);
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
        LogDebug("Shelf refresh completed for [" + string.Join(", ", targetTypes) + "] with " +
            refreshedTargets.Count + " marker(s) in " + (Time.unscaledTime - scanStartedAt).ToString("0.00") + "s.");
    }

    private void ApplyShelfIndex(IList<ProductType> targetTypes)
    {
        Dictionary<ProductType, int?> limits = new Dictionary<ProductType, int?>();
        Dictionary<ProductType, Color?> colors = new Dictionary<ProductType, Color?>();
        Dictionary<ProductType, bool> includeEmpty = new Dictionary<ProductType, bool>();
        HashSet<ProductType> targetSet = new HashSet<ProductType>(targetTypes);
        for (int i = 0; i < targetTypes.Count; i++)
        {
            int? limit;
            limits[targetTypes[i]] = _activeTargetLimits.TryGetValue(targetTypes[i], out limit) ? limit : null;
            Color? color;
            colors[targetTypes[i]] = _activeTargetColors.TryGetValue(targetTypes[i], out color) ? color : (Color?)null;
            bool showEmpty;
            includeEmpty[targetTypes[i]] = _activeTargetIncludeEmptyShelves.TryGetValue(targetTypes[i], out showEmpty) && showEmpty;
        }

        List<ShelfIconTarget> selected = new List<ShelfIconTarget>();
        Dictionary<ProductType, List<ShelfIconTarget>> limited = new Dictionary<ProductType, List<ShelfIconTarget>>();
        Dictionary<ProductType, List<float>> distances = new Dictionary<ProductType, List<float>>();
        Dictionary<Shelf, int> shelfStacks = new Dictionary<Shelf, int>();
        Camera camera = Camera.main;
        Vector3 origin = camera != null ? camera.transform.position : Vector3.zero;
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
                    (!includeEmpty.TryGetValue(cached.ProductType, out showEmpty) || !showEmpty))
                    continue;
                int stackIndex;
                shelfStacks.TryGetValue(cached.Shelf, out stackIndex);
                shelfStacks[cached.Shelf] = stackIndex + 1;
                ShelfIconTarget target = new ShelfIconTarget
                {
                    Shelf = cached.Shelf,
                    Bounds = cached.Bounds,
                    Texture = cached.Texture,
                    Uv = cached.Uv,
                    StackIndex = stackIndex,
                    ProductType = cached.ProductType,
                    MarkerColor = colors[cached.ProductType]
                };
                AddShelfIconTarget(target, limits, origin, selected, limited, distances);
            }
        }

        foreach (KeyValuePair<ProductType, List<ShelfIconTarget>> item in limited)
            selected.AddRange(item.Value);
        for (int i = _shelfTargets.Count - 1; i >= 0; i--)
        {
            if (targetSet.Contains(_shelfTargets[i].ProductType))
                _shelfTargets.RemoveAt(i);
        }
        _shelfTargets.AddRange(selected);
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
            MarkerColor = colors.TryGetValue(productType, out markerColor) ? markerColor : (Color?)null
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

    private bool CacheShelves()
    {
        Shelf[] foundShelves = FindObjectsByType<Shelf>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        bool changed = foundShelves.Length != _shelves.Length;
        if (!changed)
        {
            HashSet<Shelf> currentShelves = new HashSet<Shelf>(_shelves);
            for (int i = 0; i < foundShelves.Length; i++)
            {
                if (!currentShelves.Contains(foundShelves[i]))
                {
                    changed = true;
                    break;
                }
            }
        }

        if (!changed)
            return false;

        _shelves = foundShelves;
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
        LogDebug("Starting background shelf index build for " + shelves.Length + " shelf(s).");
        yield return null;
        Dictionary<ProductType, List<ShelfIconTarget>> index =
            new Dictionary<ProductType, List<ShelfIconTarget>>();
        Dictionary<Shelf, ShelfSnapshot> snapshots = new Dictionary<Shelf, ShelfSnapshot>();
        Dictionary<ProductType, Color?> noColors = new Dictionary<ProductType, Color?>();
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
                            product.Data.type, noColors));
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
                                    assigned, noColors);
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
        int indexedShelfEntries = 0;
        foreach (KeyValuePair<ProductType, List<ShelfIconTarget>> item in _shelfTargetIndex)
            indexedShelfEntries += item.Value.Count;
        LogDebug("Shelf index ready: " + _shelfTargetIndex.Count + " product type(s), " +
            indexedShelfEntries + " shelf entry/entries in " +
            (Time.unscaledTime - buildStartedAt).ToString("0.00") + "s.");
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
        snapshot.Position = shelf.transform.position;
        snapshot.Rotation = shelf.transform.rotation;
        snapshot.Scale = shelf.transform.lossyScale;
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
        while (true)
        {
            if (!_shelfIndexReady)
            {
                yield return null;
                continue;
            }

            Shelf[] shelves = _shelves;
            HashSet<ProductType> affectedProducts = new HashSet<ProductType>();
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

                    ShelfSnapshot stored = CloneShelfSnapshot(current);
                    UpdateShelfIndexEntry(shelf, available, stored, affectedProducts);
                    _shelfSnapshots[shelf] = stored;
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
                List<ProductType> visibleAffected = new List<ProductType>();
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

            HashSet<Shelf> knownShelves = new HashSet<Shelf>(_shelves);
            List<Shelf> newlyDiscovered = new List<Shelf>();
            HashSet<ProductType> newlyDiscoveredProducts = new HashSet<ProductType>();
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
                        shelf.gameObject.activeInHierarchy && knownShelves.Add(shelf))
                    {
                        newlyDiscovered.Add(shelf);
                        newlyDiscoveredProducts.Add(productType);
                        if (_loggedShelfInstanceIds.Add(shelf.GetInstanceID()))
                        {
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

            if (newlyDiscovered.Count > 0)
            {
                LogDebug("Shelf registry discovery checked " + inspectedShelves + " labeled shelf reference(s); " +
                    newlyDiscovered.Count + " new shelf(s) found for product(s): " +
                    string.Join(", ", newlyDiscoveredProducts) + ".");
            }
            else
            {
                LogDebug("Shelf registry discovery checked " + inspectedShelves +
                    " labeled shelf reference(s); no new shelves found.");
            }

            if (newlyDiscovered.Count > 0)
            {
                Shelf[] expandedShelves = new Shelf[_shelves.Length + newlyDiscovered.Count];
                Array.Copy(_shelves, expandedShelves, _shelves.Length);
                newlyDiscovered.CopyTo(expandedShelves, _shelves.Length);
                _shelves = expandedShelves;
                _shelfBounds.Clear();
                _shelfIndexReady = false;
                _shelfTargetIndex.Clear();
                _shelfSnapshots.Clear();
                if (_shelfIndexBuildRoutine != null)
                    StopCoroutine(_shelfIndexBuildRoutine);
                _shelfIndexBuildRoutine = StartCoroutine(BuildShelfIndexIncrementally(expandedShelves));
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

    private static ShelfSnapshot CloneShelfSnapshot(ShelfSnapshot source)
    {
        ShelfSnapshot copy = new ShelfSnapshot
        {
            Position = source.Position,
            Rotation = source.Rotation,
            Scale = source.Scale,
            LabeledProduct = source.LabeledProduct,
            EmptyLabelProduct = source.EmptyLabelProduct
        };
        copy.StockedProducts.UnionWith(source.StockedProducts);
        return copy;
    }

    private void UpdateShelfIndexEntry(Shelf shelf, List<Product> available, ShelfSnapshot snapshot,
        HashSet<ProductType> affectedProducts)
    {
        foreach (ProductType type in affectedProducts)
        {
            List<ShelfIconTarget> targets;
            if (!_shelfTargetIndex.TryGetValue(type, out targets))
                continue;
            targets.RemoveAll(target => target.Shelf == shelf);
        }

        Bounds bounds = GetShelfBounds(shelf);
        _shelfBounds[shelf] = bounds;
        HashSet<ProductType> added = new HashSet<ProductType>();
        Dictionary<ProductType, Color?> noColors = new Dictionary<ProductType, Color?>();
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
                        product.Data.type, noColors));
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
                    snapshot.EmptyLabelProduct, noColors);
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
                float distance = (productPosition - origin).sqrMagnitude;
                if (distance < bestAnyFloorDistance)
                {
                    bestAnyFloorDistance = distance;
                    nearestAnyFloor = product.Data.type;
                }

                if (Mathf.Abs(productPosition.y - origin.y) <= sameFloorHeightTolerance &&
                    distance < bestSameFloorDistance)
                {
                    bestSameFloorDistance = distance;
                    nearestSameFloor = product.Data.type;
                }
            }
        }

        return preferCurrentFloor ? nearestSameFloor ?? nearestAnyFloor : nearestAnyFloor;
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

    private static Bounds GetShelfBounds(Shelf shelf)
    {
        Renderer[] renderers = shelf.GetComponentsInChildren<Renderer>();
        bool found = false;
        Bounds bounds = new Bounds(shelf.transform.position, Vector3.zero);
        for (int i = 0; i < renderers.Length; i++)
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
