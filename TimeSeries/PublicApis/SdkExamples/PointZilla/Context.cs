﻿using System;
using System.Collections.Generic;
using Aquarius.TimeSeries.Client.Helpers;
using Aquarius.TimeSeries.Client.ServiceModels.Acquisition;
using Aquarius.TimeSeries.Client.ServiceModels.Provisioning;
using NodaTime;

namespace PointZilla
{
    public class Context
    {
        public string ExecutingFileVersion { get; set; }

        public string Server { get; set; }
        public string Username { get; set; } = "admin";
        public string Password { get; set; } = "admin";
        public string SessionToken { get; set; }

        public bool Wait { get; set; } = true;
        public TimeSpan? AppendTimeout { get; set; }

        public string TimeSeries { get; set; }
        public Interval? TimeRange { get; set; }
        public CommandType Command { get; set; } = CommandType.Auto;
        public int? GradeCode { get; set; }
        public List<string> Qualifiers { get; set; }

        public bool IgnoreGrades { get; set; }
        public bool GradeMappingEnabled { get; set; }
        public int? MappedDefaultGrade { get; set; }
        public Dictionary<int, int?> MappedGrades { get; } = new Dictionary<int, int?>();
        public bool IgnoreQualifiers { get; set; }
        public bool QualifierMappingEnabled { get; set; }
        public List<string> MappedDefaultQualifiers { get; set; }
        public Dictionary<string,string> MappedQualifiers { get; } = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);

        public CreateMode CreateMode { get; set; } = CreateMode.Never;
        public Duration GapTolerance { get; set; } = DurationExtensions.MaxGapDuration;
        public Offset? UtcOffset { get; set; }
        public string Unit { get; set; }
        public InterpolationType? InterpolationType { get; set; }
        public bool Publish { get; set; }
        public string Description { get; set; } = "Created by PointZilla";
        public string Comment { get; set; }
        public string Method { get; set; }
        public string ComputationIdentifier { get; set; }
        public string ComputationPeriodIdentifier { get; set; }
        public string SubLocationIdentifier { get; set; }
        public List<ExtendedAttributeValue> ExtendedAttributeValues { get; set; } = new List<ExtendedAttributeValue>();
        public TimeSeriesType? TimeSeriesType { get; set; }

        public TimeSeriesIdentifier SourceTimeSeries { get; set; }
        public Instant? SourceQueryFrom { get; set; }
        public Instant? SourceQueryTo { get; set; }
        public string SaveCsvPath { get; set; }
        public bool StopAfterSavingCsv { get; set; }

        public List<TimeSeriesPoint> ManualPoints { get; set; } = new List<TimeSeriesPoint>();

        public Instant StartTime { get; set; } = Instant.FromDateTimeUtc(DateTime.UtcNow);
        public TimeSpan PointInterval { get; set; } = TimeSpan.FromMinutes(1);
        public int NumberOfPoints { get; set; } // 0 means "derive the point count from number of periods"
        public int BatchSize { get; set; } = 500_000;
        public double NumberOfPeriods { get; set; } = 1;
        public WaveformType WaveformType { get; set; } = WaveformType.SineWave;
        public double WaveformOffset { get; set; } = 0;
        public double WaveformPhase { get; set; } = 0;
        public double WaveformScalar { get; set; } = 1;
        public double WaveformPeriod { get; set; } = 1440;
        public string WaveFormTextX { get; set; }
        public string WaveFormTextY { get; set; }

        public List<string> CsvFiles { get; set; } = new List<string>();

        public int CsvDateTimeField { get; set; }
        public string CsvDateTimeFormat { get; set; }
        public int CsvDateOnlyField { get; set; }
        public string CsvDateOnlyFormat { get; set; }
        public int CsvTimeOnlyField { get; set; }
        public string CsvTimeOnlyFormat { get; set; }
        public string CsvDefaultTimeOfDay { get; set; } = "00:00";
        public int CsvValueField { get; set; }
        public int CsvGradeField { get; set; }
        public int CsvQualifiersField { get; set; }
        public string CsvComment { get; set; }
        public int CsvSkipRows { get; set; }
        public bool CsvIgnoreInvalidRows { get; set; }
        public bool CsvRealign { get; set; }
        public bool CsvRemoveDuplicatePoints { get; set; } = true;
        public int? ExcelSheetNumber { get; set; }
        public string ExcelSheetName { get; set; }
    }
}
