using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEditor;
using PlayCaller.Editor.Models;

namespace PlayCaller.Editor.Handlers
{
    /// <summary>
    /// Coordinate-based input simulation handler.
    /// Converts screen coordinates (top-left origin, Y-down) to Unity coordinates (bottom-left origin, Y-up),
    /// then uses GraphicRaycaster / Physics2D.Raycast / Physics.Raycast to find targets,
    /// and fires EventSystem events via ExecuteEvents.
    /// </summary>
    public static class InputSimulationHandler
    {
        #region Tap

        public static object HandleTap(PlayCallerCommand command)
        {
            try
            {
                if (!Application.isPlaying)
                    return PlayCallerResponse.Error(command.Id, "Play Mode is required for tap", "PLAY_MODE_REQUIRED");

                float x = command.Params?["x"]?.ToObject<float>() ?? 0;
                float y = command.Params?["y"]?.ToObject<float>() ?? 0;
                int holdDurationMs = command.Params?["holdDurationMs"]?.ToObject<int>() ?? 0;
                int screenshotWidth = command.Params?["screenshotWidth"]?.ToObject<int>() ?? 0;
                int screenshotHeight = command.Params?["screenshotHeight"]?.ToObject<int>() ?? 0;
                int screenWidth = command.Params?["screenWidth"]?.ToObject<int>() ?? 0;
                int screenHeight = command.Params?["screenHeight"]?.ToObject<int>() ?? 0;

                // Convert from top-left origin to Unity bottom-left origin
                Vector2 unityScreenPos = ConvertToUnityScreenPos(x, y, screenshotWidth, screenshotHeight, screenWidth, screenHeight);

                if (holdDurationMs > 0)
                {
                    return SimulateTapAsync(command.Id, unityScreenPos, holdDurationMs);
                }
                else
                {
                    return SimulateTapSync(command.Id, unityScreenPos);
                }
            }
            catch (Exception ex)
            {
                return PlayCallerResponse.Error(command.Id, $"Tap failed: {ex.Message}", "TAP_ERROR");
            }
        }

        private static string SimulateTapSync(string id, Vector2 unityScreenPos)
        {
            var eventSystem = EnsureEventSystem();
            if (eventSystem == null)
                return PlayCallerResponse.Error(id, "No EventSystem found", "NO_EVENT_SYSTEM");

            var target = FindTargetAtPosition(unityScreenPos);
            var pointer = CreatePointerEventData(eventSystem, unityScreenPos);

            string clickHandlerName = null;
            if (target != null)
            {
                Debug.Log($"[PlayCaller.Tap] target={target.name}, path={GetGameObjectPath(target)}, unityScreenPos={unityScreenPos}");

                // Check if there's a Button in the hierarchy
                var btn = target.GetComponentInParent<Button>();
                if (btn != null)
                {
                    Debug.Log($"[PlayCaller.Tap] Found Button in parent: {btn.gameObject.name}, interactable={btn.interactable}, IsActive={btn.IsActive()}");
                }

                var enterHandler = ExecuteEvents.ExecuteHierarchy(target, pointer, ExecuteEvents.pointerEnterHandler);
                var downHandler = ExecuteEvents.ExecuteHierarchy(target, pointer, ExecuteEvents.pointerDownHandler);
                var upHandler = ExecuteEvents.ExecuteHierarchy(target, pointer, ExecuteEvents.pointerUpHandler);
                var clickHandler = ExecuteEvents.ExecuteHierarchy(target, pointer, ExecuteEvents.pointerClickHandler);
                var exitHandler = ExecuteEvents.ExecuteHierarchy(target, pointer, ExecuteEvents.pointerExitHandler);

                clickHandlerName = clickHandler != null ? clickHandler.name : "null";
                Debug.Log($"[PlayCaller.Tap] enter={enterHandler?.name}, down={downHandler?.name}, up={upHandler?.name}, click={clickHandler?.name}, exit={exitHandler?.name}");
            }

            return PlayCallerResponse.Success(id, new
            {
                tapped = target != null,
                targetName = target != null ? target.name : null,
                clickHandler = clickHandlerName,
                screenPosition = new { x = unityScreenPos.x, y = unityScreenPos.y }
            });
        }

