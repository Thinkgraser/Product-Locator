using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Optional settings for one product or an entire source request.</summary>
public sealed class ProductLocatorHighlightOptions
{
    /// <summary>Maximum nearest shelves per product. Null shows all matching shelves.</summary>
    public int? MaxShelvesPerProduct { get; set; }

    /// <summary>Maximum nearest product types from this source. Null shows all requested products.</summary>
    public int? MaxProductsPerSource { get; set; }

    /// <summary>Optional marker border color. Null inherits the user's configured color.</summary>
    public Color? MarkerColor { get; set; }

    /// <summary>Optional opacity of the dark marker background, from 0 (transparent) to 1 (opaque). Null inherits the user's configured opacity.</summary>
    public float? BackgroundOpacity { get; set; }

    /// <summary>Whether to show empty labeled shelves. Null inherits the user's configuration.</summary>
    public bool? IncludeEmptyShelves { get; set; }
}

/// <summary>
/// Public API for requesting product shelf highlights and querying stocked products.
/// Unity-backed query and refresh methods must be called from Unity's main thread.
/// </summary>
public static class ProductLocatorApi
{
    public const string PluginGuid = "thinkgraser.megastore.productlocator";

    private static readonly object Sync = new object();
    private sealed class HighlightRequest
    {
        public readonly List<ProductType> Products = new List<ProductType>();
        public readonly Dictionary<ProductType, int?> Limits = new Dictionary<ProductType, int?>();
        public readonly Dictionary<ProductType, Color?> Colors = new Dictionary<ProductType, Color?>();
        public readonly Dictionary<ProductType, float?> BackgroundOpacities = new Dictionary<ProductType, float?>();
        public readonly Dictionary<ProductType, bool?> IncludeEmptyShelves = new Dictionary<ProductType, bool?>();
        public int? MaxProductsPerSource;
    }

