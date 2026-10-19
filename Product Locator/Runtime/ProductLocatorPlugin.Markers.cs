using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

public sealed partial class ProductLocatorPlugin
{
    private const float MarkerVisibilityRefreshInterval = 0.2f;
    private const float VisibleColorRangeMultiplier = 3f;
    private readonly RaycastHit[] _markerVisibilityHits = new RaycastHit[64];
    private float _nextMarkerVisibilityCheck;

    private void DrawShelfIcons()
    {
        // Unity calls OnGUI for layout, input, and repaint events. Projection and
        // draw work is only useful during repaint, which avoids repeating it for
        // every other IMGUI event in the frame.
        if (Event.current.type != EventType.Repaint || _markerCanvas == null)
            return;

        EnsureMarkerViews();
        if (IsComputerUiFocused())
        {
            SetMarkerViewsVisible(false, "computer UI focus");
            return;
        }

        if (_shelfTargets.Count == 0)
        {
            SetMarkerViewsVisible(false, "no active shelf targets");
            return;
        }

        Camera camera = Camera.main;
        if (camera == null)
        {
            SetMarkerViewsVisible(false, "no main camera");
            return;
        }

        float baseSize = Mathf.Clamp(_iconSize.Value, 50, 150);
        Vector3 cameraPosition = camera.transform.position;
        bool refreshVisibility = Time.unscaledTime >= _nextMarkerVisibilityCheck;
        if (refreshVisibility)
            _nextMarkerVisibilityCheck = Time.unscaledTime + MarkerVisibilityRefreshInterval;

        for (int i = _shelfTargets.Count - 1; i >= 0; i--)
        {
            ShelfIconTarget target = _shelfTargets[i];
            MarkerView view = _markerViews[i];
            if (target.Shelf == null || target.Texture == null)
            {
                RecordMarkerRenderState(view, "hidden: missing shelf or icon");
                view.Root.gameObject.SetActive(false);
                continue;
            }

            Vector3 screen = camera.WorldToScreenPoint(target.Bounds.center);
            float x = screen.x;
            if (screen.z <= 0f)
            {
                RecordMarkerRenderState(view, "hidden: behind camera");
                view.Root.gameObject.SetActive(false);
                continue;
            }

            float projectedShelfHeight = GetProjectedShelfHeight(camera, target.Bounds);
            float iconSize = Mathf.Clamp(Mathf.Max(baseSize, projectedShelfHeight * 0.16f), baseSize, baseSize * 2f);
            bool? requestedShowDistance;
            bool showDistance = _activeTargetShowDistance.TryGetValue(target.ProductType, out requestedShowDistance) &&
                requestedShowDistance.HasValue ? requestedShowDistance.Value : _showMarkerDistance.Value;
            float labelHeight = showDistance ? Mathf.Clamp(iconSize * 0.22f, 18f, 26f) : 0f;
            float y = Screen.height - screen.y - target.StackIndex * (baseSize + 6f + labelHeight);
            float halfIconSize = iconSize * 0.5f;
            if (x + halfIconSize < 0f || x - halfIconSize > Screen.width ||
                y + halfIconSize + labelHeight < 0f || y - halfIconSize > Screen.height)
            {
                RecordMarkerRenderState(view, "hidden: entire icon off-screen");
                view.Root.gameObject.SetActive(false);
                continue;
            }

            MarkerVisibilityState visibilityState = GetMarkerVisibilityState(target);
            if (refreshVisibility)
                UpdateMarkerVisibility(target, visibilityState, HasClearLineOfSight(camera, target));

            view.Root.anchoredPosition = new Vector2(x - Screen.width * 0.5f,
                Screen.height * 0.5f - y - labelHeight * 0.5f);
            float? requestedBackgroundOpacity;
            bool hasRequestedOpacity = _activeTargetBackgroundOpacities.TryGetValue(target.ProductType,
                out requestedBackgroundOpacity) && requestedBackgroundOpacity.HasValue;
            Color? requestedBackgroundColor;
            Color backgroundColor = _activeTargetBackgroundColors.TryGetValue(target.ProductType,
                out requestedBackgroundColor) && requestedBackgroundColor.HasValue
                ? requestedBackgroundColor.Value : _backgroundColor.Value;
            if (visibilityState.HasClearLineOfSight)
            {
                Color? requestedVisibleBackgroundColor;
                backgroundColor = _activeTargetVisibleBackgroundColors.TryGetValue(target.ProductType,
                    out requestedVisibleBackgroundColor) && requestedVisibleBackgroundColor.HasValue
                    ? requestedVisibleBackgroundColor.Value : _visibleBackgroundColor.Value;
            }
            if (hasRequestedOpacity)
                backgroundColor.a = Mathf.Clamp01(requestedBackgroundOpacity.Value);
            Color markerColor = target.MarkerColor.HasValue ? target.MarkerColor.Value : _markerColor.Value;
            if (visibilityState.HasClearLineOfSight)
            {
                Color? requestedVisibleColor;
                markerColor = _activeTargetVisibleColors.TryGetValue(target.ProductType, out requestedVisibleColor) &&
                    requestedVisibleColor.HasValue ? requestedVisibleColor.Value : _visibleMarkerColor.Value;
            }
            int distanceMeters = showDistance
                ? Mathf.RoundToInt((target.Bounds.center - cameraPosition).magnitude)
                : 0;
            ApplyMarkerAppearance(view, target, iconSize, backgroundColor, markerColor,
                showDistance, distanceMeters);
            RecordMarkerRenderState(view, "visible on-screen");
            view.Root.gameObject.SetActive(true);
        }
    }