        private static string GetGameObjectPath(GameObject obj)
        {
            string path = obj.name;
            Transform parent = obj.transform.parent;
            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }
            return path;
        }

        private static Task<string> SimulateTapAsync(string id, Vector2 unityScreenPos, int holdDurationMs)
        {
            var tcs = new TaskCompletionSource<string>();
            var eventSystem = EnsureEventSystem();
            if (eventSystem == null)
            {
                tcs.SetResult(PlayCallerResponse.Error(id, "No EventSystem found", "NO_EVENT_SYSTEM"));
                return tcs.Task;
            }

            var target = FindTargetAtPosition(unityScreenPos);
            var pointer = CreatePointerEventData(eventSystem, unityScreenPos);

            if (target != null)
            {
                ExecuteEvents.ExecuteHierarchy(target, pointer, ExecuteEvents.pointerEnterHandler);
                ExecuteEvents.ExecuteHierarchy(target, pointer, ExecuteEvents.pointerDownHandler);
            }

            // Wait holdDuration then release
            double start = EditorApplication.timeSinceStartup;
            void Tick()
            {
                if (EditorApplication.timeSinceStartup - start >= holdDurationMs / 1000.0)
                {
                    EditorApplication.update -= Tick;

                    if (target != null)
                    {
                        ExecuteEvents.ExecuteHierarchy(target, pointer, ExecuteEvents.pointerUpHandler);
                        ExecuteEvents.ExecuteHierarchy(target, pointer, ExecuteEvents.pointerClickHandler);
                        ExecuteEvents.ExecuteHierarchy(target, pointer, ExecuteEvents.pointerExitHandler);
                    }

                    tcs.TrySetResult(PlayCallerResponse.Success(id, new
                    {
                        tapped = target != null,
                        targetName = target != null ? target.name : null,
                        screenPosition = new { x = unityScreenPos.x, y = unityScreenPos.y },
                        holdDurationMs = holdDurationMs
                    }));
                }
            }

            EditorApplication.update += Tick;
            return tcs.Task;
        }

        #endregion

        #region Drag

        public static object HandleDrag(PlayCallerCommand command)
        {
            try
            {
                if (!Application.isPlaying)
                    return PlayCallerResponse.Error(command.Id, "Play Mode is required for drag", "PLAY_MODE_REQUIRED");

                float fromX = command.Params?["fromX"]?.ToObject<float>() ?? 0;
                float fromY = command.Params?["fromY"]?.ToObject<float>() ?? 0;
                float toX = command.Params?["toX"]?.ToObject<float>() ?? 0;
                float toY = command.Params?["toY"]?.ToObject<float>() ?? 0;
                int durationMs = command.Params?["durationMs"]?.ToObject<int>() ?? 300;
                int steps = command.Params?["steps"]?.ToObject<int>() ?? 10;
                int screenshotWidth = command.Params?["screenshotWidth"]?.ToObject<int>() ?? 0;
                int screenshotHeight = command.Params?["screenshotHeight"]?.ToObject<int>() ?? 0;
                int screenWidth = command.Params?["screenWidth"]?.ToObject<int>() ?? 0;
                int screenHeight = command.Params?["screenHeight"]?.ToObject<int>() ?? 0;

                if (steps < 2) steps = 2;
                if (steps > 100) steps = 100;
                if (durationMs < 0) durationMs = 0;
                if (durationMs > 10000) durationMs = 10000;

                Vector2 fromUnity = ConvertToUnityScreenPos(fromX, fromY, screenshotWidth, screenshotHeight, screenWidth, screenHeight);
                Vector2 toUnity = ConvertToUnityScreenPos(toX, toY, screenshotWidth, screenshotHeight, screenWidth, screenHeight);

                return SimulateDragAsync(command.Id, fromUnity, toUnity, durationMs, steps);
            }
            catch (Exception ex)
            {
                return PlayCallerResponse.Error(command.Id, $"Drag failed: {ex.Message}", "DRAG_ERROR");
            }
        }

