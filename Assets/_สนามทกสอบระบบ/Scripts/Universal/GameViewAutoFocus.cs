// ============================================================================
// GameViewAutoFocus.cs — editor-only Play Mode focus utility for NotDEER.
//
// WHAT IT DOES
//   The instant Play Mode starts, focuses the Game view window.
//
// WHY THE GAME NEEDS IT
//   Unity silently DROPS every Cursor.lockState / Cursor.visible request while
//   the Game view has no keyboard focus — and pressing Play does NOT focus the
//   Game view (focus stays in the Scene view / Hierarchy). PlayerCameraController
//   requests the cursor lock from Awake, so without this utility the cursor
//   stays visible until the player's first click. This script gives the Game
//   view focus BEFORE the first scene Awake runs, so the very first lock
//   request engages: cursor hidden from frame zero, no click needed.
//
// v2.0 (2026-10-19) — Unity 6.6 (6000.6.3f1) analyzer fix, warning UAC0005:
//   Unity 6.6 ships new code analyzers. UAC0005 flags AppDomain.GetAssemblies()
//   ("may return already unloaded assemblies and result in errors or leaks"),
//   which the v1.0 assembly scan used. This version calls
//   UnityEngine.Assemblies.CurrentAssemblies.GetLoadedAssemblies() instead —
//   the exact replacement Unity's own warning message recommends (Unity 6.4+).
//   The old AppDomain scan is kept ONLY for Unity 6.3 LTS and older, inside a
//   preprocessor branch that is removed before the analyzer runs, so no
//   version can ever produce the warning. No behavior change.
//
//   v2.0 also re-asserts the focus once on the next editor tick as a safety
//   net for layouts that activate another window a moment after play starts
//   (focusing an already-focused window is a no-op).
//
// SETUP
//   None. Keep this file anywhere under Assets/ (it does not need an "Editor"
//   folder): the whole file is wrapped in #if UNITY_EDITOR, so it compiles
//   away completely in builds. If you are replacing the old v1.0, replace the
//   CONTENTS of the existing file so its .meta (GUID) is preserved.
// ============================================================================

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static class GameViewAutoFocus
{
    /// <summary>Full name of Unity's internal Game view window class.</summary>
    private const string GameViewTypeName = "UnityEditor.GameView";

    static GameViewAutoFocus()
    {
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
    }

    private static void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.EnteredPlayMode)
            return;

        // Focus immediately — EnteredPlayMode fires before the first scene
        // Awake, so PlayerCameraController's first cursor request engages.
        FocusGameView();

        // Safety net: some layouts activate another window a moment after
        // play starts. Re-assert once on the next editor tick (delayCall is
        // one-shot). Focusing an already-focused window is a no-op.
        EditorApplication.delayCall += FocusGameView;
    }

    private static void FocusGameView()
    {
        try
        {
            Type gameViewType = FindGameViewType();
            if (gameViewType == null)
            {
                Debug.LogWarning("[GameViewAutoFocus] Could not find the Game view window type ('"
                    + GameViewTypeName + "'). The Game view will not be auto-focused.");
                return;
            }

            EditorWindow gameView = EditorWindow.GetWindow(gameViewType);
            if (gameView != null)
                gameView.Focus();
        }
        catch (Exception e)
        {
            Debug.LogWarning("[GameViewAutoFocus] Could not focus the Game view: " + e.Message);
        }
    }

    /// <summary>
    /// Searches every loaded assembly for the internal GameView window type.
    /// Unity splits editor code across many assemblies (UnityEditor,
    /// UnityEditor.CoreModule, ...), so a single hard-coded assembly name would
    /// break on some versions – scanning them all works everywhere.
    /// </summary>
    private static Type FindGameViewType()
    {
#if UNITY_6000_4_OR_NEWER
        // Unity 6.4+ (incl. 6000.6.3f1): CurrentAssemblies.GetLoadedAssemblies()
        // returns only live, loaded assemblies — the Unity-recommended
        // replacement for AppDomain.GetAssemblies() (warning UAC0005 in
        // Unity 6.6 names it directly). Returns IReadOnlyList, so use Count.
        IReadOnlyList<Assembly> assemblies =
            UnityEngine.Assemblies.CurrentAssemblies.GetLoadedAssemblies();

        if (assemblies != null)
        {
            for (int i = 0; i < assemblies.Count; i++)
            {
                Type type = null;
                try
                {
                    type = assemblies[i].GetType(GameViewTypeName, false);
                }
                catch
                {
                    continue;
                }
                if (type != null)
                    return type;
            }
        }
        return null;
#else
        // Unity 6.3 LTS and older: the CurrentAssemblies API does not exist
        // there and the UAC0005 analyzer is not shipped there either, so the
        // classic AppDomain scan is the correct approach. This branch is
        // preprocessor-disabled on Unity 6.4+ (inactive code is invisible to
        // Roslyn analyzers), so it can never produce UAC0005.
        Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
        for (int i = 0; i < assemblies.Length; i++)
        {
            Type type = null;
            try
            {
                type = assemblies[i].GetType(GameViewTypeName, false);
            }
            catch
            {
                continue;
            }
            if (type != null)
                return type;
        }
        return null;
#endif
    }
}
#endif