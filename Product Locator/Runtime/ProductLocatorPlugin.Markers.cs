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
            SetMarkerViewsVisible(false);
            return;
        }

        if (_shelfTargets.Count == 0)
        {
            SetMarkerViewsVisible(false);
            return;
        }

        Camera camera = Camera.main;
        if (camera == null)
        {
            SetMarkerViewsVisible(false);
            return;
        }

        float baseSize = Mathf.Clamp(_iconSize.Value, 50, 150);
        for (int i = _shelfTargets.Count - 1; i >= 0; i--)
        {
            ShelfIconTarget target = _shelfTargets[i];
            MarkerView view = _markerViews[i];
            if (target.Shelf == null || target.Texture == null)
            {
                view.Root.gameObject.SetActive(false);
                continue;
            }

            Vector3 screen = camera.WorldToScreenPoint(target.Bounds.center);
            float x = screen.x;
            float y = Screen.height - screen.y - target.StackIndex * (baseSize + 6f);
            if (screen.z <= 0f)
            {
                view.Root.gameObject.SetActive(false);
                continue;
            }

            float projectedShelfHeight = GetProjectedShelfHeight(camera, target.Bounds);
            float iconSize = Mathf.Clamp(Mathf.Max(baseSize, projectedShelfHeight * 0.16f), baseSize, baseSize * 2f);
            float halfIconSize = iconSize * 0.5f;
            if (x + halfIconSize < 0f || x - halfIconSize > Screen.width ||
                y + halfIconSize < 0f || y - halfIconSize > Screen.height)
            {
                view.Root.gameObject.SetActive(false);
                continue;
            }

            float border = Mathf.Clamp(iconSize * 0.045f, 2f, 5f);
            float iconPadding = Mathf.Clamp(iconSize * 0.16f, 7f, 18f);
            view.Root.sizeDelta = new Vector2(iconSize, iconSize);
            view.Root.anchoredPosition = new Vector2(x - Screen.width * 0.5f, Screen.height * 0.5f - y);
            view.Background.color = new Color(0.035f, 0.045f, 0.065f, Mathf.Clamp01(_backgroundOpacity.Value));
            Color markerColor = target.MarkerColor.HasValue ? target.MarkerColor.Value : _markerColor.Value;
            view.BorderTop.color = markerColor;
            view.BorderBottom.color = markerColor;
            view.BorderLeft.color = markerColor;
            view.BorderRight.color = markerColor;
            view.BorderTop.rectTransform.sizeDelta = new Vector2(0f, border);
            view.BorderBottom.rectTransform.sizeDelta = new Vector2(0f, border);
            view.BorderLeft.rectTransform.sizeDelta = new Vector2(border, 0f);
            view.BorderRight.rectTransform.sizeDelta = new Vector2(border, 0f);
            view.Icon.texture = target.Texture;
            view.Icon.uvRect = target.Uv;
            float contentSize = iconSize - iconPadding * 2f;
            float aspect = target.Uv.height > 0f
                ? Mathf.Max(0.01f, target.Uv.width / target.Uv.height)
                : 1f;
            float iconWidth = contentSize;
            float iconHeight = contentSize;
            if (aspect > 1f)
                iconHeight = contentSize / aspect;
            else
                iconWidth = contentSize * aspect;
            view.Icon.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
            view.Icon.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            view.Icon.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            view.Icon.rectTransform.anchoredPosition = Vector2.zero;
            view.Icon.rectTransform.sizeDelta = new Vector2(iconWidth, iconHeight);
            view.Root.gameObject.SetActive(true);
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
        bool rebuild = _markerViews.Count != _shelfTargets.Count;
        if (!rebuild)
        {
            for (int i = 0; i < _shelfTargets.Count; i++)
            {
                if (_markerViews[i].Target == _shelfTargets[i])
                    continue;
                rebuild = true;
                break;
            }
        }
        if (!rebuild)
            return;

        for (int i = 0; i < _markerViews.Count; i++)
        {
            if (_markerViews[i].Root != null)
                Destroy(_markerViews[i].Root.gameObject);
        }
        _markerViews.Clear();

        for (int i = 0; i < _shelfTargets.Count; i++)
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
            iconRect.anchorMin = Vector2.zero;
            iconRect.anchorMax = Vector2.one;
            iconRect.offsetMin = Vector2.zero;
            iconRect.offsetMax = Vector2.zero;
            RawImage icon = iconObject.GetComponent<RawImage>();
            icon.raycastTarget = false;

            _markerViews.Add(new MarkerView
            {
                Target = _shelfTargets[i],
                Root = root,
                Background = background,
                BorderTop = borderTop,
                BorderBottom = borderBottom,
                BorderLeft = borderLeft,
                BorderRight = borderRight,
                Icon = icon
            });
        }
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

    private void SetMarkerViewsVisible(bool visible)
    {
        for (int i = 0; i < _markerViews.Count; i++)
        {
            if (_markerViews[i].Root != null)
                _markerViews[i].Root.gameObject.SetActive(visible);
        }
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
