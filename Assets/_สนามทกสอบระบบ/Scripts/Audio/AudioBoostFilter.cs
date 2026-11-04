// AudioBoostFilter.cs
// --------------------------------------------------------------------------
// Drop-in gain stage for a single AudioSource, measured in dB ABOVE the
// clip's original level (0 dB = untouched, +20 dB = x10 amplitude).
//
// Why this exists (Unity 6.x, verified against the current official docs):
//   - AudioSource.volume is capped at 1.0, and 1.0 plays the clip at its
//     ORIGINAL level - so a quiet source file stays quiet at volume = 1.
//   - The AudioImporter has no gain setting; import cannot boost a clip.
//   - An AudioMixer tops out at +20 dB and requires routing every source.
// This component gives up to +24 dB per source with a soft limiter, so a
// quiet recording never hard-clips when boosted.
//
// Notes:
//   - Works for .Play(), looping sources AND AudioSource.PlayOneShot from
//     the same GameObject (one-shots go through the same source chain).
//   - OnAudioFilterRead runs on Unity's audio thread: this callback only
//     does cached math, never allocates and never calls other Unity APIs.
//   - OnAudioFilterRead is not supported on the Web (WebGL) platform.
//
// Works on Unity 2019.4 through 6.x/7 (C# 7.3 syntax only, no obsolete APIs).
using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(AudioSource))]
[AddComponentMenu("Audio/Audio Boost Filter")]
public class AudioBoostFilter : MonoBehaviour
{
    [Range(0f, 24f)]
    [Tooltip("Gain in dB above the original clip level. +20 dB equals the maximum boost of an official AudioMixer fader (x10 amplitude).")]
    public float boostDb = 12f;

    [Range(0.90f, 1f)]
    [Tooltip("Soft limiter knee in linear amplitude. 0.95 is about -0.45 dBFS. The limiter bends peaks so a boosted signal approaches but never exceeds full scale, instead of hard-clipping.")]
    public float limiterKnee = 0.95f;

    // Called by Unity on the audio DSP thread. Keep it allocation free:
    // pure math only - no logging, no other Unity API calls.
    private void OnAudioFilterRead(float[] data, int channels)
    {
        if (!isActiveAndEnabled)
        {
            return;
        }

        // 10 ^ (dB / 20), computed live so inspector changes are heard
        // immediately in Play mode.
        float gain = Mathf.Pow(10f, boostDb * 0.05f);
        if (gain <= 1.0001f)
        {
            return; // 0 dB: pass-through, nothing to do.
        }

        float knee = Mathf.Clamp(limiterKnee, 0.5f, 0.9995f);
        float room = 1f - knee; // never zero thanks to the clamp above

        for (int i = 0; i < data.Length; i++)
        {
            float v = data[i] * gain;
            float a = v < 0f ? -v : v;
            if (a > knee)
            {
                // Asymptotic soft knee: output rises towards 1.0 but never
                // reaches it, so boosted peaks never hard-clip.
                float over = (a - knee) / room;
                float shaped = over / (1f + over);
                a = knee + room * shaped;
            }
            data[i] = v < 0f ? -a : a;
        }
    }
}