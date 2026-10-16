# Product Locator API

The Product Locator is a BepInEx 5 plugin for **Megastore Simulator**. This document describes the public API for other plugins.

The plugin is built for `BepInEx/plugins/thinkgraser/ProductLocator/ProductLocator.dll`.

## Referencing the API

Reference `ProductLocator.dll` from your plugin project and declare a BepInEx dependency on plugin GUID `thinkgraser.megastore.productlocator`. The API is exposed through `ProductLocatorApi` and configured with `ProductLocatorHighlightOptions`.

```csharp
using UnityEngine;

// Optional: wait until the locator plugin has registered its game services.
if (ProductLocatorApi.IsReady)
{
    ProductLocatorApi.AddHighlight(ProductType.MILK, "MyPlugin");
}
```

Sources are case-insensitive names owned by the calling plugin. Use a stable source name for all calls you want to update, remove, or clear together.

## Requesting highlights

`AddHighlight` adds or updates one product under a source. Adding another product does not remove the source's existing products. To edit a product's marker, call `AddHighlight` again with the same source and product type, passing updated options.

```csharp
// One nearest shelf.
var milkOptions = new ProductLocatorHighlightOptions
{
    MaxShelvesPerProduct = 1
};
ProductLocatorApi.AddHighlight(ProductType.MILK, "MyPlugin", milkOptions);

// Unlimited matching shelves (the default).
ProductLocatorApi.AddHighlight(ProductType.BREAD, "MyPlugin");
```

`SetHighlights` atomically replaces the complete product list for a source. Passing `null` or an empty sequence clears that source.

```csharp
var options = new ProductLocatorHighlightOptions
{
    MaxShelvesPerProduct = 3,
    MaxProductsPerSource = 2
};
ProductLocatorApi.SetHighlights("MyPlugin", requestedProducts, options);
```

## Highlight options

All option properties are optional and can be combined freely. Unset properties use their defaults: no shelf limit, the user's configured marker color, and the user's empty-shelf setting.

```csharp
ProductLocatorApi.AddHighlight(ProductType.MILK, "MyPlugin",
    new ProductLocatorHighlightOptions
    {
        MaxShelvesPerProduct = 1,
        MarkerColor = Color.cyan,
        BackgroundOpacity = 0.8f,
        IncludeEmptyShelves = true
    });

ProductLocatorApi.AddHighlight(ProductType.BREAD, "MyPlugin",
    new ProductLocatorHighlightOptions
    {
        MaxShelvesPerProduct = 3,
        IncludeEmptyShelves = false
    });
```

`MaxShelvesPerProduct` limits the nearest matching shelves. Set it to `1` for a closest-shelf marker, to a higher positive number for that many nearest shelves, or leave it unset (or set it to `null`) to show all matching shelves. Values below 1 are rejected.

`MaxProductsPerSource` limits how many product types from one source's request are shown. For example, set it to `1` to show the nearest stocked product type from that source, or `2` to show the two nearest stocked product types. Product types are ranked by their closest stocked product, preferring the player's current floor when possible. Leave it unset (or set it to `null`) to show all products from the source. Values below 1 are rejected. This is separate from `MaxShelvesPerProduct`, which limits how many shelves are shown for each product type.

The selected product types are ranked when the source request changes. They do not automatically change as the player moves. Call `RefreshProductSelections` when the caller wants capped requests ranked again from the player's current position:

```csharp
ProductLocatorApi.RefreshProductSelections();
```

`MarkerColor` optionally sets the marker border color. `BackgroundOpacity` optionally sets the dark background opacity from `0` (transparent) to `1` (opaque). `IncludeEmptyShelves` optionally controls markers on empty shelves that still have a product label. Set any property to `null` or leave it unset to inherit the user's configuration.

`SetHighlights` applies one shared options object to every product in the source's replacement list. Use `AddHighlight` when different products from the same source need different options.

If multiple sources request the same product, the first explicitly supplied marker color and background opacity are used; otherwise the user's configured settings are used. Shelf limits are merged using the least restrictive request. Empty labeled shelves are included if any source requesting that product explicitly enables them; otherwise an explicit setting is used, falling back to the user's configuration when no source specifies one. Manual selections in the picker (F6 by default) take precedence for products selected there.

## Removing requests

```csharp
// Remove one product from a source while keeping its other products.
ProductLocatorApi.RemoveHighlight("MyPlugin", ProductType.MILK);

// Clear all products requested by this source.
ProductLocatorApi.Clear("MyPlugin");
```

## Querying stocked products

`TryGetClosestAvailableProduct` chooses the nearest stocked candidate and can prefer products on the player's current floor. Cross-floor candidates are measured from that floor's escalator landing. `HasAvailableProduct` reports whether a product currently has stock on a shelf. Both inspect Unity scene state and must be called from Unity's main thread.

```csharp
ProductType closest;
bool found = ProductLocatorApi.TryGetClosestAvailableProduct(
    candidates, out closest, preferCurrentFloor: true);

bool hasStock = ProductLocatorApi.HasAvailableProduct(ProductType.MILK);
```

Both methods return `false` when the locator services are unavailable; `TryGetClosestAvailableProduct` also returns `false` when none of the candidates is stocked. Check `ProductLocatorApi.IsReady` before relying on these queries.

## Refreshing after stock changes

After a plugin changes stock for a highlighted product, call `RefreshProduct` to refresh that product's shelf markers without rebuilding markers for other products. It returns `false` if that product is not currently highlighted. This does not change which product types are selected by `MaxProductsPerSource`; use `RefreshProductSelections` for that.

```csharp
ProductLocatorApi.RefreshProduct(changedProduct);
```

Call this method from Unity's main thread after the game has applied the stock change.

## Picker state

`ProductLocatorApi.IsPickerOpen` reports whether the product picker is open. The picker uses F6 by default; players can change that keybind in the plugin configuration. A plugin can use this to pause its own scroll or input handling while the picker is active.