    private static void ApplyMarkerAppearance(MarkerView view, ShelfIconTarget target, float iconSize,
        Color backgroundColor, Color markerColor, bool showDistance, int distanceMeters)
    {
        // Position still follows the camera every repaint. The other UI values
        // usually stay unchanged, so avoid repeated native transform writes.
        bool iconSizeChanged = !view.HasAppliedLayout || view.AppliedIconSize != iconSize;
        float labelHeight = showDistance ? Mathf.Clamp(iconSize * 0.22f, 18f, 26f) : 0f;
        if (iconSizeChanged || view.AppliedDistanceVisible != showDistance)
        {
            view.Root.sizeDelta = new Vector2(iconSize, iconSize + labelHeight);
        }
        if (iconSizeChanged)
        {
            view.AppliedIconSize = iconSize;
            view.DistanceLabel.fontSize = Mathf.RoundToInt(Mathf.Clamp(iconSize * 0.18f, 16f, 22f));
        }

        float border = Mathf.Clamp(iconSize * 0.045f, 2f, 5f);
        if (!view.HasAppliedLayout || view.AppliedBorderSize != border)
        {
            view.BorderTop.rectTransform.sizeDelta = new Vector2(0f, border);
            view.BorderBottom.rectTransform.sizeDelta = new Vector2(0f, border);
            view.BorderLeft.rectTransform.sizeDelta = new Vector2(border, 0f);
            view.BorderRight.rectTransform.sizeDelta = new Vector2(border, 0f);
            view.AppliedBorderSize = border;
        }

        float iconPadding = Mathf.Clamp(iconSize * 0.16f, 7f, 18f);
        float contentSize = iconSize - iconPadding * 2f;
        float aspect = target.Uv.height > 0f
            ? Mathf.Max(0.01f, target.Uv.width / target.Uv.height)
            : 1f;
        Vector2 iconContentSize = aspect > 1f
            ? new Vector2(contentSize, contentSize / aspect)
            : new Vector2(contentSize * aspect, contentSize);
        if (!view.HasAppliedLayout || !view.AppliedIconContentSize.Equals(iconContentSize))
        {
            view.Icon.rectTransform.sizeDelta = iconContentSize;
            view.AppliedIconContentSize = iconContentSize;
        }
        if (iconSizeChanged || view.AppliedDistanceVisible != showDistance)
        {
            view.Icon.rectTransform.anchoredPosition = showDistance
                ? new Vector2(0f, labelHeight * 0.5f) : Vector2.zero;
            view.DistanceLabel.rectTransform.sizeDelta = new Vector2(-8f, labelHeight);
        }
        if (view.AppliedDistanceVisible != showDistance)
        {
            view.DistanceLabel.gameObject.SetActive(showDistance);
            view.AppliedDistanceVisible = showDistance;
        }
        if (showDistance && view.DistanceLabel != null &&
            (!view.HasAppliedDistanceValue || view.AppliedDistanceMeters != distanceMeters))
        {
            view.AppliedDistanceText = distanceMeters + " m";
            view.DistanceLabel.text = view.AppliedDistanceText;
            view.AppliedDistanceMeters = distanceMeters;
            view.HasAppliedDistanceValue = true;
        }
        view.HasAppliedLayout = true;

        if (!view.HasAppliedColors || !view.AppliedBackgroundColor.Equals(backgroundColor))
        {
            view.Background.color = backgroundColor;
            view.AppliedBackgroundColor = backgroundColor;
        }
        if (!view.HasAppliedColors || !view.AppliedBorderColor.Equals(markerColor))
        {
            view.BorderTop.color = markerColor;
            view.BorderBottom.color = markerColor;
            view.BorderLeft.color = markerColor;
            view.BorderRight.color = markerColor;
            view.AppliedBorderColor = markerColor;
        }
        view.HasAppliedColors = true;

        if (!view.HasAppliedIcon || view.AppliedTexture != target.Texture)
        {
            view.Icon.texture = target.Texture;
            view.AppliedTexture = target.Texture;
        }
        if (!view.HasAppliedIcon || !view.AppliedUv.Equals(target.Uv))
        {
            view.Icon.uvRect = target.Uv;
            view.AppliedUv = target.Uv;
        }
        view.HasAppliedIcon = true;
    }

