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
    private static readonly Dictionary<ProductGroup, string> DepartmentNames =
        new Dictionary<ProductGroup, string>();
    private readonly Dictionary<ProductType, string> _productTypeSearchNames =
        new Dictionary<ProductType, string>();
    private string _normalizedSearchSource;
    private string _normalizedSearch = string.Empty;

    private void OnGUI()
    {
        DrawShelfIcons();
        if (_windowOpen)
        {
            Color previous = GUI.color;
            GUI.color = new Color(0.025f, 0.035f, 0.055f, 0.88f);
            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = previous;

            float scale = Mathf.Clamp(_uiScale.Value, 0.5f, 2f);
            Matrix4x4 previousMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(Vector3.one * scale);
            _window = new Rect(0f, 0f, Screen.width / scale, Screen.height / scale);
            GUI.Window(736214, _window, DrawWindow, GUIContent.none, GUIStyle.none);
            GUI.matrix = previousMatrix;
        }
    }

    private void DrawWindow(int windowId)
    {
        EnsureGuiStyles();
        // Read the pointer in window coordinates before nested GUILayout groups
        // adjust Event.current.mousePosition for their local coordinate spaces.
        _cursorOverDepartmentDropdown = _departmentDropdownOpen &&
            GetDepartmentMenuRect().Contains(Event.current.mousePosition);
        if (_departmentDropdownOpen)
            HandleDepartmentDropdownInput();

        GUILayout.BeginVertical();
        GUILayout.BeginHorizontal();
        GUILayout.Space(Ui(22f));
        GUILayout.BeginVertical(GUILayout.ExpandWidth(true));
        GUILayout.Space(22f);
        GUILayout.BeginHorizontal();
        GUILayout.BeginVertical();
        GUILayout.Label("PRODUCT LOCATOR", _titleStyle);
        GUILayout.Label("Choose one or more products to locate on store shelves.", _subtitleStyle);
        GUILayout.EndVertical();
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("CLOSE", _buttonStyle, GUILayout.Width(Ui(150f)), GUILayout.Height(Ui(44f))))
            SetWindowOpen(false);
        GUILayout.Space(Ui(20f));
        GUILayout.EndHorizontal();
        GUILayout.Space(22f);

        GUILayout.BeginHorizontal(GUILayout.ExpandWidth(true));
        GUILayout.BeginHorizontal(_searchBarStyle, GUILayout.ExpandWidth(true));
        GUILayout.Label("SEARCH", _centerSectionStyle, GUILayout.Width(Ui(94f)), GUILayout.Height(Ui(46f)));
        _search = GUILayout.TextField(_search ?? string.Empty, _searchFieldStyle, GUILayout.ExpandWidth(true), GUILayout.Height(Ui(46f)));
        if (GUILayout.Button("X", _clearButtonStyle, GUILayout.Width(Ui(46f)), GUILayout.Height(Ui(46f))))
            _search = string.Empty;
        GUILayout.EndHorizontal();
        GUILayout.Space(Ui(24f));
        DrawDepartmentDropdown();
        GUILayout.Space(Ui(22f));
        GUILayout.EndHorizontal();
        GUILayout.Space(Ui(16f));

        GUILayout.BeginHorizontal(GUILayout.ExpandHeight(true));
        GUILayout.BeginVertical(GUILayout.ExpandHeight(true));
        GUILayout.Space(Ui(36f));
        DrawSelectedProductsPanel();
        GUILayout.EndVertical();
        GUILayout.Space(Ui(24f));
        DrawProductTilesPanel();
        GUILayout.EndHorizontal();
        GUILayout.Space(20f);
        GUILayout.EndVertical();
        GUILayout.EndVertical();
        GUILayout.Space(Ui(22f));
        GUILayout.EndHorizontal();
        if (_departmentDropdownOpen)
            DrawDepartmentDropdownOverlay();
    }

    private void DrawSelectedProductsPanel()
    {
        GUILayout.BeginVertical(_panelStyle, GUILayout.Width(Ui(340f)), GUILayout.ExpandHeight(true));
        if (GUILayout.Button((_manualProductsExpanded ? "▾  " : "▸  ") +
            "SELECTED PRODUCTS  ·  " + _manualSelections.Count, _buttonStyle,
            GUILayout.ExpandWidth(true), GUILayout.Height(Ui(42f))))
            _manualProductsExpanded = !_manualProductsExpanded;

        _selectedScroll = GUILayout.BeginScrollView(_selectedScroll, GUILayout.ExpandHeight(true));
        if (_manualProductsExpanded)
        {
            GUILayout.Label("Choose one or more products to locate.", _helpStyle);
            GUILayout.Space(Ui(8f));
            for (int i = _manualSelections.Count - 1; i >= 0; i--)
            {
                ProductType type = _manualSelections[i];
                ProductData data = GetProductData(type);
                GUILayout.BeginHorizontal(_tileStyle, GUILayout.Height(Ui(72f)));
                DrawProductImage(data != null ? data.productSprite : null, Ui(54f));
                GUILayout.BeginVertical();
                GUILayout.Label(GetProductName(type, data), _selectedNameStyle);
                GUILayout.Label(GetProductBrand(data), _brandStyle);
                GUILayout.EndVertical();
                if (GUILayout.Button("Remove", _buttonStyle, GUILayout.Width(Ui(92f)), GUILayout.Height(Ui(36f))))
                {
                    _manualSelections.RemoveAt(i);
                    GUI.changed = true;
                }
                GUILayout.EndHorizontal();
                GUILayout.Space(Ui(6f));
            }
        }
        GUILayout.Space(Ui(12f));
        GUILayout.Label("API REQUESTS", _sectionStyle);
        ProductLocatorApi.GetRequestSources(_apiRequestSources);
        if (_apiRequestSources.Count == 0)
        {
            GUILayout.Label("No active API requests", _helpStyle);
        }
        else
        {
            for (int i = 0; i < _apiRequestSources.Count; i++)
            {
                string source = _apiRequestSources[i];
                ProductLocatorApi.GetRequestedProductsForSource(source, _apiRequestProducts);
                GUILayout.BeginVertical(_tileStyle);
                GUILayout.BeginHorizontal();
                bool expanded;
                if (!_apiSourceExpanded.TryGetValue(source, out expanded))
                    expanded = true;
                string sourceHeader = (expanded ? "▾  " : "▸  ") + source + "  ·  " +
                    _apiRequestProducts.Count;
                if (GUILayout.Button(sourceHeader, _selectedNameStyle, GUILayout.ExpandWidth(true),
                    GUILayout.Height(Ui(38f))))
                    _apiSourceExpanded[source] = !expanded;
                if (GUILayout.Button("Clear all", _buttonStyle, GUILayout.Width(Ui(96f)), GUILayout.Height(Ui(32f))))
                {
                    ProductLocatorApi.Clear(source);
                    _apiSourceExpanded.Remove(source);
                    _apiRequestSources.RemoveAt(i);
                    _apiRequestProducts.Clear();
                    i--;
                }
                GUILayout.EndHorizontal();
                if (expanded)
                {
                    for (int productIndex = 0; productIndex < _apiRequestProducts.Count; productIndex++)
                    {
                        ProductType type = _apiRequestProducts[productIndex];
                        ProductData data = GetProductData(type);
                        GUILayout.BeginHorizontal(_tileStyle, GUILayout.Height(Ui(72f)));
                        DrawProductImage(data != null ? data.productSprite : null, Ui(54f));
                        GUILayout.BeginVertical();
                        GUILayout.Label(GetProductName(type, data), _selectedNameStyle, GUILayout.ExpandWidth(true));
                        GUILayout.Label(GetProductBrand(data), _brandStyle, GUILayout.ExpandWidth(true));
                        GUILayout.EndVertical();
                        if (GUILayout.Button("Remove", _buttonStyle, GUILayout.Width(Ui(92f)), GUILayout.Height(Ui(36f))))
                        {
                            ProductLocatorApi.RemoveHighlight(source, type);
                            _apiRequestProducts.RemoveAt(productIndex);
                            productIndex--;
                            if (_apiRequestProducts.Count == 0)
                            {
                                _apiSourceExpanded.Remove(source);
                                _apiRequestSources.RemoveAt(i);
                                i--;
                            }
                        }
                        GUILayout.EndHorizontal();
                        GUILayout.Space(Ui(6f));
                    }
                }
                GUILayout.EndVertical();
                GUILayout.Space(Ui(6f));
            }
        }
        GUILayout.EndScrollView();

        if (GUILayout.Button("Clear selected products", _buttonStyle, GUILayout.Height(Ui(40f))))
            _manualSelections.Clear();
        GUILayout.EndVertical();
    }

    private void DrawProductTilesPanel()
    {
        GUILayout.BeginVertical(GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
        GUILayout.Label("PRODUCTS", _centerSectionStyle, GUILayout.ExpandWidth(true), GUILayout.Height(Ui(24f)));
        GUILayout.Space(Ui(8f));
        _tileScroll = GUILayout.BeginScrollView(_tileScroll, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
        // GUILayout inside the scaled GUI.Window uses virtual coordinates.
        // Compute columns from that same width so the grid neither overflows
        // at larger scales nor leaves a blank strip at smaller scales.
        float gridWidth = Mathf.Max(160f, Screen.width / GetUiScale() - 408f);
        float gridGap = 14f;
        float minimumTileWidth = 264f;
        int columns = Mathf.Clamp(Mathf.FloorToInt((gridWidth + gridGap) / minimumTileWidth), 1, 10);
        float tileWidth = (gridWidth - (columns - 1) * gridGap) / columns;
        int column = 0;
        GUILayout.BeginHorizontal(GUILayout.ExpandWidth(true));
        for (int i = 0; i < _productTypes.Count; i++)
        {
            ProductType type = _productTypes[i];
            ProductData data = GetProductData(type);
            if (!MatchesDepartment(data) || !MatchesSearch(type, data))
                continue;

            if (column >= columns)
            {
                GUILayout.EndHorizontal();
                GUILayout.Space(Ui(8f));
                GUILayout.BeginHorizontal();
                column = 0;
            }

            DrawProductTile(type, data, tileWidth);
            column++;
        }
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.EndScrollView();
        GUILayout.EndVertical();
    }

    private void DrawDepartmentDropdown()
    {
        if (GUILayout.Button(_departmentLabels[_departmentFilterIndex] + "   ▼", _dropdownButtonStyle,
            GUILayout.Width(Ui(280f)), GUILayout.Height(Ui(46f))))
            _departmentDropdownOpen = !_departmentDropdownOpen;

        if (Event.current.type == EventType.Repaint)
            _departmentButtonRect = GUILayoutUtility.GetLastRect();
    }

    private void HandleDepartmentDropdownInput()
    {
        Event current = Event.current;
        Rect menu = GetDepartmentMenuRect();
        Rect viewport = GetDepartmentMenuViewportRect();
        bool insideMenu = menu.Contains(current.mousePosition);
        float maxScroll = Mathf.Max(0f, _departmentLabels.Length * Ui(40f) - viewport.height);

        if (current.type == EventType.ScrollWheel && insideMenu)
        {
            _departmentMenuScroll.y = Mathf.Clamp(_departmentMenuScroll.y + current.delta.y * Ui(20f), 0f, maxScroll);
            current.Use();
        }
        else if (current.type == EventType.MouseDown && insideMenu)
        {
            _departmentMenuPointerCaptured = true;
            current.Use();
        }
        else if (_departmentMenuPointerCaptured && current.type == EventType.MouseDrag)
        {
            current.Use();
        }
        else if (_departmentMenuPointerCaptured && current.type == EventType.MouseUp)
        {
            if (viewport.Contains(current.mousePosition))
            {
                for (int i = 0; i < _departmentLabels.Length; i++)
                {
                    if (!GetDepartmentOptionRect(i).Contains(current.mousePosition))
                        continue;

                    _departmentFilterIndex = i;
                    _departmentDropdownOpen = false;
                    break;
                }
            }
            _departmentMenuPointerCaptured = false;
            current.Use();
        }
        else if (current.type == EventType.MouseDown && !insideMenu &&
                 !_departmentButtonRect.Contains(current.mousePosition))
        {
            _departmentDropdownOpen = false;
        }
    }

    private void DrawDepartmentDropdownOverlay()
    {
        if (Event.current.type != EventType.Repaint)
            return;

        Rect menuRect = GetDepartmentMenuRect();
        bool pointerInsideMenu = _cursorOverDepartmentDropdown;
        GUI.Box(menuRect, GUIContent.none, _dropdownMenuStyle);
        Rect viewport = GetDepartmentMenuViewportRect();
        float contentHeight = _departmentLabels.Length * Ui(40f);
        _departmentMenuScroll.y = Mathf.Clamp(_departmentMenuScroll.y, 0f,
            Mathf.Max(0f, contentHeight - viewport.height));

        Vector2 mousePosition = Event.current.mousePosition;
        GUI.BeginGroup(viewport);
        for (int i = 0; i < _departmentLabels.Length; i++)
        {
            Rect optionRect = new Rect(0f, i * Ui(40f) - _departmentMenuScroll.y, viewport.width, Ui(40f));
            Rect windowOptionRect = GetDepartmentOptionRect(i);
            GUIStyle optionStyle = windowOptionRect.Contains(mousePosition) ? _dropdownOptionHoverStyle : _dropdownOptionStyle;
            GUI.Label(optionRect, _departmentLabels[i], optionStyle);
        }
        GUI.EndGroup();
    }

    private Rect GetDepartmentMenuViewportRect()
    {
        Rect menu = GetDepartmentMenuRect();
        float horizontalPadding = _dropdownMenuStyle != null ? _dropdownMenuStyle.padding.left : Ui(8f);
        float verticalPadding = _dropdownMenuStyle != null ? _dropdownMenuStyle.padding.top : Ui(8f);
        return new Rect(menu.x + horizontalPadding, menu.y + verticalPadding,
            menu.width - horizontalPadding * 2f, menu.height - verticalPadding * 2f);
    }

    private Rect GetDepartmentOptionRect(int index)
    {
        Rect viewport = GetDepartmentMenuViewportRect();
        return new Rect(viewport.x, viewport.y + index * Ui(40f) - _departmentMenuScroll.y,
            viewport.width, Ui(40f));
    }

    private Rect GetDepartmentMenuRect()
    {
        return new Rect(_departmentButtonRect.x, _departmentButtonRect.yMax + Ui(5f),
            _departmentButtonRect.width, Ui(236f));
    }

    private void DrawProductTile(ProductType type, ProductData data, float tileWidth)
    {
        GUILayout.BeginVertical(_tileStyle, GUILayout.Width(tileWidth), GUILayout.Height(Ui(296f)));
        GUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();
        DrawProductImage(data != null ? data.productSprite : null,
            Mathf.Min(Ui(156f), Mathf.Max(Ui(72f), tileWidth * 0.52f)));
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(Ui(4f));
        GUILayout.Label(GetProductName(type, data), _tileNameStyle, GUILayout.Height(Ui(42f)));
        GUILayout.Label(GetProductBrand(data), _brandStyle, GUILayout.Height(Ui(24f)));
        GUILayout.Label(data != null ? FormatDepartment(data.productGroup) : "Other", _departmentTextStyle,
            GUILayout.Height(Ui(38f)));
        GUILayout.FlexibleSpace();
        bool selected = _manualSelections.Contains(type);
        GUIStyle buttonStyle = selected ? _selectedButtonStyle : _buttonStyle;
        if (_cursorOverDepartmentDropdown)
            buttonStyle = selected ? _selectedButtonNoHoverStyle : _productButtonNoHoverStyle;
        if (GUILayout.Button(selected ? "✓  Selected" : "＋  Add product", buttonStyle,
            GUILayout.Height(Ui(36f))))
        {
            if (selected)
                _manualSelections.Remove(type);
            else
                _manualSelections.Add(type);
        }
        GUILayout.EndVertical();
    }

    private void DrawProductImage(Sprite sprite, float size)
    {
        Rect rect = GUILayoutUtility.GetRect(size, size, GUILayout.Width(size), GUILayout.Height(size));
        GUI.color = new Color(1f, 1f, 1f, sprite != null && sprite.texture != null ? 1f : 0.15f);
        if (sprite != null && sprite.texture != null)
        {
            Rect uv = GetSpriteUv(sprite);
            float aspect = uv.height > 0f ? uv.width / uv.height : 1f;
            Rect imageRect = FitRectToAspect(rect, aspect);
            GUI.DrawTextureWithTexCoords(imageRect, sprite.texture, uv, true);
        }
        else
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = Color.white;
    }

    private static Rect FitRectToAspect(Rect container, float aspect)
    {
        aspect = Mathf.Max(0.01f, aspect);
        float width = container.width;
        float height = container.height;
        if (width / height > aspect)
            width = height * aspect;
        else
            height = width / aspect;

        return new Rect(container.center.x - width * 0.5f,
            container.center.y - height * 0.5f, width, height);
    }

    private bool MatchesDepartment(ProductData data)
    {
        return _departmentFilterIndex <= 0 || (data != null && data.productGroup == _departmentGroups[_departmentFilterIndex - 1]);
    }

    private bool MatchesSearch(ProductType type, ProductData data)
    {
        if (string.IsNullOrEmpty(_search))
            return true;

        if (!string.Equals(_normalizedSearchSource, _search, StringComparison.Ordinal))
        {
            _normalizedSearchSource = _search;
            _normalizedSearch = _search.Trim();
        }

        if (GetProductName(type, data).IndexOf(_normalizedSearch, StringComparison.OrdinalIgnoreCase) >= 0 ||
            GetProductBrand(data).IndexOf(_normalizedSearch, StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        string typeName;
        if (!_productTypeSearchNames.TryGetValue(type, out typeName))
        {
            typeName = type.ToString();
            _productTypeSearchNames[type] = typeName;
        }
        return typeName.IndexOf(_normalizedSearch, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private ProductData GetProductData(ProductType type)
    {
        ProductData data;
        if (_productData.TryGetValue(type, out data))
            return data;

        ProductPool pool = ProductPool.Instance;
        if (pool == null)
            return null;

        try
        {
            data = pool.GetProductData(type);
            if (data != null)
                _productData[type] = data;
            return data;
        }
        catch
        {
            return null;
        }
    }

    private void CacheProductData()
    {
        ProductPool pool = ProductPool.Instance;
        if (pool == null)
            return;

        for (int i = 0; i < _productTypes.Count; i++)
        {
            try
            {
                ProductData data = pool.GetProductData(_productTypes[i]);
                if (data != null)
                    _productData[_productTypes[i]] = data;
            }
            catch
            {
                // Some enum values are non-shelf products and do not have catalog data.
            }
        }
    }

    private void CacheStoreProductTypes()
    {
        HashSet<ProductType> labeledStoreTypes = new HashSet<ProductType>();
        for (int shelfIndex = 0; shelfIndex < _shelves.Length; shelfIndex++)
        {
            Shelf shelf = _shelves[shelfIndex];
            if (shelf == null || !shelf.isActiveAndEnabled || !shelf.gameObject.activeInHierarchy)
                continue;

            try
            {
                if (!shelf.HasProductLabel())
                    continue;

                // This mirrors LabeledStockManager.OnPriceTagAdded: an empty
                // but labeled shelf keeps its assignment in PreviousContainedType.
                ProductType type = shelf.ContainedProductType();
                if (type == ProductType.NONE)
                    type = shelf.PreviousContainedProductType();
                if (type != ProductType.NONE)
                    labeledStoreTypes.Add(type);
            }
            catch
            {
                // Ignore shelf labels while the store scene is initializing.
            }
        }

        List<ProductType> catalog = new List<ProductType>();
        foreach (ProductType type in Enum.GetValues(typeof(ProductType)))
        {
            if (!labeledStoreTypes.Contains(type))
                continue;

            ProductData data = GetProductData(type);
            if (data == null || data.productGroup == ProductGroup.EQUIPMENTS ||
                data.productGroup == ProductGroup.NONE)
                continue;

            catalog.Add(type);
        }

        catalog.Sort(CompareProductsByDepartmentAndShelfType);

        _productTypes.Clear();
        _productTypes.AddRange(catalog);
        RefreshDepartmentOptions();
    }

    private void RefreshDepartmentOptions()
    {
        ProductGroup? previousSelection = _departmentFilterIndex > 0 &&
            _departmentFilterIndex <= _departmentGroups.Length
            ? _departmentGroups[_departmentFilterIndex - 1]
            : (ProductGroup?)null;
        HashSet<ProductGroup> availableGroups = new HashSet<ProductGroup>();
        for (int i = 0; i < _productTypes.Count; i++)
        {
            ProductData data = GetProductData(_productTypes[i]);
            if (data != null && data.productGroup != ProductGroup.EQUIPMENTS &&
                data.productGroup != ProductGroup.NONE)
                availableGroups.Add(data.productGroup);
        }

        List<ProductGroup> sortedGroups = new List<ProductGroup>(availableGroups);
        sortedGroups.Sort((left, right) =>
            string.Compare(FormatDepartment(left), FormatDepartment(right), StringComparison.OrdinalIgnoreCase));
        _departmentGroups = sortedGroups.ToArray();
        _departmentLabels = new string[_departmentGroups.Length + 1];
        _departmentLabels[0] = "All departments";
        _departmentFilterIndex = 0;
        for (int i = 0; i < _departmentGroups.Length; i++)
        {
            _departmentLabels[i + 1] = FormatDepartment(_departmentGroups[i]);
            if (previousSelection.HasValue && _departmentGroups[i] == previousSelection.Value)
                _departmentFilterIndex = i + 1;
        }
    }

    private int CompareProductsByDepartmentAndShelfType(ProductType first, ProductType second)
    {
        ProductData firstData = GetProductData(first);
        ProductData secondData = GetProductData(second);
        if (firstData == null || secondData == null)
            return first.CompareTo(second);

        int comparison = string.Compare(FormatDepartment(firstData.productGroup),
            FormatDepartment(secondData.productGroup), StringComparison.OrdinalIgnoreCase);
        if (comparison != 0)
            return comparison;

        comparison = firstData.shelfType.CompareTo(secondData.shelfType);
        return comparison != 0 ? comparison : first.CompareTo(second);
    }

    private string GetProductName(ProductType type, ProductData data)
    {
        string cachedName;
        if (_productNames.TryGetValue(type, out cachedName))
            return cachedName;

        string name = FormatProduct(type);
        if (data != null)
        {
            try
            {
                string localized = DFTGames.Localization.Locale.GetWord(type.ToString());
                if (!string.IsNullOrEmpty(localized) && !string.Equals(localized, type.ToString(), StringComparison.OrdinalIgnoreCase))
                    name = localized;
            }
            catch
            {
                // Fall back to the enum name when localization data is unavailable.
            }
        }

        if (data != null)
            _productNames[type] = name;
        return name;
    }

    private static string GetProductBrand(ProductData data)
    {
        return data != null && !string.IsNullOrWhiteSpace(data.brand) ? data.brand : "No brand";
    }

    private static string FormatDepartment(ProductGroup group)
    {
        string name;
        if (DepartmentNames.TryGetValue(group, out name))
            return name;

        string value = group.ToString();
        name = value.Length > 0 ? char.ToUpperInvariant(value[0]) + value.Substring(1).ToLowerInvariant() : value;
        DepartmentNames[group] = name;
        return name;
    }

    private void EnsureGuiStyles()
    {
        // GUI.matrix scales the picker, including these styles. Their values do
        // not change with UI scale, so keep the styles and native textures.
        if (_titleStyle != null)
            return;

        _titleStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = UiFont(30),
            fontStyle = FontStyle.Bold,
            wordWrap = true,
            normal = { textColor = new Color(0.94f, 0.97f, 1f) }
        };
        _subtitleStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = UiFont(16),
            wordWrap = true,
            normal = { textColor = new Color(0.67f, 0.73f, 0.82f) }
        };
        _sectionStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = UiFont(16),
            fontStyle = FontStyle.Bold,
            normal = { textColor = new Color(0.43f, 0.75f, 1f) }
        };
        _centerSectionStyle = new GUIStyle(_sectionStyle)
        {
            alignment = TextAnchor.MiddleCenter
        };
        _panelStyle = new GUIStyle(GUI.skin.box)
        {
            padding = new RectOffset(Ui(16), Ui(16), Ui(16), Ui(16)),
            normal = { background = GUI.skin.box.normal.background, textColor = new Color(0.9f, 0.93f, 0.98f) }
        };
        _tileStyle = new GUIStyle(GUI.skin.box)
        {
            padding = new RectOffset(Ui(12), Ui(12), Ui(10), Ui(10)),
            margin = new RectOffset(Ui(4), Ui(4), Ui(4), Ui(4))
        };
        _tileNameStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = UiFont(17),
            fontStyle = FontStyle.Bold,
            wordWrap = true,
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = new Color(0.94f, 0.97f, 1f) }
        };
        _selectedNameStyle = new GUIStyle(_tileNameStyle)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = UiFont(16)
        };
        _brandStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = UiFont(15),
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = Color.cyan }
        };
        Texture2D buttonNormal = CreateGuiTexture(new Color(0.09f, 0.14f, 0.2f, 1f));
        Texture2D buttonHover = CreateGuiTexture(new Color(0.12f, 0.24f, 0.34f, 1f));
        Texture2D buttonActive = CreateGuiTexture(new Color(0.07f, 0.36f, 0.53f, 1f));
        _buttonStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = UiFont(15),
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            padding = new RectOffset(Ui(12), Ui(12), Ui(7), Ui(7)),
            margin = new RectOffset(Ui(3), Ui(3), Ui(3), Ui(3)),
            border = new RectOffset(Ui(1), Ui(1), Ui(1), Ui(1)),
            normal = { background = buttonNormal, textColor = new Color(0.86f, 0.92f, 1f) },
            hover = { background = buttonHover, textColor = Color.white },
            active = { background = buttonActive, textColor = Color.white },
            focused = { background = buttonHover, textColor = Color.white }
        };
        _selectedButtonStyle = new GUIStyle(_buttonStyle)
        {
            fontSize = UiFont(16),
            fontStyle = FontStyle.Bold,
            normal = { background = buttonActive, textColor = Color.white },
            hover = { background = buttonHover, textColor = Color.white },
            active = { background = buttonActive, textColor = Color.white }
        };
        _productButtonNoHoverStyle = CreateNoHoverStyle(_buttonStyle);
        _selectedButtonNoHoverStyle = CreateNoHoverStyle(_selectedButtonStyle);
        _searchBarStyle = new GUIStyle(GUIStyle.none)
        {
            margin = new RectOffset(0, 0, 0, 0),
            padding = new RectOffset(0, 0, 0, 0)
        };
        Texture2D searchFieldBackground = CreateGuiTexture(new Color(0.09f, 0.12f, 0.16f, 1f));
        _searchFieldStyle = new GUIStyle(GUI.skin.textField)
        {
            fontSize = UiFont(20),
            padding = new RectOffset(Ui(12), Ui(12), Ui(6), Ui(6)),
            margin = new RectOffset(0, 0, 0, 0),
            alignment = TextAnchor.MiddleLeft,
            normal = { background = searchFieldBackground, textColor = new Color(0.94f, 0.97f, 1f) },
            hover = { background = searchFieldBackground, textColor = new Color(0.94f, 0.97f, 1f) },
            focused = { background = CreateGuiTexture(new Color(0.11f, 0.15f, 0.2f, 1f)), textColor = Color.white }
        };
        _clearButtonStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = UiFont(18),
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            margin = new RectOffset(0, 0, 0, 0),
            padding = new RectOffset(0, 0, 0, 0),
            normal = { background = CreateGuiTexture(new Color(0.15f, 0.2f, 0.27f, 1f)), textColor = new Color(0.75f, 0.82f, 0.9f) },
            hover = { background = CreateGuiTexture(new Color(0.24f, 0.31f, 0.4f, 1f)), textColor = Color.white },
            active = { background = CreateGuiTexture(new Color(0.1f, 0.44f, 0.68f, 1f)), textColor = Color.white }
        };
        _dropdownButtonStyle = new GUIStyle(_buttonStyle)
        {
            fontSize = UiFont(17),
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            margin = new RectOffset(0, 0, 0, 0),
            padding = new RectOffset(Ui(14), Ui(14), 0, 0),
            normal = { background = buttonNormal, textColor = new Color(0.9f, 0.94f, 1f) },
            hover = { background = buttonHover, textColor = Color.white },
            active = { background = buttonActive, textColor = Color.white }
        };
        _dropdownMenuStyle = new GUIStyle(GUI.skin.box)
        {
            padding = new RectOffset(Ui(8), Ui(8), Ui(8), Ui(8)),
            normal = { background = CreateGuiTexture(new Color(0.035f, 0.05f, 0.075f, 0.98f)) }
        };
        _dropdownOptionStyle = new GUIStyle(_buttonStyle)
        {
            fontSize = UiFont(16),
            alignment = TextAnchor.MiddleCenter,
            padding = new RectOffset(Ui(12), Ui(8), Ui(4), Ui(4)),
            normal = { background = buttonNormal, textColor = new Color(0.83f, 0.88f, 0.95f) },
            hover = { background = buttonHover, textColor = Color.white },
            active = { background = buttonActive, textColor = Color.white }
        };
        _dropdownOptionHoverStyle = new GUIStyle(_dropdownOptionStyle);
        _dropdownOptionHoverStyle.normal.background = _dropdownOptionStyle.hover.background;
        _dropdownOptionHoverStyle.normal.textColor = _dropdownOptionStyle.hover.textColor;
        _helpStyle = new GUIStyle(_subtitleStyle) { fontSize = UiFont(15) };
        _departmentTextStyle = new GUIStyle(_helpStyle) { alignment = TextAnchor.MiddleCenter };
    }

    private static GUIStyle CreateNoHoverStyle(GUIStyle source)
    {
        GUIStyle style = new GUIStyle(source);
        style.hover.background = style.normal.background;
        style.hover.textColor = style.normal.textColor;
        style.active.background = style.normal.background;
        style.active.textColor = style.normal.textColor;
        style.focused.background = style.normal.background;
        style.focused.textColor = style.normal.textColor;
        return style;
    }

    private float GetUiScale()
    {
        return _uiScale != null ? Mathf.Clamp(_uiScale.Value, 0.5f, 2f) : 1f;
    }

    private float Ui(float value)
    {
        return value;
    }

    private int Ui(int value)
    {
        return value;
    }

    private int UiFont(int value)
    {
        return value;
    }

    private static Texture2D CreateGuiTexture(Color color)
    {
        Texture2D texture = new Texture2D(1, 1, TextureFormat.RGBA32, false)
        {
            hideFlags = HideFlags.HideAndDontSave,
            filterMode = FilterMode.Point
        };
        texture.SetPixel(0, 0, color);
        texture.Apply();
        return texture;
    }

    private void SetWindowOpen(bool open)
    {
        if (_windowOpen == open)
            return;

        _windowOpen = open;
        LogDebug("Product picker " + (open ? "opened" : "closed") + ".");
        ProductLocatorApi.SetPickerOpen(open);
        if (open)
        {
            CacheShelves();
            CacheStoreProductTypes();
            CacheProductData();

            _previousCursorLock = Cursor.lockState;
            _previousCursorVisible = Cursor.visible;
            _blockedInputManager = InputManager.Instance;
            if (_blockedInputManager != null)
                InvokeInputManagerAction("DisableAllActions");

            _blockedPlayerMove = PlayerMove.Instance;
            if (_blockedPlayerMove != null)
            {
                _previousMovementLocked = _blockedPlayerMove.MovementLocked;
                _blockedPlayerMove.MovementLocked = true;
            }

            _blockedPlayerLook = PlayerLook.Instance;
            if (_blockedPlayerLook != null)
            {
                _previousRotationLocked = _blockedPlayerLook.RotationLocked;
                _blockedPlayerLook.RotationLocked = true;
            }

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
        else
        {
            RestoreGameplayInput();
        }
    }

    private void RestoreGameplayInput()
    {
        if (_blockedInputManager != null)
            InvokeInputManagerAction("EnableAllActions");
        if (_blockedPlayerMove != null)
            _blockedPlayerMove.MovementLocked = _previousMovementLocked;
        if (_blockedPlayerLook != null)
            _blockedPlayerLook.RotationLocked = _previousRotationLocked;

        Cursor.lockState = _previousCursorLock;
        Cursor.visible = _previousCursorVisible;
        _blockedInputManager = null;
        _blockedPlayerMove = null;
        _blockedPlayerLook = null;
    }

    private void InvokeInputManagerAction(string methodName)
    {
        MethodInfo method = typeof(InputManager).GetMethod(methodName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (method == null)
            return;

        try
        {
            method.Invoke(_blockedInputManager, null);
        }
        catch (Exception exception)
        {
            Logger.LogWarning("Could not " + methodName + ": " + exception.GetBaseException().Message);
        }
    }


}
