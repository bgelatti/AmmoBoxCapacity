// Diagnostics (Debug builds only): dumps the PlayMaker FSMs of an ammo box, logs int/float actions that touch
// values near the box limit, and watches the dumped box so every variable and state change shows up in LogOutput.log.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace AmmoBoxCapacity
{
    internal class Diagnostics
    {
        internal static Diagnostics Instance;

        private readonly ConfigEntry<KeyCode> _dumpKey, _dumpManagerKey;
        private readonly ConfigEntry<bool> _logActions;
        private readonly ConfigEntry<int> _logMin, _logMax;
        private readonly ConfigEntry<float> _aimDistance;
        private readonly ConfigEntry<string> _boxKeywords, _contextKeywords;

        private string[] _boxWords = new string[0];
        private string[] _contextWords = new string[0];
        // Variable names containing one of these make an action run worth logging from any FSM
        private static readonly string[] VarWords = { "ammo", "bullet", "cartridge", "capacity", "caliber", "calibre" };

        internal Diagnostics(ConfigFile config, Harmony h)
        {
            Instance = this;
            _dumpKey = config.Bind("Debug", "DumpKey", KeyCode.F9,
                "Look at an ammo box (or stand within AimDistance of one) and press to write all of its PlayMaker FSMs to LogOutput.log. The box is then watched: every change of its variables and states is logged.");
            _dumpManagerKey = config.Bind("Debug", "DumpGameManagerKey", KeyCode.F10,
                "Writes the PlayMaker FSMs of __GameManager__ and the global variables to LogOutput.log (long).");
            _logActions = config.Bind("Debug", "LogActions", false,
                "Log int/float PlayMaker actions in ammo box FSMs, __GameManager__ and any FSM variable with 'ammo' in its name, when a value is between LogMin and LogMax. Takes effect after a restart; slows the game a little.");
            _logMin = config.Bind("Debug", "LogMin", 300, "Lowest value that makes an action run get logged.");
            _logMax = config.Bind("Debug", "LogMax", 2000, "Highest value that makes an action run get logged.");
            _aimDistance = config.Bind("Debug", "AimDistance", 5f,
                new ConfigDescription("How far an ammo box can be from the camera to be dumped (m).", new AcceptableValueRange<float>(1f, 20f)));
            _boxKeywords = config.Bind("Debug", "BoxKeywords", "ammo_box,ammobox,ammo box,ammo crate,ammocrate,ammo_crate",
                "Comma separated words; an object whose name (or a parent's name) contains one of them is an ammo box.");
            _contextKeywords = config.Bind("Debug", "ContextKeywords", "__GameManager__",
                "Comma separated words; action runs in FSMs on objects whose path contains one of them are logged too.");
            config.SettingChanged += (sender, e) => ReloadKeywords();
            ReloadKeywords();
            if (_logActions.Value) InstallHooks(h);
            Plugin.Log.LogInfo("Diagnostics on (Debug build): " + _dumpKey.Value + " dumps the aimed ammo box, " + _dumpManagerKey.Value + " dumps __GameManager__.");
        }

        private void ReloadKeywords()
        {
            _boxWords = Split(_boxKeywords.Value);
            _contextWords = Split(_contextKeywords.Value);
            Hooks.Min = _logMin.Value;
            Hooks.Max = _logMax.Value;
            Hooks.On = _logActions.Value;
        }

        // Runs every frame from the plugin's tick.
        internal void Update()
        {
            if (Input.GetKeyDown(_dumpKey.Value)) DumpAimedBox();
            if (Input.GetKeyDown(_dumpManagerKey.Value)) DumpGameManager();
            UpdateWatch();
        }

        private static string[] Split(string s)
        {
            var list = new List<string>();
            foreach (var p in (s ?? "").Split(','))
            {
                var t = p.Trim();
                if (t.Length > 0) list.Add(t);
            }
            return list.ToArray();
        }

        internal void OnSceneLoaded()
        {
            Hooks.Infos.Clear();
            _watch.Clear();
        }

        // ---------------------------------------------------------------- names

        internal static bool ContainsAny(string s, string[] words)
        {
            if (string.IsNullOrEmpty(s)) return false;
            for (int i = 0; i < words.Length; i++)
                if (s.IndexOf(words[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        internal static bool IsVarWord(string s) { return ContainsAny(s, VarWords); }

        internal static string PathOf(Transform t)
        {
            if (t == null) return "(null)";
            var sb = new StringBuilder(t.name);
            for (var p = t.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/");
            return sb.ToString();
        }

        // Topmost ancestor (or self) whose name contains a box keyword; null when there is none.
        private Transform BoxRoot(Transform t)
        {
            Transform found = null;
            for (var p = t; p != null; p = p.parent)
                if (ContainsAny(p.name, _boxWords)) found = p;
            return found;
        }

        internal bool IsContextPath(string path) { return ContainsAny(path, _contextWords) || ContainsAny(path, _boxWords); }

        // ---------------------------------------------------------------- aim

        private Camera[] _cameras = new Camera[8];

        private Camera GetCamera()
        {
            var cam = Camera.main;
            if (cam != null && cam.isActiveAndEnabled) return cam;
            if (_cameras.Length < Camera.allCamerasCount) _cameras = new Camera[Camera.allCamerasCount + 4];
            int n = Camera.GetAllCameras(_cameras);
            Camera best = null;
            for (int i = 0; i < n; i++)
            {
                var c = _cameras[i];
                if (c != null && c.isActiveAndEnabled && (best == null || c.depth > best.depth)) best = c;
                _cameras[i] = null;
            }
            return best;
        }

        internal void DumpAimedBox()
        {
            var cam = GetCamera();
            if (cam == null) { Plugin.Log.LogWarning("[dump] No camera."); return; }
            float maxDist = _aimDistance.Value;
            Vector3 eye = cam.transform.position;

            Transform target = null;
            Transform firstHit = null;
            var hits = Physics.RaycastAll(eye, cam.transform.forward, maxDist, ~0, QueryTriggerInteraction.Collide);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            var hitNames = new StringBuilder();
            foreach (var h in hits)
            {
                var t = h.collider.transform;
                hitNames.Append("\n   hit ").Append(h.distance.ToString("0.00")).Append(" m: ").Append(PathOf(t));
                if (firstHit == null && !h.collider.isTrigger) firstHit = t;
                if (target == null) target = BoxRoot(t);
            }
            string how = "aimed";

            // nothing aimed: the nearest ammo box within reach
            if (target == null)
            {
                float best = maxDist;
                foreach (var fsm in PlayMakerFSM.FsmList)
                {
                    if (fsm == null) continue;
                    var root = BoxRoot(fsm.transform);
                    if (root == null) continue;
                    float d = Vector3.Distance(eye, root.position);
                    if (d <= best) { best = d; target = root; }
                }
                if (target != null) how = "nearest (" + best.ToString("0.0") + " m)";
            }

            Plugin.Log.LogInfo("[dump] F9 pressed. Raycast hits:" + (hits.Length == 0 ? " none" : hitNames.ToString()));
            LogNearbyBoxes(eye);

            if (target == null)
            {
                if (firstHit == null) { Plugin.Log.LogWarning("[dump] No ammo box found within " + maxDist + " m."); return; }
                // no name match: dump what was hit, in case the box has a different name
                target = firstHit.GetComponentInParent<PlayMakerFSM>() != null ? firstHit.GetComponentInParent<PlayMakerFSM>().transform : firstHit;
                how = "no box name matched, dumping the aimed object";
            }

            var sb = new StringBuilder();
            sb.Append("[dump] ===== ").Append(PathOf(target)).Append(" (").Append(how).Append(") =====\n");
            AppendHierarchy(sb, target, 0);
            var fsms = target.GetComponentsInChildren<PlayMakerFSM>(true);
            if (fsms.Length == 0)
            {
                fsms = target.GetComponentsInParent<PlayMakerFSM>(true);
                sb.Append("No FSM in the box or its children; FSMs on its parents:\n");
            }
            foreach (var fsm in fsms) AppendFsm(sb, fsm);
            sb.Append("[dump] ===== end of ").Append(target.name).Append(" =====");
            Plugin.Log.LogInfo(sb.ToString());

            Watch(fsms);
        }

        private void LogNearbyBoxes(Vector3 eye)
        {
            var seen = new HashSet<Transform>();
            var sb = new StringBuilder("[dump] Ammo boxes within 30 m:");
            foreach (var fsm in PlayMakerFSM.FsmList)
            {
                if (fsm == null) continue;
                var root = BoxRoot(fsm.transform);
                if (root == null || !seen.Add(root)) continue;
                float d = Vector3.Distance(eye, root.position);
                if (d > 30f) continue;
                sb.Append("\n   ").Append(d.ToString("0.0")).Append(" m: ").Append(PathOf(root));
            }
            if (seen.Count == 0) sb.Append(" none with a PlayMakerFSM");
            Plugin.Log.LogInfo(sb.ToString());
        }

        internal void DumpGameManager()
        {
            var go = GameObject.Find("__GameManager__");
            var sb = new StringBuilder();
            if (go == null)
            {
                foreach (var fsm in PlayMakerFSM.FsmList)
                    if (fsm != null && fsm.transform.root.name.IndexOf("GameManager", StringComparison.OrdinalIgnoreCase) >= 0) { go = fsm.transform.root.gameObject; break; }
            }
            if (go == null) Plugin.Log.LogWarning("[dump] __GameManager__ not found.");
            else
            {
                sb.Append("[dump] ===== ").Append(PathOf(go.transform)).Append(" =====\n");
                foreach (var fsm in go.GetComponentsInChildren<PlayMakerFSM>(true)) AppendFsm(sb, fsm);
            }
            sb.Append("Global variables:\n");
            AppendVariables(sb, FsmVariables.GlobalVariables, "  ");
            sb.Append("[dump] ===== end of game manager =====");
            Plugin.Log.LogInfo(sb.ToString());
        }

        // ---------------------------------------------------------------- dump text

        private static void AppendHierarchy(StringBuilder sb, Transform t, int depth)
        {
            if (depth > 4) return;
            sb.Append(' ', 2 + depth * 2).Append(t.name).Append(t.gameObject.activeInHierarchy ? "" : " (inactive)").Append(" [");
            var comps = t.GetComponents<Component>();
            for (int i = 0; i < comps.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(comps[i] == null ? "missing" : comps[i].GetType().Name);
            }
            sb.Append("]\n");
            for (int i = 0; i < t.childCount; i++) AppendHierarchy(sb, t.GetChild(i), depth + 1);
        }

        private static void AppendFsm(StringBuilder sb, PlayMakerFSM fsm)
        {
            try
            {
                sb.Append("FSM '").Append(fsm.FsmName).Append("' on ").Append(PathOf(fsm.transform))
                  .Append(" | enabled=").Append(fsm.enabled)
                  .Append(" | active state=").Append(fsm.ActiveStateName)
                  .Append(fsm.FsmTemplate != null ? " | template=" + fsm.FsmTemplate.name : "").Append('\n');
                sb.Append("  Variables:\n");
                AppendVariables(sb, fsm.FsmVariables, "    ");
                var globals = fsm.Fsm.GlobalTransitions;
                if (globals != null && globals.Length > 0)
                {
                    sb.Append("  Global transitions:");
                    foreach (var tr in globals) sb.Append(' ').Append(tr.EventName).Append("->").Append(tr.ToState);
                    sb.Append('\n');
                }
                foreach (var state in fsm.FsmStates)
                {
                    sb.Append("  State '").Append(state.Name).Append('\'');
                    if (state.Transitions != null && state.Transitions.Length > 0)
                    {
                        sb.Append(" transitions:");
                        foreach (var tr in state.Transitions) sb.Append(' ').Append(tr.EventName).Append("->").Append(tr.ToState);
                    }
                    sb.Append('\n');
                    var actions = state.Actions;
                    if (actions == null) continue;
                    for (int i = 0; i < actions.Length; i++)
                    {
                        var a = actions[i];
                        if (a == null) { sb.Append("    [").Append(i).Append("] null\n"); continue; }
                        sb.Append("    [").Append(i).Append("] ").Append(a.GetType().Name);
                        if (!a.Enabled) sb.Append(" (disabled)");
                        sb.Append(": ").Append(ActionFields(a)).Append('\n');
                    }
                }
            }
            catch (Exception e)
            {
                sb.Append("  ! error while dumping: ").Append(e.Message).Append('\n');
                Plugin.Log.LogError("[dump] " + fsm.FsmName + ": " + e);
            }
        }

        private static void AppendVariables(StringBuilder sb, FsmVariables v, string indent)
        {
            if (v == null) { sb.Append(indent).Append("(none)\n"); return; }
            AppendVars(sb, indent, "int", v.IntVariables);
            AppendVars(sb, indent, "float", v.FloatVariables);
            AppendVars(sb, indent, "bool", v.BoolVariables);
            AppendVars(sb, indent, "string", v.StringVariables);
            AppendVars(sb, indent, "vector2", v.Vector2Variables);
            AppendVars(sb, indent, "vector3", v.Vector3Variables);
            AppendVars(sb, indent, "quaternion", v.QuaternionVariables);
            AppendVars(sb, indent, "rect", v.RectVariables);
            AppendVars(sb, indent, "color", v.ColorVariables);
            AppendVars(sb, indent, "gameobject", v.GameObjectVariables);
            AppendVars(sb, indent, "object", v.ObjectVariables);
            AppendVars(sb, indent, "material", v.MaterialVariables);
            AppendVars(sb, indent, "texture", v.TextureVariables);
            AppendVars(sb, indent, "enum", v.EnumVariables);
            AppendVars(sb, indent, "array", v.ArrayVariables);
        }

        private static void AppendVars(StringBuilder sb, string indent, string kind, NamedVariable[] vars)
        {
            if (vars == null) return;
            foreach (var nv in vars)
                sb.Append(indent).Append(kind).Append(' ').Append(nv.Name).Append(" = ").Append(VarValue(nv)).Append('\n');
        }

        private static string VarValue(NamedVariable nv)
        {
            if (nv == null) return "null";
            var arr = nv as FsmArray;
            if (arr != null)
            {
                var sb = new StringBuilder();
                sb.Append(arr.ElementType).Append('[').Append(arr.Length).Append("] {");
                var vals = arr.Values;
                for (int i = 0; vals != null && i < vals.Length && i < 30; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(Fmt(vals[i], 1));
                }
                if (vals != null && vals.Length > 30) sb.Append(", ...");
                return sb.Append('}').ToString();
            }
            if (nv.IsNone) return "none";
            return nv.ToString();
        }

        private static readonly Dictionary<Type, FieldInfo[]> _fieldCache = new Dictionary<Type, FieldInfo[]>();

        internal static FieldInfo[] FieldsOf(Type type)
        {
            FieldInfo[] result;
            if (_fieldCache.TryGetValue(type, out result)) return result;
            var list = new List<FieldInfo>();
            for (var t = type; t != null && t != typeof(FsmStateAction) && t != typeof(object); t = t.BaseType)
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    if (!f.Name.StartsWith("<")) list.Add(f);
            result = list.ToArray();
            _fieldCache[type] = result;
            return result;
        }

        internal static string ActionFields(FsmStateAction a)
        {
            var sb = new StringBuilder();
            var fields = FieldsOf(a.GetType());
            for (int i = 0; i < fields.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                object val;
                try { val = fields[i].GetValue(a); } catch (Exception e) { val = "!" + e.Message; }
                sb.Append(fields[i].Name).Append('=').Append(Fmt(val, 0));
            }
            return sb.ToString();
        }

        internal static string Fmt(object o, int depth)
        {
            if (o == null) return "null";
            try
            {
                var ev = o as FsmEvent;
                if (ev != null) return "event:" + ev.Name;
                var od = o as FsmOwnerDefault;
                if (od != null) return od.OwnerOption == OwnerDefaultOption.UseOwner ? "owner" : "go:" + Fmt(od.GameObject, depth + 1);
                var nv = o as NamedVariable;
                if (nv != null)
                {
                    string val = VarValue(nv);
                    return nv.UseVariable && !string.IsNullOrEmpty(nv.Name) ? "$" + nv.Name + "(" + val + ")" : val;
                }
                var uo = o as UnityEngine.Object;
                if (uo is object) return uo == null ? "destroyed" : uo.GetType().Name + ":" + uo.name;
                if (o is string) return "\"" + o + "\"";
                if (o is IList && depth < 2)
                {
                    var sb = new StringBuilder("[");
                    int n = 0;
                    foreach (var x in (IList)o)
                    {
                        if (n > 0) sb.Append(", ");
                        if (n++ >= 20) { sb.Append("..."); break; }
                        sb.Append(Fmt(x, depth + 1));
                    }
                    return sb.Append(']').ToString();
                }
                var fv = o as FsmVar;
                if (fv != null) return "var:" + fv.variableName + "(" + fv.Type + ")=" + Fmt(fv.GetValue(), depth + 1);
                return o.ToString();
            }
            catch (Exception e) { return "!" + e.Message; }
        }

        // ---------------------------------------------------------------- watch

        private class Watched
        {
            public PlayMakerFSM Fsm;
            public string Label;
            public string State;
            public FsmInt[] Ints; public int[] LastInts;
            public FsmFloat[] Floats; public float[] LastFloats;
            public FsmBool[] Bools; public bool[] LastBools;
            public FsmString[] Strings; public string[] LastStrings;
        }

        private readonly List<Watched> _watch = new List<Watched>();

        private void Watch(PlayMakerFSM[] fsms)
        {
            _watch.Clear();
            foreach (var fsm in fsms)
            {
                if (fsm == null) continue;
                var v = fsm.FsmVariables;
                var w = new Watched
                {
                    Fsm = fsm,
                    Label = PathOf(fsm.transform) + " :: " + fsm.FsmName,
                    State = fsm.ActiveStateName,
                    Ints = v.IntVariables, Floats = v.FloatVariables, Bools = v.BoolVariables, Strings = v.StringVariables
                };
                w.LastInts = new int[w.Ints.Length];
                for (int i = 0; i < w.Ints.Length; i++) w.LastInts[i] = w.Ints[i].Value;
                w.LastFloats = new float[w.Floats.Length];
                for (int i = 0; i < w.Floats.Length; i++) w.LastFloats[i] = w.Floats[i].Value;
                w.LastBools = new bool[w.Bools.Length];
                for (int i = 0; i < w.Bools.Length; i++) w.LastBools[i] = w.Bools[i].Value;
                w.LastStrings = new string[w.Strings.Length];
                for (int i = 0; i < w.Strings.Length; i++) w.LastStrings[i] = w.Strings[i].Value;
                _watch.Add(w);
            }
            if (_watch.Count > 0)
                Plugin.Log.LogInfo("[watch] Watching " + _watch.Count + " FSM(s) of this box. Put ammo in / take ammo out now; every change is logged.");
        }

        // Runs every frame; allocates nothing unless something changed.
        internal void UpdateWatch()
        {
            for (int k = _watch.Count - 1; k >= 0; k--)
            {
                var w = _watch[k];
                if (w.Fsm == null) { Plugin.Log.LogInfo("[watch] " + w.Label + " is gone."); _watch.RemoveAt(k); continue; }
                string state = w.Fsm.ActiveStateName;
                if (!string.Equals(state, w.State))
                {
                    Plugin.Log.LogInfo("[watch] " + w.Label + " state: " + w.State + " -> " + state + " (frame " + Time.frameCount + ")");
                    w.State = state;
                }
                for (int i = 0; i < w.Ints.Length; i++)
                {
                    int v = w.Ints[i].Value;
                    if (v == w.LastInts[i]) continue;
                    Plugin.Log.LogInfo("[watch] " + w.Label + " int " + w.Ints[i].Name + ": " + w.LastInts[i] + " -> " + v + " (state " + state + ")");
                    w.LastInts[i] = v;
                }
                for (int i = 0; i < w.Floats.Length; i++)
                {
                    float v = w.Floats[i].Value;
                    if (v == w.LastFloats[i]) continue;
                    Plugin.Log.LogInfo("[watch] " + w.Label + " float " + w.Floats[i].Name + ": " + w.LastFloats[i] + " -> " + v + " (state " + state + ")");
                    w.LastFloats[i] = v;
                }
                for (int i = 0; i < w.Bools.Length; i++)
                {
                    bool v = w.Bools[i].Value;
                    if (v == w.LastBools[i]) continue;
                    Plugin.Log.LogInfo("[watch] " + w.Label + " bool " + w.Bools[i].Name + ": " + w.LastBools[i] + " -> " + v + " (state " + state + ")");
                    w.LastBools[i] = v;
                }
                for (int i = 0; i < w.Strings.Length; i++)
                {
                    string v = w.Strings[i].Value;
                    if (ReferenceEquals(v, w.LastStrings[i]) || v == w.LastStrings[i]) continue;
                    Plugin.Log.LogInfo("[watch] " + w.Label + " string " + w.Strings[i].Name + ": \"" + w.LastStrings[i] + "\" -> \"" + v + "\" (state " + state + ")");
                    w.LastStrings[i] = v;
                }
            }
        }

        // ---------------------------------------------------------------- hooks

        private void InstallHooks(Harmony h)
        {
            int ok = 0;
            ok += Patch(h, typeof(IntClamp), "DoClamp", "IntClampPre", "IntClampPost");
            ok += Patch(h, typeof(IntCompare), "DoIntCompare", null, "IntComparePost");
            ok += Patch(h, typeof(IntAdd), "OnEnter", "IntAddPre", "IntAddPost");
            ok += Patch(h, typeof(IntAdd), "OnUpdate", "IntAddPre", "IntAddPost");
            ok += Patch(h, typeof(IntSubtract), "doSubtract", "IntSubtractPre", "IntSubtractPost");
            ok += Patch(h, typeof(IntOperator), "DoIntOperator", null, "IntOperatorPost");
            ok += Patch(h, typeof(SetIntValue), "OnEnter", "SetIntPre", "SetIntPost");
            ok += Patch(h, typeof(SetIntValue), "OnUpdate", "SetIntPre", "SetIntPost");
            ok += Patch(h, typeof(FloatClamp), "DoClamp", "FloatClampPre", "FloatClampPost");
            ok += Patch(h, typeof(FloatCompare), "DoCompare", null, "FloatComparePost");
            // actions that move values between FSMs: logged through reflection
            foreach (var name in new[] { "SetFsmInt", "GetFsmInt", "AddToFsmInt", "IntSwitch", "IntChanged", "SetFsmFloat", "GetFsmFloat", "ConvertIntToFloat", "ConvertFloatToInt", "SetIntFromFloat" })
            {
                var t = AccessTools.TypeByName("HutongGames.PlayMaker.Actions." + name);
                if (t == null) { Plugin.Log.LogWarning("[hooks] Type not found: " + name); continue; }
                ok += Patch(h, t, "OnEnter", null, "GenericPost");
                if (AccessTools.DeclaredMethod(t, "OnUpdate") != null) ok += Patch(h, t, "OnUpdate", null, "GenericPost");
            }
            Plugin.Log.LogInfo("[hooks] " + ok + " action hooks installed. Logging values " + Hooks.Min + ".." + Hooks.Max + (Hooks.On ? "." : " is OFF (LogIntActions=false)."));
        }

        private static int Patch(Harmony h, Type type, string method, string pre, string post)
        {
            try
            {
                var m = AccessTools.DeclaredMethod(type, method);
                if (m == null) { Plugin.Log.LogWarning("[hooks] " + type.Name + "." + method + " not found."); return 0; }
                h.Patch(m,
                    prefix: pre == null ? null : new HarmonyMethod(typeof(Hooks), pre),
                    postfix: post == null ? null : new HarmonyMethod(typeof(Hooks), post));
                return 1;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("[hooks] " + type.Name + "." + method + ": " + e.Message);
                return 0;
            }
        }
    }

    // Harmony patches. They run for every such action in the game, so the early checks allocate nothing.
    internal static class Hooks
    {
        internal static bool On = true;
        internal static int Min = 300, Max = 2000;
        private const int MaxLines = 4000;

        internal class FsmInfo { public string Label; public bool Context; }
        internal static readonly Dictionary<Fsm, FsmInfo> Infos = new Dictionary<Fsm, FsmInfo>();
        private static readonly HashSet<string> _seen = new HashSet<string>();
        private static int _lines;
        private static int _errors;

        private static bool In(int v) { return v >= Min && v <= Max; }
        private static bool In(float v) { return v >= Min && v <= Max; }
        private static int V(FsmInt x) { return x == null ? 0 : x.Value; }
        private static float V(FsmFloat x) { return x == null ? 0f : x.Value; }
        private static bool Named(NamedVariable x) { return x != null && x.UseVariable && Diagnostics.IsVarWord(x.Name); }

        private static FsmInfo Info(Fsm fsm)
        {
            FsmInfo info;
            if (Infos.TryGetValue(fsm, out info)) return info;
            if (Infos.Count > 20000) Infos.Clear();
            var go = fsm.GameObject;
            string path = go != null ? Diagnostics.PathOf(go.transform) : "(no object)";
            info = new FsmInfo { Label = path + " :: " + fsm.Name, Context = Diagnostics.Instance.IsContextPath(path) };
            Infos[fsm] = info;
            return info;
        }

        // True when the run should be logged: a value in range and an ammo box / game manager FSM or an ammo variable.
        private static bool Wanted(FsmStateAction a, bool inRange, NamedVariable n0, NamedVariable n1, NamedVariable n2)
        {
            if (!On || !inRange || a == null || _lines >= MaxLines) return false;
            var fsm = a.Fsm;
            if (fsm == null) return false;
            return Info(fsm).Context || Named(n0) || Named(n1) || Named(n2);
        }

        private static void Report(FsmStateAction a, string kind, string values)
        {
            try
            {
                var info = Info(a.Fsm);
                var state = a.State;
                int index = state != null && state.Actions != null ? Array.IndexOf(state.Actions, a) : -1;
                string key = info.Label + "|" + (state != null ? state.Name : "?") + "|" + index + "|" + values;
                if (!_seen.Add(key)) return;
                if (++_lines >= MaxLines) Plugin.Log.LogWarning("[action] Line limit reached, no more action logs this session.");
                Plugin.Log.LogInfo("[action] " + kind + " in " + info.Label + " | state '" + (state != null ? state.Name : "?") + "' [" + index + "] | " + values
                    + " | frame " + Time.frameCount);
            }
            catch (Exception e)
            {
                if (_errors++ < 5) Plugin.Log.LogError("[action] " + e);
            }
        }

        private static string N(NamedVariable x) { return x == null ? "null" : (x.UseVariable ? "$" + x.Name : "literal"); }

        public static void IntClampPre(IntClamp __instance, out int __state) { __state = V(__instance.intVariable); }
        public static void IntClampPost(IntClamp __instance, int __state)
        {
            int after = V(__instance.intVariable), min = V(__instance.minValue), max = V(__instance.maxValue);
            if (!Wanted(__instance, In(__state) || In(after) || In(max) || In(min), __instance.intVariable, __instance.minValue, __instance.maxValue)) return;
            Report(__instance, "IntClamp", N(__instance.intVariable) + " " + __state + " -> " + after + " | min " + N(__instance.minValue) + "=" + min
                + " | max " + N(__instance.maxValue) + "=" + max + (after != __state ? " | CLAMPED" : ""));
        }

        public static void IntComparePost(IntCompare __instance)
        {
            int a = V(__instance.integer1), b = V(__instance.integer2);
            if (!Wanted(__instance, In(a) || In(b), __instance.integer1, __instance.integer2, null)) return;
            Report(__instance, "IntCompare", N(__instance.integer1) + "=" + a + " vs " + N(__instance.integer2) + "=" + b
                + " | equal=" + Ev(__instance.equal) + " less=" + Ev(__instance.lessThan) + " greater=" + Ev(__instance.greaterThan)
                + " -> " + (a == b ? "EQUAL" : a < b ? "LESS" : "GREATER"));
        }

        private static string Ev(FsmEvent e) { return e == null ? "-" : e.Name; }

        public static void IntAddPre(IntAdd __instance, out int __state) { __state = V(__instance.intVariable); }
        public static void IntAddPost(IntAdd __instance, int __state)
        {
            int after = V(__instance.intVariable), add = V(__instance.add);
            if (!Wanted(__instance, In(__state) || In(after) || In(add), __instance.intVariable, __instance.add, null)) return;
            Report(__instance, "IntAdd", N(__instance.intVariable) + " " + __state + " -> " + after + " | add " + N(__instance.add) + "=" + add);
        }

        public static void IntSubtractPre(IntSubtract __instance, out int __state) { __state = V(__instance.intVariable); }
        public static void IntSubtractPost(IntSubtract __instance, int __state)
        {
            int after = V(__instance.intVariable), sub = V(__instance.subtract);
            if (!Wanted(__instance, In(__state) || In(after) || In(sub), __instance.intVariable, __instance.subtract, null)) return;
            Report(__instance, "IntSubtract", N(__instance.intVariable) + " " + __state + " -> " + after + " | subtract " + N(__instance.subtract) + "=" + sub);
        }

        public static void IntOperatorPost(IntOperator __instance)
        {
            int a = V(__instance.integer1), b = V(__instance.integer2), r = V(__instance.storeResult);
            if (!Wanted(__instance, In(a) || In(b) || In(r), __instance.integer1, __instance.integer2, __instance.storeResult)) return;
            Report(__instance, "IntOperator", N(__instance.integer1) + "=" + a + " " + __instance.operation + " " + N(__instance.integer2) + "=" + b
                + " -> " + N(__instance.storeResult) + "=" + r);
        }

        public static void SetIntPre(SetIntValue __instance, out int __state) { __state = V(__instance.intVariable); }
        public static void SetIntPost(SetIntValue __instance, int __state)
        {
            int after = V(__instance.intVariable);
            if (!Wanted(__instance, In(__state) || In(after), __instance.intVariable, __instance.intValue, null)) return;
            Report(__instance, "SetIntValue", N(__instance.intVariable) + " " + __state + " -> " + after + " | value " + N(__instance.intValue));
        }

        public static void FloatClampPre(FloatClamp __instance, out float __state) { __state = V(__instance.floatVariable); }
        public static void FloatClampPost(FloatClamp __instance, float __state)
        {
            float after = V(__instance.floatVariable), min = V(__instance.minValue), max = V(__instance.maxValue);
            if (!Wanted(__instance, In(__state) || In(after) || In(max), __instance.floatVariable, __instance.minValue, __instance.maxValue)) return;
            Report(__instance, "FloatClamp", N(__instance.floatVariable) + " " + __state + " -> " + after + " | min " + N(__instance.minValue) + "=" + min
                + " | max " + N(__instance.maxValue) + "=" + max + (after != __state ? " | CLAMPED" : ""));
        }

        public static void FloatComparePost(FloatCompare __instance)
        {
            float a = V(__instance.float1), b = V(__instance.float2);
            if (!Wanted(__instance, In(a) || In(b), __instance.float1, __instance.float2, null)) return;
            Report(__instance, "FloatCompare", N(__instance.float1) + "=" + a + " vs " + N(__instance.float2) + "=" + b
                + " | equal=" + Ev(__instance.equal) + " less=" + Ev(__instance.lessThan) + " greater=" + Ev(__instance.greaterThan));
        }

        // Any other action type: FsmInt/FsmFloat fields in range, plus ammo-named variables or strings.
        public static void GenericPost(FsmStateAction __instance)
        {
            if (!On || __instance == null || _lines >= MaxLines) return;
            try
            {
                var fields = Diagnostics.FieldsOf(__instance.GetType());
                bool inRange = false, named = false;
                for (int i = 0; i < fields.Length; i++)
                {
                    var o = fields[i].GetValue(__instance);
                    var fi = o as FsmInt;
                    if (fi != null) { if (In(fi.Value)) inRange = true; if (Named(fi)) named = true; continue; }
                    var ff = o as FsmFloat;
                    if (ff != null) { if (In(ff.Value)) inRange = true; if (Named(ff)) named = true; continue; }
                    var fs = o as FsmString;
                    if (fs != null && Diagnostics.IsVarWord(fs.Value)) named = true;
                }
                if (!inRange) return;
                var fsm = __instance.Fsm;
                if (fsm == null || !(named || Info(fsm).Context)) return;
                Report(__instance, __instance.GetType().Name, Diagnostics.ActionFields(__instance));
            }
            catch (Exception e)
            {
                if (_errors++ < 5) Plugin.Log.LogError("[action] " + e);
            }
        }
    }
}
