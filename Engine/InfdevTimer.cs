using System;
using System.Diagnostics;

namespace CubeApp.Engine
{
    // Fixed-step timer modeled after the IF-20100630 Timer update pattern.
    public sealed class InfdevTimer
    {
        private readonly float ticksPerSecond;
        private double lastHighResTime;
        private long lastSyncSystemClock;
        private long lastSyncHighResClock;
        private double timeSyncAdjustment = 1.0;

        public int ElapsedTicks { get; private set; }
        public float RenderPartialTicks { get; private set; }
        public float TimerSpeed { get; set; } = 1.0f;
        public float ElapsedPartialTicks { get; private set; }

        public InfdevTimer(float ticksPerSecond)
        {
            this.ticksPerSecond = ticksPerSecond;
            lastSyncSystemClock = Environment.TickCount64;
            lastSyncHighResClock = GetHighResMillis();
        }

        public void Update()
        {
            long systemNow = Environment.TickCount64;
            long systemDelta = systemNow - lastSyncSystemClock;
            long highResNow = GetHighResMillis();

            if (systemDelta > 1000L)
            {
                long highResDelta = highResNow - lastSyncHighResClock;
                if (highResDelta > 0)
                {
                    double ratio = (double)systemDelta / highResDelta;
                    timeSyncAdjustment += (ratio - timeSyncAdjustment) * 0.2;
                }

                lastSyncSystemClock = systemNow;
                lastSyncHighResClock = highResNow;
            }

            if (systemDelta < 0L)
            {
                lastSyncSystemClock = systemNow;
                lastSyncHighResClock = highResNow;
            }

            double highResSeconds = highResNow / 1000.0;
            double deltaSeconds = (highResSeconds - lastHighResTime) * timeSyncAdjustment;
            lastHighResTime = highResSeconds;

            // Clamp large stalls to avoid the simulation spiraling.
            if (deltaSeconds < 0.0) deltaSeconds = 0.0;
            if (deltaSeconds > 1.0) deltaSeconds = 1.0;

            ElapsedPartialTicks += (float)(deltaSeconds * TimerSpeed * ticksPerSecond);
            ElapsedTicks = (int)ElapsedPartialTicks;
            ElapsedPartialTicks -= ElapsedTicks;
            if (ElapsedTicks > 10)
            {
                ElapsedTicks = 10;
            }

            RenderPartialTicks = ElapsedPartialTicks;
        }

        private static long GetHighResMillis()
        {
            return (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
        }
    }
}