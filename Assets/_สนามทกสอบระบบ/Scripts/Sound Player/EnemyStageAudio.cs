using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// ENEMY STAGE AUDIO — per-stage sound layer groups (v1.0).
///
/// A pure passenger: this script only READS the enemy's state
/// (EnemyController.IsFlee / IsStun / IsDisappearing / IsAttack). It never
/// moves the enemy, never changes a timer, never touches EnemyController
/// logic. Put it on the enemy ROOT (same GameObject as EnemyController).
///
/// STAGES — one sound setup each (the "dropdown" per stage):
///   • Idle   – Waiting / Stopped.
///   • Attack – chasing the player.
///   • Stun   – flashlight / SafeZone stun AND the disappear-thrash that
///              follows it (one continuous reaction).
///   • Flee   – the run-away despawn.
///   Priority when several flags are somehow true: Flee > Stun > Attack.
///
/// SOUND LAYER GROUPS (inside each stage):
///   Groups play step-by-step in list order (Group 1 → Group 2 → ...).
///   All sounds inside one group start AT THE SAME TIME.
///   Each group chooses how it continues from the PREVIOUS group:
///     • After Previous Group Ended (default) — starts when the previous
///       group is finished.
///     • After Previous Group Started — starts a set number of seconds
///       after the previous group BEGAN, without waiting for it to finish
///       (0 s = together with it).
///     • Delayed After Previous Group Ended — starts a set number of
///       seconds after the previous group finished.
///   When does a group count as "ended"? — at the end of its LONGEST
///   non-looping sound; looping sounds are ignored for that. A group with
///   ONLY looping sounds never ends by itself, so the next group can only
///   use "After Previous Group Started" — the Inspector auto-corrects any
///   other choice (and at runtime the system falls back to starting
///   together with it, with a console warning). The FIRST group of a stage
///   has no previous group, so it always starts with the stage itself (its
///   continue setting is ignored; the Inspector also resets
///   "After Previous Group Started" there).
///
/// LOOPING SOUNDS (per sound):
///   • None — plays once.
///   • Till Group End — loops until its group span has passed: "Loop Group
///     Span" groups counting its OWN group as the first (default 1 = stops
///     when its own group ends; 2 = also through the next group, ...).
///     If the span reaches past the last group that ever ends, the loop
///     simply keeps going till the stage ends.
///   • Till Stage End — loops until the stage changes.
///
/// Everything a stage plays is cut when the stage changes, the game pauses
/// (on unpause the current stage restarts from Group 1) or the enemy is
/// disabled/destroyed. AudioSources are auto-created on a child object
/// ("Enemy Stage Audio") — a separate pool, so nothing can fight the
/// footsteps (EnemyFootsteps) or any other audio on the enemy.
///
/// SETUP
///   1. Add to the enemy root. EnemyController is auto-found.
///   2. Open a stage, add layer groups top-to-bottom in play order.
///   3. Drop clips into each group (same group = simultaneous).
///   4. Right-click the component header → "Log Stage Schedules" to print
///      every stage's computed timing table into the Console.
///
/// v1.0 – first version.
/// </summary>
[DisallowMultipleComponent]
public class EnemyStageAudio : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Public types (Inspector)
    // ------------------------------------------------------------------ //

    /// <summary>How a sound loops.</summary>
    public enum SoundLoopMode
    {
        /// <summary>Plays once.</summary>
        None,
        /// <summary>Loops until its group span (own group + following ones) has ended.</summary>
        TillGroupEnd,
        /// <summary>Loops until the stage changes.</summary>
        TillStageEnd
    }

    /// <summary>How a layer group continues from the previous group.</summary>
    public enum GroupContinueMode
    {
        /// <summary>Starts when the previous group ended (default).</summary>
        AfterPreviousGroupEnded = 0,
        /// <summary>Starts a set number of seconds after the previous group BEGAN (no waiting for it to finish).</summary>
        AfterPreviousGroupStarted = 1,
        /// <summary>Starts a set number of seconds after the previous group ended.</summary>
        DelayedAfterPreviousGroupEnded = 2
    }

    /// <summary>One sound inside a layer group.</summary>
    [Serializable]
    public class LayerGroupSound
    {
        [Tooltip("The audio clip. An empty slot is skipped.")]
        public AudioClip clip;

        [Tooltip("Loudness of this sound (0–1).")]
        [Range(0f, 1f)] public float volume = 1f;

        [Tooltip("None = plays once. Till Group End = loops until its group span has ended. Till Stage End = loops until the stage changes.")]
        public SoundLoopMode loopMode = SoundLoopMode.None;

        [Tooltip("Used only by 'Till Group End': the sound keeps looping until this many groups (its own group counts as the first) have ended. 1 = stops when its own group ends. 2 = also through the next group, and so on.")]
        public int loopGroupSpan = 1;
    }

    /// <summary>One layer group: sounds that all start together.</summary>
    [Serializable]
    public class SoundLayerGroup
    {
        [Tooltip("Display name (e.g. 'Roar', 'Chase loop').")]
        public string name = "Group";

        [Tooltip("How this group continues from the previous group. The first group of a stage always starts with the stage itself (its setting is ignored).")]
        public GroupContinueMode continueFromPrevious = GroupContinueMode.AfterPreviousGroupEnded;

        [Tooltip("For 'After Previous Group Started': seconds the previous group plays before this one starts (0 = together with it).")]
        public float startAfterPreviousSeconds = 0f;

        [Tooltip("For 'Delayed After Previous Group Ended': extra seconds after the previous group ended.")]
        public float delayAfterPreviousEndSeconds = 0f;

        [Tooltip("All sounds in this list start at the same time when the group starts.")]
        public LayerGroupSound[] sounds = new LayerGroupSound[1];
    }

    /// <summary>The layer groups of one stage, played top-to-bottom.</summary>
    [Serializable]
    public class StageAudio
    {
        [Tooltip("Layer groups in play order: Group 1 → Group 2 → ... (list order is the play order).")]
        public SoundLayerGroup[] groups = new SoundLayerGroup[1];
    }

    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("References")]

    [Tooltip("The enemy this audio follows. Auto-found on this object (or its parent) when empty. This script only READS the enemy's state — no enemy logic is touched.")]
    [SerializeField] private EnemyController enemyController;

    [Header("Idle Stage (Waiting / Stopped)")]

    [Tooltip("Layer groups for the idle stage.")]
    [SerializeField] private StageAudio idleStage = new StageAudio();

    [Header("Attack Stage (chasing the player)")]

    [Tooltip("Layer groups for the attack stage.")]
    [SerializeField] private StageAudio attackStage = new StageAudio();

    [Header("Stun Stage (stun + the disappear-thrash)")]

    [Tooltip("Layer groups for the stun stage (flashlight / SafeZone stun and the disappear-thrash that follows).")]
    [SerializeField] private StageAudio stunStage = new StageAudio();

    [Header("Flee Stage (run-away despawn)")]

    [Tooltip("Layer groups for the flee stage.")]
    [SerializeField] private StageAudio fleeStage = new StageAudio();

    [Header("Audio Sources (auto-created child)")]

    [Tooltip("Configure the pooled AudioSources automatically (3D sound, no play-on-awake, no doppler). Turn OFF to keep their default settings.")]
    [SerializeField] private bool configureAudioSources = true;

    [Tooltip("0 = fully 2D, 1 = fully 3D (fades with distance — recommended for an enemy).")]
    [Range(0f, 1f)] [SerializeField] private float spatialBlend = 1f;

    [Tooltip("How far away the sounds are heard at full volume.")]
    [SerializeField] private float minDistance = 1f;

    [Tooltip("How far away the sounds fade out completely.")]
    [SerializeField] private float maxDistance = 60f;

    // ------------------------------------------------------------------ //
    // Runtime
    // ------------------------------------------------------------------ //

    /// <summary>Stage this component is currently following (for debugging/tests).</summary>
    public AudioStage CurrentStage => currentStage;

    /// <summary>Internal stage identifier.</summary>
    public enum AudioStage { None, Idle, Attack, Stun, Flee }

    /// <summary>One group's computed place in the schedule.</summary>
    private class ScheduledGroup
    {
        public SoundLayerGroup config;
        public float startAt;
        public float endAt;      // float.NaN = the group never ends on its own
        public bool started;
    }

    /// <summary>One currently-looping sound and when it must stop.</summary>
    private class ActiveLoop
    {
        public AudioSource source;
        public AudioClip clip;
        public float stopAt;     // float.NaN = till the stage ends
        public int groupIndex;
    }

    private readonly List<ScheduledGroup> schedule = new List<ScheduledGroup>();
    private readonly List<ActiveLoop> activeLoops = new List<ActiveLoop>();
    private readonly List<AudioSource> sourcePool = new List<AudioSource>();

    private GameObject poolObject;
    private AudioSource oneShotSource;   // pool[0] — reserved for one-shots
    private AudioStage currentStage = AudioStage.None;
    private bool wasPaused;

    // ------------------------------------------------------------------ //
    // Unity messages
    // ------------------------------------------------------------------ //

    private void Awake()
    {
        if (enemyController == null) enemyController = GetComponent<EnemyController>();
        if (enemyController == null) enemyController = GetComponentInParent<EnemyController>();
        if (enemyController == null)
        {
            Debug.LogWarning("[EnemyStageAudio] No EnemyController found. Add this component to the enemy root (same object as EnemyController) or assign the reference — it stays silent until then.", this);
        }

        // Dedicated child object: this pool can never fight EnemyFootsteps
        // or any other AudioSource on the enemy.
        poolObject = new GameObject("Enemy Stage Audio");
        poolObject.transform.SetParent(transform, false);

        oneShotSource = CreateConfiguredSource();
        sourcePool.Add(oneShotSource);
    }

    private void OnEnable()
    {
        // Force fresh state detection on the next Update.
        currentStage = AudioStage.None;
        wasPaused = false;
    }

    private void OnDisable()
    {
        StopAllStageAudio();
        currentStage = AudioStage.None;
        wasPaused = false;
    }

    private void Update()
    {
        bool paused = GamePauseManager.IsPaused;

        if (paused)
        {
            if (!wasPaused)
            {
                // Silence the stage; the schedule restarts on unpause.
                StopAllStageAudio();
                wasPaused = true;
            }
            return;
        }

        if (wasPaused)
        {
            wasPaused = false;
            // Restart the current stage from Group 1 (deterministic, no
            // skipped groups, no stuck loops), then advance normally.
            EnterStage(currentStage);
        }

        if (enemyController == null) return;   // reference destroyed — nothing to follow

        AudioStage stage = DetectStage();
        if (stage != currentStage)
        {
            EnterStage(stage);
        }

        AdvanceSchedule();
    }

    /// <summary>Clamps Inspector values and enforces the group rules at edit time.</summary>
    private void OnValidate()
    {
        ValidateStage(idleStage, "Idle");
        ValidateStage(attackStage, "Attack");
        ValidateStage(stunStage, "Stun");
        ValidateStage(fleeStage, "Flee");

        minDistance = Mathf.Max(0f, minDistance);
        maxDistance = Mathf.Max(minDistance, maxDistance);
    }

    // ------------------------------------------------------------------ //
    // Stage detection (read-only passenger)
    // ------------------------------------------------------------------ //

    /// <summary>Maps the enemy's state to a stage. Priority: Flee > Stun > Attack > Idle.</summary>
    private AudioStage DetectStage()
    {
        if (enemyController.IsFlee) return AudioStage.Flee;
        if (enemyController.IsStun || enemyController.IsDisappearing) return AudioStage.Stun;
        if (enemyController.IsAttack) return AudioStage.Attack;
        return AudioStage.Idle;
    }

    private StageAudio GetStageConfig(AudioStage stage)
    {
        switch (stage)
        {
            case AudioStage.Attack: return attackStage;
            case AudioStage.Stun: return stunStage;
            case AudioStage.Flee: return fleeStage;
            default: return idleStage;
        }
    }

    private static string GetStageName(AudioStage stage)
    {
        switch (stage)
        {
            case AudioStage.Attack: return "Attack";
            case AudioStage.Stun: return "Stun";
            case AudioStage.Flee: return "Flee";
            default: return "Idle";
        }
    }

    // ------------------------------------------------------------------ //
    // Stage entry / schedule
    // ------------------------------------------------------------------ //

    /// <summary>Stops everything, then computes the new stage's schedule.</summary>
    private void EnterStage(AudioStage stage)
    {
        StopAllStageAudio();
        currentStage = stage;

        if (stage == AudioStage.None) return;

        StageAudio config = GetStageConfig(stage);
        if (config == null || config.groups == null || config.groups.Length == 0) return;

        List<ScheduledGroup> built = BuildSchedule(config, Time.time, GetStageName(stage));
        schedule.Clear();
        schedule.AddRange(built);
    }

    /// <summary>
    /// Computes every group's start and end time. Group end = start + the
    /// longest NON-looping clip (loops never define a group's end). Groups
    /// that only contain loops (or nothing playable) never end on their own.
    /// </summary>
    private List<ScheduledGroup> BuildSchedule(StageAudio config, float stageStart, string stageName)
    {
        List<ScheduledGroup> result = new List<ScheduledGroup>();
        if (config == null || config.groups == null) return result;

        int count = config.groups.Length;
        float[] startAt = new float[count];
        float[] endAt = new float[count];

        for (int k = 0; k < count; k++)
        {
            SoundLayerGroup group = config.groups[k];
            if (group == null) group = new SoundLayerGroup();

            // ---- start time ------------------------------------------- //
            if (k == 0)
            {
                // The first group has no previous group — it always starts
                // with the stage itself, whatever its continue setting.
                startAt[0] = stageStart;
            }
            else
            {
                bool previousEndDefined = !float.IsNaN(endAt[k - 1]);

                switch (group.continueFromPrevious)
                {
                    case GroupContinueMode.AfterPreviousGroupStarted:
                        startAt[k] = startAt[k - 1] + group.startAfterPreviousSeconds;
                        break;

                    case GroupContinueMode.DelayedAfterPreviousGroupEnded:
                        if (previousEndDefined)
                        {
                            startAt[k] = endAt[k - 1] + group.delayAfterPreviousEndSeconds;
                        }
                        else
                        {
                            startAt[k] = startAt[k - 1];
                            WarnNeverEndingPrevious(stageName, k, group);
                        }
                        break;

                    default: // AfterPreviousGroupEnded
                        if (previousEndDefined)
                        {
                            startAt[k] = endAt[k - 1];
                        }
                        else
                        {
                            startAt[k] = startAt[k - 1];
                            WarnNeverEndingPrevious(stageName, k, group);
                        }
                        break;
                }
            }

            // ---- end time ---------------------------------------------- //
            float longest = 0f;
            bool hasUnloopedSound = false;
            if (group.sounds != null)
            {
                for (int s = 0; s < group.sounds.Length; s++)
                {
                    LayerGroupSound sound = group.sounds[s];
                    if (sound == null || sound.clip == null) continue;
                    if (sound.loopMode != SoundLoopMode.None) continue;   // loops don't define the end
                    hasUnloopedSound = true;
                    if (sound.clip.length > longest) longest = sound.clip.length;
                }
            }
            endAt[k] = hasUnloopedSound ? startAt[k] + longest : float.NaN;

            ScheduledGroup scheduled = new ScheduledGroup();
            scheduled.config = group;
            scheduled.startAt = startAt[k];
            scheduled.endAt = endAt[k];
            scheduled.started = false;
            result.Add(scheduled);
        }

        return result;
    }

    /// <summary>Starts due groups and stops due loops. Driven entirely by Time.time.</summary>
    private void AdvanceSchedule()
    {
        for (int i = 0; i < schedule.Count; i++)
        {
            ScheduledGroup group = schedule[i];
            if (group.started || Time.time < group.startAt) continue;
            group.started = true;
            StartGroupSounds(i, group);
        }

        for (int i = activeLoops.Count - 1; i >= 0; i--)
        {
            ActiveLoop loop = activeLoops[i];
            if (float.IsNaN(loop.stopAt) || Time.time < loop.stopAt) continue;
            StopLoop(i);
        }
    }

    // ------------------------------------------------------------------ //
    // Playing
    // ------------------------------------------------------------------ //

    /// <summary>Plays every sound of a group — same group = same moment.</summary>
    private void StartGroupSounds(int groupIndex, ScheduledGroup group)
    {
        if (group.config.sounds == null) return;

        for (int s = 0; s < group.config.sounds.Length; s++)
        {
            LayerGroupSound sound = group.config.sounds[s];
            if (sound == null || sound.clip == null) continue;
            if (sound.volume <= 0f) continue;

            if (sound.loopMode == SoundLoopMode.None)
            {
                if (oneShotSource != null)
                {
                    oneShotSource.PlayOneShot(sound.clip, sound.volume);
                }
                continue;
            }

            AudioSource source = GetFreeLoopSource();
            if (source == null) continue;

            source.clip = sound.clip;
            source.loop = true;
            source.volume = sound.volume;
            source.Play();

            ActiveLoop loop = new ActiveLoop();
            loop.source = source;
            loop.clip = sound.clip;
            loop.groupIndex = groupIndex;
            loop.stopAt = sound.loopMode == SoundLoopMode.TillStageEnd
                ? float.NaN
                : ComputeLoopStopTime(groupIndex, sound.loopGroupSpan);
            activeLoops.Add(loop);
        }
    }

    /// <summary>
    /// When a 'Till Group End' loop stops: at the end of the group
    /// (own index + span - 1). If that group (or every later one) never
    /// ends on its own, the loop keeps going till the stage ends.
    /// </summary>
    private float ComputeLoopStopTime(int groupIndex, int span)
    {
        int target = groupIndex + span - 1;
        if (target > schedule.Count - 1) target = schedule.Count - 1;

        for (int g = target; g < schedule.Count; g++)
        {
            if (!float.IsNaN(schedule[g].endAt)) return schedule[g].endAt;
        }
        return float.NaN;
    }

    /// <summary>Stops one active loop and frees its source.</summary>
    private void StopLoop(int index)
    {
        ActiveLoop loop = activeLoops[index];
        if (loop.source != null)
        {
            loop.source.Stop();
            loop.source.clip = null;
        }
        activeLoops.RemoveAt(index);
    }

    /// <summary>Stops every sound of the current stage (loops and one-shots).</summary>
    private void StopAllStageAudio()
    {
        for (int i = activeLoops.Count - 1; i >= 0; i--) StopLoop(i);
        if (oneShotSource != null) oneShotSource.Stop();
        schedule.Clear();
    }

    /// <summary>A pooled source not currently used by a loop (index 0 is reserved for one-shots).</summary>
    private AudioSource GetFreeLoopSource()
    {
        for (int i = 1; i < sourcePool.Count; i++)
        {
            bool inUse = false;
            for (int l = 0; l < activeLoops.Count; l++)
            {
                if (activeLoops[l].source == sourcePool[i]) { inUse = true; break; }
            }
            if (!inUse) return sourcePool[i];
        }

        AudioSource source = CreateConfiguredSource();
        sourcePool.Add(source);
        return source;
    }

    /// <summary>Creates one more AudioSource on the pool child object.</summary>
    private AudioSource CreateConfiguredSource()
    {
        if (poolObject == null) return null;

        AudioSource source = poolObject.AddComponent<AudioSource>();
        if (source != null && configureAudioSources)
        {
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = spatialBlend;
            source.dopplerLevel = 0f;
            source.rolloffMode = AudioRolloffMode.Logarithmic;
            source.minDistance = minDistance;
            source.maxDistance = maxDistance;
        }
        return source;
    }

    // ------------------------------------------------------------------ //
    // Validation (edit time)
    // ------------------------------------------------------------------ //

    /// <summary>Clamps values and enforces the group rules while editing.</summary>
    private void ValidateStage(StageAudio stage, string stageName)
    {
        if (stage == null || stage.groups == null) return;

        for (int k = 0; k < stage.groups.Length; k++)
        {
            SoundLayerGroup group = stage.groups[k];
            if (group == null) continue;

            group.startAfterPreviousSeconds = Mathf.Max(0f, group.startAfterPreviousSeconds);
            group.delayAfterPreviousEndSeconds = Mathf.Max(0f, group.delayAfterPreviousEndSeconds);

            if (group.sounds != null)
            {
                for (int s = 0; s < group.sounds.Length; s++)
                {
                    if (group.sounds[s] != null)
                    {
                        group.sounds[s].loopGroupSpan = Mathf.Max(1, group.sounds[s].loopGroupSpan);
                    }
                }
            }

            if (k == 0)
            {
                // The first group has no previous group — 'After Previous
                // Group Started' is not available there.
                if (group.continueFromPrevious == GroupContinueMode.AfterPreviousGroupStarted)
                {
                    group.continueFromPrevious = GroupContinueMode.AfterPreviousGroupEnded;
                    Debug.LogWarning("[EnemyStageAudio] " + stageName + ": group 1 has no previous group — 'After Previous Group Started' is not available there. Reset to 'After Previous Group Ended' (the first group always starts with the stage).", this);
                }
                continue;
            }

            // When the PREVIOUS group contains only looping sounds (it never
            // ends on its own), only 'After Previous Group Started' works.
            SoundLayerGroup previous = stage.groups[k - 1];
            if (previous != null && GroupHasOnlyLoops(previous) &&
                group.continueFromPrevious != GroupContinueMode.AfterPreviousGroupStarted)
            {
                group.continueFromPrevious = GroupContinueMode.AfterPreviousGroupStarted;
                Debug.LogWarning("[EnemyStageAudio] " + stageName + ": group " + (k + 1) +
                    " waits for group " + k + " to end, but group " + k +
                    " contains only looping sounds (a group's end comes from its longest NON-looping clip). Switched to 'After Previous Group Started'.", this);
            }
        }
    }

    /// <summary>True when the group has at least one playable clip and ALL of them loop.</summary>
    private static bool GroupHasOnlyLoops(SoundLayerGroup group)
    {
        if (group.sounds == null) return false;

        bool hasPlayableClip = false;
        for (int s = 0; s < group.sounds.Length; s++)
        {
            LayerGroupSound sound = group.sounds[s];
            if (sound == null || sound.clip == null) continue;
            if (sound.loopMode == SoundLoopMode.None) return false;
            hasPlayableClip = true;
        }
        return hasPlayableClip;
    }

    private static void WarnNeverEndingPrevious(string stageName, int groupIndex, SoundLayerGroup group)
    {
        Debug.LogWarning("[EnemyStageAudio] " + stageName + ": group " + (groupIndex + 1) +
            " ('" + (group.name ?? "?") + "') waits for the previous group to end, but that group contains only looping sounds or nothing playable — it never ends on its own. The group now starts together with the previous group instead.", null);
    }

    // ------------------------------------------------------------------ //
    // Debug helpers
    // ------------------------------------------------------------------ //

    [ContextMenu("Log Stage Schedules")]
    private void LogStageSchedules()
    {
        LogOneStageSchedule(AudioStage.Idle, idleStage);
        LogOneStageSchedule(AudioStage.Attack, attackStage);
        LogOneStageSchedule(AudioStage.Stun, stunStage);
        LogOneStageSchedule(AudioStage.Flee, fleeStage);
    }

    private void LogOneStageSchedule(AudioStage stage, StageAudio config)
    {
        List<ScheduledGroup> built = BuildSchedule(config, 0f, GetStageName(stage));

        System.Text.StringBuilder text = new System.Text.StringBuilder();
        text.Append("[EnemyStageAudio] ").Append(GetStageName(stage)).Append(" stage — ");
        text.Append(built.Count).Append(" layer group(s). Times relative to stage start:");

        for (int k = 0; k < built.Count; k++)
        {
            ScheduledGroup group = built[k];

            text.Append("\n  Group ").Append(k + 1).Append(" '").Append(group.config.name ?? "?").Append("'");
            text.Append(" — starts ").Append(group.startAt.ToString("0.00")).Append(" s");
            if (float.IsNaN(group.endAt)) text.Append(", never ends on its own (only loops / nothing playable)");
            else text.Append(", ends ").Append(group.endAt.ToString("0.00")).Append(" s (longest non-looping clip)");

            if (group.config.sounds == null) continue;
            for (int s = 0; s < group.config.sounds.Length; s++)
            {
                LayerGroupSound sound = group.config.sounds[s];
                if (sound == null || sound.clip == null) continue;

                text.Append("\n    • '").Append(sound.clip.name).Append("' (volume ").Append(sound.volume.ToString("0.00")).Append(") — ");
                if (sound.loopMode == SoundLoopMode.None)
                {
                    text.Append("one-shot");
                }
                else if (sound.loopMode == SoundLoopMode.TillStageEnd)
                {
                    text.Append("loops till the STAGE ends");
                }
                else
                {
                    float stopAt = float.NaN;
                    int target = k + sound.loopGroupSpan - 1;
                    if (target > built.Count - 1) target = built.Count - 1;
                    for (int g = target; g < built.Count; g++)
                    {
                        if (!float.IsNaN(built[g].endAt)) { stopAt = built[g].endAt; break; }
                    }
                    if (float.IsNaN(stopAt)) text.Append("loops (span ").Append(sound.loopGroupSpan).Append(") till the STAGE ends (no later group ever ends)");
                    else text.Append("loops (span ").Append(sound.loopGroupSpan).Append(") until ").Append(stopAt.ToString("0.00")).Append(" s");
                }
            }
        }

        Debug.Log(text.ToString(), this);
    }

    [ContextMenu("Stop All Stage Audio")]
    private void StopAllStageAudioMenu()
    {
        StopAllStageAudio();
    }
}
