using System;
using System.Collections.Generic;
using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Cubuild
{
    /// <summary>
    /// Non-positional music playback, kept separate from <see cref="SoundEngine"/>: songs are
    /// long, so they stream straight out of an embedded MP3 in memory instead of being decoded
    /// into one big PCM buffer at startup. Only ever one track at a time; scheduling (when a
    /// track should start) lives in Program.Music.cs.
    ///
    /// Every track is loudness-normalized before being baked in, so a quiet ambient piece and a
    /// busier one sit at the same perceived level; the fade in/out rides on a VolumeSampleProvider.
    /// </summary>
    public sealed class MusicPlayer : IDisposable
    {
        private sealed class Track
        {
            public string Name = "";
            public byte[] Data = Array.Empty<byte>();
        }

        private const float FadeInSeconds = 3.5f;
        private const float FadeOutSeconds = 6f;
        private const float TargetVolume = 0.55f;

        private readonly List<Track> _tracks = new();
        private readonly Random _rng = new();
        private readonly List<int> _shuffleBag = new();
        private int _lastIndex = -1;

        private WaveOutEvent? _output;
        private WaveStream? _reader;
        private VolumeSampleProvider? _volumeProvider;
        private float _level;
        private bool _fadingOut;
        private bool _disposed;
        private long _startTicks;

        public bool IsPlaying => _output != null;
        public string? CurrentTrack { get; private set; }
        public int TrackCount => _tracks.Count;

        /// <summary>Longest registered track; the scheduler uses it to pick a start time that
        /// still finishes before sunrise.</summary>
        public TimeSpan MaxTrackDuration { get; private set; }

        // ------------------------------------------------------------------
        // Loading (startup only)
        // ------------------------------------------------------------------

        /// <summary>Registers every embedded Assets/Music/*.mp3 by file name.</summary>
        public void RegisterEmbedded()
        {
            var asm = typeof(MusicPlayer).Assembly;
            var names = new List<string>(asm.GetManifestResourceNames());
            names.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var resource in names)
            {
                if (!resource.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)) continue;
                if (resource.IndexOf(".Music.", StringComparison.OrdinalIgnoreCase) < 0) continue;

                using var stream = asm.GetManifestResourceStream(resource);
                if (stream == null) continue;
                using var ms = new MemoryStream();
                stream.CopyTo(ms);

                // "Cubuild.Assets.Music.song_puddles.mp3" -> "song puddles"
                string fileName = resource.Substring(resource.LastIndexOf('.', resource.Length - 5) + 1);
                int ext = fileName.LastIndexOf('.');
                if (ext > 0) fileName = fileName.Substring(0, ext);
                fileName = fileName.Replace('_', ' ').Trim();
                if (fileName.Length == 0) continue;

                var track = new Track { Name = fileName, Data = ms.ToArray() };
                _tracks.Add(track);

                // Reading the frame table also gives us the length for scheduling.
                try
                {
                    using var probe = new Mp3FileReader(new MemoryStream(track.Data, writable: false));
                    if (probe.TotalTime > MaxTrackDuration) MaxTrackDuration = probe.TotalTime;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Music] cannot read '{fileName}': {ex.Message}");
                }
            }
        }

        // ------------------------------------------------------------------
        // Playback
        // ------------------------------------------------------------------

        /// <summary>Plays a random track, avoiding an immediate repeat. Returns false when the
        /// audio device refuses to start (the scheduler then gives up for that night).</summary>
        public bool PlayRandom()
        {
            if (_disposed || _tracks.Count == 0) return false;
            return Play(NextShuffledIndex());
        }

        public bool Play(int index)
        {
            if (_disposed || index < 0 || index >= _tracks.Count) return false;
            Stop();

            var track = _tracks[index];
            try
            {
                // MemoryStream over the shared byte[]: the MP3 frames are decoded as they are
                // consumed, so a 3-minute track costs megabytes instead of a 50 MB PCM block.
                var reader = new Mp3FileReader(new MemoryStream(track.Data, writable: false));
                var volume = new VolumeSampleProvider(reader.ToSampleProvider()) { Volume = 0f };
                var output = new WaveOutEvent { DesiredLatency = 200, NumberOfBuffers = 3 };
                output.Init(volume);
                output.Play();

                _reader = reader;
                _volumeProvider = volume;
                _output = output;
                _level = 0f;
                _fadingOut = false;
                _startTicks = Environment.TickCount64;
                CurrentTrack = track.Name;
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Music] cannot play '{track.Name}': {ex.Message}");
                Stop();
                return false;
            }
        }

        /// <summary>Call every frame: ramps the fade and detects the end of a track.</summary>
        public void Update(float deltaSeconds)
        {
            if (_disposed || _output == null || _volumeProvider == null) return;

            // WaveOut reports Stopped once the reader runs dry. Ignore the first moments, when
            // the device may not have spun up yet.
            if (_output.PlaybackState == PlaybackState.Stopped
                && Environment.TickCount64 - _startTicks > 500)
            {
                Stop();
                return;
            }

            float fade = _fadingOut ? FadeOutSeconds : FadeInSeconds;
            float step = TargetVolume / fade * Math.Max(0f, deltaSeconds);
            _level = _fadingOut
                ? Math.Max(0f, _level - step)
                : Math.Min(TargetVolume, _level + step);
            _volumeProvider.Volume = _level;
            if (_fadingOut && _level <= 0.001f) Stop();
        }

        /// <summary>Starts the soft sunrise fade-out; Update finishes the stop.</summary>
        public void FadeOut()
        {
            if (_output != null) _fadingOut = true;
        }

        public void Stop()
        {
            var output = _output;
            var reader = _reader;
            _output = null;
            _reader = null;
            _volumeProvider = null;
            _fadingOut = false;
            _level = 0f;
            CurrentTrack = null;

            if (output != null)
            {
                try { output.Stop(); } catch { }
                try { output.Dispose(); } catch { }
            }
            if (reader != null)
            {
                try { reader.Dispose(); } catch { }
            }
        }

        private int NextShuffledIndex()
        {
            if (_shuffleBag.Count == 0)
            {
                for (int i = 0; i < _tracks.Count; i++) _shuffleBag.Add(i);
                for (int i = _shuffleBag.Count - 1; i > 0; i--)
                {
                    int j = _rng.Next(i + 1);
                    (_shuffleBag[i], _shuffleBag[j]) = (_shuffleBag[j], _shuffleBag[i]);
                }
                // The bag must not open with the track that just finished.
                int last = _shuffleBag.Count - 1;
                if (_shuffleBag.Count > 1 && _shuffleBag[last] == _lastIndex)
                {
                    (_shuffleBag[last], _shuffleBag[0]) = (_shuffleBag[0], _shuffleBag[last]);
                }
            }

            int pick = _shuffleBag[_shuffleBag.Count - 1];
            _shuffleBag.RemoveAt(_shuffleBag.Count - 1);
            _lastIndex = pick;
            return pick;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }
    }
}
