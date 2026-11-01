// Ammo Box Capacity - BepInEx 5 plugin for Apocalypter
// Sets how much ammo fits in the game's big ammo boxes (ammo_box_big_*), per caliber, as a hard limit.
// Written from scratch; does not use code from other mods.

using System;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyTitle("Ammo Box Capacity")]

namespace AmmoBoxCapacity
{
    [BepInPlugin(GUID, NAME, VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "com.gelatti.apocalypter.ammoboxcapacity";
        public const string NAME = "Ammo Box Capacity";
        public const string VERSION = "1.0.0";

        internal static ManualLogSource Log;

        private AmmoBoxLimiter _limiter;
#if DIAGNOSTICS
        private Diagnostics _diag;
#endif

        private void Awake()
        {
            Log = Logger;

            Config.Bind("General", "Apocasetter", true, "Show this mod in the Apocasetter Mods menu.");
            _limiter = new AmmoBoxLimiter(Config);

            CreateRunner();
            Application.onBeforeRender += Tick;
            try
            {
                var h = new Harmony(GUID);
                _limiter.InstallHooks(h);
#if DIAGNOSTICS
                _diag = new Diagnostics(Config, h);
#endif
            }
            catch (Exception e) { Log.LogError("Hooks failed: " + e); }
            SceneManager.sceneLoaded += (scene, mode) =>
            {
                _limiter.OnSceneLoaded();
#if DIAGNOSTICS
                _diag?.OnSceneLoaded();
#endif
            };
            _limiter.LogSettings();
            Log.LogInfo(NAME + " " + VERSION + " loaded.");
        }

        // The game removes the plugin's own object, so the work runs on a separate object that survives.
        private void CreateRunner()
        {
            var go = new GameObject("AmmoBoxCapacity_Runner");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<PluginRunner>().Plugin = this;
        }

        private int _lastTickFrame = -1;
        private int _tickErrors;

        internal void Tick()
        {
            if (Time.frameCount == _lastTickFrame) return;
            _lastTickFrame = Time.frameCount;
            // Unity errors are not written to this game's log, so catch and write them ourselves (first few only)
            try
            {
                _limiter.Update();
#if DIAGNOSTICS
                _diag?.Update();
#endif
            }
            catch (Exception e)
            {
                if (_tickErrors++ < 5) Log.LogError("Tick: " + e);
            }
        }
    }

    internal class PluginRunner : MonoBehaviour
    {
        public Plugin Plugin;

        private void Update()
        {
            if (Plugin != null) Plugin.Tick();
        }
    }
}
