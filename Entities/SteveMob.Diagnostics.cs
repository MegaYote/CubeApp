using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Cubuild
{
    public sealed partial class SteveMob
    {
        private const int MaxShelterDiagnosticCount = 999;

        private long _lastShelterPrepareTicks;
        private long _lastShelterFinishTicks;
        private int _prepareSiteSearches;
        private int _prepareSiteValidations;
        private int _prepareResourceSearches;
        private int _prepareStationSearches;
        private int _prepareStationCacheHits;
        private int _prepareDropScans;
        private int _prepareDropDemandBuilds;
        private int _prepareToolPlanChecks;
        private int _prepareToolTaskContinuations;
        private int _finishTaskActions;

        private void ResetShelterPrepareDiagnostics()
        {
            _prepareSiteSearches = 0;
            _prepareSiteValidations = 0;
            _prepareResourceSearches = 0;
            _prepareStationSearches = 0;
            _prepareStationCacheHits = 0;
            _prepareDropScans = 0;
            _prepareDropDemandBuilds = 0;
            _prepareToolPlanChecks = 0;
            _prepareToolTaskContinuations = 0;
        }

        private void ResetShelterFinishDiagnostics() => _finishTaskActions = 0;

        private static void CountShelterDiagnostic(ref int counter)
        {
            if (counter < MaxShelterDiagnosticCount) counter++;
        }

        private void RecordShelterSiteSearch() => CountShelterDiagnostic(ref _prepareSiteSearches);
        private void RecordShelterSiteValidation() => CountShelterDiagnostic(ref _prepareSiteValidations);
        private void RecordShelterResourceSearch() => CountShelterDiagnostic(ref _prepareResourceSearches);
        private void RecordShelterStationSearch() => CountShelterDiagnostic(ref _prepareStationSearches);
        private void RecordShelterStationCacheHit() => CountShelterDiagnostic(ref _prepareStationCacheHits);
        private void RecordShelterDropScan() => CountShelterDiagnostic(ref _prepareDropScans);
        private void RecordShelterDropDemandBuild() => CountShelterDiagnostic(ref _prepareDropDemandBuilds);
        private void RecordShelterToolPlanCheck() => CountShelterDiagnostic(ref _prepareToolPlanChecks);
        private void RecordShelterToolTaskContinuation() => CountShelterDiagnostic(ref _prepareToolTaskContinuations);
        private void RecordShelterTaskAction() => CountShelterDiagnostic(ref _finishTaskActions);

        private static float ShelterTicksToMilliseconds(long ticks)
            => (float)(ticks * (1000.0 / Stopwatch.Frequency));

        /// <summary>
        /// Adds transient, design-neutral F3 diagnostics. The caller invokes this only while the
        /// debug overlay is enabled; simulation timing and counters themselves allocate nothing.
        /// </summary>
        internal void AppendShelterAIDiagnostics(List<string> lines, int index)
        {
            var position = Position;
            lines.Add($"Steve AI[{index}] @ {position.X:0.0},{position.Y:0.0},{position.Z:0.0}: "
                + $"Prepare {ShelterTicksToMilliseconds(_lastShelterPrepareTicks):0.00} ms | "
                + $"Finish {ShelterTicksToMilliseconds(_lastShelterFinishTicks):0.00} ms");
            lines.Add($"  scans site={_prepareSiteSearches}/{_prepareSiteValidations} "
                + $"resource={_prepareResourceSearches} "
                + $"station={_prepareStationSearches}+{_prepareStationCacheHits}hit "
                + $"drop={_prepareDropScans}/demand={_prepareDropDemandBuilds} "
                + $"tool={_prepareToolPlanChecks}/{_prepareToolTaskContinuations}active "
                + $"actions={_finishTaskActions}");
        }
    }
}