        private static Task<string> SimulateDragAsync(string id, Vector2 from, Vector2 to, int durationMs, int steps)
        {
            var tcs = new TaskCompletionSource<string>();
            var eventSystem = EnsureEventSystem();
            if (eventSystem == null)
            {
                tcs.SetResult(PlayCallerResponse.Error(id, "No EventSystem found", "NO_EVENT_SYSTEM"));
                return tcs.Task;
            }

            var target = FindTargetAtPosition(from);
            var pointer = CreatePointerEventData(eventSystem, from);

            if (target != null)
            {
                ExecuteEvents.ExecuteHierarchy(target, pointer, ExecuteEvents.pointerDownHandler);
                ExecuteEvents.ExecuteHierarchy(target, pointer, ExecuteEvents.beginDragHandler);
            }

            double startTime = EditorApplication.timeSinceStartup;
            double totalDuration = durationMs / 1000.0;
            int currentStep = 0;

            void Tick()
            {
                double elapsed = EditorApplication.timeSinceStartup - startTime;
                float t = totalDuration > 0 ? Mathf.Clamp01((float)(elapsed / totalDuration)) : 1f;

                // If we've reached the end
                if (t >= 1f || currentStep >= steps)
                {
                    EditorApplication.update -= Tick;

                    // Final drag position
                    pointer.position = to;
                    pointer.delta = to - (Vector2)pointer.position;
                    if (target != null)
                    {
                        ExecuteEvents.ExecuteHierarchy(target, pointer, ExecuteEvents.dragHandler);
                        ExecuteEvents.ExecuteHierarchy(target, pointer, ExecuteEvents.endDragHandler);
                        ExecuteEvents.ExecuteHierarchy(target, pointer, ExecuteEvents.pointerUpHandler);
                    }

                    tcs.TrySetResult(PlayCallerResponse.Success(id, new
                    {
                        dragged = target != null,
                        targetName = target != null ? target.name : null,
                        from = new { x = from.x, y = from.y },
                        to = new { x = to.x, y = to.y },
                        steps = currentStep
                    }));
                    return;
                }

                // Interpolate position
                Vector2 currentPos = Vector2.Lerp(from, to, t);
                Vector2 prevPos = pointer.position;
                pointer.position = currentPos;
                pointer.delta = currentPos - prevPos;
                currentStep++;

                if (target != null)
                {
                    ExecuteEvents.ExecuteHierarchy(target, pointer, ExecuteEvents.dragHandler);
                }
            }

            EditorApplication.update += Tick;
            return tcs.Task;
        }

        #endregion

        #region Flick

