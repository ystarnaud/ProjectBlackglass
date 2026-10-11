using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// A small pool of spatial AudioSources for combat sounds, played through the scene's existing AudioListener. It limits
    /// simultaneous voices, repeated plays and stacked gain, and pauses and resumes its voices with the tactical pause.
    /// Whether a voice is still in use comes from its own audio clock (which does not advance while paused), never from
    /// AudioSource.isPlaying, so a paused voice is never mistaken for a finished one. Sounds are only started by callers that
    /// already passed the knowledge gate.
    /// </summary>
    public sealed class CombatAudio : MonoBehaviour
    {
        public const int DefaultVoices = 10;

        sealed class Voice
        {
            public AudioSource Source;
            public bool InUse;
            public float EndClock;
            public int Priority;
            public long Order;
        }

        readonly CueThrottle throttle = new CueThrottle();
        readonly List<AudioSource> sources = new List<AudioSource>();
        Voice[] voices = new Voice[0];
        bool[] busyBuffer = new bool[0];
        int[] priorityBuffer = new int[0];
        long[] orderBuffer = new long[0];
        float clock;
        long order;

        public bool IsPaused { get; private set; }
        public int VoiceCount => voices.Length;
        public IReadOnlyList<AudioSource> Sources => sources;

        public int ActiveVoices
        {
            get
            {
                var n = 0;
                foreach (var voice in voices)
                    if (IsBusy(voice))
                        n++;
                return n;
            }
        }

        public void Initialize(int voiceCount = DefaultVoices)
        {
            if (voices.Length > 0)
                return;
            voiceCount = Mathf.Max(1, voiceCount);
            voices = new Voice[voiceCount];
            busyBuffer = new bool[voiceCount];
            priorityBuffer = new int[voiceCount];
            orderBuffer = new long[voiceCount];
            for (var i = 0; i < voiceCount; i++)
            {
                var child = new GameObject("Voice_" + i);
                child.transform.SetParent(transform, false);
                var source = child.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.loop = false;
                source.spatialBlend = 1f;
                source.rolloffMode = AudioRolloffMode.Logarithmic;
                source.dopplerLevel = 0f;
                voices[i] = new Voice { Source = source };
                sources.Add(source);
            }
        }

        // The audio clock is real time that stops while the combat sounds are paused. It is audio bookkeeping, not
        // simulation, and the only unscaled-time read in the presentation layer (allow-listed in the source audit).
        void Update()
        {
            if (!IsPaused)
                clock += Time.unscaledDeltaTime;
        }

        bool IsBusy(Voice voice) => voice.InUse && clock < voice.EndClock;

        public bool Play(AudioCue cue, Vector3 position, float masterVolume, CosmeticRandom random)
        {
            if (IsPaused || cue == null || voices.Length == 0)
                return false;
            var clip = cue.Pick(random);
            if (clip == null)
                return false;
            for (var i = 0; i < voices.Length; i++)
            {
                busyBuffer[i] = IsBusy(voices[i]);
                priorityBuffer[i] = voices[i].Priority;
                orderBuffer[i] = voices[i].Order;
            }
            var index = VoicePicker.Pick(busyBuffer, priorityBuffer, orderBuffer, cue.Priority);
            if (index < 0)
                return false;
            // Only a sound that has a voice may consume the throttle; a dropped one must not block later plays.
            if (!throttle.TryPlay(RuntimeHelpers.GetHashCode(cue), clock, cue.MinInterval, out var gain))
                return false;

            var voice = voices[index];
            var source = voice.Source;
            source.Stop();
            source.transform.position = position;
            source.clip = clip;
            source.volume = Mathf.Clamp01(cue.Volume * masterVolume * gain);
            source.pitch = random.Range(cue.PitchMin, cue.PitchMax);
            source.minDistance = cue.MinDistance;
            source.maxDistance = cue.MaxDistance;
            source.priority = cue.Priority;
            source.Play();
            voice.InUse = true;
            voice.EndClock = clock + clip.length / Mathf.Max(0.1f, source.pitch);
            voice.Priority = cue.Priority;
            voice.Order = ++order;
            return true;
        }

        /// <summary>Pauses or resumes every voice that is still in use. Idempotent.</summary>
        public void SetPaused(bool paused)
        {
            if (paused == IsPaused)
                return;
            IsPaused = paused;
            foreach (var voice in voices)
            {
                if (!IsBusy(voice))
                    continue;
                if (paused)
                    voice.Source.Pause();
                else
                    voice.Source.UnPause();
            }
        }

        /// <summary>Stops everything and forgets all throttling (mission change); safe while paused.</summary>
        public void StopAll()
        {
            foreach (var voice in voices)
            {
                voice.Source.Stop();
                voice.InUse = false;
                voice.EndClock = 0f;
            }
            throttle.Clear();
        }
    }
}
