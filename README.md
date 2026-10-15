# Product Locator API

The Product Locator is a BepInEx 5 plugin for **Megastore Simulator**. This document describes the public API for other plugins.

The plugin is built for `BepInEx/plugins/thinkgraser/ProductLocator/ProductLocator.dll`.

## Referencing the API

Reference `ProductLocator.dll` from your plugin project and declare a BepInEx dependency on plugin GUID `thinkgraser.megastore.productlocator`. The public API class is `ProductLocatorApi`; shelf modes use `ProductLocatorHighlightMode`.

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

`AddHighlight` adds or updates one product under a source. Adding another product does not remove the source's existing products. To edit a product's marker, call `AddHighlight` again with the same source and product type and the new settings; this updates that product's shelf limit, color, and empty-shelf setting in place without clearing or re-adding the rest of the source's products.

```csharp
ProductLocatorApi.AddHighlight(ProductType.MILK, "MyPlugin");
ProductLocatorApi.AddHighlight(ProductType.BREAD, "MyPlugin");

// Shorthand for one nearest shelf, or all shelves.
ProductLocatorApi.AddHighlight(ProductType.MILK,
    ProductLocatorHighlightMode.Closest, "MyPlugin");
ProductLocatorApi.AddHighlight(ProductType.BREAD,
    ProductLocatorHighlightMode.All, "MyPlugin");
```

`SetHighlights` atomically replaces the complete product list for a source. Passing `null` or an empty sequence clears that source.

```csharp
ProductLocatorApi.SetHighlights("MyPlugin", requestedProducts);

// Show at most three nearest shelves per product. Use null for no limit.
ProductLocatorApi.SetHighlights("MyPlugin", requestedProducts,
    maxShelvesPerProduct: 3);
ProductLocatorApi.SetHighlights("MyPlugin", requestedProducts,
    maxShelvesPerProduct: null);
```

The `ProductLocatorHighlightMode.Closest` and `.All` overloads are shorthand for limits of one and unlimited. For numeric limits, the nearest shelves are selected first.

## Per-product color and empty shelves

Each product request can specify its marker border color and whether empty labeled shelves should be included. Pass `null` for either setting to inherit Product Locator's user configuration. Empty-shelf markers use the product assigned to the shelf label.

```csharp
ProductLocatorApi.AddHighlight(ProductType.MILK,
    ProductLocatorHighlightMode.Closest,
    "MyPlugin",
    markerColor: Color.cyan,
    includeEmptyShelves: true);

ProductLocatorApi.AddHighlight(ProductType.BREAD,
    ProductLocatorHighlightMode.All,
    "MyPlugin",
    markerColor: Color.magenta,
    includeEmptyShelves: false);
```

An API source can use a different color and empty-shelf setting for each product by calling `AddHighlight` for each one. `SetHighlights` also accepts shared color and empty-shelf settings for every product in its replacement list.

If multiple sources request the same product, the first registered source supplies its marker color. Shelf limits are merged using the least restrictive request. Empty labeled shelves are included if any source requesting that product explicitly enables them; otherwise, an explicit setting is used, falling back to the user's configuration when no source specifies one. Manual selections in the picker (F6 by default) take precedence for products selected there.

## Removing requests

```csharp
// Remove one product from a source while keeping its other products.
ProductLocatorApi.RemoveHighlight("MyPlugin", ProductType.MILK);

// Clear all products requested by this source.
ProductLocatorApi.Clear("MyPlugin");
```

## Querying stocked products

`TryGetClosestAvailableProduct` chooses the nearest stocked candidate and can prefer products on the player's current floor. `HasAvailableProduct` reports whether a product currently has stock on a shelf. Both inspect Unity scene state and must be called from Unity's main thread.

```csharp
ProductType closest;
bool found = ProductLocatorApi.TryGetClosestAvailableProduct(
    candidates, out closest, preferCurrentFloor: true);

bool hasStock = ProductLocatorApi.HasAvailableProduct(ProductType.MILK);
```

Both methods return `false` when the locator services are unavailable; `TryGetClosestAvailableProduct` also returns `false` when none of the candidates is stocked. Check `ProductLocatorApi.IsReady` before relying on these queries.

## Refreshing after stock changes

After a plugin changes stock for a highlighted product, call `RefreshProduct` to refresh that product's shelf markers without rebuilding markers for other products. It returns `false` if that product is not currently highlighted.

```csharp
ProductLocatorApi.RefreshProduct(changedProduct);
```

Call this method from Unity's main thread after the game has applied the stock change.

## Picker state

`ProductLocatorApi.IsPickerOpen` reports whether the product picker is open. The picker uses F6 by default; players can change that keybind in the plugin configuration. A plugin can use this to pause its own scroll or input handling while the picker is active.