        public static object HandleFlick(PlayCallerCommand command)
        {
            try
            {
                if (!Application.isPlaying)
                    return PlayCallerResponse.Error(command.Id, "Play Mode is required for flick", "PLAY_MODE_REQUIRED");

                float fromX = command.Params?["fromX"]?.ToObject<float>() ?? 0;
                float fromY = command.Params?["fromY"]?.ToObject<float>() ?? 0;
                float dx = command.Params?["dx"]?.ToObject<float>() ?? 0;
                float dy = command.Params?["dy"]?.ToObject<float>() ?? 0;
                int durationMs = command.Params?["durationMs"]?.ToObject<int>() ?? 150;
                int screenshotWidth = command.Params?["screenshotWidth"]?.ToObject<int>() ?? 0;
                int screenshotHeight = command.Params?["screenshotHeight"]?.ToObject<int>() ?? 0;
                int screenWidth = command.Params?["screenWidth"]?.ToObject<int>() ?? 0;
                int screenHeight = command.Params?["screenHeight"]?.ToObject<int>() ?? 0;

                Vector2 fromUnity = ConvertToUnityScreenPos(fromX, fromY, screenshotWidth, screenshotHeight, screenWidth, screenHeight);

                // dx/dy: positive-right, positive-down in input coords
                // In Unity: positive-right, positive-UP so negate dy
                // Scale dx/dy from screenshot space to screen space
                float sX = (screenWidth > 0 && screenshotWidth > 0) ? (float)screenWidth / screenshotWidth : 1f;
                float sY = (screenHeight > 0 && screenshotHeight > 0) ? (float)screenHeight / screenshotHeight : 1f;
                Vector2 toUnity = new Vector2(fromUnity.x + dx * sX, fromUnity.y - dy * sY);

                int steps = Mathf.Max(5, durationMs / 16); // ~60fps

                return SimulateDragAsync(command.Id, fromUnity, toUnity, durationMs, steps);
            }
            catch (Exception ex)
            {
                return PlayCallerResponse.Error(command.Id, $"Flick failed: {ex.Message}", "FLICK_ERROR");
            }
        }

        #endregion

        #region Coordinate Conversion

        /// <summary>
        /// Convert from MCP input coordinates (top-left origin, Y-down) to Unity screen coordinates (bottom-left origin, Y-up).
        /// Input coordinates are in the screenshot image coordinate system.
        ///
        /// screenshotWidth/Height: リサイズ後のスクリーンショット画像サイズ（入力座標系）
        /// screenWidth/Height: スクリーンショット取得時の Screen.width/height（= Game View 解像度）
        ///
        /// スクリーンショットがリサイズされている場合、入力座標をスクリーン座標空間にスケーリングする。
        /// RectTransformUtility.RectangleContainsScreenPoint は Screen.width × Screen.height 空間の座標を期待する。
        /// </summary>
        private static Vector2 ConvertToUnityScreenPos(float inputX, float inputY,
            int screenshotWidth = 0, int screenshotHeight = 0,
            int screenWidth = 0, int screenHeight = 0)
        {
            // Target: Unity screen coordinate space (what RectTransformUtility / Camera.ScreenToWorldPoint expects)
            // screenWidth/Height は Screenshot 取得時の Screen.width/height で、Game View 解像度に一致する。
            // EditorApplication.update コンテキストでは Screen.width/height が不正確な場合があるため、
            // Camera.main.pixelWidth/pixelHeight をフォールバックとして使用する。
            int targetW, targetH;
            if (screenWidth > 0 && screenHeight > 0)
            {
                targetW = screenWidth;
                targetH = screenHeight;
            }
            else
            {
                var cam = Camera.main;
                if (cam != null)
                {
                    targetW = cam.pixelWidth;
                    targetH = cam.pixelHeight;
                }
                else
                {
                    targetW = Screen.width;
                    targetH = Screen.height;
                }
            }

            // Source: screenshot image coordinate space
            int sourceW = (screenshotWidth > 0) ? screenshotWidth : targetW;
            int sourceH = (screenshotHeight > 0) ? screenshotHeight : targetH;

            // Scale from screenshot space to screen space, and flip Y
            float scaleX = (float)targetW / sourceW;
            float scaleY = (float)targetH / sourceH;

            float unityX = inputX * scaleX;
            float unityY = targetH - (inputY * scaleY);

            return new Vector2(unityX, unityY);
        }

        #endregion

        #region Target Finding

        /// <summary>
        /// Find the topmost interactive target at the given Unity screen position.
        /// Priority: UI (GraphicRaycaster) > 2D Physics > 3D Physics
        /// </summary>
        private static GameObject FindTargetAtPosition(Vector2 unityScreenPos)
        {
            // 1. Try UI Raycast (all canvases)
            var uiTarget = FindUITargetAtPosition(unityScreenPos);
            if (uiTarget != null) return uiTarget;

            // 2. Try 2D Physics Raycast
            var target2D = FindPhysics2DTargetAtPosition(unityScreenPos);
            if (target2D != null) return target2D;

            // 3. Try 3D Physics Raycast
            var target3D = FindPhysics3DTargetAtPosition(unityScreenPos);
            if (target3D != null) return target3D;

            return null;
        }

