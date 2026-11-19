#if !LOGOVERLAY_DISABLED
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM && LOGOVERLAY_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace VIB.LogOverlay
{
    /// <summary>
    /// Overlay de logs affiché au-dessus de tout (build et éditeur). Ctrl+H : afficher / masquer.
    /// Se crée tout seul au lancement (aucune mise en place dans la scène).
    /// Capture tous les Debug.Log / LogWarning / LogError / exceptions, y compris depuis d'autres threads.
    /// Canvas "Screen Space - Overlay" : en VR, visible uniquement sur la fenêtre PC, jamais dans le casque.
    /// </summary>
    [DefaultExecutionOrder(-10000)]
    public class LogOverlay : MonoBehaviour
    {
        const int MaxEntries = 300;           // nombre de logs conservés
        const int MaxMessageLength = 600;     // tronque les messages trop longs
        const int MaxDisplayedChars = 14000;  // limite de vertices d'un Text uGUI (~65k)
        const float OverlayAlpha = 0.8f;
        const float ScrollStep = 80f;         // pixels par cran de molette
        const float FilterButtonWidth = 190f;
        const float FilterButtonGap = 10f;

        struct LogEntry
        {
            public string time;
            public string message;
            public string stack;
            public LogType type;
        }

        static LogOverlay instance;
        static readonly ConcurrentQueue<LogEntry> pending = new ConcurrentQueue<LogEntry>();

        readonly LinkedList<LogEntry> entries = new LinkedList<LogEntry>();
        readonly StringBuilder sb = new StringBuilder(MaxDisplayedChars + 1024);

        Canvas canvas;
        CanvasGroup canvasGroup;
        RectTransform viewport;
        RectTransform content;
        Text logText;
        Text headerText;
        bool visible;
        bool dirty;
        bool autoScroll = true;
        int logCount, warningCount, errorCount;

        // Filtres par niveau : 0 = infos, 1 = warnings, 2 = erreurs (Error / Assert / Exception)
        const int LevelInfo = 0, LevelWarning = 1, LevelError = 2;
        static readonly string[] levelLabels = { "Infos", "Warnings", "Erreurs" };
        static readonly Color[] levelColors =
        {
            new Color(0.88f, 0.88f, 0.88f),
            new Color(1f, 0.835f, 0.31f),
            new Color(1f, 0.43f, 0.43f)
        };
        readonly bool[] levelShown = { true, true, true };
        readonly RectTransform[] filterButtons = new RectTransform[3];
        readonly Image[] filterButtonImages = new Image[3];
        readonly Text[] filterButtonTexts = new Text[3];

        public static bool IsVisible => instance != null && instance.visible;
        public static void Toggle() { if (instance != null) instance.SetVisible(!instance.visible); }
        public static void Show(bool show) { if (instance != null) instance.SetVisible(show); }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Subscribe()
        {
            // Abonnement le plus tôt possible pour ne rater aucun log
            Application.logMessageReceivedThreaded -= OnLogReceived;
            Application.logMessageReceivedThreaded += OnLogReceived;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Bootstrap()
        {
            if (instance != null) return;
            GameObject go = new GameObject("[LogOverlay]");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<LogOverlay>();
        }

        static void OnLogReceived(string condition, string stackTrace, LogType type)
        {
            pending.Enqueue(new LogEntry
            {
                time = DateTime.Now.ToString("HH:mm:ss.fff"),
                message = condition,
                stack = stackTrace,
                type = type
            });
        }

        void Awake()
        {
            BuildUI();
            SetVisible(false);
            Debug.Log($"[LogOverlay] Prêt (Ctrl+H pour afficher). {Application.productName} v{Application.version} - Unity {Application.unityVersion} - {SystemInfo.operatingSystem} - PC {SystemInfo.deviceName}");
        }

        void OnDestroy()
        {
            if (instance == this)
            {
                Application.logMessageReceivedThreaded -= OnLogReceived;
                instance = null;
            }
        }

        void Update()
        {
            bool ctrl = KeyHeld(Key_Ctrl);
            if (ctrl && KeyDown(Key_H))
                SetVisible(!visible);

            while (pending.TryDequeue(out LogEntry entry))
                AddEntry(entry);

            if (!visible) return;

            if (ctrl && KeyDown(Key_L))
                Clear();

            // Filtres : clic sur les boutons, ou Ctrl+1 / Ctrl+2 / Ctrl+3
            if (ctrl && KeyDown(Key_1)) ToggleLevel(LevelInfo);
            if (ctrl && KeyDown(Key_2)) ToggleLevel(LevelWarning);
            if (ctrl && KeyDown(Key_3)) ToggleLevel(LevelError);
            if (MouseClicked())
            {
                Vector2 mouse = MousePosition();
                for (int i = 0; i < filterButtons.Length; i++)
                {
                    // Canvas Overlay => caméra null
                    if (RectTransformUtility.RectangleContainsScreenPoint(filterButtons[i], mouse, null))
                        ToggleLevel(i);
                }
            }

            if (dirty)
                Refresh();

            HandleScroll();
        }

        #region Input (ancien Input Manager ou nouveau Input System)

        const int Key_Ctrl = 0, Key_H = 1, Key_L = 2, Key_PageUp = 3, Key_PageDown = 4, Key_End = 5,
                  Key_1 = 6, Key_2 = 7, Key_3 = 8;

#if ENABLE_LEGACY_INPUT_MANAGER
        static readonly KeyCode[] legacyKeys = { KeyCode.LeftControl, KeyCode.H, KeyCode.L, KeyCode.PageUp, KeyCode.PageDown, KeyCode.End,
                                                 KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3 };
        static bool KeyHeld(int k) => Input.GetKey(legacyKeys[k]) || (k == Key_Ctrl && Input.GetKey(KeyCode.RightControl));
        static bool KeyDown(int k) => Input.GetKeyDown(legacyKeys[k]);
        static float ScrollDelta() => Input.mouseScrollDelta.y;
        static bool MouseClicked() => Input.GetMouseButtonDown(0);
        static Vector2 MousePosition() => Input.mousePosition;
#elif ENABLE_INPUT_SYSTEM && LOGOVERLAY_INPUT_SYSTEM
        static readonly Key[] newKeys = { Key.LeftCtrl, Key.H, Key.L, Key.PageUp, Key.PageDown, Key.End,
                                          Key.Digit1, Key.Digit2, Key.Digit3 };
        static bool MouseClicked() => Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame;
        static Vector2 MousePosition() => Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero;
        static bool KeyHeld(int k)
        {
            Keyboard kb = Keyboard.current;
            if (kb == null) return false;
            return kb[newKeys[k]].isPressed || (k == Key_Ctrl && kb[Key.RightCtrl].isPressed);
        }
        static bool KeyDown(int k) => Keyboard.current != null && Keyboard.current[newKeys[k]].wasPressedThisFrame;
        static float ScrollDelta()
        {
            if (Mouse.current == null) return 0f;
            float y = Mouse.current.scroll.ReadValue().y;
            return Mathf.Abs(y) > 0.01f ? y / 120f : 0f; // Windows : 120 par cran
        }
#else
        static bool KeyHeld(int k) => false;
        static bool KeyDown(int k) => false;
        static float ScrollDelta() => 0f;
        static bool MouseClicked() => false;
        static Vector2 MousePosition() => Vector2.zero;
#endif

        #endregion

        void HandleScroll()
        {
            float maxScroll = Mathf.Max(0f, content.rect.height - viewport.rect.height);
            float y = content.anchoredPosition.y;

            float wheel = ScrollDelta();
            if (Mathf.Abs(wheel) > 0.001f) y -= wheel * ScrollStep;
            if (KeyDown(Key_PageUp)) y -= viewport.rect.height * 0.9f;
            if (KeyDown(Key_PageDown)) y += viewport.rect.height * 0.9f;
            if (KeyDown(Key_End)) y = maxScroll;

            y = Mathf.Clamp(y, 0f, maxScroll);
            autoScroll = y >= maxScroll - 1f; // en bas => on suit les nouveaux logs
            content.anchoredPosition = new Vector2(0f, y);
        }

        void ScrollToBottom()
        {
            Canvas.ForceUpdateCanvases();
            float maxScroll = Mathf.Max(0f, content.rect.height - viewport.rect.height);
            content.anchoredPosition = new Vector2(0f, maxScroll);
        }

        void AddEntry(LogEntry entry)
        {
            switch (entry.type)
            {
                case LogType.Log: logCount++; break;
                case LogType.Warning: warningCount++; break;
                default: errorCount++; break;
            }

            entries.AddLast(entry);
            while (entries.Count > MaxEntries)
                entries.RemoveFirst();
            dirty = true;
        }

        static int LevelOf(LogType type)
        {
            switch (type)
            {
                case LogType.Log: return LevelInfo;
                case LogType.Warning: return LevelWarning;
                default: return LevelError;
            }
        }

        void ToggleLevel(int level)
        {
            levelShown[level] = !levelShown[level];
            autoScroll = true;
            dirty = true;
        }

        void UpdateFilterButtons()
        {
            int[] counts = { logCount, warningCount, errorCount };
            for (int i = 0; i < filterButtons.Length; i++)
            {
                bool on = levelShown[i];
                Color c = levelColors[i];
                filterButtonImages[i].color = on ? new Color(c.r * 0.35f, c.g * 0.35f, c.b * 0.35f, 1f) : new Color(0.15f, 0.15f, 0.17f, 1f);
                filterButtonTexts[i].color = on ? c : new Color(0.45f, 0.45f, 0.45f, 1f);
                filterButtonTexts[i].text = $"{(on ? "●" : "○")} {levelLabels[i]} ({counts[i]})";
            }
        }

        void Clear()
        {
            entries.Clear();
            logCount = warningCount = errorCount = 0;
            dirty = true;
        }

        void SetVisible(bool show)
        {
            visible = show;
            canvas.enabled = show;
            canvasGroup.alpha = show ? OverlayAlpha : 0f;
            if (show)
            {
                autoScroll = true;
                Refresh();
            }
        }

        void Refresh()
        {
            dirty = false;
            headerText.text =
                "LOGS" +
                "      <size=14><color=#AAAAAA>Ctrl+H : masquer  |  Ctrl+L : vider  |  Ctrl+1/2/3 : filtres  |  Molette / PageUp / PageDown : défiler  |  Fin : suivre</color></size>";
            UpdateFilterButtons();

            // On part des plus récents en remontant, pour respecter la limite de caractères
            var blocks = new List<string>();
            int total = 0;
            for (LinkedListNode<LogEntry> node = entries.Last; node != null; node = node.Previous)
            {
                if (!levelShown[LevelOf(node.Value.type)])
                    continue;
                string block = Format(node.Value);
                if (total + block.Length > MaxDisplayedChars && blocks.Count > 0)
                    break;
                blocks.Add(block);
                total += block.Length;
            }

            sb.Clear();
            for (int i = blocks.Count - 1; i >= 0; i--)
                sb.Append(blocks[i]);
            logText.text = sb.ToString();

            if (autoScroll)
                ScrollToBottom();
        }

        static string Format(LogEntry e)
        {
            string color;
            string tag;
            switch (e.type)
            {
                case LogType.Warning: color = "#FFD54F"; tag = "WARN"; break;
                case LogType.Error: color = "#FF6E6E"; tag = "ERR "; break;
                case LogType.Assert: color = "#FF6E6E"; tag = "ASRT"; break;
                case LogType.Exception: color = "#FF4FD8"; tag = "EXC "; break;
                default: color = "#E0E0E0"; tag = "INFO"; break;
            }

            string msg = e.message ?? "";
            if (msg.Length > MaxMessageLength)
                msg = msg.Substring(0, MaxMessageLength) + "…";
            msg = Sanitize(msg);

            var b = new StringBuilder();
            b.Append("<color=#888888>").Append(e.time).Append("</color> <color=").Append(color).Append(">[").Append(tag).Append("] ").Append(msg).Append("</color>\n");

            // Pour les erreurs / exceptions, on affiche les premières lignes de la stack
            if (e.type != LogType.Log && e.type != LogType.Warning && !string.IsNullOrEmpty(e.stack))
            {
                int shown = 0;
                foreach (string line in e.stack.Split('\n'))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    b.Append("<size=13><color=#B08080>      ").Append(Sanitize(line.Trim())).Append("</color></size>\n");
                    if (++shown >= 4) break;
                }
            }
            return b.ToString();
        }

        // Empêche le contenu des logs d'être interprété comme du rich text (ex: "<b>" dans un JSON)
        // ("<" est remplacé par "‹", visuellement proche)
        static string Sanitize(string s) => s.Replace('<', '‹');

        #region Construction de l'UI

        void BuildUI()
        {
            canvas = gameObject.AddComponent<Canvas>();
            // Screen Space Overlay : rendu uniquement sur l'écran PC, jamais dans les yeux XR
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = short.MaxValue; // au-dessus de tout
            canvas.targetDisplay = 0;

            CanvasScaler scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            // Pas de GraphicRaycaster : l'overlay ne bloque aucun clic / pointeur XR
            canvasGroup = gameObject.AddComponent<CanvasGroup>();
            canvasGroup.blocksRaycasts = false;
            canvasGroup.interactable = false;

            Font font = LoadFont();

            RectTransform background = CreateRect("Background", transform);
            Stretch(background, 0, 0, 0, 0);
            Image bg = background.gameObject.AddComponent<Image>();
            bg.color = new Color(0.05f, 0.05f, 0.08f, 1f);
            bg.raycastTarget = false;

            RectTransform header = CreateRect("Header", background);
            header.anchorMin = new Vector2(0, 1);
            header.anchorMax = new Vector2(1, 1);
            header.pivot = new Vector2(0.5f, 1);
            header.offsetMin = new Vector2(20, -50);
            header.offsetMax = new Vector2(-20 - 3 * (FilterButtonWidth + FilterButtonGap), -10);
            headerText = header.gameObject.AddComponent<Text>();
            headerText.font = font;
            headerText.fontSize = 22;
            headerText.fontStyle = FontStyle.Bold;
            headerText.color = Color.white;
            headerText.supportRichText = true;
            headerText.alignment = TextAnchor.MiddleLeft;
            headerText.horizontalOverflow = HorizontalWrapMode.Overflow;
            headerText.raycastTarget = false;

            // Boutons de filtre, alignés à droite du header (Infos | Warnings | Erreurs).
            // Pas d'EventSystem requis : le clic est testé à la main dans Update().
            for (int i = 0; i < 3; i++)
            {
                RectTransform btn = CreateRect("Filter_" + levelLabels[i], background);
                btn.anchorMin = btn.anchorMax = new Vector2(1, 1);
                btn.pivot = new Vector2(1, 1);
                btn.sizeDelta = new Vector2(FilterButtonWidth, 36);
                btn.anchoredPosition = new Vector2(-20 - (2 - i) * (FilterButtonWidth + FilterButtonGap), -12);
                Image img = btn.gameObject.AddComponent<Image>();
                img.raycastTarget = false;

                RectTransform label = CreateRect("Label", btn);
                Stretch(label, 8, 8, 0, 0);
                Text txt = label.gameObject.AddComponent<Text>();
                txt.font = font;
                txt.fontSize = 17;
                txt.fontStyle = FontStyle.Bold;
                txt.alignment = TextAnchor.MiddleCenter;
                txt.horizontalOverflow = HorizontalWrapMode.Overflow;
                txt.raycastTarget = false;

                filterButtons[i] = btn;
                filterButtonImages[i] = img;
                filterButtonTexts[i] = txt;
            }

            viewport = CreateRect("Viewport", background);
            Stretch(viewport, 20, 20, 60, 20);
            viewport.gameObject.AddComponent<RectMask2D>();

            content = CreateRect("Content", viewport);
            content.anchorMin = new Vector2(0, 1);
            content.anchorMax = new Vector2(1, 1);
            content.pivot = new Vector2(0.5f, 1);
            content.offsetMin = Vector2.zero;
            content.offsetMax = Vector2.zero;
            ContentSizeFitter fitter = content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            logText = content.gameObject.AddComponent<Text>();
            logText.font = font;
            logText.fontSize = 16;
            logText.color = Color.white;
            logText.supportRichText = true;
            logText.alignment = TextAnchor.UpperLeft;
            logText.horizontalOverflow = HorizontalWrapMode.Wrap;
            logText.verticalOverflow = VerticalWrapMode.Overflow;
            logText.raycastTarget = false;
        }

        static Font LoadFont()
        {
            Font font = null;
            try { font = Font.CreateDynamicFontFromOSFont(new[] { "Consolas", "Courier New", "Arial" }, 16); } catch { }
            if (font == null)
            {
                try { font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch { }
            }
            if (font == null)
            {
                try { font = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { }
            }
            return font;
        }

        static RectTransform CreateRect(string name, Transform parent)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go.GetComponent<RectTransform>();
        }

        static void Stretch(RectTransform rt, float left, float right, float top, float bottom)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(left, bottom);
            rt.offsetMax = new Vector2(-right, -top);
        }

        #endregion
    }
}
#endif
