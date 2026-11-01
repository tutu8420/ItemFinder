using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace ChineseCanFly.ItemFinder
{
    [BepInPlugin(Guid, "Item Finder", "1.3.2")]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "ChineseCanFly.ItemFinder";

        internal static bool WindowOpen;
        internal static ManualLogSource Log;
        private Harmony _harmony;

        private static float _blockLogTimer;
        private static float _useLogTimer;

        // The inventory must stay open while the search window is up. Even if the
        // ZInput patch is bypassed (another mod replaced the call site), Hide() is
        // the single choke point that actually closes the window.
        internal static void NoteBlockedHide()
        {
            if (Log == null || Time.unscaledTime < _blockLogTimer) return;
            _blockLogTimer = Time.unscaledTime + 1f;

            string caller = "?";
            try
            {
                StackFrame[] frames = new StackTrace(1, false).GetFrames();
                if (frames != null)
                {
                    for (int i = 0; i < frames.Length; i++)
                    {
                        MethodBase m = frames[i].GetMethod();
                        if (m == null || m.DeclaringType == typeof(Plugin) || m.DeclaringType == typeof(InventoryGuiHideGuard)) continue;
                        caller = m.DeclaringType.Name + "." + m.Name;
                        break;
                    }
                }
            }
            catch { }

            Log.LogInfo("Kept the inventory open while searching (blocked InventoryGui.Hide from " + caller + ").");
        }

        internal static void NoteSuppressedUse()
        {
            if (Log == null || Time.unscaledTime < _useLogTimer) return;
            _useLogTimer = Time.unscaledTime + 1f;
            Log.LogInfo("Swallowed ZInput.GetButtonDown(\"Use\") while searching.");
        }

        private sealed class Entry
        {
            public Container Box;
            public string Display;
            public string English;
            public string Initials;
            public string Key;
            public int Count;
            public float Distance;
        }

        // ---------------------------------------------------------------- config
        private ConfigEntry<bool> _showButton;
        private ConfigEntry<bool> _anchorToInventory;
        private ConfigEntry<float> _buttonX;
        private ConfigEntry<float> _buttonY;
        private ConfigEntry<float> _searchRadius;
        private ConfigEntry<bool> _pinyin;
        private ConfigEntry<int> _maxResults;
        private ConfigEntry<bool> _includeShips;
        private ConfigEntry<bool> _sound;
        private ConfigEntry<float> _soundInterval;
        private ConfigEntry<float> _markerScale;
        private ConfigEntry<KeyboardShortcut> _measureKey;
        private ConfigEntry<bool> _measurePlayer;
        private ConfigEntry<float> _measureRange;

        // ---------------------------------------------------------------- state
        private bool _searchOpen;
        private string _query = "";
        private bool _focusSearch;
        private readonly List<Entry> _index = new List<Entry>();
        private readonly List<Entry> _results = new List<Entry>();
        private readonly List<string> _suggestions = new List<string>();
        private Vector2 _listScroll;
        private Container _target;
        private float _soundTimer;
        private Rect _windowRect = new Rect(0f, 0f, 680f, 620f);

        private Vector3 _pointA;
        private Vector3 _pointB;
        private int _measureStage;
        private float _pulse;

        // ---------------------------------------------------------------- UI
        private GUIStyle _windowStyle;
        private GUIStyle _labelStyle;
        private GUIStyle _textFieldStyle;
        private GUIStyle _buttonStyle;
        private GUIStyle _listStyle;
        private GUIStyle _worldLabelStyle;
        private Texture2D _panelTex;
        private Texture2D _fieldTex;
        private Texture2D _buttonTex;
        private Texture2D _buttonHoverTex;
        private Texture2D _worldLabelTex;
        private bool _stylesReady;
        private bool _dumpedLayout;
        private Button _inventoryButton;
        private Button _template;
        private RectTransform _gridRect;
        private RectTransform _panelRect;

        private Texture2D _ring;
        private Texture2D _arrow;

        private static Dictionary<string, string> _lang = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private void Awake()
        {
            Log = Logger;
            LoadLanguage();

            _showButton = Config.Bind("UI", "Show Inventory Button", true, new ConfigDescription(T("ShowButton", "Show a search button next to the inventory grid.")));
            _anchorToInventory = Config.Bind("UI", "Anchor To Inventory", true, new ConfigDescription(T("Anchor", "Attach the button to the inventory panel. Turn off to attach it to the item grid instead, for custom inventory layouts.")));
            _buttonX = Config.Bind("UI", "Button X Percent", 100f, new ConfigDescription(T("BtnX", "Horizontal position in percent of the anchored panel width. 100 = flush with the right edge, 0 = left edge."), new AcceptableValueRange<float>(0f, 100f)));
            _buttonY = Config.Bind("UI", "Button Y Pixels", -6f, new ConfigDescription(T("BtnY", "Pixels from the bottom edge of the anchored panel. Negative puts the button below it, positive moves it up inside the panel."), new AcceptableValueRange<float>(-250f, 250f)));
            _searchRadius = Config.Bind("Search", "Search Radius", 0f, new ConfigDescription(T("Radius", "Only list chests within this many meters. 0 = every loaded container."), new AcceptableValueRange<float>(0f, 500f)));
            _pinyin = Config.Bind("Search", "Pinyin Search", true, new ConfigDescription(T("Pinyin", "Allow pinyin initials, e.g. 'td' finds 铁锭.")));
            _maxResults = Config.Bind("Search", "Max Results", 24, new ConfigDescription(T("MaxResults", "How many results to show."), new AcceptableValueRange<int>(5, 60)));
            _includeShips = Config.Bind("Search", "Include Ships And Carts", true, new ConfigDescription(T("IncludeShips", "Include containers on ships and carts.")));

            _sound = Config.Bind("Guide", "Use Sound", true, new ConfigDescription(T("Sound", "Play the wishbone ping while a target is selected.")));
            _soundInterval = Config.Bind("Guide", "Sound Interval", 1.5f, new ConfigDescription(T("SoundInterval", "Seconds between pings when far away; it speeds up as you get closer."), new AcceptableValueRange<float>(0.4f, 5f)));
            _markerScale = Config.Bind("Guide", "Marker Scale", 1f, new ConfigDescription(T("MarkerScale", "Size multiplier for the on-screen marker and arrow."), new AcceptableValueRange<float>(0.5f, 3f)));

            _measureKey = Config.Bind("Measure", "Measure Hotkey", new KeyboardShortcut(KeyCode.P), new ConfigDescription(T("MeasureKey", "First press sets the start point, second sets the end point, third clears it.")));
            _measurePlayer = Config.Bind("Measure", "Show Player Distance", true, new ConfigDescription(T("MeasurePlayer", "Also show the live distance from you to the start point.")));
            _measureRange = Config.Bind("Measure", "Place Range", 60f, new ConfigDescription(T("MeasureRange", "How far you can place a point with the crosshair."), new AcceptableValueRange<float>(5f, 200f)));

            MakeTextures();

            try
            {
                _harmony = new Harmony(Guid);
                _harmony.PatchAll(typeof(TextInputVisibilityPatch));
                _harmony.PatchAll(typeof(ZInputUsePatch));
                _harmony.PatchAll(typeof(InventoryGuiHideGuard));
            }
            catch (Exception e)
            {
                Logger.LogWarning("Could not apply input patches: " + e.Message);
            }

            LogPatchState();
            Logger.LogInfo("Item Finder 1.3.2 loaded.");
        }

        private void LogPatchState()
        {
            try
            {
                MethodInfo down = AccessTools.Method(typeof(ZInput), "GetButtonDown", new[] { typeof(string) });
                MethodInfo hide = AccessTools.Method(typeof(InventoryGui), "Hide");
                Logger.LogInfo("Patch state: ZInput.GetButtonDown = " + DescribePatches(down) + " | InventoryGui.Hide = " + DescribePatches(hide));
            }
            catch (Exception e)
            {
                Logger.LogWarning("Could not inspect Harmony patches: " + e.Message);
            }
        }

        private static string DescribePatches(MethodBase method)
        {
            if (method == null) return "missing method";
            Patches info = Harmony.GetPatchInfo(method);
            if (info == null || info.Prefixes == null || info.Prefixes.Count == 0) return "no prefixes";

            StringBuilder sb = new StringBuilder();
            bool mine = false;
            for (int i = 0; i < info.Prefixes.Count; i++)
            {
                Patch p = info.Prefixes[i];
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(p.owner);
                if (p.owner == Guid) mine = true;
            }
            sb.Append(mine ? " [ours present]" : " [OURS MISSING]");
            return sb.ToString();
        }

        private void Update()
        {
            try
            {
                _pulse += Time.unscaledDeltaTime;
                KeepInventoryButton();

                if (_searchOpen && Input.GetKeyDown(KeyCode.Escape))
                {
                    CloseSearch();
                    return;
                }
                if (_searchOpen && ZInput.GetButtonDown("Inventory")) CloseSearch();
                if (_searchOpen && !InventoryGui.IsVisible()) CloseSearch();

                bool uiBlocked = _searchOpen || InventoryGui.IsVisible() || Console.IsVisible() || (Chat.instance != null && Chat.instance.HasFocus()) || Menu.IsVisible();
                if (!uiBlocked && _measureKey.Value.IsDown()) PlaceMeasurePoint();

                if (_target != null && _sound.Value)
                {
                    float dist = DistanceTo(_target.transform.position);
                    float interval = Mathf.Clamp(_soundInterval.Value * (0.35f + dist / 40f), 0.35f, _soundInterval.Value);
                    _soundTimer -= Time.deltaTime;
                    if (_soundTimer <= 0f)
                    {
                        _soundTimer = interval;
                        PlayPing(dist);
                    }
                }
            }
            catch (Exception e)
            {
                Logger.LogError("Item Finder update failed: " + e);
            }
        }

        // ---------------------------------------------------------------- inventory button
        private void KeepInventoryButton()
        {
            InventoryGui gui = InventoryGui.instance;
            if (gui == null) return;

            bool visible = InventoryGui.IsVisible();
            if (!_showButton.Value || !visible)
            {
                if (_inventoryButton != null) _inventoryButton.gameObject.SetActive(false);
                return;
            }

            if (_inventoryButton == null || _template == null) TryCreateInventoryButton(gui);
            if (_inventoryButton != null)
            {
                _inventoryButton.gameObject.SetActive(true);
                ApplyButtonPlacement(gui);
            }

            DumpInventoryLayout(gui);
        }

        // Position is re-applied every frame so the config can be tweaked live
        // (ConfigurationManager) and so the button follows the panel when extra
        // inventory rows are unlocked and the panel grows.
        private void ApplyButtonPlacement(InventoryGui gui)
        {
            if (_inventoryButton == null) return;

            RectTransform rt = _inventoryButton.transform as RectTransform;
            if (rt == null) return;

            RectTransform anchor = _anchorToInventory.Value ? InventoryPanel(gui) : _gridRect;
            if (anchor == null) anchor = _gridRect;
            if (anchor == null) return;

            float x = Mathf.Clamp(_buttonX != null ? _buttonX.Value : 100f, 0f, 100f) / 100f;
            float y = _buttonY != null ? _buttonY.Value : -6f;

            bool needsMove = rt.parent != anchor
                || Mathf.Abs(rt.anchorMin.x - x) > 0.0001f
                || Mathf.Abs(rt.anchorMax.x - x) > 0.0001f
                || Mathf.Abs(rt.anchoredPosition.y - y) > 0.01f;
            if (!needsMove) return;

            if (rt.parent != anchor) rt.SetParent(anchor, false);
            rt.anchorMin = new Vector2(x, 0f);
            rt.anchorMax = new Vector2(x, 0f);
            rt.pivot = new Vector2(1f, 1f);
            rt.anchoredPosition = new Vector2(0f, y);
            rt.localScale = Vector3.one;
        }

        private RectTransform InventoryPanel(InventoryGui gui)
        {
            if (_panelRect != null) return _panelRect;

            // m_inventoryRoot is the whole screen; the visible brown panel is the
            // "Bkg" image inside the vanilla "Player" group and it grows together
            // with the extra inventory rows.
            if (gui.m_player != null)
            {
                Transform bkg = gui.m_player.Find("Bkg");
                if (bkg != null) _panelRect = bkg as RectTransform;
                if (_panelRect == null) _panelRect = gui.m_player;
            }
            return _panelRect;
        }

        // One-shot layout dump, so the button anchor can be checked against the
        // rects the game really uses (rows unlocked by quests change them).
        private void DumpInventoryLayout(InventoryGui gui)
        {
            if (_dumpedLayout) return;
            _dumpedLayout = true;
            try
            {
                Logger.LogInfo("INVDUMP screen=" + Screen.width + "x" + Screen.height);
                FieldInfo gridField = typeof(InventoryGui).GetField("m_playerGrid", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                Component grid = gridField != null ? gridField.GetValue(gui) as Component : null;
                RectTransform gridRect = grid != null ? grid.GetComponent<RectTransform>() : null;

                DumpRectNode("m_inventoryRoot", gui.m_inventoryRoot as RectTransform);
                DumpRectNode("m_player", gui.m_player);
                DumpRectNode("m_container", gui.m_container);
                DumpRectNode("m_info", gui.m_info);
                DumpRectNode("m_playerGrid", gridRect);

                int count = 0;
                WalkRects(gui.transform as RectTransform, 0, ref count);
                Logger.LogInfo("INVDUMP end, " + count + " nodes");
            }
            catch (Exception e)
            {
                Logger.LogWarning("Inventory layout dump failed: " + e.Message);
            }
        }

        private void WalkRects(RectTransform rt, int depth, ref int count)
        {
            if (rt == null || depth > 5 || count >= 150) return;

            Vector3[] corners = new Vector3[4];
            rt.GetWorldCorners(corners);
            Vector2 bl = RectTransformUtility.WorldToScreenPoint(null, corners[0]);
            Vector2 tr = RectTransformUtility.WorldToScreenPoint(null, corners[2]);
            float w = tr.x - bl.x;
            float h = tr.y - bl.y;

            if (w > 40f && h > 40f)
            {
                Image img = rt.GetComponent<Image>();
                string sprite = img == null ? "-" : (img.sprite != null ? img.sprite.name : "flat");
                Logger.LogInfo(string.Format(
                    "INVDUMP n d{0} '{1}' img={2} px=({3:F0},{4:F0})-({5:F0},{6:F0}) norm=({7:F3},{8:F3})-({9:F3},{10:F3})",
                    depth, RectPath(rt), sprite, bl.x, bl.y, tr.x, tr.y,
                    bl.x / Screen.width, bl.y / Screen.height,
                    tr.x / Screen.width, tr.y / Screen.height));
                count++;
            }

            for (int i = 0; i < rt.childCount; i++)
            {
                WalkRects(rt.GetChild(i) as RectTransform, depth + 1, ref count);
            }
        }

        private void DumpRectNode(string label, RectTransform rt)
        {
            if (rt == null)
            {
                Logger.LogInfo("INVDUMP " + label + " = null");
                return;
            }

            Vector3[] corners = new Vector3[4];
            rt.GetWorldCorners(corners);
            string screen;
            try
            {
                Vector2 bl = RectTransformUtility.WorldToScreenPoint(null, corners[0]);
                Vector2 tr = RectTransformUtility.WorldToScreenPoint(null, corners[2]);
                screen = string.Format("screen=({0:F0},{1:F0})-({2:F0},{3:F0})", bl.x, bl.y, tr.x, tr.y);
            }
            catch
            {
                screen = "screen=?";
            }

            Logger.LogInfo(string.Format(
                "INVDUMP {0} path='{1}' anchorMin={2} anchorMax={3} pivot={4} sizeDelta={5} rect={6} {7}",
                label, RectPath(rt), rt.anchorMin, rt.anchorMax, rt.pivot, rt.sizeDelta, rt.rect.size, screen));
        }

        private static string RectPath(Transform t)
        {
            string result = t.name;
            Transform p = t.parent;
            int guard = 0;
            while (p != null && guard++ < 6)
            {
                result = p.name + "/" + result;
                p = p.parent;
            }
            return result;
        }

        private void TryCreateInventoryButton(InventoryGui gui)
        {
            try
            {
                _template = gui.m_takeAllButton;
                if (_template == null) return;

                FieldInfo gridField = typeof(InventoryGui).GetField("m_playerGrid", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                Component grid = gridField != null ? gridField.GetValue(gui) as Component : null;
                RectTransform gridRect = grid != null ? grid.GetComponent<RectTransform>() : null;
                RectTransform templateRect = _template.GetComponent<RectTransform>();
                if (gridRect == null || templateRect == null) return;

                _gridRect = gridRect;
                _panelRect = null;

                RectTransform anchor = _anchorToInventory.Value ? InventoryPanel(gui) : gridRect;
                if (anchor == null) anchor = gridRect;

                GameObject clone = UnityEngine.Object.Instantiate(_template.gameObject, anchor);
                clone.name = "ItemFinder_SearchButton";
                _inventoryButton = clone.GetComponent<Button>();
                if (_inventoryButton == null) return;

                _inventoryButton.onClick.RemoveAllListeners();
                _inventoryButton.onClick.AddListener(new UnityAction(OpenSearch));
                SetButtonLabel(_inventoryButton, T("Button", "查找"));

                RectTransform rt = clone.GetComponent<RectTransform>();
                rt.SetParent(anchor, false);
                rt.sizeDelta = new Vector2(140f, Mathf.Max(templateRect.sizeDelta.y, 40f));
                rt.localScale = Vector3.one;
            }
            catch (Exception e)
            {
                Logger.LogWarning("Could not create the inventory button: " + e.Message);
            }
        }

        private static void SetButtonLabel(Button button, string label)
        {
            if (button == null) return;
            Text[] texts = button.GetComponentsInChildren<Text>(true);
            for (int i = 0; i < texts.Length; i++) texts[i].text = label;
            TMP_Text[] tmps = button.GetComponentsInChildren<TMP_Text>(true);
            for (int i = 0; i < tmps.Length; i++) tmps[i].text = label;
        }

        // ---------------------------------------------------------------- measure
        private void PlaceMeasurePoint()
        {
            Player p = Player.m_localPlayer;
            Camera cam = Camera.main;
            if (p == null || cam == null) return;

            RaycastHit hit;
            if (!Physics.Raycast(cam.transform.position, cam.transform.forward, out hit, _measureRange.Value, ~0, QueryTriggerInteraction.Ignore))
            {
                p.Message(MessageHud.MessageType.Center, T("MeasureMiss", "对着地面或建筑按 P"));
                return;
            }

            if (_measureStage == 0)
            {
                _pointA = hit.point;
                _pointB = Vector3.zero;
                _measureStage = 1;
                p.Message(MessageHud.MessageType.Center, T("MeasureStart", "起点已记录，再按一次设置终点"));
            }
            else if (_measureStage == 1)
            {
                _pointB = hit.point;
                _measureStage = 2;
                p.Message(MessageHud.MessageType.Center, string.Format(T("MeasureDone", "起点 → 终点：{0:F1} 米"), Vector3.Distance(_pointA, _pointB)));
            }
            else
            {
                _measureStage = 0;
                _pointA = Vector3.zero;
                _pointB = Vector3.zero;
                p.Message(MessageHud.MessageType.Center, T("MeasureCleared", "测量已清除"));
            }
        }

        // ---------------------------------------------------------------- search data
        private void OpenSearch()
        {
            _searchOpen = true;
            WindowOpen = true;
            _focusSearch = true;
            _query = "";
            PositionWindow();
            BuildIndex();
            Refresh();
        }

        private void CloseSearch()
        {
            _searchOpen = false;
            WindowOpen = false;
            _query = "";
            _results.Clear();
            _suggestions.Clear();
        }

        private void PositionWindow()
        {
            _windowRect.width = Mathf.Clamp(Screen.width * 0.44f, 680f, 900f);
            _windowRect.height = Mathf.Clamp(Screen.height * 0.72f, 620f, 920f);
            _windowRect.x = (Screen.width - _windowRect.width) * 0.5f;
            _windowRect.y = (Screen.height - _windowRect.height) * 0.5f;
        }

        private void BuildIndex()
        {
            _index.Clear();
            Player p = Player.m_localPlayer;
            if (p == null) return;

            Vector3 origin = p.transform.position;
            Container[] boxes = UnityEngine.Object.FindObjectsOfType<Container>();
            for (int i = 0; i < boxes.Length; i++)
            {
                Container box = boxes[i];
                if (box == null) continue;

                Inventory inv = box.GetInventory();
                if (inv == null) continue;

                float dist = Vector3.Distance(origin, box.transform.position);
                if (_searchRadius.Value > 0f && dist > _searchRadius.Value) continue;
                if (!_includeShips.Value && IsVehicle(box)) continue;

                List<ItemDrop.ItemData> items = inv.GetAllItems();
                for (int j = 0; j < items.Count; j++)
                {
                    ItemDrop.ItemData item = items[j];
                    if (item == null || item.m_shared == null) continue;

                    string token = item.m_shared.m_name;
                    string key = token + "@" + box.GetInstanceID();

                    Entry existing = null;
                    for (int k = 0; k < _index.Count; k++)
                    {
                        if (_index[k].Key == key) { existing = _index[k]; break; }
                    }
                    if (existing != null)
                    {
                        existing.Count += item.m_stack;
                        continue;
                    }

                    string display = Localize(token);
                    _index.Add(new Entry
                    {
                        Box = box,
                        Display = display,
                        English = item.m_dropPrefab != null ? item.m_dropPrefab.name : token,
                        Initials = _pinyin.Value ? Pinyin.Initials(display) : "",
                        Key = key,
                        Count = item.m_stack,
                        Distance = dist
                    });
                }
            }
        }

        private static bool IsVehicle(Container box)
        {
            return box.GetComponentInParent<Ship>() != null || box.GetComponentInParent<Vagon>() != null;
        }

        private void Refresh()
        {
            _results.Clear();
            _suggestions.Clear();

            string q = Normalize(_query);
            if (q.Length == 0) return;

            HashSet<string> seen = new HashSet<string>();
            for (int i = 0; i < _index.Count; i++)
            {
                Entry e = _index[i];
                int score = Match(q, e);
                if (score < 0) continue;

                e.Distance = DistanceTo(e.Box.transform.position);
                _results.Add(e);

                string pair = e.Display + "|" + e.English;
                if (seen.Add(pair) && _suggestions.Count < 10)
                {
                    _suggestions.Add(e.Display + "  –  " + e.English);
                }
            }

            _results.Sort(delegate (Entry a, Entry b)
            {
                int sa = Match(q, a), sb = Match(q, b);
                if (sa != sb) return sa.CompareTo(sb);
                return a.Distance.CompareTo(b.Distance);
            });

            if (_results.Count > _maxResults.Value) _results.RemoveRange(_maxResults.Value, _results.Count - _maxResults.Value);
            _listScroll = Vector2.zero;
        }

        private int Match(string q, Entry e)
        {
            string display = Normalize(e.Display);
            string english = Normalize(e.English);

            if (display == q || english == q) return 0;
            if (display.StartsWith(q) || english.StartsWith(q)) return 1;
            if (display.Contains(q) || english.Contains(q)) return 2;
            if (e.Initials.Length > 0 && e.Initials.Contains(q)) return 3;
            return -1;
        }

        private static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (char.IsWhiteSpace(c) || c == '$' || c == '_' || c == '-' || c == '.') continue;
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        // ---------------------------------------------------------------- GUI
        private void OnGUI()
        {
            try
            {
                if (_searchOpen)
                {
                    GUI.depth = -2000;
                    if (Event.current.type == EventType.MouseDown && !_windowRect.Contains(Event.current.mousePosition)) Event.current.Use();
                    EnsureStyles();
                    _windowRect = GUI.Window(735881, _windowRect, DrawWindow, T("Title", "物品查找 / Item Search"), _windowStyle);
                }

                EnsureWorldStyles();
                DrawGuidance();
                DrawMeasure();
            }
            catch (Exception e)
            {
                Logger.LogError("Item Finder GUI failed: " + e);
            }
        }

        private void DrawWindow(int id)
        {
            Event current = Event.current;
            if (current.type == EventType.KeyDown && (current.keyCode == KeyCode.Return || current.keyCode == KeyCode.KeypadEnter))
            {
                current.Use();
                if (_results.Count > 0) SelectResult(0);
                return;
            }
            if (current.type == EventType.KeyDown && current.keyCode == KeyCode.Escape)
            {
                current.Use();
                CloseSearch();
                return;
            }

            GUILayout.Label(T("WindowHint", "查找附近箱子里的物品"), _labelStyle);
            GUI.SetNextControlName("ItemFinderSearch");
            string typed = GUILayout.TextField(_query ?? "", _textFieldStyle, GUILayout.Height(32f));
            if (_focusSearch)
            {
                GUI.FocusControl("ItemFinderSearch");
                _focusSearch = false;
            }
            if (typed != _query)
            {
                _query = typed;
                Refresh();
            }

            if (GUILayout.Button(T("SearchBtn", "搜索"), _buttonStyle, GUILayout.Height(32f)))
            {
                BuildIndex();
                Refresh();
            }

            GUILayout.Space(10f);
            GUILayout.Label(T("SuggestHeader", "搜索建议"), _labelStyle);
            for (int i = 0; i < _suggestions.Count; i++)
            {
                GUILayout.Label(_suggestions[i], _listStyle);
            }

            GUILayout.Space(8f);
            GUILayout.Label(T("MatchHeader", "附近匹配"), _labelStyle);

            _listScroll = GUILayout.BeginScrollView(_listScroll);
            for (int i = 0; i < _results.Count; i++)
            {
                Entry e = _results[i];
                string text = string.Format("{0}  –  {1}    ({2:0} m)  x{3}", e.Display, e.English, e.Distance, e.Count);
                if (GUILayout.Button(text, _listStyle, GUILayout.Height(27f)))
                {
                    SelectResult(i);
                    return;
                }
            }
            GUILayout.EndScrollView();

            GUILayout.Label(_results.Count == 0 && _query.Length > 0 ? T("Empty", "附近没有找到这个物品") : T("Footer", "回车选第一个结果，Esc 关闭"), _listStyle);
        }

        private void SelectResult(int index)
        {
            if (index < 0 || index >= _results.Count) return;
            Entry e = _results[index];
            _target = e.Box;
            _soundTimer = 0f;
            CloseSearch();
            InventoryGui gui = InventoryGui.instance;
            if (gui != null) gui.Hide();
            Player p = Player.m_localPlayer;
            if (p != null) p.Message(MessageHud.MessageType.Center, string.Format(T("Selected", "已锁定目标：{0}"), e.Display));
        }

        // ---------------------------------------------------------------- guidance
        private void DrawGuidance()
        {
            if (_target == null) return;

            if (DistanceTo(_target.transform.position) < 2.5f)
            {
                _target = null;
                Player p = Player.m_localPlayer;
                if (p != null) p.Message(MessageHud.MessageType.Center, T("Arrived", "已到达目标箱子"));
                return;
            }

            Vector3 pos = _target.transform.position + Vector3.up * 0.6f;
            DrawWorldMarker(pos, T("Target", "箱子"), new Color(0.55f, 1f, 0.6f, 1f));
            DrawCenterArrow(pos);

            Camera cam = Camera.main;
            if (cam != null)
            {
                Vector3 sp = cam.WorldToScreenPoint(pos);
                if (sp.z >= 0f)
                {
                    GUI.Label(new Rect(Screen.width / 2f - 90f, Screen.height / 2f - 150f, 180f, 26f),
                        string.Format(T("Distance", "{0:F1} 米"), DistanceTo(pos)), _labelStyle);
                }
            }
        }

        private void DrawWorldMarker(Vector3 world, string text, Color color)
        {
            Camera cam = Camera.main;
            if (cam == null) return;

            Vector3 sp = cam.WorldToScreenPoint(world);
            if (sp.z < 0f)
            {
                sp.x = Screen.width - sp.x;
                sp.y = Screen.height - sp.y;
            }

            float x = Mathf.Clamp(sp.x, 30f, Screen.width - 30f);
            float y = Mathf.Clamp(Screen.height - sp.y, 30f, Screen.height - 30f);

            float dist = DistanceTo(world);
            float pulse = 0.75f + 0.25f * Mathf.Sin(_pulse * Mathf.Lerp(6f, 14f, Mathf.Clamp01(1f - dist / 40f)));
            float size = Mathf.Clamp(26f + 140f / Mathf.Max(dist, 2f), 22f, 90f) * _markerScale.Value * pulse;

            GUI.color = color;
            GUI.DrawTexture(new Rect(x - size / 2f, y - size / 2f, size, size), _ring);
            GUI.color = Color.white;

            GUIContent content = new GUIContent(text);
            Vector2 textSize = _worldLabelStyle.CalcSize(content);
            float labelW = textSize.x + 18f;
            float labelH = textSize.y + 12f;
            GUI.Label(new Rect(x + size / 2f + 6f, y - labelH / 2f, labelW, labelH), content, _worldLabelStyle);
        }

        private void DrawCenterArrow(Vector3 world)
        {
            Camera cam = Camera.main;
            if (cam == null) return;

            Vector3 sp = cam.WorldToScreenPoint(world);
            Vector2 screenTarget = new Vector2(sp.x, Screen.height - sp.y);
            Vector2 center = new Vector2(Screen.width / 2f, Screen.height / 2f);
            Vector2 dir = screenTarget - center;
            if (dir.sqrMagnitude < 4f) return;

            float dist = DistanceTo(world);
            float scale = Mathf.Clamp(90f / Mathf.Max(dist, 3f), 0.5f, 2.2f) * _markerScale.Value;
            float angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg + 90f;

            float size = 34f * scale;
            GUIUtility.RotateAroundPivot(angle, center);
            GUI.color = new Color(0.55f, 1f, 0.6f, 0.85f);
            GUI.DrawTexture(new Rect(center.x - size / 2f, center.y - 90f - size, size, size), _arrow);
            GUI.color = Color.white;
            GUIUtility.RotateAroundPivot(-angle, center);
        }

        private void DrawMeasure()
        {
            if (_measureStage == 0) return;

            if (_measureStage >= 1) DrawWorldMarker(_pointA, T("PointA", "起点"), new Color(1f, 0.86f, 0.42f, 1f));
            if (_measureStage >= 2) DrawWorldMarker(_pointB, T("PointB", "终点"), new Color(1f, 0.6f, 0.35f, 1f));

            Player p = Player.m_localPlayer;
            if (p == null) return;

            string text = "";
            if (_measureStage >= 2)
            {
                text = string.Format(T("MeasureAB", "起点 → 终点：{0:F1} 米"), Vector3.Distance(_pointA, _pointB));
                if (_measurePlayer.Value)
                {
                    text += "\n" + string.Format(T("MeasureAP", "你 → 起点：{0:F1} 米"), Vector3.Distance(p.transform.position, _pointA));
                }
            }
            else if (_measurePlayer.Value)
            {
                text = string.Format(T("MeasureAP", "你 → 起点：{0:F1} 米"), Vector3.Distance(p.transform.position, _pointA));
            }

            if (text.Length > 0)
            {
                GUI.Label(new Rect(Screen.width / 2f - 170f, 56f, 340f, 54f), text, _labelStyle);
            }
        }

        // ---------------------------------------------------------------- styles
        private void EnsureStyles()
        {
            if (_stylesReady) return;

            Color panel = new Color(0.105f, 0.075f, 0.065f, 1f);
            Color button = new Color(0.22f, 0.12f, 0.06f, 1f);
            Color buttonHover = new Color(0.38f, 0.2f, 0.08f, 1f);
            Color text = new Color(1f, 0.91f, 0.7f, 1f);

            _panelTex = MakeTexture(panel);
            _fieldTex = MakeTexture(new Color(0.07f, 0.05f, 0.035f, 1f));
            _buttonTex = MakeTexture(button);
            _buttonHoverTex = MakeTexture(buttonHover);

            _windowStyle = new GUIStyle(GUI.skin.window);
            _windowStyle.fontSize = 20;
            _windowStyle.alignment = TextAnchor.UpperCenter;
            _windowStyle.padding = new RectOffset(14, 14, 38, 14);
            SetStyleState(_windowStyle.normal, _panelTex, text);
            SetStyleState(_windowStyle.hover, _panelTex, text);
            SetStyleState(_windowStyle.active, _panelTex, text);
            SetStyleState(_windowStyle.focused, _panelTex, text);
            SetStyleState(_windowStyle.onNormal, _panelTex, text);
            SetStyleState(_windowStyle.onHover, _panelTex, text);
            SetStyleState(_windowStyle.onActive, _panelTex, text);
            SetStyleState(_windowStyle.onFocused, _panelTex, text);

            _labelStyle = new GUIStyle(GUI.skin.label);
            _labelStyle.fontSize = 18;
            _labelStyle.normal.textColor = text;

            _textFieldStyle = new GUIStyle(GUI.skin.textField);
            _textFieldStyle.fontSize = 19;
            _textFieldStyle.normal.textColor = Color.white;
            _textFieldStyle.normal.background = _fieldTex;

            _buttonStyle = new GUIStyle(GUI.skin.button);
            _buttonStyle.fontSize = 18;
            _buttonStyle.fontStyle = FontStyle.Bold;
            _buttonStyle.normal.textColor = text;
            _buttonStyle.normal.background = _buttonTex;
            _buttonStyle.hover.textColor = Color.white;
            _buttonStyle.hover.background = _buttonHoverTex;

            _listStyle = new GUIStyle(GUI.skin.label);
            _listStyle.fontSize = 17;
            _listStyle.normal.textColor = Color.white;

            _stylesReady = true;
        }

        private void EnsureWorldStyles()
        {
            if (_worldLabelStyle != null) return;
            _worldLabelTex = MakeTexture(new Color(0.08f, 0.05f, 0.03f, 0.9f));
            _worldLabelStyle = new GUIStyle(GUI.skin.box);
            _worldLabelStyle.fontSize = 22;
            _worldLabelStyle.fontStyle = FontStyle.Bold;
            _worldLabelStyle.alignment = TextAnchor.MiddleCenter;
            _worldLabelStyle.wordWrap = false;
            _worldLabelStyle.clipping = TextClipping.Overflow;
            _worldLabelStyle.padding = new RectOffset(8, 8, 4, 4);
            _worldLabelStyle.border = new RectOffset(0, 0, 0, 0);
            _worldLabelStyle.normal.textColor = new Color(1f, 0.82f, 0.12f, 1f);
            _worldLabelStyle.normal.background = _worldLabelTex;
        }

        private static void SetStyleState(GUIStyleState state, Texture2D texture, Color color)
        {
            state.background = texture;
            state.textColor = color;
        }

        private static Texture2D MakeTexture(Color color)
        {
            Texture2D tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.SetPixel(0, 0, color);
            tex.Apply();
            return tex;
        }

        private void MakeTextures()
        {
            int s = 64;
            _ring = new Texture2D(s, s, TextureFormat.RGBA32, false);
            for (int y = 0; y < s; y++)
            {
                for (int x = 0; x < s; x++)
                {
                    float dx = x - s / 2f + 0.5f, dy = y - s / 2f + 0.5f;
                    float r = Mathf.Sqrt(dx * dx + dy * dy) / (s / 2f);
                    float a = Mathf.Clamp01(1f - Mathf.Abs(r - 0.82f) * 6f) * Mathf.Clamp01(1f - r * 0.2f);
                    _ring.SetPixel(x, y, new Color(1f, 1f, 1f, a * 0.9f));
                }
            }
            _ring.Apply();

            int a2 = 48;
            _arrow = new Texture2D(a2, a2, TextureFormat.RGBA32, false);
            for (int y = 0; y < a2; y++)
            {
                for (int x = 0; x < a2; x++)
                {
                    float nx = (x - a2 / 2f) / (a2 / 2f);
                    float ny = (y - a2 / 2f) / (a2 / 2f);
                    bool inside = ny > -0.2f && Mathf.Abs(nx) < (1f - ny) * 0.95f;
                    _arrow.SetPixel(x, y, inside ? new Color(1f, 1f, 1f, 1f) : new Color(1f, 1f, 1f, 0f));
                }
            }
            _arrow.Apply();
        }

        // ---------------------------------------------------------------- helpers
        private float DistanceTo(Vector3 pos)
        {
            Player p = Player.m_localPlayer;
            if (p == null) return 0f;
            return Vector3.Distance(p.transform.position, pos);
        }

        private void PlayPing(float dist)
        {
            try
            {
                ObjectDB db = ObjectDB.instance;
                Player p = Player.m_localPlayer;
                if (db == null || p == null) return;

                for (int i = 0; i < db.m_StatusEffects.Count; i++)
                {
                    SE_Finder finder = db.m_StatusEffects[i] as SE_Finder;
                    if (finder == null) continue;

                    EffectList list = dist < 15f ? finder.m_pingEffectNear : (dist < 40f ? finder.m_pingEffectMed : finder.m_pingEffectFar);
                    list.Create(p.transform.position, p.transform.rotation, p.transform, 1f);
                    return;
                }
            }
            catch
            {
                // sound is optional
            }
        }

        // ---------------------------------------------------------------- localization
        private void LoadLanguage()
        {
            try
            {
                string lang = PlayerPrefs.GetString("language", "English");
                if (string.IsNullOrEmpty(lang)) lang = "English";

                string dir = Path.GetDirectoryName(Info.Location);
                string file = Path.Combine(dir, "ItemFinder." + lang + ".yml");
                if (!File.Exists(file)) file = Path.Combine(dir, "ItemFinder.English.yml");
                if (!File.Exists(file)) return;

                string[] lines = File.ReadAllLines(file, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int sep = line.IndexOf(':');
                    if (sep <= 0) continue;
                    string key = line.Substring(0, sep).Trim();
                    string value = line.Substring(sep + 1).Trim();
                    if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
                    {
                        value = value.Substring(1, value.Length - 2).Replace("\\\"", "\"").Replace("\\n", "\n");
                    }
                    _lang[key] = value;
                }
            }
            catch
            {
                // fall back to built-in text
            }
        }

        private static string T(string key, string fallback)
        {
            string value;
            if (_lang.TryGetValue(key, out value) && !string.IsNullOrEmpty(value)) return value;
            return fallback;
        }

        // ---------------------------------------------------------------- game string localization
        private static MethodInfo _localizeMethod;
        private static object _localizeInstance;
        private static readonly Dictionary<string, string> NameCache = new Dictionary<string, string>();

        private static string Localize(string token)
        {
            if (string.IsNullOrEmpty(token)) return "";
            string cached;
            if (NameCache.TryGetValue(token, out cached)) return cached;

            string result = token;
            try
            {
                if (_localizeMethod == null && _localizeInstance == null) ResolveLocalizer();
                if (_localizeMethod != null && _localizeInstance != null)
                {
                    string localized = (string)_localizeMethod.Invoke(_localizeInstance, new object[] { token });
                    if (!string.IsNullOrEmpty(localized)) result = localized;
                }
            }
            catch
            {
                result = token;
            }

            NameCache[token] = result;
            return result;
        }

        private static void ResolveLocalizer()
        {
            Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                Type[] types;
                try { types = asms[i].GetTypes(); }
                catch { continue; }
                for (int j = 0; j < types.Length; j++)
                {
                    if (types[j].Name != "Localization") continue;
                    Type t = types[j];
                    PropertyInfo[] props = t.GetProperties(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    for (int k = 0; k < props.Length; k++)
                    {
                        if (props[k].PropertyType != t) continue;
                        try { _localizeInstance = props[k].GetValue(null); } catch { }
                        if (_localizeInstance != null) break;
                    }
                    _localizeMethod = t.GetMethod("Localize", new[] { typeof(string) });
                    return;
                }
            }
        }
    }

    [HarmonyPatch(typeof(TextInput), "IsVisible")]
    internal static class TextInputVisibilityPatch
    {
        private static bool Prefix(ref bool __result)
        {
            if (!Plugin.WindowOpen) return true;
            __result = true;
            return false;
        }
    }

    [HarmonyPatch(typeof(ZInput), "GetButtonDown")]
    internal static class ZInputUsePatch
    {
        // InventoryGui.Update closes the inventory when ZInput.GetButtonDown("Use") is true.
        // Swallowing only the "Use" button while the search window is open is enough,
        // and it is far less fragile than rewriting that call with a transpiler.
        private static bool Prefix(string name, ref bool __result)
        {
            if (!Plugin.WindowOpen) return true;
            if (name == "Use")
            {
                Plugin.NoteSuppressedUse();
                __result = false;
                return false;
            }
            return true;
        }
    }

    // Last line of defence: whatever path tries to close the inventory (E via
    // InventoryGui.Update, another mod replacing that call, a stray Hide call),
    // the search window keeps the inventory open.
    [HarmonyPatch(typeof(InventoryGui), "Hide")]
    internal static class InventoryGuiHideGuard
    {
        private static bool Prefix()
        {
            if (!Plugin.WindowOpen) return true;

            Player p = Player.m_localPlayer;
            if (p == null || p.IsDead() || p.IsTeleporting() || p.InCutscene()) return true;

            Plugin.NoteBlockedHide();
            return false;
        }
    }
}