        private static GameObject FindUITargetAtPosition(Vector2 unityScreenPos)
        {
            // EventSystem.RaycastAll は EditorApplication.update コンテキストでは
            // 正常に動作しない場合がある（GraphicRaycaster のキャンバス状態が不完全なため）。
            // 代わりに全 Graphic を走査し、RectTransformUtility で直接ヒットテストする。
            Canvas.ForceUpdateCanvases();

            var allGraphics = UnityEngine.Object.FindObjectsOfType<Graphic>();
            GameObject bestTarget = null;
            int bestCanvasOrder = int.MinValue;
            int bestDepth = int.MinValue;
            int bestSiblingIndex = -1;

            foreach (var graphic in allGraphics)
            {
                if (!graphic.raycastTarget) continue;
                if (!graphic.gameObject.activeInHierarchy) continue;

                var canvas = graphic.canvas;
                if (canvas == null) continue;

                // rootCanvas の sortingOrder を使う（ネストされた Canvas の場合）
                int canvasOrder = canvas.rootCanvas.sortingOrder;

                Camera cam;
                Vector2 testPos;

                if (canvas.renderMode == RenderMode.ScreenSpaceOverlay)
                {
                    cam = null;
                    testPos = unityScreenPos;
                }
                else
                {
                    cam = canvas.worldCamera;
                    testPos = unityScreenPos;
                }

                if (RectTransformUtility.RectangleContainsScreenPoint(graphic.rectTransform, testPos, cam))
                {
                    // Canvas の sortingOrder → Graphic の depth → sibling index の順で最前面を選ぶ
                    int depth = graphic.depth;
                    int siblingIndex = graphic.transform.GetSiblingIndex();

                    if (canvasOrder > bestCanvasOrder
                        || (canvasOrder == bestCanvasOrder && depth > bestDepth)
                        || (canvasOrder == bestCanvasOrder && depth == bestDepth && siblingIndex > bestSiblingIndex))
                    {
                        bestCanvasOrder = canvasOrder;
                        bestDepth = depth;
                        bestSiblingIndex = siblingIndex;
                        bestTarget = graphic.gameObject;
                    }
                }
            }

            return bestTarget;
        }

        private static GameObject FindPhysics2DTargetAtPosition(Vector2 unityScreenPos)
        {
            var camera = Camera.main;
            if (camera == null) return null;

            Vector2 worldPos = camera.ScreenToWorldPoint(unityScreenPos);
            var hit = Physics2D.Raycast(worldPos, Vector2.zero);

            if (hit.collider != null)
                return hit.collider.gameObject;

            return null;
        }

        private static GameObject FindPhysics3DTargetAtPosition(Vector2 unityScreenPos)
        {
            var camera = Camera.main;
            if (camera == null) return null;

            Ray ray = camera.ScreenPointToRay(unityScreenPos);
            if (Physics.Raycast(ray, out RaycastHit hit))
                return hit.collider.gameObject;

            return null;
        }

        #endregion

        #region Event Helpers

        private static PointerEventData CreatePointerEventData(EventSystem eventSystem, Vector2 unityScreenPos)
        {
            return new PointerEventData(eventSystem)
            {
                button = PointerEventData.InputButton.Left,
                position = unityScreenPos,
                pressPosition = unityScreenPos,
                clickCount = 1,
                clickTime = Time.unscaledTime
            };
        }

        private static EventSystem EnsureEventSystem()
        {
            if (EventSystem.current != null)
                return EventSystem.current;

            var existing = UnityEngine.Object.FindObjectOfType<EventSystem>();
            if (existing != null)
                return existing;

            return null;
        }

        #endregion
    }
}
