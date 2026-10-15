using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Public API for requesting product shelf highlights and querying stocked products.
/// Unity-backed query and refresh methods must be called from Unity's main thread.
/// </summary>
public enum ProductLocatorHighlightMode
{
    Closest,
    All
}

public static class ProductLocatorApi
{
    public const string PluginGuid = "thinkgraser.megastore.productlocator";

    private static readonly object Sync = new object();
    private sealed class HighlightRequest
    {
        public readonly List<ProductType> Products = new List<ProductType>();
        public readonly Dictionary<ProductType, int?> Limits = new Dictionary<ProductType, int?>();
        public readonly Dictionary<ProductType, Color?> Colors = new Dictionary<ProductType, Color?>();
        public readonly Dictionary<ProductType, bool?> IncludeEmptyShelves = new Dictionary<ProductType, bool?>();
    }

    private static readonly Dictionary<string, HighlightRequest> Requests =
        new Dictionary<string, HighlightRequest>(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> RequestOrder = new List<string>();
    private static readonly HashSet<ProductType> RequestProductScratch = new HashSet<ProductType>();
    private static Func<IList<ProductType>, bool, ProductType?> ClosestProductResolver;
    private static Func<ProductType, bool> AvailabilityResolver;
    private static Func<ProductType, bool> ProductRefreshHandler;
    private static bool PickerOpen;

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

    /// <summary>Adds or updates one product for this source without replacing its other products.</summary>
    public static void AddHighlight(ProductType productType, string source = "External")
    {
        AddHighlight(productType, ProductLocatorHighlightMode.All, source, null);
    }

    /// <summary>Adds or updates one product and its shelf limit without replacing other products.</summary>
    public static void AddHighlight(ProductType productType, ProductLocatorHighlightMode mode, string source = "External")
    {
        AddHighlight(productType, mode, source, null);
    }

    /// <summary>Adds or updates one product with an optional product-specific border color.</summary>
    public static void AddHighlight(ProductType productType, ProductLocatorHighlightMode mode,
        string source, Color? markerColor)
    {
        AddHighlight(productType, mode == ProductLocatorHighlightMode.Closest ? 1 : (int?)null, source, markerColor, null);
    }

    /// <summary>Adds or updates one product and optionally overrides empty labeled-shelf markers for it.</summary>
    public static void AddHighlight(ProductType productType, ProductLocatorHighlightMode mode,
        string source, Color? markerColor, bool? includeEmptyShelves)
    {
        AddHighlight(productType, mode == ProductLocatorHighlightMode.Closest ? 1 : (int?)null,
            source, markerColor, includeEmptyShelves);
    }

    /// <summary>Adds or updates one product and sets whether its empty labeled shelves are shown.</summary>
    public static void AddHighlight(ProductType productType, ProductLocatorHighlightMode mode,
        string source, bool includeEmptyShelves)
    {
        AddHighlight(productType, mode, source, null, includeEmptyShelves);
    }

    /// <summary>Adds or updates one product using all matching shelves and a product-specific border color.</summary>
    public static void AddHighlight(ProductType productType, Color markerColor, string source = "External")
    {
        AddHighlight(productType, (int?)null, source, markerColor);
    }

    /// <summary>Adds or updates one product with a shelf limit and optional product-specific border color.</summary>
    public static void AddHighlight(ProductType productType, int? maxShelvesPerProduct,
        string source, Color? markerColor = null)
    {
        AddHighlight(productType, maxShelvesPerProduct, source, markerColor, null);
    }

    /// <summary>Adds or updates one product and sets whether its empty labeled shelves are shown.</summary>
    public static void AddHighlight(ProductType productType, int? maxShelvesPerProduct,
        string source, bool includeEmptyShelves)
    {
        AddHighlight(productType, maxShelvesPerProduct, source, null, includeEmptyShelves);
    }

    /// <summary>
    /// Adds or updates one product with a shelf limit, optional border color, and optional empty-shelf setting.
    /// A null empty-shelf setting inherits Product Locator's configuration.
    /// </summary>
    public static void AddHighlight(ProductType productType, int? maxShelvesPerProduct,
        string source, Color? markerColor, bool? includeEmptyShelves)
    {
        if (maxShelvesPerProduct.HasValue && maxShelvesPerProduct.Value < 1)
            throw new ArgumentOutOfRangeException("maxShelvesPerProduct", "Use null for unlimited or a positive shelf count.");

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
            if (!request.Limits.ContainsKey(productType))
                request.Products.Add(productType);
            request.Limits[productType] = maxShelvesPerProduct;
            request.Colors[productType] = markerColor;
            request.IncludeEmptyShelves[productType] = includeEmptyShelves;
        }
    }

    /// <summary>
    /// Atomically replaces this caller's highlights with the supplied products.
    /// Passing an empty or null sequence clears this caller's request.
    /// </summary>
    public static void SetHighlights(string source, IEnumerable<ProductType> productTypes)
    {
        SetHighlights(source, productTypes, (int?)null);
    }

