// AudioBoostWindow.cs   (editor only)
// --------------------------------------------------------------------------
// Audio Boost Studio - one-click loudness repair for projects whose source
// audio was recorded too quietly.
//
//   Menu: Window > Audio > Audio Boost Studio
//
// Official limits this tool works around (Unity 6.x docs, verified):
//   - AudioSource.volume: 0.0..1.0, where 1.0 = the clip's ORIGINAL level.
//     A quiet file can never be made louder than itself through volume.
//   - AudioImporter: no gain property exists, so import cannot boost.
//   - AudioMixer: group faders top out at +20 dB.
// What this window does instead:
//   1. Rewrites the quiet .wav files themselves (peak-normalize to a target,
//      or apply +dB, auto-capped to a ceiling so nothing ever clips).
//      Every change is backed up to '<file>.wav.bak' and can be undone.
//   2. Optionally drops an AudioBoostFilter (+dB with soft limiter) on every
//      AudioSource in the loaded scenes / prefabs - the right tool for
//      compressed sources (mp3/ogg) that cannot be rewritten losslessly.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public class AudioBoostStudio : EditorWindow
{
    // ---------------- settings ----------------
    private bool normalizeMode = true;   // false = fixed +dB boost
    private float targetPeakDb = -1.0f;  // normalize target (dBFS)
    private float boostDb = 12f;         // fixed boost (dB)
    private float ceilingDb = -0.5f;     // never exceed this peak (dBFS)
    private bool makeBackups = true;

    // ---------------- scan data ----------------
    private sealed class Row
    {
        public AudioClip clip;
        public string path;
        public string name;
        public string ext;
        public bool isWav;
        public bool analyzed;        // wav analyzed straight from the source file
        public string note = "";
        public string detail = "";
        public string status = "-";
        public Color statusColor = Color.white;
        public float peakDb;
        public float rmsDb;
        public bool hasBackup;
    }

    private readonly List<Row> rows = new List<Row>();
    private string scanSummary = "";
    private bool anyImporterNormalize;
    private Vector2 rowsScroll;
    private Vector2 logScroll;
    private readonly List<string> log = new List<string>();

    private static readonly Color ColQuiet = new Color(1f, 0.35f, 0.35f);
    private static readonly Color ColSoft = new Color(1f, 0.75f, 0.25f);
    private static readonly Color ColOk = new Color(0.45f, 0.9f, 0.45f);

    [MenuItem("Window/Audio/Audio Boost Studio")]
    private static void Open()
    {
        AudioBoostStudio w = GetWindow<AudioBoostStudio>("Audio Boost Studio");
        w.minSize = new Vector2(560f, 460f);
    }

    private void OnEnable()
    {
        Scan();
    }

    // ======================================================================
    //  SCAN
    // ======================================================================
    private void Scan()
    {
        rows.Clear();
        int wavCount = 0;
        int quietCount = 0;
        anyImporterNormalize = false;

        string[] guids = AssetDatabase.FindAssets("t:AudioClip", new[] { "Assets" });
        for (int g = 0; g < guids.Length; g++)
        {
            string path = AssetDatabase.GUIDToAssetPath(guids[g]);
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }
            AudioClip clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            if (clip == null)
            {
                continue;
            }

            Row r = new Row();
            r.clip = clip;
            r.path = path;
            r.name = Path.GetFileNameWithoutExtension(path);
            r.ext = Path.GetExtension(path).ToLowerInvariant();
            r.isWav = r.ext == ".wav";
            r.hasBackup = File.Exists(path + ".bak");

            // Importer settings (scripting API) + the Force To Mono / Normalize
            // flags, which only apply to the mono down-mix, read from the .meta.
            AudioImporter imp = AssetImporter.GetAtPath(path) as AudioImporter;
            AudioClipLoadType loadType = imp != null
                ? imp.defaultSampleSettings.loadType
                : AudioClipLoadType.DecompressOnLoad;
            bool metaForceToMono;
            bool metaNormalize;
            ReadMetaFlags(path, out metaForceToMono, out metaNormalize);
            if (metaForceToMono && metaNormalize)
            {
                anyImporterNormalize = true;
            }

            if (r.isWav)
            {
                wavCount++;
                WavClip w = WavClip.Load(path);
                if (w.Ok)
                {
                    r.analyzed = true;
                    r.peakDb = w.PeakDb;
                    r.rmsDb = w.RmsDb;
                    if (r.rmsDb < -30f)
                    {
                        r.status = "QUIET";
                        r.statusColor = ColQuiet;
                        quietCount++;
                    }
                    else if (r.rmsDb < -18f)
                    {
                        r.status = "SOFT";
                        r.statusColor = ColSoft;
                    }
                    else
                    {
                        r.status = "OK";
                        r.statusColor = ColOk;
                    }
                    r.detail = "peak " + w.PeakDb.ToString("F1") + " dBFS · RMS " + w.RmsDb.ToString("F1")
                        + " dBFS · " + w.SampleRate + " Hz · " + w.Channels + "ch · "
                        + (w.IsFloat ? "float32" : w.BitsPerSample + "-bit")
                        + " · " + loadType
                        + (metaForceToMono && metaNormalize ? " · importer Normalize active (Force To Mono)" : "");
                }
                else
                {
                    r.note = w.Error;
                    r.status = "ERR";
                    r.statusColor = ColSoft;
                    r.detail = w.Error;
                }
            }
            else
            {
                r.note = "compressed source (" + r.ext + ") - use the AudioBoostFilter, or convert to WAV";
                r.status = "n/a";
                r.statusColor = Color.white;
                r.detail = clip.length.ToString("F1") + "s · " + clip.frequency + " Hz · " + clip.channels
                    + "ch · " + loadType + " · use AudioBoostFilter (below)";
            }
            rows.Add(r);
        }

        scanSummary = rows.Count + " clips · " + wavCount + " wav · " + quietCount + " quiet (RMS < -30 dBFS)";
        Repaint();
    }

    private static void ReadMetaFlags(string assetPath, out bool forceToMono, out bool normalize)
    {
        forceToMono = false;
        normalize = false;
        string meta = assetPath + ".meta";
        if (!File.Exists(meta))
        {
            return;
        }
        try
        {
            string t = File.ReadAllText(meta);
            Match fm = Regex.Match(t, @"(?m)^\s*forceToMono:\s*(\d)");
            Match nm = Regex.Match(t, @"(?m)^\s*normalize:\s*(\d)");
            if (fm.Success)
            {
                forceToMono = fm.Groups[1].Value == "1";
            }
            if (nm.Success)
            {
                normalize = nm.Groups[1].Value == "1";
            }
        }
        catch (Exception)
        {
            // unreadable .meta: keep defaults, nothing to report
        }
    }

    // ======================================================================
    //  APPLY / RESTORE (wav files)
    // ======================================================================
    private enum ApplyResult { Boosted, NoChange, Failed }

    private ApplyResult ApplyToRow(Row r)
    {
        WavClip w = WavClip.Load(r.path);
        if (!w.Ok)
        {
            r.note = w.Error;
            AddLog("x " + r.name + " - " + w.Error);
            return ApplyResult.Failed;
        }

        // Compute the gain for the selected mode. Both modes are capped so
        // the written file can never clip: normalize hits the target exactly,
        // fixed boost is reduced to the ceiling if +dB would overshoot.
        float gain;
        string desc;
        if (normalizeMode)
        {
            gain = WavClip.ToLinear(targetPeakDb) / w.Peak;
            desc = "normalize -> " + targetPeakDb.ToString("F1") + " dBFS";
        }
        else
        {
            gain = WavClip.ToLinear(boostDb);
            float ceiling = WavClip.ToLinear(ceilingDb);
            if (w.Peak * gain > ceiling)
            {
                gain = ceiling / w.Peak;
            }
            desc = "boost +" + boostDb.ToString("F1") + " dB (capped at " + ceilingDb.ToString("F1") + " dBFS)";
        }

        if (gain <= 1.0001f)
        {
            AddLog("- " + r.name + " - already at target, no change needed");
            return ApplyResult.NoChange;
        }

        float beforePeakDb = w.PeakDb;
        float beforeRmsDb = w.RmsDb;

        if (makeBackups && !File.Exists(r.path + ".bak"))
        {
            try
            {
                File.Copy(r.path, r.path + ".bak", false);
            }
            catch (Exception e)
            {
                AddLog("x " + r.name + " - backup failed: " + e.Message);
                return ApplyResult.Failed;
            }
        }

        w.ApplyGain(gain);
        try
        {
            w.Save(r.path);
        }
        catch (Exception e)
        {
            AddLog("x " + r.name + " - write failed: " + e.Message);
            return ApplyResult.Failed;
        }
        AssetDatabase.ImportAsset(r.path, ImportAssetOptions.ForceUpdate);
        AddLog("OK " + r.name + "  peak " + beforePeakDb.ToString("F1") + " -> " + w.PeakDb.ToString("F1")
            + " dBFS · RMS " + beforeRmsDb.ToString("F1") + " -> " + w.RmsDb.ToString("F1") + " dBFS  (" + desc + ")");
        return ApplyResult.Boosted;
    }

    private void ApplyToAllWavs()
    {
        int targets = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].isWav)
            {
                targets++;
            }
        }
        if (targets == 0)
        {
            EditorUtility.DisplayDialog("Audio Boost Studio", "No .wav files found under Assets.", "OK");
            return;
        }
        if (!EditorUtility.DisplayDialog("Boost all WAV files?",
                "This will rewrite " + targets + " .wav file(s) in place ("
                + (makeBackups ? "with .bak backups - undoable per file" : "BACKUPS OFF - not undoable") + ").",
                "Apply", "Cancel"))
        {
            return;
        }

        int boosted = 0;
        int noChange = 0;
        int failed = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            if (!rows[i].isWav)
            {
                continue;
            }
            ApplyResult res = ApplyToRow(rows[i]);
            if (res == ApplyResult.Boosted)
            {
                boosted++;
            }
            else if (res == ApplyResult.NoChange)
            {
                noChange++;
            }
            else
            {
                failed++;
            }
        }
        AddLog("---- batch done: " + boosted + " boosted, " + noChange + " already at target, " + failed + " failed ----");
        Scan();
    }

    private bool RestoreRow(Row r)
    {
        if (!File.Exists(r.path + ".bak"))
        {
            return false;
        }
        try
        {
            File.Copy(r.path + ".bak", r.path, true);
            AssetDatabase.ImportAsset(r.path, ImportAssetOptions.ForceUpdate);
            AddLog("undo " + r.name + " - restored original from .bak");
            return true;
        }
        catch (Exception e)
        {
            AddLog("x " + r.name + " - restore failed: " + e.Message);
            return false;
        }
    }

    private void RestoreAllBackups()
    {
        int n = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            Row r = rows[i];
            if (r.isWav && File.Exists(r.path + ".bak"))
            {
                if (RestoreRow(r))
                {
                    n++;
                }
            }
        }
        AddLog("---- restored " + n + " file(s) from .bak ----");
        if (n > 0)
        {
            Scan();
        }
    }

    private void DeleteBackups()
    {
        if (!EditorUtility.DisplayDialog("Delete .bak backups?",
                "This removes the backup files (and their .meta). The boosted wav files stay as they are now.",
                "Delete", "Cancel"))
        {
            return;
        }
        int n = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            string bak = rows[i].path + ".bak";
            if (File.Exists(bak))
            {
                try
                {
                    File.Delete(bak);
                    if (File.Exists(bak + ".meta"))
                    {
                        File.Delete(bak + ".meta");
                    }
                    n++;
                }
                catch (Exception e)
                {
                    AddLog("x " + rows[i].name + " - delete failed: " + e.Message);
                }
            }
        }
        AssetDatabase.Refresh();
        AddLog("---- deleted " + n + " .bak file(s) ----");
        Scan();
    }

    // ======================================================================
    //  COMPONENT BATCH (AudioBoostFilter on AudioSources)
    // ======================================================================
    private void AddFiltersToLoadedScenes()
    {
        int addedTotal = 0;
        List<string> touched = new List<string>();
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            Scene sc = SceneManager.GetSceneAt(i);
            if (!sc.isLoaded)
            {
                continue;
            }
            GameObject[] roots = sc.GetRootGameObjects();
            if (roots == null)
            {
                continue;
            }
            int added = 0;
            for (int g = 0; g < roots.Length; g++)
            {
                AudioSource[] sources = roots[g].GetComponentsInChildren<AudioSource>(true);
                for (int s = 0; s < sources.Length; s++)
                {
                    AudioSource src = sources[s];
                    if (src == null)
                    {
                        continue;
                    }
                    if (src.gameObject.GetComponent<AudioBoostFilter>() != null)
                    {
                        continue;
                    }
                    Undo.AddComponent<AudioBoostFilter>(src.gameObject);
                    added++;
                }
            }
            if (added > 0)
            {
                EditorSceneManager.MarkSceneDirty(sc);
                touched.Add(sc.name + " (+" + added + ")");
                addedTotal += added;
            }
        }
        if (addedTotal > 0)
        {
            AddLog("AudioBoostFilter added to " + addedTotal + " AudioSource(s): " + string.Join(", ", touched.ToArray()));
        }
        else
        {
            AddLog("No AudioSource needed a new filter (loaded scenes).");
        }
    }

    private void AddFiltersToPrefabs()
    {
        if (!EditorUtility.DisplayDialog("Apply to all Prefabs?",
                "Loads every prefab under Assets, adds an AudioBoostFilter to each AudioSource that does not have one yet, and saves the prefab.",
                "Apply", "Cancel"))
        {
            return;
        }
        string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" });
        int changedPrefabs = 0;
        int addedTotal = 0;
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
                AudioSource[] sources = contents.GetComponentsInChildren<AudioSource>(true);
                int added = 0;
                for (int s = 0; s < sources.Length; s++)
                {
                    AudioSource src = sources[s];
                    if (src == null)
                    {
                        continue;
                    }
                    if (src.gameObject.GetComponent<AudioBoostFilter>() != null)
                    {
                        continue;
                    }
                    src.gameObject.AddComponent<AudioBoostFilter>();
                    added++;
                }
                if (added > 0)
                {
                    PrefabUtility.SaveAsPrefabAsset(contents, path);
                    changedPrefabs++;
                    addedTotal += added;
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }
        AddLog("Prefabs updated: " + changedPrefabs + " file(s), " + addedTotal + " filter(s) added.");
    }

    private void RemoveAllFilters()
    {
        if (!EditorUtility.DisplayDialog("Remove all AudioBoostFilters?",
                "Removes the component from every AudioSource in the loaded scenes and in all prefabs under Assets. The wav files themselves are not touched.",
                "Remove", "Cancel"))
        {
            return;
        }
        int removedScenes = 0;
        int removedTotal = 0;
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            Scene sc = SceneManager.GetSceneAt(i);
            if (!sc.isLoaded)
            {
                continue;
            }
            GameObject[] roots = sc.GetRootGameObjects();
            if (roots == null)
            {
                continue;
            }
            int removedInScene = 0;
            for (int g = 0; g < roots.Length; g++)
            {
                AudioBoostFilter[] filters = roots[g].GetComponentsInChildren<AudioBoostFilter>(true);
                for (int f = 0; f < filters.Length; f++)
                {
                    if (filters[f] == null)
                    {
                        continue;
                    }
                    Undo.DestroyObjectImmediate(filters[f]);
                    removedInScene++;
                }
            }
            if (removedInScene > 0)
            {
                removedTotal += removedInScene;
                EditorSceneManager.MarkSceneDirty(sc);
                removedScenes++;
            }
        }

        string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" });
        int changedPrefabs = 0;
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
                AudioBoostFilter[] filters = contents.GetComponentsInChildren<AudioBoostFilter>(true);
                if (filters.Length > 0)
                {
                    for (int f = 0; f < filters.Length; f++)
                    {
                        if (filters[f] == null)
                        {
                            continue;
                        }
                        UnityEngine.Object.DestroyImmediate(filters[f]);
                        removedTotal++;
                    }
                    PrefabUtility.SaveAsPrefabAsset(contents, path);
                    changedPrefabs++;
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }
        AddLog("Removed " + removedTotal + " filter(s) from " + removedScenes + " scene(s) and " + changedPrefabs + " prefab(s).");
    }

    // ======================================================================
    //  LOG
    // ======================================================================
    private void AddLog(string line)
    {
        log.Add(line);
        if (log.Count > 200)
        {
            log.RemoveRange(0, log.Count - 200);
        }
        Repaint();
    }

    // ======================================================================
    //  GUI
    // ======================================================================
    private void OnGUI()
    {
        EditorGUILayout.Space();
        EditorGUILayout.HelpBox(
            "Official limits: AudioSource.volume is capped at 1.0 (= the source file's own level), " +
            "the AudioImporter has no gain, an AudioMixer tops out at +20 dB.\n" +
            "This tool instead rewrites the quiet .wav files themselves (peak-normalize / +dB, auto-capped so nothing clips) " +
            "and can add an AudioBoostFilter (+dB, soft limiter) to AudioSources for compressed sources.",
            MessageType.Info);

        // ---- mode + parameters ----
        int mode = GUILayout.Toolbar(normalizeMode ? 0 : 1, new string[] { "Normalize (peak -> target)", "Boost (+ dB)" });
        normalizeMode = mode == 0;
        if (normalizeMode)
        {
            targetPeakDb = EditorGUILayout.Slider("Target peak (dBFS)", targetPeakDb, -6f, -0.1f);
        }
        else
        {
            boostDb = EditorGUILayout.FloatField("+ dB above original", boostDb);
            boostDb = Mathf.Clamp(boostDb, 1f, 24f);
            ceilingDb = EditorGUILayout.Slider("Ceiling (dBFS)", ceilingDb, -6f, -0.1f);
        }
        makeBackups = EditorGUILayout.Toggle("Keep .bak backups (undoable)", makeBackups);

        // ---- actions ----
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Rescan", GUILayout.Width(80)))
        {
            Scan();
        }
        if (GUILayout.Button("Apply to ALL .wav"))
        {
            ApplyToAllWavs();
        }
        if (GUILayout.Button("Restore all backups"))
        {
            RestoreAllBackups();
        }
        if (GUILayout.Button("Delete .bak files", GUILayout.Width(110)))
        {
            DeleteBackups();
        }
        EditorGUILayout.EndHorizontal();

        if (anyImporterNormalize)
        {
            EditorGUILayout.HelpBox(
                "Some clips have Force To Mono + importer Normalize enabled: the importer will re-peak-normalize " +
                "the mono down-mix at import, which flattens custom file levels. Untick 'Normalize' in those clips' import settings.",
                MessageType.Warning);
        }

        EditorGUILayout.LabelField(scanSummary, EditorStyles.miniLabel);
        EditorGUILayout.Space();

        // ---- file rows ----
        rowsScroll = EditorGUILayout.BeginScrollView(rowsScroll, GUILayout.ExpandHeight(true));
        for (int i = 0; i < rows.Count; i++)
        {
            DrawRow(rows[i]);
        }
        if (rows.Count == 0)
        {
            EditorGUILayout.HelpBox("No AudioClips found. Click 'Rescan'.", MessageType.None);
        }
        EditorGUILayout.EndScrollView();

        EditorGUILayout.Space();

        // ---- component section ----
        EditorGUILayout.LabelField("Runtime component (for mp3/ogg sources + live control)", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Add filter to AudioSources in loaded scenes"))
        {
            AddFiltersToLoadedScenes();
        }
        if (GUILayout.Button("Also all Prefabs"))
        {
            AddFiltersToPrefabs();
        }
        if (GUILayout.Button("Remove all filters", GUILayout.Width(120)))
        {
            RemoveAllFilters();
        }
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.LabelField(
            "Adds AudioBoostFilter (+12 dB default, soft limiter) to every AudioSource. Works with PlayOneShot. " +
            "Not supported on the Web (WebGL) platform.",
            EditorStyles.miniLabel);

        EditorGUILayout.Space();

        // ---- log ----
        EditorGUILayout.LabelField("Log", EditorStyles.boldLabel);
        logScroll = EditorGUILayout.BeginScrollView(logScroll, false, true, GUILayout.Height(130));
        for (int i = 0; i < log.Count; i++)
        {
            EditorGUILayout.LabelField(log[i], EditorStyles.miniLabel);
        }
        EditorGUILayout.EndScrollView();
    }

    private void DrawRow(Row r)
    {
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button(new GUIContent(r.name), EditorStyles.boldLabel, GUILayout.MinWidth(140)))
        {
            EditorGUIUtility.PingObject(r.clip); // ping the asset in the Project window
        }
        GUI.color = r.statusColor;
        GUILayout.Label(r.status, EditorStyles.miniLabel, GUILayout.Width(46));
        GUI.color = Color.white;

        if (r.isWav && r.analyzed)
        {
            GUILayout.Label(r.peakDb.ToString("F1") + " dB", EditorStyles.miniLabel, GUILayout.Width(64));
            if (GUILayout.Button("Apply", EditorStyles.miniButton, GUILayout.Width(56)))
            {
                ApplyToRow(r);
                Scan();
            }
            GUI.enabled = r.hasBackup;
            if (GUILayout.Button("Undo", EditorStyles.miniButton, GUILayout.Width(52)))
            {
                RestoreRow(r);
                Scan();
            }
            GUI.enabled = true;
        }
        else
        {
            GUILayout.Label(r.ext, EditorStyles.miniLabel, GUILayout.Width(64));
            GUILayout.Label(r.note, EditorStyles.miniLabel, GUILayout.MinWidth(80));
        }
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.LabelField(r.path + "  ·  " + r.detail, EditorStyles.miniLabel);
    }
}