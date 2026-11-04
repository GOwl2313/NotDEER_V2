// ============================================================================
//  LoudnessBoost.cs  -  the simple loudness plugin (cassette-player style)
// ----------------------------------------------------------------------------
//  What it does: multiplies the sound by ONE constant number. That is all.
//  Like the volume knob on a cassette player: louder, but the tone, pitch and
//  frequency content stay exactly the same. A constant multiply is a linear
//  operation - it cannot add distortion, cannot change frequencies.
//
//  USE IT ONLY ON THE PROBLEM SOUNDS:
//    1. Select the GameObject that has the AudioSource with the quiet clip.
//    2. Add Component > Audio > Loudness Boost.
//    3. Drag the slider: +6 dB = 2x louder, +12 dB = 4x louder.
//    AudioSources WITHOUT this component are not touched in any way - the
//    rest of the game keeps its original sound.
//
//  Slider at 0 dB = sound passes through completely untouched.
//  Not supported on WebGL (Unity does not run audio filters there).
//
//  This same file also carries two editor-only clean-up menus that undo the
//  previous "Audio Boost Studio" version. Run them BEFORE deleting the old
//  scripts, then save your scenes:
//    Tools > Audio Loudness > Remove old AudioBoostFilter components
//    Tools > Audio Loudness > Restore original WAVs from .bak
// ============================================================================

using UnityEngine;
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
#endif

[RequireComponent(typeof(AudioSource))]
[DisallowMultipleComponent]
[AddComponentMenu("Audio/Loudness Boost")]
public class LoudnessBoost : MonoBehaviour
{
    [Tooltip("How much louder. +6 dB = 2x, +12 dB = 4x. Raise slowly - if the sound starts to crackle, back the slider off.")]
    [Range(0f, 24f)]
    public float boostDb = 6f;

    // The entire plugin: every sample is multiplied by ONE constant.
    // A constant multiply is linear - exactly what a volume knob does.
    private void OnAudioFilterRead(float[] data, int channels)
    {
        float g = Mathf.Pow(10f, boostDb * 0.05f);   // dB -> linear
        if (g <= 1.0001f)
        {
            return;                                  // 0 dB - pass through untouched
        }
        for (int i = 0; i < data.Length; i++)
        {
            data[i] *= g;
        }
    }
}

#if UNITY_EDITOR
// ----------------------------------------------------------------------------
//  One-time clean-up for the old "Audio Boost Studio" delivery.
// ----------------------------------------------------------------------------
public static class LoudnessBoostCleanup
{
    private const string OldFilterName = "AudioBoostFilter";

    [MenuItem("Tools/Audio Loudness/Remove old AudioBoostFilter components")]
    private static void RemoveOldFilters()
    {
        if (!EditorUtility.DisplayDialog("Remove old AudioBoostFilter components?",
                "Removes every component named '" + OldFilterName + "' from the loaded scenes and from all prefabs under Assets.\n\n" +
                "Run this BEFORE deleting the old scripts, then save your scenes (Ctrl+S).",
                "Remove", "Cancel"))
        {
            return;
        }

        int removed = 0;
        int dirtyScenes = 0;

        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            Scene sc = SceneManager.GetSceneAt(i);
            if (!sc.IsValid())
            {
                continue;
            }
            GameObject[] roots = sc.GetRootGameObjects();
            if (roots == null)
            {
                continue;
            }
            int before = removed;
            for (int g = 0; g < roots.Length; g++)
            {
                removed += Strip(roots[g], true);
            }
            if (removed > before)
            {
                EditorSceneManager.MarkSceneDirty(sc);
                dirtyScenes++;
            }
        }

        int changedPrefabs = 0;
        string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" });
        for (int i = 0; i < guids.Length; i++)
        {
            string path = AssetDatabase.GUIDToAssetPath(guids[i]);
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }
            GameObject contents = PrefabUtility.LoadPrefabContents(path);
            if (contents == null)
            {
                continue;
            }
            try
            {
                int n = Strip(contents, false);
                if (n > 0)
                {
                    removed += n;
                    PrefabUtility.SaveAsPrefabAsset(contents, path);
                    changedPrefabs++;
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        EditorUtility.DisplayDialog("Done",
            "Removed " + removed + " component(s).\n" +
            "Scenes marked dirty: " + dirtyScenes + " - save them now (Ctrl+S).\n" +
            "Prefabs updated and saved: " + changedPrefabs + ".",
            "OK");
    }

    [MenuItem("Tools/Audio Loudness/Restore original WAVs from .bak")]
    private static void RestoreBackups()
    {
        if (!EditorUtility.DisplayDialog("Restore original WAV files?",
                "Every '<name>.wav.bak' under Assets is copied back over '<name>.wav', " +
                "undoing the old batch boost of the audio files. Continue?",
                "Restore", "Cancel"))
        {
            return;
        }

        string assets = Application.dataPath;
        string[] baks;
        try
        {
            baks = Directory.GetFiles(assets, "*.wav.bak", SearchOption.AllDirectories);
        }
        catch (Exception)
        {
            baks = new string[0];
        }

        int restored = 0;
        List<string> reimport = new List<string>();
        for (int i = 0; i < baks.Length; i++)
        {
            string bak = baks[i];
            string wav = bak.Substring(0, bak.Length - 4);          // strip ".bak"
            if (!File.Exists(wav))
            {
                continue;
            }
            try
            {
                File.Copy(bak, wav, true);
                reimport.Add(("Assets" + wav.Substring(assets.Length)).Replace('\\', '/'));
                restored++;
            }
            catch (Exception e)
            {
                Debug.LogWarning("LoudnessBoost: could not restore " + wav + " - " + e.Message);
            }
        }

        for (int i = 0; i < reimport.Count; i++)
        {
            AssetDatabase.ImportAsset(reimport[i], ImportAssetOptions.ForceUpdate);
        }

        EditorUtility.DisplayDialog("Done",
            restored == 0
                ? "No .wav.bak backups found - nothing to restore."
                : "Restored " + restored + " wav file(s) to their original level.",
            "OK");
    }

    // Remove every component whose class name matches the old filter.
    // undoable = true for live scene objects (Ctrl+Z works), false for
    // prefab contents.
    private static int Strip(GameObject root, bool undoable)
    {
        MonoBehaviour[] comps = root.GetComponentsInChildren<MonoBehaviour>(true);
        int n = 0;
        for (int i = 0; i < comps.Length; i++)
        {
            MonoBehaviour mb = comps[i];
            if (mb == null)
            {
                continue;              // missing script - not ours, leave it alone
            }
            if (mb.GetType().Name != OldFilterName)
            {
                continue;
            }
            if (undoable)
            {
                Undo.DestroyObjectImmediate(mb);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(mb);
            }
            n++;
        }
        return n;
    }
}
#endif