    /// <summary>Atomically replaces this caller's highlights and selects one or all matching shelves per product.</summary>
    public static void SetHighlights(string source, IEnumerable<ProductType> productTypes, ProductLocatorHighlightMode mode)
    {
        SetHighlights(source, productTypes, mode == ProductLocatorHighlightMode.Closest ? 1 : (int?)null);
    }

    /// <summary>Atomically replaces highlights, selecting one or all shelves and an optional source border color.</summary>
    public static void SetHighlights(string source, IEnumerable<ProductType> productTypes,
        ProductLocatorHighlightMode mode, Color? markerColor)
    {
        SetHighlights(source, productTypes, mode == ProductLocatorHighlightMode.Closest ? 1 : (int?)null, markerColor, null);
    }

    /// <summary>Replaces this source's requests with products using the given color and empty-shelf setting.</summary>
    public static void SetHighlights(string source, IEnumerable<ProductType> productTypes,
        ProductLocatorHighlightMode mode, Color? markerColor, bool? includeEmptyShelves)
    {
        SetHighlights(source, productTypes, mode == ProductLocatorHighlightMode.Closest ? 1 : (int?)null,
            markerColor, includeEmptyShelves);
    }

    /// <summary>Replaces this source's product list and sets whether empty labeled shelves are shown.</summary>
    public static void SetHighlights(string source, IEnumerable<ProductType> productTypes,
        ProductLocatorHighlightMode mode, bool includeEmptyShelves)
    {
        SetHighlights(source, productTypes, mode, null, includeEmptyShelves);
    }

    /// <summary>
    /// Atomically replaces this caller's highlights and limits the number of shelves shown
    /// per product type. Use null for no limit; limited results are chosen nearest-first.
    /// </summary>
    public static void SetHighlights(string source, IEnumerable<ProductType> productTypes, int? maxShelvesPerProduct)
    {
        SetHighlights(source, productTypes, maxShelvesPerProduct, null);
    }

    /// <summary>
    /// Atomically replaces this caller's highlights, limits shelves per product, and optionally sets the
    /// border color for this source. A null color uses Product Locator's configured marker color.
    /// If multiple sources request the same product, the first registered source supplies its color.
    /// </summary>
    public static void SetHighlights(string source, IEnumerable<ProductType> productTypes,
        int? maxShelvesPerProduct, Color? markerColor)
    {
        SetHighlights(source, productTypes, maxShelvesPerProduct, markerColor, null);
    }

    /// <summary>Replaces this source's product list and sets whether empty labeled shelves are shown.</summary>
    public static void SetHighlights(string source, IEnumerable<ProductType> productTypes,
        int? maxShelvesPerProduct, bool includeEmptyShelves)
    {
        SetHighlights(source, productTypes, maxShelvesPerProduct, null, includeEmptyShelves);
    }

    /// <summary>Replaces this source's requests with a shelf limit, optional color, and optional empty-shelf setting.</summary>
    public static void SetHighlights(string source, IEnumerable<ProductType> productTypes,
        int? maxShelvesPerProduct, Color? markerColor, bool? includeEmptyShelves)
    {
        if (maxShelvesPerProduct.HasValue && maxShelvesPerProduct.Value < 1)
            throw new ArgumentOutOfRangeException("maxShelvesPerProduct", "Use null for unlimited or a positive shelf count.");
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
                Requests.Remove(source);
                RequestOrder.Remove(source);
                return;
            }

            if (!Requests.ContainsKey(source))
                RequestOrder.Add(source);
            HighlightRequest request = new HighlightRequest();
            for (int i = 0; i < products.Count; i++)
            {
                ProductType productType = products[i];
                request.Products.Add(productType);
                request.Limits[productType] = maxShelvesPerProduct;
                request.Colors[productType] = markerColor;
                request.IncludeEmptyShelves[productType] = includeEmptyShelves;
            }
            Requests[source] = request;
        }
    }

    /// <summary>Removes all highlight requests made by this caller.</summary>
    public static void Clear(string source = "External")
    {
        source = NormalizeSource(source);
        lock (Sync)
        {
            Requests.Remove(source);
            RequestOrder.Remove(source);
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
            request.IncludeEmptyShelves.Remove(productType);

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
        productTypes.Clear();
        if (limits != null)
            limits.Clear();
        if (colors != null)
            colors.Clear();
        if (includeEmptyShelves != null)
            includeEmptyShelves.Clear();
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
                    request.Limits.TryGetValue(productType, out requestLimit);
                    request.Colors.TryGetValue(productType, out requestColor);
                    request.IncludeEmptyShelves.TryGetValue(productType, out requestIncludeEmpty);
                    if (RequestProductScratch.Add(productType))
                    {
                        productTypes.Add(productType);
                        if (limits != null)
                            limits[productType] = requestLimit;
                        if (colors != null)
                            colors[productType] = requestColor;
                        if (includeEmptyShelves != null)
                            includeEmptyShelves[productType] = requestIncludeEmpty;
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
