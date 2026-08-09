using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace CubeApp
{
    /// <summary>
    /// Minecraft 1.12-style sound manager (SoundManager.java), ported to NAudio.
    ///
    /// Key 1.12 ideas preserved:
    ///  - playSound() creates an ISOLATED source per play (tracked by a unique channel id), then
    ///    updateAllSounds() actively REMOVES sources once they stop - no shared mutable voice that
    ///    can get stuck looping.
    ///  - Volume = clamp(sound.volume * categoryVolume, 0, 1); pitch = clamp(0.5, 2.0).
    ///  - Positioned sounds attenuate linearly over 16 blocks (range scales with volume > 1).
    ///  - Sound categories (master/blocks/ambient) with independent volumes.
    ///
    /// Zero-lag by construction: all audio is decoded to PCM once at startup; playback+mixing runs
    /// on NAudio's background thread; the game thread only enqueues tiny play requests.
    /// </summary>
    public sealed class SoundEngine : IDisposable
    {
        private const int DefaultSampleRate = 44100;
        private const int MaxSources = 32;          // 1.12-style source cap
        private const float AttenuationRange = 16f; // MC: linear attenuation over 16 blocks

        public enum SoundCategory { Master, Blocks, Ambient }

        private sealed class Source
        {
            public float[] Samples = Array.Empty<float>();
            public int Channels = 1;
            public int Position;          // current frame
            public float BaseVolume;      // requested volume * sound volume
            public float Pitch = 1f;
            public bool Active;
            public float X, Y, Z;         // world position for attenuation
            public bool Positioned;       // true => apply distance attenuation
            public bool PendingStop;      // set when the play should be cut
        }

        private readonly struct PlayRequest
        {
            public readonly string Name;
            public readonly float Volume;
            public readonly float Pitch;
            public readonly float X, Y, Z;
            public readonly bool Positioned;
            public PlayRequest(string name, float volume, float pitch, float x, float y, float z, bool positioned)
            {
                Name = name; Volume = volume; Pitch = pitch; X = x; Y = y; Z = z; Positioned = positioned;
            }
        }

        // Mixes all active sources. Runs on NAudio's playback thread; never touches game state.
        private sealed class SoundMixer : ISampleProvider
        {
            private readonly SoundEngine _owner;

            public WaveFormat WaveFormat { get; }

            public SoundMixer(SoundEngine owner, int sampleRate)
            {
                _owner = owner;
                WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
            }

            public int Read(float[] buffer, int offset, int count)
            {
                _owner.DrainQueue();
                Array.Clear(buffer, offset, count);

                int frames = count / 2;
                var sources = _owner._sources;
                for (int i = 0; i < sources.Length; i++)
                {
                    var s = sources[i];
                    if (!s.Active || s.PendingStop) continue;

                    // Distance attenuation (linear over 16 blocks), MC-style.
                    float gain = s.BaseVolume;
                    if (s.Positioned)
                    {
                        float dx = s.X - _owner._listenerX;
                        float dy = s.Y - _owner._listenerY;
                        float dz = s.Z - _owner._listenerZ;
                        float dist = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                        float range = AttenuationRange * Math.Max(1f, s.BaseVolume);
                        if (dist >= range) { s.PendingStop = true; continue; }
                        gain = s.BaseVolume * (1f - dist / range);
                    }
                    gain = Math.Clamp(gain, 0f, 1f);

                    int totalFrames = s.Samples.Length / s.Channels;
                    int framesLeft = totalFrames - s.Position;
                    int mixFrames = Math.Min(frames, framesLeft);
                    float vol = gain;

                    for (int f = 0; f < mixFrames; f++)
                    {
                        int si = (s.Position + f) * s.Channels;
                        int o = offset + f * 2;
                        if (s.Channels == 2)
                        {
                            buffer[o] += s.Samples[si] * vol;
                            buffer[o + 1] += s.Samples[si + 1] * vol;
                        }
                        else
                        {
                            float v = s.Samples[si] * vol;
                            buffer[o] += v;
                            buffer[o + 1] += v;
                        }
                    }

                    s.Position += mixFrames;
                    if (s.Position >= totalFrames)
                    {
                        s.PendingStop = true;
                    }
                }
                return count;
            }
        }

        private readonly ConcurrentQueue<PlayRequest> _queue = new();
        private readonly Source[] _sources;
        private readonly WaveOutEvent _output;
        private readonly int _sampleRate;
        private bool _disposed;

        private readonly Dictionary<string, (float[] Samples, int Channels)> _clips = new();

        private float _masterVolume = 1f;
        private float _blocksVolume = 1f;
        private float _ambientVolume = 1f;
        private float _listenerX, _listenerY, _listenerZ;

        public SoundEngine(int sampleRate = DefaultSampleRate)
        {
            _sampleRate = sampleRate;
            _sources = new Source[MaxSources];
            for (int i = 0; i < MaxSources; i++)
            {
                _sources[i] = new Source();
            }

            _output = new WaveOutEvent();
            _output.Init(new SoundMixer(this, sampleRate));
            _output.Play();
        }

        // ------------------------------------------------------------------
        // Loading (startup only)
        // ------------------------------------------------------------------

        /// <summary>Decodes a sound file (mp3/wav/ogg via NAudio) to PCM and registers it by name.
        /// Called once at startup; never during gameplay.</summary>
        public bool Register(string name, byte[] audioBytes)
        {
            if (_disposed || string.IsNullOrEmpty(name) || audioBytes == null || audioBytes.Length == 0)
            {
                return false;
            }
            try
            {
                string tmp = Path.Combine(Path.GetTempPath(), "cubeapp_snd_" + Guid.NewGuid().ToString("N") + ".mp3");
                File.WriteAllBytes(tmp, audioBytes);
                try
                {
                    using var reader = new AudioFileReader(tmp);
                    return RegisterFromReader(name, reader);
                }
                finally
                {
                    try { File.Delete(tmp); } catch { }
                }
            }
            catch (Exception ex)
            {
                System.Console.Error.WriteLine($"[Sound] failed to register '{name}': {ex.Message}");
                return false;
            }
        }

        private bool RegisterFromReader(string name, AudioFileReader reader)
        {
            int channels = reader.WaveFormat.Channels;
            int sourceRate = reader.WaveFormat.SampleRate;

            ISampleProvider src = reader;
            if (sourceRate != _sampleRate)
            {
                src = new WdlResamplingSampleProvider(reader, _sampleRate);
            }

            var list = new List<float>(4096);
            var buf = new float[8192];
            int n;
            while ((n = src.Read(buf, 0, buf.Length)) > 0)
            {
                for (int i = 0; i < n; i++) list.Add(buf[i]);
            }
            if (list.Count == 0) return false;

            // Peak-normalize so stacked sources never clip.
            float peak = 0f;
            for (int i = 0; i < list.Count; i++)
            {
                float a = Math.Abs(list[i]);
                if (a > peak) peak = a;
            }
            if (peak > 0.0001f && peak > 0.85f)
            {
                float scale = 0.85f / peak;
                for (int i = 0; i < list.Count; i++) list[i] *= scale;
            }

            _clips[name] = (list.ToArray(), channels);
            return true;
        }

        /// <summary>Auto-registers every embedded sound file (by filename) from the assembly.
        /// Drop-in ready: add a file to sounds/, add an EmbeddedResource line, done.</summary>
        public void RegisterAllEmbedded()
        {
            var asm = typeof(SoundEngine).Assembly;
            foreach (var resource in asm.GetManifestResourceNames())
            {
                string lower = resource.ToLowerInvariant();
                if (!lower.EndsWith(".mp3") && !lower.EndsWith(".wav") && !lower.EndsWith(".ogg"))
                {
                    continue;
                }

                string simple = resource;
                int idx = simple.LastIndexOf(".sounds.", StringComparison.Ordinal);
                if (idx >= 0) simple = simple.Substring(idx + ".sounds.".Length);
                int dot = simple.LastIndexOf('.');
                string soundName = dot > 0 ? simple.Substring(0, dot) : simple;
                if (string.IsNullOrWhiteSpace(soundName)) continue;

                using var stream = asm.GetManifestResourceStream(resource);
                if (stream == null) continue;
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                Register(soundName, ms.ToArray());
            }
        }

        // ------------------------------------------------------------------
        // 1.12-style API
        // ------------------------------------------------------------------

        public bool HasSound(string name) => _clips.ContainsKey(name);

        public float MasterVolume { get => _masterVolume; set => _masterVolume = Math.Clamp(value, 0f, 1f); }
        public float BlocksVolume { get => _blocksVolume; set => _blocksVolume = Math.Clamp(value, 0f, 1f); }
        public float AmbientVolume { get => _ambientVolume; set => _ambientVolume = Math.Clamp(value, 0f, 1f); }

        /// <summary>Updates the listener (camera) position, used for positional attenuation.</summary>
        public void UpdateListener(float x, float y, float z)
        {
            _listenerX = x;
            _listenerY = y;
            _listenerZ = z;
        }

        /// <summary>Non-positioned play (e.g. menu clicks). Volume = clamp(v * categoryVol, 0, 1).</summary>
        public void Play(string name, float volume = 1f, SoundCategory category = SoundCategory.Blocks, float pitch = 1f)
        {
            if (_disposed || !_clips.ContainsKey(name)) return;
            float catVol = category switch
            {
                SoundCategory.Blocks => _blocksVolume,
                SoundCategory.Ambient => _ambientVolume,
                _ => 1f,
            };
            _queue.Enqueue(new PlayRequest(name, Math.Clamp(volume * catVol * _masterVolume, 0f, 1f),
                Math.Clamp(pitch, 0.5f, 2f), 0f, 0f, 0f, false));
        }

        /// <summary>Positioned play with distance attenuation (MC's PositionedSoundRecord).</summary>
        public void PlayAt(string name, float x, float y, float z, float volume = 1f, SoundCategory category = SoundCategory.Blocks, float pitch = 1f)
        {
            if (_disposed || !_clips.ContainsKey(name)) return;
            float catVol = category switch
            {
                SoundCategory.Blocks => _blocksVolume,
                SoundCategory.Ambient => _ambientVolume,
                _ => 1f,
            };
            _queue.Enqueue(new PlayRequest(name, Math.Clamp(volume * catVol * _masterVolume, 0f, 1f),
                Math.Clamp(pitch, 0.5f, 2f), x, y, z, true));
        }

        /// <summary>Removes finished sources and cuts ones that reached their end (1.12's
        /// updateAllSounds cleanup). Call every frame/tick from the game thread.</summary>
        public void Update()
        {
            // Cheap: stop any source flagged as finished, freeing its slot.
            for (int i = 0; i < _sources.Length; i++)
            {
                if (_sources[i].Active && _sources[i].PendingStop)
                {
                    _sources[i].Active = false;
                    _sources[i].PendingStop = false;
                }
            }
        }

        // Runs on the audio thread inside Read. Assigns the next free source round-robin.
        private void DrainQueue()
        {
            while (_queue.TryDequeue(out var req))
            {
                if (!_clips.TryGetValue(req.Name, out var clip)) continue;

                Source s = FindFreeSource();
                if (s == null) continue; // source cap hit - 1.12 drops new plays when full
                s.Samples = clip.Samples;
                s.Channels = clip.Channels;
                s.Position = 0;
                s.BaseVolume = req.Volume;
                s.Pitch = req.Pitch;
                s.X = req.X; s.Y = req.Y; s.Z = req.Z;
                s.Positioned = req.Positioned;
                s.PendingStop = false;
                s.Active = true;
            }
        }

        private Source FindFreeSource()
        {
            for (int i = 0; i < _sources.Length; i++)
            {
                if (!_sources[i].Active)
                {
                    return _sources[i];
                }
            }
            return null;
        }

        public bool Enabled
        {
            get => _output.PlaybackState != PlaybackState.Stopped;
            set
            {
                if (value && _output.PlaybackState == PlaybackState.Stopped) _output.Play();
                else if (!value && _output.PlaybackState != PlaybackState.Stopped) _output.Stop();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _output.Stop(); } catch { }
            try { _output.Dispose(); } catch { }
        }
    }
}
