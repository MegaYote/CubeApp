using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Silk.NET.OpenAL;

namespace Cubuild
{
    /// <summary>
    /// OpenAL sound playback: every sound gets its own hardware source that plays once and
    /// transitions to Stopped on its own: no custom mixer, no shared mutable voice, no stuck-buffer
    /// loop.
    ///
    /// Structure:
    ///  - playSound() creates an isolated source per play (unique channel), applies volume/pitch
    ///    and position, plays it.
    ///  - updateAllSounds() every tick polls each source; when OpenAL reports Stopped, the source
    ///    is deleted and its slot freed.
    ///  - Volume = clamp(sound.volume * categoryVolume, 0, 1); pitch = clamp(0.5, 2.0).
    ///  - Positioned sounds use OpenAL's native linear rolloff (16-block reference distance).
    ///  - Sound categories (master/blocks/ambient) with independent volumes.
    ///
    /// Decoding happens once at startup (NAudio -> PCM), then the PCM is uploaded into an OpenAL
    /// buffer. The game thread only enqueues tiny play requests.
    /// </summary>
    public sealed class SoundEngine : IDisposable
    {
        private const int DefaultSampleRate = 44100;
        private const int MaxSources = 32;
        private const float AttenuationRange = 16f; // linear attenuation reference distance

        public enum SoundCategory { Master, Blocks, Ambient }

        private sealed class Channel
        {
            public uint Source;      // OpenAL source handle
            public uint Buffer;      // OpenAL buffer handle
            public bool InUse;
            public bool Positioned;
            public float BaseVolume;
            public float X, Y, Z;
        }

        private readonly struct PlayRequest
        {
            public readonly string Name;
            public readonly float Volume;
            public readonly float Pitch;
            public readonly float X, Y, Z;
            public readonly bool Positioned;
            public readonly SoundCategory Category;
            public PlayRequest(string name, float volume, float pitch, float x, float y, float z, bool positioned, SoundCategory category)
            {
                Name = name; Volume = volume; Pitch = pitch; X = x; Y = y; Z = z; Positioned = positioned; Category = category;
            }
        }

        private readonly AL _al;
        private readonly ALContext _alc;
        private unsafe Device* _device;
        private unsafe Context* _context;
        private readonly ConcurrentQueue<PlayRequest> _queue = new();
        private readonly Channel[] _channels;
        private readonly Dictionary<string, (uint Buffer, int Channels, int SampleRate)> _clips = new();
        private bool _disposed;

        private float _masterVolume = 1f;
        private float _blocksVolume = 1f;
        private float _ambientVolume = 1f;

        public SoundEngine(int sampleRate = DefaultSampleRate)
        {
            _al = AL.GetApi();
            _alc = ALContext.GetApi();
            unsafe
            {
                _device = _alc.OpenDevice(null);
                if (_device == null)
                {
                    _device = _alc.OpenDevice("OpenAL Soft");
                }
                if (_device == null)
                {
                    throw new InvalidOperationException("OpenAL: no audio device available");
                }
                _context = _alc.CreateContext(_device, null);
                _alc.MakeContextCurrent(_context);

                // Listener at the origin, facing -Z (MC convention).
                _al.SetListenerProperty(ListenerVector3.Position, 0f, 0f, 0f);
            }

            _channels = new Channel[MaxSources];
            unsafe
            {
                for (int i = 0; i < MaxSources; i++)
                {
                    _channels[i] = new Channel();
                    uint src = 0;
                    _al.GenSources(1, &src);
                    _channels[i].Source = src;
                    uint buf = 0;
                    _al.GenBuffers(1, &buf);
                    _channels[i].Buffer = buf;
                    // Never loop: each source plays its clip exactly once, then goes Stopped.
                    _al.SetSourceProperty(src, SourceBoolean.Looping, false);
                    // Native linear rolloff over the MC attenuation range.
                    _al.SetSourceProperty(src, SourceFloat.ReferenceDistance, AttenuationRange);
                    _al.SetSourceProperty(src, SourceFloat.RolloffFactor, 1f);
                    _al.SetSourceProperty(src, SourceFloat.MaxDistance, 64f);
                }
            }
        }

        // ------------------------------------------------------------------
        // Loading (startup only)
        // ------------------------------------------------------------------

        /// <summary>Decodes a sound file (mp3/wav via NAudio) to PCM, uploads it into an OpenAL
        /// buffer, and registers it by name. Called once at startup.</summary>
        public bool Register(string name, byte[] audioBytes)
        {
            if (_disposed || string.IsNullOrEmpty(name) || audioBytes == null || audioBytes.Length == 0)
            {
                return false;
            }
            try
            {
                string tmp = Path.Combine(Path.GetTempPath(), "Cubuild_snd_" + Guid.NewGuid().ToString("N") + ".mp3");
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
            if (sourceRate != DefaultSampleRate)
            {
                src = new WdlResamplingSampleProvider(reader, DefaultSampleRate);
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

            // Convert to 16-bit PCM for OpenAL and upload into a fresh buffer.
            uint buffer = 0;
            unsafe
            {
                _al.GenBuffers(1, &buffer);
                var pcm = new short[list.Count];
                for (int i = 0; i < list.Count; i++)
                {
                    float v = list[i] * 32767f;
                    if (v > 32767f) v = 32767f;
                    else if (v < -32768f) v = -32768f;
                    pcm[i] = (short)v;
                }
                BufferFormat fmt = channels == 2 ? BufferFormat.Stereo16 : BufferFormat.Mono16;
                fixed (short* p = pcm)
                {
                    _al.BufferData(buffer, fmt, p, pcm.Length * 2, DefaultSampleRate);
                }
            }

            _clips[name] = (buffer, channels, DefaultSampleRate);
            return true;
        }

        /// <summary>Auto-registers every embedded sound file (by filename) from the assembly.
        /// Drop-in ready: add a file to Assets/Sounds/, add an EmbeddedResource line, done.</summary>
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

                // Embedded resource names use dots for the folder path (e.g. "Cubuild.Assets.Sounds.sounds.grass.mp3").
                // The sound name is the FILE NAME without extension, regardless of which folder it lives in.
                string[] parts = resource.Split('.');
                if (parts.Length < 2) continue;
                string soundName = parts[parts.Length - 2]; // last segment before the extension
                if (string.IsNullOrWhiteSpace(soundName)) continue;

                using var stream = asm.GetManifestResourceStream(resource);
                if (stream == null) continue;
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                Register(soundName, ms.ToArray());
            }
        }

