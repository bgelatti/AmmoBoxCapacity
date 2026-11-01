// Box capacity: the big ammo boxes (ammo_box_big_*) only accept another box while their ammo is below a limit.
// The limit is two IntCompare literals in the box's 'AmmoStorage' FSM ('Trigger' [ammo > 499 -> full] and
// 'full' [ammo < 500 -> Trigger]). Those literals are replaced with the configured limit, and the 'Attach' state's
// transfer is replaced with one that stops at the limit, leaving the rest in the box held in the hand.

using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace AmmoBoxCapacity
{
    internal class AmmoBoxLimiter
    {
        internal static AmmoBoxLimiter Instance;

        // Config key suffix for each caliber, in the order they are matched against the box name
        internal static readonly string[] Calibers = { "22", "9mm", "556mm", "762mm", "3006", "12gauge", "20gauge", "arrow" };

        private readonly ConfigEntry<bool> _enabled;
        private readonly ConfigEntry<float> _multiplier;
        private readonly Dictionary<string, ConfigEntry<int>> _perCaliber = new Dictionary<string, ConfigEntry<int>>();

        private enum Role { Event, Destroy, Add }

        private class Managed
        {
            public Box Box;
            public Role Role;
        }

        private class Box
        {
            public PlayMakerFSM Storage;
            public string Name;
            public string Caliber;
            public FsmInt TriggerLimit, FullLimit; // the 499 and 500 literals
            public FsmInt Ammo;                    // 'ammo' of the box's own 'Ammo' FSM
            public FsmGameObject Item;             // the box held in the hand
            public int Original;
            public int Applied = -1;
            public bool HardCap;
            public readonly List<FsmStateAction> Actions = new List<FsmStateAction>();
            public int AttachFrame = -1;
            public bool Planned, KeepHeld;
        }

        private readonly Dictionary<PlayMakerFSM, Box> _boxes = new Dictionary<PlayMakerFSM, Box>();
        private readonly HashSet<PlayMakerFSM> _skipped = new HashSet<PlayMakerFSM>();
        private static readonly Dictionary<FsmStateAction, Managed> _actions = new Dictionary<FsmStateAction, Managed>();
        private readonly Dictionary<string, int> _originals = new Dictionary<string, int>();
        private readonly List<PlayMakerFSM> _dead = new List<PlayMakerFSM>();

        private float _nextScan;
        private const float ScanInterval = 2f;
        private bool _settingsChanged;
        private int _errors;
#if DIAGNOSTICS
        private int _skipLogs;
#endif

        internal AmmoBoxLimiter(ConfigFile config)
        {
            Instance = this;
            _enabled = config.Bind("General", "Enabled", true,
                "Use the limits below for the big ammo boxes. Off: the game's own limit (500).");
            _multiplier = config.Bind("General", "Multiplier", 1f,
                new ConfigDescription("Multiplies the original limit of each big ammo box (500 in the game) for every caliber whose MaxPerBox is 0.",
                    new AcceptableValueRange<float>(0.1f, 100f)));
            foreach (var cal in Calibers)
                _perCaliber[cal] = config.Bind("General", "MaxPerBox_" + cal, 0,
                    new ConfigDescription("Most ammo a big " + cal + " ammo box holds. 0 = original limit x Multiplier.",
                        new AcceptableValueRange<int>(0, 1000000)));
            config.SettingChanged += (sender, e) => _settingsChanged = true;
            config.ConfigReloaded += (sender, e) => _settingsChanged = true;
        }

        internal void LogSettings()
        {
            var sb = new System.Text.StringBuilder("[box] Settings: Enabled=" + _enabled.Value + ", Multiplier=" + _multiplier.Value);
            foreach (var cal in Calibers)
            {
                int v = _perCaliber[cal].Value;
                int original;
                bool known = _originals.TryGetValue(cal, out original);
                sb.Append("\n   ").Append(cal).Append(": original ").Append(known ? original.ToString() : "? (read from the first box found)")
                  .Append(" -> new ");
                if (!_enabled.Value) sb.Append(known ? original.ToString() : "original (mod off)");
                else if (v > 0) sb.Append(v);
                else sb.Append(known ? Limit(cal, original).ToString() : "original x " + _multiplier.Value);
            }
            Plugin.Log.LogInfo(sb.ToString());
        }

        internal void InstallHooks(Harmony h)
        {
            PatchOnEnter(h, typeof(SendEvent));
            PatchOnEnter(h, typeof(DestroyObject));
            PatchOnEnter(h, typeof(AddToFsmInt));
        }

        private static void PatchOnEnter(Harmony h, Type type)
        {
            try
            {
                var m = AccessTools.DeclaredMethod(type, "OnEnter");
                if (m == null) { Plugin.Log.LogError("[box] " + type.Name + ".OnEnter not found; the hard cap will not work."); return; }
                h.Patch(m, prefix: new HarmonyMethod(typeof(AmmoBoxLimiter), nameof(ActionPrefix)));
            }
            catch (Exception e) { Plugin.Log.LogError("[box] Hook on " + type.Name + ".OnEnter failed: " + e.Message); }
        }

        internal void OnSceneLoaded()
        {
            _boxes.Clear();
            _skipped.Clear();
            _actions.Clear();
            _nextScan = 0f;
        }

        // ---------------------------------------------------------------- limits

        private int Limit(string caliber, int original)
        {
            if (!_enabled.Value) return original;
            ConfigEntry<int> entry;
            if (caliber != null && _perCaliber.TryGetValue(caliber, out entry) && entry.Value > 0) return entry.Value;
            return Math.Max(1, Mathf.RoundToInt(original * _multiplier.Value));
        }

        private void Apply(Box box)
        {
            int n = Limit(box.Caliber, box.Original);
            if (n == box.Applied && box.FullLimit.Value == n && box.TriggerLimit.Value == n - 1) return;
            box.TriggerLimit.Value = n - 1;
            box.FullLimit.Value = n;
            box.Applied = n;
        }

        internal static string CaliberOf(string name)
        {
            string n = name.ToLowerInvariant();
            int big = n.IndexOf("big", StringComparison.Ordinal);
            if (big >= 0) n = n.Substring(big + 3);
            if (n.Contains("12gauge") || n.Contains("12ga")) return "12gauge";
            if (n.Contains("20gauge") || n.Contains("20ga")) return "20gauge";
            if (n.Contains("556")) return "556mm";
            if (n.Contains("762")) return "762mm";
            if (n.Contains("3006") || n.Contains("30_06")) return "3006";
            if (n.Contains("9mm")) return "9mm";
            if (n.Contains("arrow")) return "arrow";
            if (n.Contains("22")) return "22";
            return null;
        }

        // ---------------------------------------------------------------- scan

        // Runs every frame; the work happens every ScanInterval seconds and allocates only for new boxes.
        internal void Update()
        {
            if (_settingsChanged)
            {
                _settingsChanged = false;
                foreach (var kv in _boxes) if (kv.Key != null) Apply(kv.Value);
#if DIAGNOSTICS
                LogSettings();
#endif
            }
            if (Time.unscaledTime < _nextScan) return;
            _nextScan = Time.unscaledTime + ScanInterval;
            Scan();
        }

        private void Scan()
        {
            _dead.Clear();
            foreach (var kv in _boxes) if (kv.Key == null) _dead.Add(kv.Key);
            foreach (var key in _dead)
            {
                foreach (var a in _boxes[key].Actions) _actions.Remove(a);
                _boxes.Remove(key);
            }
            _dead.Clear();
            foreach (var fsm in _skipped) if (fsm == null) _dead.Add(fsm);
            foreach (var key in _dead) _skipped.Remove(key);
            _dead.Clear();

            var list = PlayMakerFSM.FsmList;
            for (int i = 0; i < list.Count; i++)
            {
                var fsm = list[i];
                if (fsm == null || _boxes.ContainsKey(fsm) || _skipped.Contains(fsm)) continue;
                if (fsm.FsmName != "AmmoStorage") continue;
                try { Register(fsm); }
                catch (Exception e)
                {
                    _skipped.Add(fsm);
                    if (_errors++ < 5) Plugin.Log.LogError("[box] " + fsm.gameObject.name + ": " + e);
                }
            }
        }

        private void Register(PlayMakerFSM fsm)
        {
            string name = fsm.gameObject.name;
            if (name.IndexOf("big", StringComparison.OrdinalIgnoreCase) < 0)
            {
                // small boxes are left alone
                _skipped.Add(fsm);
#if DIAGNOSTICS
                if (_skipLogs++ < 5) Plugin.Log.LogInfo("[box] Not a big box, left as is: " + name);
#endif
                return;
            }

            var box = new Box { Storage = fsm, Name = name, Caliber = CaliberOf(name) };
            FsmStateAction add = null, destroy = null;
            var events = new List<FsmStateAction>();
            foreach (var state in fsm.FsmStates)
            {
                var actions = state.Actions;
                if (actions == null) continue;
                foreach (var a in actions)
                {
                    if (state.Name == "Trigger" && box.TriggerLimit == null)
                    {
                        var c = a as IntCompare;
                        if (c != null && c.integer2 != null && !c.integer2.UseVariable) box.TriggerLimit = c.integer2;
                    }
                    else if (state.Name == "full" && box.FullLimit == null)
                    {
                        var c = a as IntCompare;
                        if (c != null && c.integer2 != null && !c.integer2.UseVariable) box.FullLimit = c.integer2;
                    }
                    else if (state.Name == "Attach")
                    {
                        if (a is SendEvent) events.Add(a);
                        else if (a is DestroyObject) destroy = a;
                        else if (a is AddToFsmInt) add = a;
                    }
                }
            }
            if (box.TriggerLimit == null || box.FullLimit == null)
            {
                _skipped.Add(fsm);
                Plugin.Log.LogWarning("[box] " + name + ": the limit checks were not found in its AmmoStorage FSM; left as is.");
                return;
            }

            // A box seen after its limit was already changed (shared FSM data) keeps the original of its caliber.
            int original = box.FullLimit.Value;
            int known;
            if (box.Caliber != null && _originals.TryGetValue(box.Caliber, out known) && original == Limit(box.Caliber, known)) original = known;
            if (box.TriggerLimit.Value != box.FullLimit.Value - 1)
                Plugin.Log.LogWarning("[box] " + name + ": unexpected limits " + box.TriggerLimit.Value + "/" + box.FullLimit.Value + "; using " + original + ".");
            box.Original = original;

            foreach (var c in fsm.GetComponents<PlayMakerFSM>())
                if (c.FsmName == "Ammo") { box.Ammo = c.FsmVariables.GetFsmInt("ammo"); break; }
            box.Item = fsm.FsmVariables.GetFsmGameObject("Item");
            box.HardCap = add != null && destroy != null && box.Ammo != null && box.Item != null;
            if (box.HardCap)
            {
                Manage(box, add, Role.Add);
                Manage(box, destroy, Role.Destroy);
                foreach (var e in events) Manage(box, e, Role.Event);
            }
            else Plugin.Log.LogWarning("[box] " + name + ": transfer actions not found; the limit is set but a transfer can go past it.");

            _boxes[fsm] = box;
            Apply(box);

            string calKey = box.Caliber ?? "unknown";
            if (!_originals.ContainsKey(calKey))
            {
                _originals[calKey] = original;
                Plugin.Log.LogInfo("[box] " + calKey + ": original limit " + original + ", new limit " + box.Applied + " (first box: " + name + ")"
                    + (box.Caliber == null ? " - caliber not recognised, using Multiplier." : ""));
            }
        }

        private void Manage(Box box, FsmStateAction a, Role role)
        {
            _actions[a] = new Managed { Box = box, Role = role };
            box.Actions.Add(a);
        }

        // ---------------------------------------------------------------- transfer

        // Moves ammo from the held box up to the limit. Returns false when the game's own transfer should run.
        private bool Transfer(Box box)
        {
            var held = box.Item.Value;
            if (held == null || box.Ammo == null) return false;
            FsmInt heldAmmo = null;
            foreach (var c in held.GetComponents<PlayMakerFSM>())
                if (c.FsmName == "Ammo") { heldAmmo = c.FsmVariables.GetFsmInt("ammo"); break; }
            if (heldAmmo == null) return false;

            int own = box.Ammo.Value, give = heldAmmo.Value;
            int move = Mathf.Clamp(box.Applied - own, 0, Math.Max(0, give));
            box.Ammo.Value = own + move;
            heldAmmo.Value = give - move;
            box.KeepHeld = heldAmmo.Value > 0;
#if DIAGNOSTICS
            Plugin.Log.LogInfo("[box] " + box.Name + ": +" + move + " (" + own + " -> " + box.Ammo.Value + " of " + box.Applied
                + "), held box " + give + " -> " + heldAmmo.Value + (box.KeepHeld ? ", kept in hand" : ", used up"));
#endif
            return true;
        }

        // Prefix on SendEvent/DestroyObject/AddToFsmInt.OnEnter. Only the actions of a big box's 'Attach' state are touched:
        // the first one that runs does the capped transfer; the game's add is skipped, and when ammo is left in the
        // held box, the box is not destroyed and the "hand is empty" events are not sent.
        public static bool ActionPrefix(FsmStateAction __instance)
        {
            if (_actions.Count == 0) return true;
            Managed m;
            if (!_actions.TryGetValue(__instance, out m)) return true;
            try
            {
                var box = m.Box;
                if (box.AttachFrame != Time.frameCount)
                {
                    box.AttachFrame = Time.frameCount;
                    box.KeepHeld = false;
                    box.Planned = Instance._enabled.Value && Instance.Transfer(box);
                }
                if (!box.Planned) return true;
                if (m.Role != Role.Add && !box.KeepHeld) return true;
                __instance.Finish();
                return false;
            }
            catch (Exception e)
            {
                if (Instance._errors++ < 5) Plugin.Log.LogError("[box] Transfer: " + e);
                return true;
            }
        }
    }
}
