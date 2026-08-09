using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace CubeApp
{
    /// <summary>
    /// Zero-lag game audio. All sounds are decoded to PCM ONCE at startup into memory; the game
    /// thread only enqueues tiny play requests; NAudio's WaveOutEvent runs playback and mixing on
    /// its own background thread. A fixed voice pool reuses channels (no per-play allocation, no
    /// GC churn, no file I/O on the render thread) - the architecture that avoids the MCI-style
    /// frame drops.
    ///
    /// Drop-in design: every .mp3/.wav/.ogg embedded under sounds/ auto-registers under its
    /// filename, so adding a sound = drop the file in + add one EmbeddedResource line.
    /// </summary>
    public sealed class SoundEngine : IDisposable
    {
        private const int DefaultSampleRate = 44100;
        private const int DefaultVoiceCount = 24;

        // One pooled playback voice. Mutated ONLY on the audio thread.
        private sealed class Voice
        {
            public float[] Samples = Array.Empty<float>();
            public int Channels = 1;
            public int Position;      // current frame
            public float Volume;
            public bool Active;
        }

        private readonly struct PlayRequest
        {
            public readonly string Name;
            public readonly float Volume;
            public PlayRequest(string name, float volume) { Name = name; Volume = volume; }
        }

        // Mixes all active voices into the output buffer. Runs on NAudio's playback thread.
        private sealed class VoiceMixer : ISampleProvider
        {
            private readonly SoundEngine _owner;

            public WaveFormat WaveFormat { get; }

            public VoiceMixer(SoundEngine owner, int sampleRate)
            {
                _owner = owner;
                WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
            }

            public int Read(float[] buffer, int offset, int count)
            {
                // All queue draining happens here, on the audio thread - the game thread never
                // touches voice state, so there's no lock contention.
                _owner.DrainQueue();

                Array.Clear(buffer, offset, count);
                int frames = count / 2;
                var voices = _owner._voices;
                for (int i = 0; i < voices.Length; i++)
                {
                    var v = voices[i];
                    if (!v.Active) continue;

                    int totalFrames = v.Samples.Length / v.Channels;
                    int framesLeft = totalFrames - v.Position;
                    int mixFrames = Math.Min(frames, framesLeft);
                    float vol = v.Volume;

                    for (int f = 0; f < mixFrames; f++)
                    {
                        int si = (v.Position + f) * v.Channels;
                        int o = offset + f * 2;
                        if (v.Channels == 2)
                        {
                            buffer[o] += v.Samples[si] * vol;
                            buffer[o + 1] += v.Samples[si + 1] * vol;
                        }
                        else
                        {
                            float s = v.Samples[si] * vol;
                            buffer[o] += s;
                            buffer[o + 1] += s;
                        }
                    }

                    v.Position += mixFrames;
                    if (v.Position >= totalFrames)
                    {
                        v.Active = false;
                    }
                }
                return count;
            }
        }

        private readonly ConcurrentQueue<PlayRequest> _queue = new();
        private readonly Voice[] _voices;
        private int _nextVoice;
        private readonly WaveOutEvent _output;
        private readonly int _sampleRate;
        private bool _disposed;

        // name -> decoded float samples (one per channel) + channel count.
        private readonly Dictionary<string, (float[] Samples, int Channels)> _clips = new();

        public SoundEngine(int voiceCount = DefaultVoiceCount, int sampleRate = DefaultSampleRate)
        {
            _sampleRate = sampleRate;
            _voices = new Voice[voiceCount];
            for (int i = 0; i < voiceCount; i++)
            {
                _voices[i] = new Voice();
            }

            _output = new WaveOutEvent();
            _output.Init(new VoiceMixer(this, sampleRate));
            _output.Play();
        }

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
                // AudioFileReader only accepts a path in NAudio 2.2.1, so stage the bytes to a
                // temp file. This is LOAD-time only (once at startup), never during gameplay.
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

            // Resample to the engine rate so all clips mix at one clock (pure managed resampler).
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

            // Peak-normalize to ~0.85 so stacked voices never clip (low-fi friendly).
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

                // Resource names look like "CubeApp.sounds.grass.mp3". Strip the ".sounds." part
                // and the extension to get the sound name ("grass", "cavesound1", ...).
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

        /// <summary>Enqueues a play request. Never blocks; never touches audio I/O on the calling
        /// thread. If the voice pool is full, the oldest voice is cut (Minecraft-style).</summary>
        public void Play(string name, float volume = 1f)
        {
            if (_disposed || !_clips.ContainsKey(name)) return;
            _queue.Enqueue(new PlayRequest(name, Math.Clamp(volume, 0f, 1f)));
        }

        public bool HasSound(string name) => _clips.ContainsKey(name);

        public bool Enabled
        {
            get => _output.PlaybackState != PlaybackState.Stopped;
            set
            {
                if (value && _output.PlaybackState == PlaybackState.Stopped) _output.Play();
                else if (!value && _output.PlaybackState != PlaybackState.Stopped) _output.Stop();
            }
        }

        private void DrainQueue()
        {
            while (_queue.TryDequeue(out var req))
            {
                if (!_clips.TryGetValue(req.Name, out var clip)) continue;

                var v = _voices[_nextVoice];
                _nextVoice = (_nextVoice + 1) % _voices.Length;
                v.Samples = clip.Samples;
                v.Channels = clip.Channels;
                v.Position = 0;
                v.Volume = req.Volume;
                v.Active = true;
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