        // ------------------------------------------------------------------
        // Playback API
        // ------------------------------------------------------------------

        public bool HasSound(string name) => _clips.ContainsKey(name);

        public float MasterVolume { get => _masterVolume; set => _masterVolume = Math.Clamp(value, 0f, 1f); }
        public float BlocksVolume { get => _blocksVolume; set => _blocksVolume = Math.Clamp(value, 0f, 1f); }
        public float AmbientVolume { get => _ambientVolume; set => _ambientVolume = Math.Clamp(value, 0f, 1f); }

        /// <summary>Updates the listener (camera) position/orientation (MC's setListener).</summary>
        public void UpdateListener(float x, float y, float z)
        {
            if (_disposed) return;
            _al.SetListenerProperty(ListenerVector3.Position, x, y, z);
        }

        /// <summary>Non-positioned play (e.g. menu clicks).</summary>
        public void Play(string name, float volume = 1f, SoundCategory category = SoundCategory.Blocks, float pitch = 1f)
        {
            if (_disposed || !_clips.ContainsKey(name)) return;
            _queue.Enqueue(new PlayRequest(name, volume, pitch, 0f, 0f, 0f, false, category));
        }

        /// <summary>Positioned play with native OpenAL attenuation (MC's PositionedSoundRecord).</summary>
        public void PlayAt(string name, float x, float y, float z, float volume = 1f, SoundCategory category = SoundCategory.Blocks, float pitch = 1f)
        {
            if (_disposed || !_clips.ContainsKey(name)) return;
            _queue.Enqueue(new PlayRequest(name, volume, pitch, x, y, z, true, category));
        }

        /// <summary>Drain pending plays, poll every source, delete the finished ones and free
        /// their slots. Call every tick from the game thread.</summary>
        public void Update()
        {
            if (_disposed) return;
            DrainQueue();
            for (int i = 0; i < _channels.Length; i++)
            {
                var ch = _channels[i];
                if (!ch.InUse) continue;

                unsafe
                {
                    int state = (int)SourceState.Playing;
                    _al.GetSourceProperty(ch.Source, GetSourceInteger.SourceState, &state);
                    if (state != (int)SourceState.Playing && state != (int)SourceState.Paused)
                    {
                        // Remove the source and free the slot.
                        _al.SourceStop(ch.Source);
                        ch.InUse = false;
                    }
                }
            }
        }

        // Drains queued play requests into OpenAL sources. Runs on the game thread inside Update()
        // so all OpenAL calls happen on one thread.
        private void DrainQueue()
        {
            while (_queue.TryDequeue(out var req))
            {
                if (!_clips.TryGetValue(req.Name, out var clip)) continue;

                Channel ch = FindFreeChannel();
                if (ch == null) continue; // source cap hit - drop new plays when full

                float catVol = req.Category switch
                {
                    SoundCategory.Blocks => _blocksVolume,
                    SoundCategory.Ambient => _ambientVolume,
                    _ => 1f,
                };
                float vol = Math.Clamp(req.Volume * catVol * _masterVolume, 0f, 1f);
                if (vol <= 0f) continue;

                unsafe
                {
                    _al.SourceStop(ch.Source);
                    _al.SetSourceProperty(ch.Source, SourceInteger.Buffer, (int)clip.Buffer);
                    _al.SetSourceProperty(ch.Source, SourceFloat.Gain, vol);
                    _al.SetSourceProperty(ch.Source, SourceFloat.Pitch, Math.Clamp(req.Pitch, 0.5f, 2f));
                    _al.SetSourceProperty(ch.Source, SourceBoolean.SourceRelative, req.Positioned ? false : true);
                    _al.SetSourceProperty(ch.Source, SourceVector3.Position, req.X, req.Y, req.Z);
                    _al.SourcePlay(ch.Source);
                }

                ch.InUse = true;
                ch.Positioned = req.Positioned;
                ch.BaseVolume = vol;
                ch.X = req.X; ch.Y = req.Y; ch.Z = req.Z;
            }
        }

        private Channel FindFreeChannel()
        {
            for (int i = 0; i < _channels.Length; i++)
            {
                if (!_channels[i].InUse)
                {
                    return _channels[i];
                }
            }
            return null;
        }

        public bool Enabled
        {
            get => !_disposed;
            set { /* OpenAL has no master on/off here; volume 0 is the MC way to mute */ }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            unsafe
            {
                for (int i = 0; i < _channels.Length; i++)
                {
                    uint src = _channels[i].Source;
                    uint buf = _channels[i].Buffer;
                    _al.SourceStop(src);
                    _al.DeleteSources(1, &src);
                    _al.DeleteBuffers(1, &buf);
                }
                foreach (var kv in _clips)
                {
                    uint b = kv.Value.Buffer;
                    _al.DeleteBuffers(1, &b);
                }
                _alc.DestroyContext(_context);
                _alc.CloseDevice(_device);
            }
        }
    }
}