    private static readonly Dictionary<string, HighlightRequest> Requests =
        new Dictionary<string, HighlightRequest>(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> RequestOrder = new List<string>();
    private static readonly HashSet<ProductType> RequestProductScratch = new HashSet<ProductType>();
    private static Func<IList<ProductType>, bool, ProductType?> ClosestProductResolver;
    private static Func<ProductType, bool> AvailabilityResolver;
    private static Func<ProductType, bool> ProductRefreshHandler;
    private static bool PickerOpen;
    private static long RequestVersion;

    internal static long GetRequestVersion()
    {
        lock (Sync)
            return RequestVersion;
    }

    /// <summary>True while the locator plugin has registered its game services.</summary>
    public static bool IsReady
    {
        get
        {
            lock (Sync)
                return ClosestProductResolver != null;
        }
    }

    /// <summary>True while the Product Locator picker is open (F6 is the default keybind).</summary>
    public static bool IsPickerOpen
    {
        get
        {
            lock (Sync)
                return PickerOpen;
        }
    }

    internal static void SetPickerOpen(bool isOpen)
    {
        lock (Sync)
            PickerOpen = isOpen;
    }

    /// <summary>
    /// Adds or updates one product for this source without replacing its other products.
    /// Null options use unlimited shelves and inherit the user's color and empty-shelf settings.
    /// A MaxProductsPerSource value in options applies to the source's complete product request.
    /// </summary>
    public static void AddHighlight(ProductType productType, string source = "External",
        ProductLocatorHighlightOptions options = null)
    {
        int? maxShelvesPerProduct = options != null ? options.MaxShelvesPerProduct : (int?)null;
        int? maxProductsPerSource = options != null ? options.MaxProductsPerSource : (int?)null;
        float? backgroundOpacity = options != null ? options.BackgroundOpacity : (float?)null;
        ValidateShelfLimit(maxShelvesPerProduct);
        ValidateProductLimit(maxProductsPerSource);
        ValidateBackgroundOpacity(backgroundOpacity);
        source = NormalizeSource(source);
        lock (Sync)
        {
            HighlightRequest request;
            if (!Requests.TryGetValue(source, out request))
            {
                request = new HighlightRequest();
                Requests[source] = request;
                RequestOrder.Add(source);
            }
            int? previousLimit;
            Color? previousColor;
            float? previousBackgroundOpacity;
            bool? previousIncludeEmpty;
            request.Limits.TryGetValue(productType, out previousLimit);
            request.Colors.TryGetValue(productType, out previousColor);
            request.BackgroundOpacities.TryGetValue(productType, out previousBackgroundOpacity);
            request.IncludeEmptyShelves.TryGetValue(productType, out previousIncludeEmpty);
            bool changed = !request.Limits.ContainsKey(productType) || previousLimit != maxShelvesPerProduct ||
                previousColor != (options != null ? options.MarkerColor : (Color?)null) ||
                previousBackgroundOpacity != backgroundOpacity ||
                previousIncludeEmpty != (options != null ? options.IncludeEmptyShelves : (bool?)null) ||
                (options != null && request.MaxProductsPerSource != maxProductsPerSource);
            if (!request.Limits.ContainsKey(productType))
                request.Products.Add(productType);
            request.Limits[productType] = maxShelvesPerProduct;
            request.Colors[productType] = options != null ? options.MarkerColor : (Color?)null;
            request.BackgroundOpacities[productType] = backgroundOpacity;
            request.IncludeEmptyShelves[productType] = options != null ? options.IncludeEmptyShelves : (bool?)null;
            if (options != null)
                request.MaxProductsPerSource = maxProductsPerSource;
            if (changed)
                RequestVersion++;
        }
    }

    /// <summary>
    /// Atomically replaces this caller's highlights with the supplied products and shared options.
    /// Passing an empty or null sequence clears this caller's request. Null options use unlimited shelves
    /// and inherit the user's color and empty-shelf settings. MaxProductsPerSource selects that many
    /// nearest stocked product types from this source's list.
    /// </summary>
    public static void SetHighlights(string source, IEnumerable<ProductType> productTypes,
        ProductLocatorHighlightOptions options = null)
    {
        int? maxShelvesPerProduct = options != null ? options.MaxShelvesPerProduct : (int?)null;
        int? maxProductsPerSource = options != null ? options.MaxProductsPerSource : (int?)null;
        ValidateShelfLimit(maxShelvesPerProduct);
        ValidateProductLimit(maxProductsPerSource);
        Color? markerColor = options != null ? options.MarkerColor : (Color?)null;
        float? backgroundOpacity = options != null ? options.BackgroundOpacity : (float?)null;
        ValidateBackgroundOpacity(backgroundOpacity);
        bool? includeEmptyShelves = options != null ? options.IncludeEmptyShelves : (bool?)null;
        source = NormalizeSource(source);
        List<ProductType> products = new List<ProductType>();
        if (productTypes != null)
        {
            HashSet<ProductType> seen = new HashSet<ProductType>();
            foreach (ProductType productType in productTypes)
            {
                if (seen.Add(productType))
                    products.Add(productType);
            }
        }

        lock (Sync)
        {
            if (products.Count == 0)
            {
                if (Requests.Remove(source))
                {
                    RequestOrder.Remove(source);
                    RequestVersion++;
                }
                return;
            }

            HighlightRequest previousRequest;
            bool hadRequest = Requests.TryGetValue(source, out previousRequest);
            if (!hadRequest)
                RequestOrder.Add(source);
            HighlightRequest request = new HighlightRequest();
            request.MaxProductsPerSource = maxProductsPerSource;
            for (int i = 0; i < products.Count; i++)
            {
                ProductType productType = products[i];
                request.Products.Add(productType);
                request.Limits[productType] = maxShelvesPerProduct;
                request.Colors[productType] = markerColor;
                request.BackgroundOpacities[productType] = backgroundOpacity;
                request.IncludeEmptyShelves[productType] = includeEmptyShelves;
            }
            if (!hadRequest || !RequestsEqual(previousRequest, request))
                RequestVersion++;
            Requests[source] = request;
        }
    }

    private static void ValidateShelfLimit(int? maxShelvesPerProduct)
    {
        if (maxShelvesPerProduct.HasValue && maxShelvesPerProduct.Value < 1)
            throw new ArgumentOutOfRangeException("MaxShelvesPerProduct", "Use null for unlimited or a positive shelf count.");
    }

    private static void ValidateProductLimit(int? maxProductsPerSource)
    {
        if (maxProductsPerSource.HasValue && maxProductsPerSource.Value < 1)
            throw new ArgumentOutOfRangeException("MaxProductsPerSource", "Use null for unlimited or a positive product count.");
    }

    private static void ValidateBackgroundOpacity(float? backgroundOpacity)
    {
        if (backgroundOpacity.HasValue && (float.IsNaN(backgroundOpacity.Value) ||
            float.IsInfinity(backgroundOpacity.Value) || backgroundOpacity.Value < 0f || backgroundOpacity.Value > 1f))
            throw new ArgumentOutOfRangeException("BackgroundOpacity", "Use null or an opacity from 0 to 1.");
    }

    private static bool RequestsEqual(HighlightRequest first, HighlightRequest second)
    {
        if (first.MaxProductsPerSource != second.MaxProductsPerSource ||
            first.Products.Count != second.Products.Count)
            return false;
        for (int i = 0; i < first.Products.Count; i++)
        {
            ProductType type = first.Products[i];
            int? firstLimit;
            int? secondLimit;
            Color? firstColor;
            Color? secondColor;
            float? firstOpacity;
            float? secondOpacity;
            bool? firstEmpty;
            bool? secondEmpty;
            if (second.Products[i] != type || !first.Limits.TryGetValue(type, out firstLimit) ||
                !second.Limits.TryGetValue(type, out secondLimit) || firstLimit != secondLimit ||
                !first.Colors.TryGetValue(type, out firstColor) || !second.Colors.TryGetValue(type, out secondColor) ||
                firstColor != secondColor ||
                !first.BackgroundOpacities.TryGetValue(type, out firstOpacity) ||
                !second.BackgroundOpacities.TryGetValue(type, out secondOpacity) || firstOpacity != secondOpacity ||
                !first.IncludeEmptyShelves.TryGetValue(type, out firstEmpty) ||
                !second.IncludeEmptyShelves.TryGetValue(type, out secondEmpty) || firstEmpty != secondEmpty)
                return false;
        }
        return true;
    }

    /// <summary>Removes all highlight requests made by this caller.</summary>
    public static void Clear(string source = "External")
    {
        source = NormalizeSource(source);
        lock (Sync)
        {
            if (Requests.Remove(source))
            {
                RequestOrder.Remove(source);
                RequestVersion++;
            }
        }
    }

    /// <summary>
    /// Returns whether a source currently requests exactly the supplied product types.
    /// Product order is ignored. Use this to detect requests removed or edited by another caller.
    /// </summary>
    public static bool HasHighlights(string source, IEnumerable<ProductType> productTypes)
    {
        List<ProductType> expected = MaterializeDistinct(productTypes);
        source = NormalizeSource(source);
        lock (Sync)
        {
            HighlightRequest request;
            if (expected.Count == 0)
                return !Requests.ContainsKey(source);
            if (!Requests.TryGetValue(source, out request) || request.Products.Count != expected.Count)
                return false;

            for (int i = 0; i < expected.Count; i++)
            {
                if (!request.Limits.ContainsKey(expected[i]))
                    return false;
            }
            return true;
        }
    }

    /// <summary>Removes one product type from one caller's request without changing its other products.</summary>
    public static bool RemoveHighlight(string source, ProductType productType)
    {
        source = NormalizeSource(source);
        lock (Sync)
        {
            HighlightRequest request;
            if (!Requests.TryGetValue(source, out request) || !request.Products.Remove(productType))
                return false;
            request.Limits.Remove(productType);
            request.Colors.Remove(productType);
            request.BackgroundOpacities.Remove(productType);
            request.IncludeEmptyShelves.Remove(productType);
            RequestVersion++;

            if (request.Products.Count == 0)
            {
                Requests.Remove(source);
                RequestOrder.Remove(source);
            }
            return true;
        }
    }

    /// <summary>
    /// Finds the closest currently stocked product from the candidate set.
    /// Products on the player's floor are preferred when one is within 2.5 world units vertically.
    /// Products on another floor are ranked by distance from that floor's escalator landing.
    /// Returns false if the locator is unavailable or none of the candidates is stocked.
    /// Call on Unity's main thread; this inspects the current shelf inventory.
    /// </summary>
    public static bool TryGetClosestAvailableProduct(IEnumerable<ProductType> candidates,
        out ProductType productType, bool preferCurrentFloor = true)
    {
        List<ProductType> products = MaterializeDistinct(candidates);
        Func<IList<ProductType>, bool, ProductType?> resolver;
        lock (Sync)
            resolver = ClosestProductResolver;

        if (resolver != null && products.Count > 0)
        {
            ProductType? result = resolver(products, preferCurrentFloor);
            if (result.HasValue)
            {
                productType = result.Value;
                return true;
            }
        }

        productType = default(ProductType);
        return false;
    }

    /// <summary>
    /// Returns whether a product currently has at least one stocked shelf.
    /// Call on Unity's main thread; this inspects the current shelf inventory.
    /// </summary>
    public static bool HasAvailableProduct(ProductType productType)
    {
        Func<ProductType, bool> resolver;
        lock (Sync)
            resolver = AvailabilityResolver;
        return resolver != null && resolver(productType);
    }

    /// <summary>
    /// Refreshes markers for this product only after its shelf stock changes.
    /// Other highlighted products stay visible. Returns false if the product is not highlighted.
    /// Call on Unity's main thread.
    /// </summary>
    public static bool RefreshProduct(ProductType productType)
    {
        Func<ProductType, bool> handler;
        lock (Sync)
            handler = ProductRefreshHandler;
        return handler != null && handler(productType);
    }

    /// <summary>
    /// Re-ranks product types for API requests that set MaxProductsPerSource, using the player's current
    /// position. Call this when the caller wants a new nearest-product selection after the player moves.
    /// Request changes are ranked automatically; unchanged requests are not periodically re-ranked.
    /// </summary>
    public static void RefreshProductSelections()
    {
        lock (Sync)
        {
            foreach (KeyValuePair<string, HighlightRequest> item in Requests)
            {
                if (item.Value.MaxProductsPerSource.HasValue)
                {
                    RequestVersion++;
                    return;
                }
            }
        }
    }

    internal static void RegisterGameServices(
        Func<IList<ProductType>, bool, ProductType?> closestProductResolver,
        Func<ProductType, bool> availabilityResolver,
        Func<ProductType, bool> productRefreshHandler)
    {
        lock (Sync)
        {
            ClosestProductResolver = closestProductResolver;
            AvailabilityResolver = availabilityResolver;
            ProductRefreshHandler = productRefreshHandler;
        }
    }

    internal static void UnregisterGameServices()
    {
        lock (Sync)
        {
            ClosestProductResolver = null;
            AvailabilityResolver = null;
            ProductRefreshHandler = null;
            PickerOpen = false;
        }
    }

    internal static void GetRequestedProducts(List<ProductType> productTypes)
    {
        GetRequestedProducts(productTypes, null);
    }

    internal static void GetRequestedProducts(List<ProductType> productTypes,
        Dictionary<ProductType, int?> limits)
    {
        GetRequestedProducts(productTypes, limits, null);
    }

    internal static void GetRequestedProducts(List<ProductType> productTypes,
        Dictionary<ProductType, int?> limits, Dictionary<ProductType, Color?> colors)
    {
        GetRequestedProducts(productTypes, limits, colors, null);
    }

    internal static void GetRequestedProducts(List<ProductType> productTypes,
        Dictionary<ProductType, int?> limits, Dictionary<ProductType, Color?> colors,
        Dictionary<ProductType, bool?> includeEmptyShelves)
    {
        GetRequestedProducts(productTypes, limits, colors, includeEmptyShelves, null);
    }

    internal static void GetRequestedProducts(List<ProductType> productTypes,
        Dictionary<ProductType, int?> limits, Dictionary<ProductType, Color?> colors,
        Dictionary<ProductType, bool?> includeEmptyShelves,
        Dictionary<ProductType, float?> backgroundOpacities)
    {
        productTypes.Clear();
        if (limits != null)
            limits.Clear();
        if (colors != null)
            colors.Clear();
        if (includeEmptyShelves != null)
            includeEmptyShelves.Clear();
        if (backgroundOpacities != null)
            backgroundOpacities.Clear();
        lock (Sync)
        {
            RequestProductScratch.Clear();
            for (int sourceIndex = 0; sourceIndex < RequestOrder.Count; sourceIndex++)
            {
                HighlightRequest request;
                if (!Requests.TryGetValue(RequestOrder[sourceIndex], out request))
                    continue;

                for (int productIndex = 0; productIndex < request.Products.Count; productIndex++)
                {
                    ProductType productType = request.Products[productIndex];
                    int? requestLimit;
                    Color? requestColor;
                    bool? requestIncludeEmpty;
                    float? requestBackgroundOpacity;
                    request.Limits.TryGetValue(productType, out requestLimit);
                    request.Colors.TryGetValue(productType, out requestColor);
                    request.IncludeEmptyShelves.TryGetValue(productType, out requestIncludeEmpty);
                    request.BackgroundOpacities.TryGetValue(productType, out requestBackgroundOpacity);
                    if (RequestProductScratch.Add(productType))
                    {
                        productTypes.Add(productType);
                        if (limits != null)
                            limits[productType] = requestLimit;
                        if (colors != null)
                            colors[productType] = requestColor;
                        if (includeEmptyShelves != null)
                            includeEmptyShelves[productType] = requestIncludeEmpty;
                        if (backgroundOpacities != null)
                            backgroundOpacities[productType] = requestBackgroundOpacity;
                    }
                    else if (limits != null)
                    {
                        // The least restrictive request wins, while every finite limit
                        // still selects the nearest shelves first.
                        int? existingLimit = limits[productType];
                        if (!existingLimit.HasValue || !requestLimit.HasValue)
                            limits[productType] = null;
                        else if (requestLimit.Value > existingLimit.Value)
                            limits[productType] = requestLimit;
                    }

                    if (colors != null && !colors[productType].HasValue && requestColor.HasValue)
                        colors[productType] = requestColor;

                    if (backgroundOpacities != null && !backgroundOpacities[productType].HasValue && requestBackgroundOpacity.HasValue)
                        backgroundOpacities[productType] = requestBackgroundOpacity;

                    if (includeEmptyShelves != null && requestIncludeEmpty.HasValue)
                    {
                        bool? existingIncludeEmpty = includeEmptyShelves[productType];
                        if (!existingIncludeEmpty.HasValue || requestIncludeEmpty.Value)
                            includeEmptyShelves[productType] = requestIncludeEmpty;
                    }
                }
            }
        }
    }

    internal static void GetRequestSources(List<string> sources)
    {
        sources.Clear();
        lock (Sync)
        {
            for (int i = 0; i < RequestOrder.Count; i++)
            {
                if (Requests.ContainsKey(RequestOrder[i]))
                    sources.Add(RequestOrder[i]);
            }
        }
    }

    internal static void GetRequestedProductsForSource(string source, List<ProductType> productTypes)
    {
        productTypes.Clear();
        source = NormalizeSource(source);
        lock (Sync)
        {
            HighlightRequest request;
            if (Requests.TryGetValue(source, out request))
                productTypes.AddRange(request.Products);
        }
    }

    internal static int? GetMaxProductsPerSource(string source)
    {
        source = NormalizeSource(source);
        lock (Sync)
        {
            HighlightRequest request;
            return Requests.TryGetValue(source, out request) ? request.MaxProductsPerSource : (int?)null;
        }
    }

    internal static bool HasRequestsFromOtherSources(string source)
    {
        source = NormalizeSource(source);
        lock (Sync)
        {
            foreach (string requestedSource in RequestOrder)
            {
                if (!string.Equals(requestedSource, source, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    internal static bool TryGetRequestedProduct(out ProductType productType)
    {
        List<ProductType> products = new List<ProductType>();
        GetRequestedProducts(products);
        if (products.Count > 0)
        {
            productType = products[0];
            return true;
        }

        productType = default(ProductType);
        return false;
    }

    private static List<ProductType> MaterializeDistinct(IEnumerable<ProductType> productTypes)
    {
        List<ProductType> result = new List<ProductType>();
        if (productTypes == null)
            return result;

        HashSet<ProductType> seen = new HashSet<ProductType>();
        foreach (ProductType productType in productTypes)
        {
            if (seen.Add(productType))
                result.Add(productType);
        }
        return result;
    }

    private static string NormalizeSource(string source)
    {
        return string.IsNullOrWhiteSpace(source) ? "External" : source;
    }
}