    private bool HasClearLineOfSight(Camera camera, ShelfIconTarget target)
    {
        Vector3 origin = camera.transform.position;
        Vector3 offset = target.Bounds.center - origin;
        float distance = offset.magnitude;
        if (distance <= 0.05f)
            return true;
        if (target.Shelf == null || distance > target.Shelf.GetInteractionDistance() * VisibleColorRangeMultiplier)
            return false;

        int hitCount = Physics.RaycastNonAlloc(origin, offset / distance, _markerVisibilityHits,
            distance - 0.025f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        Transform targetShelf = target.Shelf != null ? target.Shelf.transform : null;
        for (int i = 0; i < hitCount; i++)
        {
            Collider collider = _markerVisibilityHits[i].collider;
            if (collider == null)
                continue;

            Transform hitTransform = collider.transform;
            if (targetShelf != null && (hitTransform == targetShelf || hitTransform.IsChildOf(targetShelf)))
                continue;

            Bounds fixtureBounds = target.Bounds;
            fixtureBounds.Expand(0.25f);
            if (fixtureBounds.Contains(_markerVisibilityHits[i].point))
                continue;

            Shelf hitShelf = collider.GetComponentInParent<Shelf>();
            if (hitShelf != null && IsPartOfSameShelfFixture(target.Shelf, hitShelf))
                continue;

            Product hitProduct = collider.GetComponentInParent<Product>();
            if (hitProduct != null && hitProduct.Data != null &&
                hitProduct.Data.type == target.ProductType && target.Bounds.Contains(hitProduct.transform.position))
                continue;

            return false;
        }

        return true;
    }

    private static bool IsPartOfSameShelfFixture(Shelf targetShelf, Shelf hitShelf)
    {
        if (targetShelf == null || hitShelf == null)
            return false;
        if (targetShelf == hitShelf)
            return true;
        if ((targetShelf.transform.position - hitShelf.transform.position).sqrMagnitude > 9f)
            return false;

        Transform targetAncestor = targetShelf.transform;
        for (int targetDepth = 0; targetAncestor != null && targetDepth <= 2;
            targetDepth++, targetAncestor = targetAncestor.parent)
        {
            Transform hitAncestor = hitShelf.transform;
            for (int hitDepth = 0; hitAncestor != null && hitDepth <= 2;
                hitDepth++, hitAncestor = hitAncestor.parent)
            {
                if (targetAncestor == hitAncestor && targetDepth + hitDepth <= 3)
                    return true;
            }
        }

        return false;
    }

    private MarkerVisibilityState GetMarkerVisibilityState(ShelfIconTarget target)
    {
        Dictionary<ProductType, MarkerVisibilityState> shelfStates;
        if (!_markerVisibilityByShelf.TryGetValue(target.Shelf, out shelfStates))
        {
            shelfStates = new Dictionary<ProductType, MarkerVisibilityState>();
            _markerVisibilityByShelf[target.Shelf] = shelfStates;
        }

        MarkerVisibilityState state;
        if (!shelfStates.TryGetValue(target.ProductType, out state))
        {
            state = new MarkerVisibilityState();
            shelfStates[target.ProductType] = state;
        }

        return state;
    }

    private void UpdateMarkerVisibility(ShelfIconTarget target, MarkerVisibilityState state, bool isVisible)
    {
        if (!state.Initialized)
        {
            state.HasClearLineOfSight = isVisible;
            state.Initialized = true;
            state.ClearSamples = 0;
            state.OccludedSamples = 0;
            if (_debugLogging != null && _debugLogging.Value)
                LogDebug("LOS initialized for " + DescribeShelfTargets(new[] { target }) + ": " +
                    (isVisible ? "clear" : "blocked") + ".");
            return;
        }

        if (isVisible)
        {
            state.ClearSamples++;
            state.OccludedSamples = 0;
            if (state.ClearSamples >= 2)
            {
                if (!state.HasClearLineOfSight && _debugLogging != null && _debugLogging.Value)
                    LogDebug("LOS changed to clear for " + DescribeShelfTargets(new[] { target }) + ".");
                state.HasClearLineOfSight = true;
                state.ClearSamples = 0;
            }
        }
        else
        {
            state.OccludedSamples++;
            state.ClearSamples = 0;
            if (state.OccludedSamples >= 2)
            {
                if (state.HasClearLineOfSight && _debugLogging != null && _debugLogging.Value)
                    LogDebug("LOS changed to blocked for " + DescribeShelfTargets(new[] { target }) + ".");
                state.HasClearLineOfSight = false;
                state.OccludedSamples = 0;
            }
        }
    }

    private void CreateMarkerCanvas()
    {
        GameObject canvasObject = new GameObject("ProductLocatorMarkerCanvas", typeof(RectTransform), typeof(Canvas));
        canvasObject.hideFlags = HideFlags.HideAndDontSave;
        DontDestroyOnLoad(canvasObject);
        _markerCanvas = canvasObject.GetComponent<Canvas>();
        _markerCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _markerCanvas.overrideSorting = true;
        _markerCanvas.sortingOrder = -1000;
    }

    private bool IsComputerUiFocused()
    {
        for (int i = 0; i < _computerRaycasters.Length; i++)
        {
            GraphicRaycaster raycaster = _computerRaycasters[i];
            if (raycaster != null && raycaster.enabled && raycaster.gameObject.activeInHierarchy)
                return true;
        }

        return false;
    }

    private void CacheComputerUiRaycasters()
    {
        ComputerUI[] computerUis = FindObjectsByType<ComputerUI>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        if (_computerRaycasterField == null)
            _computerRaycasterField = typeof(FocusableUI).GetField("raycaster",
                BindingFlags.Instance | BindingFlags.NonPublic);

        if (_computerRaycasterField == null || computerUis.Length == 0)
        {
            _computerRaycasters = new GraphicRaycaster[0];
            return;
        }

        List<GraphicRaycaster> raycasters = new List<GraphicRaycaster>(computerUis.Length);
        for (int i = 0; i < computerUis.Length; i++)
        {
            ComputerUI computerUi = computerUis[i];
            if (computerUi == null)
                continue;

            GraphicRaycaster raycaster = _computerRaycasterField.GetValue(computerUi) as GraphicRaycaster;
            if (raycaster != null)
                raycasters.Add(raycaster);
        }

        _computerRaycasters = raycasters.ToArray();
    }

    private void EnsureMarkerViews()
    {
        bool unchanged = _markerViews.Count == _shelfTargets.Count;
        if (unchanged)
        {
            for (int i = 0; i < _shelfTargets.Count; i++)
            {
                ShelfIconTarget existing = _markerViews[i].Target;
                ShelfIconTarget current = _shelfTargets[i];
                if (existing != null && current != null &&
                    existing.Shelf == current.Shelf &&
                    existing.ProductType == current.ProductType)
                {
                    _markerViews[i].Target = current;
                    continue;
                }
                unchanged = false;
                break;
            }
        }
        if (unchanged)
            return;

        // Reuse views by shelf/product identity even when a refresh changes their
        // order, count, empty-shelf state, or target data object. Rebuilding the
        // whole canvas made unaffected markers flash during cache reconciliation.
        List<MarkerView> unusedViews = new List<MarkerView>(_markerViews);
        List<MarkerView> orderedViews = new List<MarkerView>(_shelfTargets.Count);
        for (int i = 0; i < _shelfTargets.Count; i++)
        {
            ShelfIconTarget target = _shelfTargets[i];
            MarkerView match = null;
            for (int viewIndex = 0; viewIndex < unusedViews.Count; viewIndex++)
            {
                ShelfIconTarget existing = unusedViews[viewIndex].Target;
                if (existing != null && existing.Shelf == target.Shelf &&
                    existing.ProductType == target.ProductType)
                {
                    match = unusedViews[viewIndex];
                    unusedViews.RemoveAt(viewIndex);
                    break;
                }
            }

            if (match == null)
                match = CreateMarkerView(target);
            else
                match.Target = target;
            orderedViews.Add(match);
        }

        for (int i = 0; i < unusedViews.Count; i++)
        {
            if (unusedViews[i].Root != null)
                Destroy(unusedViews[i].Root.gameObject);
        }

        _markerViews.Clear();
        _markerViews.AddRange(orderedViews);
    }

    private MarkerView CreateMarkerView(ShelfIconTarget target)
    {
        GameObject markerObject = new GameObject("ProductMarker", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
        markerObject.transform.SetParent(_markerCanvas.transform, false);
        RectTransform root = markerObject.GetComponent<RectTransform>();
        root.anchorMin = new Vector2(0.5f, 0.5f);
        root.anchorMax = new Vector2(0.5f, 0.5f);
        root.pivot = new Vector2(0.5f, 0.5f);

        RawImage background = markerObject.GetComponent<RawImage>();
        background.texture = Texture2D.whiteTexture;
        background.raycastTarget = false;
        RawImage borderTop = CreateMarkerBorder(markerObject.transform, "BorderTop",
            new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f));
        RawImage borderBottom = CreateMarkerBorder(markerObject.transform, "BorderBottom",
            Vector2.zero, new Vector2(1f, 0f), new Vector2(0.5f, 0f));
        RawImage borderLeft = CreateMarkerBorder(markerObject.transform, "BorderLeft",
            Vector2.zero, new Vector2(0f, 1f), new Vector2(0f, 0.5f));
        RawImage borderRight = CreateMarkerBorder(markerObject.transform, "BorderRight",
            new Vector2(1f, 0f), Vector2.one, new Vector2(1f, 0.5f));

        GameObject iconObject = new GameObject("ProductIcon", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
        iconObject.transform.SetParent(markerObject.transform, false);
        RectTransform iconRect = iconObject.GetComponent<RectTransform>();
        iconRect.anchorMin = new Vector2(0.5f, 0.5f);
        iconRect.anchorMax = new Vector2(0.5f, 0.5f);
        iconRect.pivot = new Vector2(0.5f, 0.5f);
        iconRect.anchoredPosition = Vector2.zero;
        RawImage icon = iconObject.GetComponent<RawImage>();
        icon.raycastTarget = false;

        GameObject distanceObject = new GameObject("DistanceLabel", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        distanceObject.transform.SetParent(markerObject.transform, false);
        RectTransform distanceRect = distanceObject.GetComponent<RectTransform>();
        distanceRect.anchorMin = new Vector2(0f, 0f);
        distanceRect.anchorMax = new Vector2(1f, 0f);
        distanceRect.pivot = new Vector2(0.5f, 0f);
        distanceRect.anchoredPosition = new Vector2(0f, 5f);
        distanceRect.sizeDelta = new Vector2(-8f, 22f);
        Text distanceLabel = distanceObject.GetComponent<Text>();
        distanceLabel.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        distanceLabel.fontSize = 14;
        distanceLabel.fontStyle = FontStyle.Bold;
        distanceLabel.alignment = TextAnchor.MiddleCenter;
        distanceLabel.horizontalOverflow = HorizontalWrapMode.Overflow;
        distanceLabel.verticalOverflow = VerticalWrapMode.Overflow;
        distanceLabel.color = Color.white;
        distanceLabel.raycastTarget = false;
        distanceObject.SetActive(false);

        return new MarkerView
        {
            Target = target,
            Root = root,
            Background = background,
            BorderTop = borderTop,
            BorderBottom = borderBottom,
            BorderLeft = borderLeft,
            BorderRight = borderRight,
            Icon = icon,
            DistanceLabel = distanceLabel
        };
    }

    private static RawImage CreateMarkerBorder(Transform parent, string objectName,
        Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot)
    {
        GameObject borderObject = new GameObject(objectName, typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
        borderObject.transform.SetParent(parent, false);
        RectTransform rect = borderObject.GetComponent<RectTransform>();
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.pivot = pivot;
        RawImage image = borderObject.GetComponent<RawImage>();
        image.texture = Texture2D.whiteTexture;
        image.raycastTarget = false;
        return image;
    }

    private void SetMarkerViewsVisible(bool visible, string reason)
    {
        string renderState = _debugLogging != null && _debugLogging.Value
            ? (visible ? "visible: " : "hidden: ") + reason : null;
        for (int i = 0; i < _markerViews.Count; i++)
        {
            if (renderState != null)
                RecordMarkerRenderState(_markerViews[i], renderState);
            if (_markerViews[i].Root != null)
                _markerViews[i].Root.gameObject.SetActive(visible);
        }
    }

    private void RecordMarkerRenderState(MarkerView view, string renderState)
    {
        if (_debugLogging == null || !_debugLogging.Value || view == null ||
            (view.HasRenderDiagnostic && view.LastRenderDiagnostic == renderState))
            return;

        string target = view.Target != null
            ? DescribeShelfTargets(new[] { view.Target })
            : "unknown target";
        LogDebug("Marker render state for " + target + ": " + renderState + ".");
        view.HasRenderDiagnostic = true;
        view.LastRenderDiagnostic = renderState;
    }

    private static float GetProjectedShelfHeight(Camera camera, Bounds bounds)
    {
        Vector3 center = bounds.center;
        Vector3 top = camera.WorldToScreenPoint(new Vector3(center.x, bounds.max.y, center.z));
        Vector3 bottom = camera.WorldToScreenPoint(new Vector3(center.x, bounds.min.y, center.z));
        if (top.z <= 0f || bottom.z <= 0f)
            return 0f;

        return Mathf.Abs(top.y - bottom.y);
    }

    private static string FormatProduct(ProductType type)
    {
        return type.ToString().Replace('_', ' ');
    }


}
