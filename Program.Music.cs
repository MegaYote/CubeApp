using System;

namespace Cubuild
{
    public sealed partial class Program
    {
        // ---- Night music ---------------------------------------------------
        // Minecraft's clock: dusk is ~13000, midnight 18000, sunrise 23000. One track is
        // scheduled per night at a random point after dusk, and fades out before sunrise.
        private const long MusicNightStartTick = 13200;
        private const long MusicNightEndTick = 23000;
        private const long MusicDawnFadeTicks = 140;   // ~7 seconds of fade at 20 tps
        private const double FadeReserveSeconds = 10.0; // fade-out + a little breathing room

        private MusicPlayer? _music;
        private readonly Random _musicRng = new();
        private long _musicNightIndex = long.MinValue;
        private long _musicScheduledTick;
        private bool _musicPlayedTonight;
        private long _musicLastWorldTime = -1;

        private void InitMusic()
        {
            try
            {
                _music = new MusicPlayer();
                _music.RegisterEmbedded();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Music] disabled: {ex.Message}");
                _music = null;
            }
        }

        /// <summary>
        /// Runs every frame (not just sim ticks) so the fade keeps moving while paused. Time
        /// skips are handled by the same tick comparison: if the schedule is passed but the night
        /// is still going, the track starts immediately.
        /// </summary>
        private void UpdateMusic(float deltaSeconds)
        {
            if (_music == null || _music.TrackCount == 0) return;
            _music.Update(deltaSeconds);

            if (World == null)
            {
                _music.Stop();
                _musicNightIndex = long.MinValue;
                _musicLastWorldTime = -1;
                return;
            }

            long worldTime = World.WorldTime;
            // The world clock stalls in the pause menu: don't schedule or fade then.
            if (worldTime == _musicLastWorldTime) return;
            _musicLastWorldTime = worldTime;

            if (worldTime < MusicNightStartTick)
            {
                _musicNightIndex = -1;
                return;
            }

            long nightIndex = (worldTime - MusicNightStartTick) / 24000;
            long nightStart = MusicNightStartTick + nightIndex * 24000;
            long nightEnd = MusicNightEndTick + nightIndex * 24000;

            if (nightIndex != _musicNightIndex)
            {
                _musicNightIndex = nightIndex;
                _musicPlayedTonight = false;
                // Random start after dusk, late enough that even the longest track still reaches
                // its own ending before the sunrise fade (night is 9800 ticks = 8m10s).
                long span = nightEnd - nightStart;
                double nightSeconds = span / 20.0;
                double reserve = Math.Min(nightSeconds * 0.5,
                    _music.MaxTrackDuration.TotalSeconds + FadeReserveSeconds);
                long latest = nightStart + (long)((nightSeconds - reserve) * 20.0);
                long earliest = nightStart + 200;                 // ~10 s after dusk
                if (latest <= earliest) latest = earliest + 1;
                _musicScheduledTick = earliest + (long)(_musicRng.NextDouble() * (latest - earliest));
            }

            if (!_musicPlayedTonight && worldTime >= _musicScheduledTick && worldTime < nightEnd)
            {
                // Mark it played even if the device refused: retrying every tick would spam.
                _musicPlayedTonight = true;
                _music.PlayRandom();
            }

            if (_music.IsPlaying && worldTime >= nightEnd - MusicDawnFadeTicks)
            {
                _music.FadeOut();
            }
        }
    }
}
