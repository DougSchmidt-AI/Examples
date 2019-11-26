﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using Aquarius.TimeSeries.Client;
using Aquarius.TimeSeries.Client.ServiceModels.Publish;
using Humanizer;
using log4net;
using ServiceStack;
using SosExporter.Dtos;
using TimeSeriesDescription = Aquarius.TimeSeries.Client.ServiceModels.Publish.TimeSeriesDescription;
using TimeSeriesChangeEvent = Aquarius.TimeSeries.Client.ServiceModels.Publish.TimeSeriesUniqueIds;

namespace SosExporter
{
    public class Exporter
    {
        private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public Context Context { get; set; }

        private IAquariusClient Aquarius { get; set; }
        private ISosClient Sos { get; set; }
        private SyncStatus SyncStatus { get; set; }
        private TimeSeriesPointFilter TimeSeriesPointFilter { get; set; }
        private long ExportedPointCount { get; set; }
        private int ExportedTimeSeriesCount { get; set; }
        private TimeSpan MaximumExportDuration { get; set; }

        public void Run()
        {
            Log.Info($"{GetProgramVersion()} connecting to {Context.Config.AquariusServer} ...");

            using (Aquarius = CreateConnectedAquariusClient())
            {
                Log.Info($"Connected to {Context.Config.AquariusServer} (v{Aquarius.ServerVersion}) as {Context.Config.AquariusUsername}");

                if (Aquarius.ServerVersion.IsLessThan(MinimumVersion))
                    throw new ExpectedException($"This utility requires AQTS v{MinimumVersion} or greater.");

                var stopwatch = Stopwatch.StartNew();

                RunOnce();

                Log.Info($"Successfully exported {ExportedPointCount} points from {ExportedTimeSeriesCount} time-series in {stopwatch.Elapsed.Humanize(2)}");
            }
        }

        private static readonly AquariusServerVersion MinimumVersion = AquariusServerVersion.Create("17.2");

        private static string GetProgramVersion()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var fileVersionInfo = FileVersionInfo.GetVersionInfo(assembly.Location);

            // ReSharper disable once PossibleNullReferenceException
            return $"{MethodBase.GetCurrentMethod().DeclaringType.Namespace} v{fileVersionInfo.FileVersion}";
        }

        private IAquariusClient CreateConnectedAquariusClient()
        {
            var client = AquariusClient.CreateConnectedClient(
                "doug-vm2012r2", //Context.Config.AquariusServer,
                "admin", //Context.Config.AquariusUsername,
                "admin"); //Context.Config.AquariusPassword);

            foreach (var serviceClient in new[]{client.Publish, client.Provisioning, client.Acquisition})
            {
                if (!(serviceClient is JsonServiceClient jsonClient))
                    continue;

                jsonClient.Timeout = Context.Timeout;
                jsonClient.ReadWriteTimeout = Context.Timeout;
            }

            return client;
        }

        private void LogDryRun(string message)
        {
            Log.Warn($"Dry-run: {message}");
        }

        private void RunOnce()
        {
            SyncStatus = new SyncStatus(Aquarius) {Context = Context};
            TimeSeriesPointFilter = new TimeSeriesPointFilter {Context = Context};

            ValidateFilters();

            MaximumExportDuration = Context.MaximumExportDuration
                                    ?? SyncStatus.GetMaximumChangeEventDuration()
                                        .Subtract(TimeSpan.FromHours(1));

            var request = CreateFilterRequest();

            if (Context.ForceResync)
            {
                Log.Warn("Forcing a full time-series resync.");
                request.ChangesSinceToken = null;
            }
            else if (Context.ChangesSince.HasValue)
            {
                Log.Warn($"Overriding current ChangesSinceToken='{request.ChangesSinceToken:O}' with '{Context.ChangesSince:O}'");
                request.ChangesSinceToken = Context.ChangesSince.Value.UtcDateTime;
            }

            Log.Info($"Checking {GetFilterSummary(request)} ...");

            var stopwatch = Stopwatch.StartNew();

            var response = Aquarius.Publish.Get(request);

            if (response.TokenExpired ?? false)
            {
                if (Context.NeverResync)
                {
                    Log.Warn("Skipping a recommended resync.");
                }
                else
                {
                    Log.Warn($"The ChangesSinceToken of {request.ChangesSinceToken:O} has expired. Forcing a full resync. You may need to run the exporter more frequently.");
                    request.ChangesSinceToken = null;

                    response = Aquarius.Publish.Get(request);
                }
            }

            var bootstrapToken = response.ResponseTime
                .Subtract(stopwatch.Elapsed)
                .Subtract(TimeSpan.FromMinutes(1))
                .UtcDateTime;

            var nextChangesSinceToken = response.NextToken ?? bootstrapToken;

            var timeSeriesDescriptions = FetchChangedTimeSeriesDescriptions(response);

            Log.Info($"Connecting to {Context.Config.SosServer} as {Context.Config.SosUsername} ...");

            var clearExportedData = !request.ChangesSinceToken.HasValue;
            request.ChangesSinceToken = nextChangesSinceToken;

            ExportToSos(request, response, timeSeriesDescriptions, clearExportedData);

            SyncStatus.SaveConfiguration(request.ChangesSinceToken.Value);
        }

        private void ValidateFilters()
        {
            ValidateApprovalFilters();
            ValidateGradeFilters();
            ValidateQualifierFilters();
        }

        private void ValidateApprovalFilters()
        {
            if (!Context.Config.Approvals.Any()) return;

            Log.Info("Fetching approval configuration ...");
            var approvals = Aquarius.Publish.Get(new ApprovalListServiceRequest()).Approvals;

            foreach (var approvalFilter in Context.Config.Approvals)
            {
                var approvalMetadata = approvals.SingleOrDefault(a =>
                    a.DisplayName.Equals(approvalFilter.Text, StringComparison.InvariantCultureIgnoreCase)
                    || a.Identifier.Equals(approvalFilter.Text, StringComparison.InvariantCultureIgnoreCase));

                if (approvalMetadata == null)
                    throw new ExpectedException($"Unknown approval '{approvalFilter.Text}'");

                approvalFilter.Text = approvalMetadata.DisplayName;
                approvalFilter.ApprovalLevel = int.Parse(approvalMetadata.Identifier);
            }
        }

        private void ValidateGradeFilters()
        {
            if (!Context.Config.Grades.Any()) return;

            Log.Info("Fetching grade configuration ...");
            var grades = Aquarius.Publish.Get(new GradeListServiceRequest()).Grades;

            foreach (var gradeFilter in Context.Config.Grades)
            {
                var gradeMetadata = grades.SingleOrDefault(g =>
                    g.DisplayName.Equals(gradeFilter.Text, StringComparison.InvariantCultureIgnoreCase)
                    || g.Identifier.Equals(gradeFilter.Text, StringComparison.InvariantCultureIgnoreCase));

                if (gradeMetadata == null)
                    throw new ExpectedException($"Unknown grade '{gradeFilter.Text}'");

                gradeFilter.Text = gradeMetadata.DisplayName;
                gradeFilter.GradeCode = int.Parse(gradeMetadata.Identifier);
            }
        }

        private void ValidateQualifierFilters()
        {
            if (!Context.Config.Qualifiers.Any()) return;

            Log.Info("Fetching qualifier configuration ...");
            var qualifiers = Aquarius.Publish.Get(new QualifierListServiceRequest()).Qualifiers;

            foreach (var qualifierFilter in Context.Config.Qualifiers)
            {
                var qualifierMetadata = qualifiers.SingleOrDefault(q =>
                    q.Identifier.Equals(qualifierFilter.Text, StringComparison.InvariantCultureIgnoreCase)
                    || q.Code.Equals(qualifierFilter.Text, StringComparison.InvariantCultureIgnoreCase));

                if (qualifierMetadata == null)
                    throw new ExpectedException($"Unknown qualifier '{qualifierFilter.Text}'");

                qualifierFilter.Text = qualifierMetadata.Identifier;
            }
        }

        private TimeSeriesUniqueIdListServiceRequest CreateFilterRequest()
        {
            var locationIdentifier = Context.Config.LocationIdentifier;

            if (!string.IsNullOrEmpty(locationIdentifier))
            {
                var locationDescription = Aquarius.Publish
                    .Get(new LocationDescriptionListServiceRequest { LocationIdentifier = locationIdentifier })
                    .LocationDescriptions
                    .SingleOrDefault();

                if (locationDescription == null)
                    throw new ExpectedException($"Location '{locationIdentifier}' does not exist.");

                locationIdentifier = locationDescription.Identifier;
            }

            return new TimeSeriesUniqueIdListServiceRequest
            {
                ChangesSinceToken = SyncStatus.GetLastChangesSinceToken(),
                LocationIdentifier = locationIdentifier,
                ChangeEventType = Context.Config.ChangeEventType?.ToString(),
                Publish = Context.Config.Publish,
                Parameter = Context.Config.Parameter,
                ComputationIdentifier = Context.Config.ComputationIdentifier,
                ComputationPeriodIdentifier = Context.Config.ComputationPeriodIdentifier,
                ExtendedFilters = Context.Config.ExtendedFilters.Any() ? Context.Config.ExtendedFilters : null,
            };
        }

        private string GetFilterSummary(TimeSeriesUniqueIdListServiceRequest request)
        {
            var sb = new StringBuilder();

            sb.Append(string.IsNullOrEmpty(request.LocationIdentifier)
                ? "all locations"
                : $"location '{request.LocationIdentifier}'");

            var filters = new List<string>();

            if (request.Publish.HasValue)
            {
                filters.Add($"Publish={request.Publish}");
            }

            if (!string.IsNullOrEmpty(request.Parameter))
            {
                filters.Add($"Parameter={request.Parameter}");
            }

            if (!string.IsNullOrEmpty(request.ComputationIdentifier))
            {
                filters.Add($"ComputationIdentifier={request.ComputationIdentifier}");
            }

            if (!string.IsNullOrEmpty(request.ComputationPeriodIdentifier))
            {
                filters.Add($"ComputationPeriodIdentifier={request.ComputationPeriodIdentifier}");
            }

            if (!string.IsNullOrEmpty(request.ChangeEventType))
            {
                filters.Add($"ChangeEventType={request.ChangeEventType}");
            }

            if (request.ExtendedFilters != null && request.ExtendedFilters.Any())
            {
                filters.Add($"ExtendedFilters={string.Join(", ", request.ExtendedFilters.Select(f => $"{f.FilterName}={f.FilterValue}"))}");
            }

            if (filters.Any())
            {
                sb.Append($" with {string.Join(" and ", filters)}");
            }

            sb.Append(" for time-series");

            if (request.ChangesSinceToken.HasValue)
            {
                sb.Append($" change since {request.ChangesSinceToken:O}");
            }

            return sb.ToString();
        }

        private List<TimeSeriesDescription> FetchChangedTimeSeriesDescriptions(TimeSeriesUniqueIdListServiceResponse response)
        {
            var timeSeriesUniqueIdsToFetch = response.TimeSeriesUniqueIds
                .Select(ts => ts.UniqueId)
                .ToList();

            Log.Info($"Fetching descriptions of {timeSeriesUniqueIdsToFetch.Count} changed time-series ...");

            var timeSeriesDescriptions = new List<TimeSeriesDescription>();

            using (var batchClient = CreatePublishClientWithPostMethodOverride())
            {
                while (timeSeriesUniqueIdsToFetch.Any())
                {
                    const int batchSize = 400;

                    var batchList = timeSeriesUniqueIdsToFetch.Take(batchSize).ToList();
                    timeSeriesUniqueIdsToFetch = timeSeriesUniqueIdsToFetch.Skip(batchSize).ToList();

                    var request = new TimeSeriesDescriptionListByUniqueIdServiceRequest();

                    // We need to resolve the URL without any unique IDs on the GET command line
                    var requestUrl = RemoveQueryFromUrl(request.ToGetUrl());

                    request.TimeSeriesUniqueIds = batchList;

                    var batchResponse =
                        batchClient.Send<TimeSeriesDescriptionListByUniqueIdServiceResponse>(HttpMethods.Post,
                            requestUrl, request);

                    timeSeriesDescriptions.AddRange(batchResponse.TimeSeriesDescriptions);
                }
            }

            return timeSeriesDescriptions
                .OrderBy(ts => ts.LocationIdentifier)
                .ThenBy(ts => ts.Identifier)
                .ToList();
        }

        private JsonServiceClient CreatePublishClientWithPostMethodOverride()
        {
            return Aquarius.CloneAuthenticatedClientWithOverrideMethod(Aquarius.Publish, HttpMethods.Get) as JsonServiceClient;
        }

        private static string RemoveQueryFromUrl(string url)
        {
            var queryIndex = url.IndexOf("?", StringComparison.InvariantCulture);

            if (queryIndex < 0)
                return url;

            return url.Substring(0, queryIndex);
        }

        private List<TimeSeriesDescription> FilterTimeSeriesDescriptions(List<TimeSeriesDescription> timeSeriesDescriptions)
        {
            return FilterTimeSeriesDescriptionsByDescription(
                FilterTimeSeriesDescriptionsByIdentifier(timeSeriesDescriptions));
        }

        private List<TimeSeriesDescription> FilterTimeSeriesDescriptionsByIdentifier(List<TimeSeriesDescription> timeSeriesDescriptions)
        {
            return FilterTimeSeriesDescriptionsByText(
                timeSeriesDescriptions,
                Context.Config.TimeSeries,
                ts => ts.Identifier);
        }

        private List<TimeSeriesDescription> FilterTimeSeriesDescriptionsByDescription(List<TimeSeriesDescription> timeSeriesDescriptions)
        {
            return FilterTimeSeriesDescriptionsByText(
                timeSeriesDescriptions,
                Context.Config.TimeSeriesDescriptions,
                ts => ts.Description);
        }

        private static List<TimeSeriesDescription> FilterTimeSeriesDescriptionsByText(
            List<TimeSeriesDescription> timeSeriesDescriptions,
            List<TimeSeriesFilter> filters,
            Func<TimeSeriesDescription, string> textSelector)
        {
            if (!filters.Any())
                return timeSeriesDescriptions;

            var timeSeriesFilter = new Filter<TimeSeriesFilter>(filters);

            var results = new List<TimeSeriesDescription>();

            foreach (var timeSeriesDescription in timeSeriesDescriptions)
            {
                if (timeSeriesFilter.IsFiltered(f => f.Regex.IsMatch(textSelector(timeSeriesDescription))))
                    continue;

                results.Add(timeSeriesDescription);
            }

            return results;
        }

        private void ExportToSos(
            TimeSeriesUniqueIdListServiceRequest request,
            TimeSeriesUniqueIdListServiceResponse response,
            List<TimeSeriesDescription> timeSeriesDescriptions,
            bool clearExportedData)
        {
            var filteredTimeSeriesDescriptions = FilterTimeSeriesDescriptions(timeSeriesDescriptions);
            var changeEvents = response.TimeSeriesUniqueIds;

            Log.Info($"Exporting {filteredTimeSeriesDescriptions.Count} time-series ...");

            if (clearExportedData)
            {
                ClearExportedData();
            }

            var stopwatch = Stopwatch.StartNew();

            for (var i = 0; i < filteredTimeSeriesDescriptions.Count; ++i)
            {
                var timeSeriesDescription = filteredTimeSeriesDescriptions[i];

                using (Sos = SosClient.CreateConnectedClient(Context))
                {
                    // Create a separate SOS client connection to ensure that the transactions are committed after each export
                    ExportTimeSeries(
                        clearExportedData,
                        changeEvents.Single(t => t.UniqueId == timeSeriesDescription.UniqueId),
                        timeSeriesDescription);
                }

                if (stopwatch.Elapsed <= MaximumExportDuration)
                    continue;

                Log.Info($"Maximum export duration has elapsed. Checking {GetFilterSummary(request)} ...");

                stopwatch.Restart();

                FetchNewChanges(request, filteredTimeSeriesDescriptions, changeEvents);
            }
        }

        private void FetchNewChanges(TimeSeriesUniqueIdListServiceRequest request, List<TimeSeriesDescription> timeSeriesToExport, List<TimeSeriesChangeEvent> timeSeriesChangeEvents)
        {
            var response = Aquarius.Publish.Get(request);

            if (response.TokenExpired ?? !response.NextToken.HasValue)
                throw new ExpectedException($"Logic-error: A secondary changes-since response should always have an updated token.");

            request.ChangesSinceToken = response.NextToken;

            var newTimeSeriesDescriptions = FilterTimeSeriesDescriptions(FetchChangedTimeSeriesDescriptions(response));

            if (!newTimeSeriesDescriptions.Any())
                return;

            Log.Info($"Merging {newTimeSeriesDescriptions.Count} changed time-series into the export queue ...");

            timeSeriesToExport.AddRange(newTimeSeriesDescriptions);

            foreach (var newTimeSeriesDescription in newTimeSeriesDescriptions)
            {
                var newEvent = response.TimeSeriesUniqueIds.Single(e => e.UniqueId == newTimeSeriesDescription.UniqueId);

                var existingEvent = timeSeriesChangeEvents.SingleOrDefault(e => e.UniqueId == newEvent.UniqueId);

                if (existingEvent == null)
                {
                    timeSeriesChangeEvents.Add(newEvent);
                    continue;
                }

                MergeTimeSeriesChangeEvent(existingEvent, newEvent);
            }
        }

        private static void MergeTimeSeriesChangeEvent(TimeSeriesChangeEvent existingEvent, TimeSeriesChangeEvent newEvent)
        {
            if (existingEvent.HasAttributeChange.HasValue && newEvent.HasAttributeChange.HasValue)
            {
                existingEvent.HasAttributeChange = existingEvent.HasAttributeChange.Value || newEvent.HasAttributeChange.Value;
            }
            else if (newEvent.HasAttributeChange.HasValue)
            {
                existingEvent.HasAttributeChange = newEvent.HasAttributeChange;
            }

            if (existingEvent.FirstPointChanged.HasValue && newEvent.FirstPointChanged.HasValue)
            {
                if (newEvent.FirstPointChanged < existingEvent.FirstPointChanged)
                {
                    existingEvent.FirstPointChanged = newEvent.FirstPointChanged;
                }
            }
            else if (newEvent.FirstPointChanged.HasValue)
            {
                existingEvent.FirstPointChanged = newEvent.FirstPointChanged;
            }
        }

        private void ClearExportedData()
        {
            if (Context.DryRun)
            {
                LogDryRun("Would have cleared the SOS database of all existing data.");
                return;
            }

            using (var sosClient = SosClient.CreateConnectedClient(Context))
            {
                // Create a separate SOS client connection to ensure that the transactions are committed when the client disconnects
                sosClient.ClearDatasource();
                sosClient.DeleteDeletedObservations();
            }
        }

        private void ExportTimeSeries(
            bool clearExportedData,
            TimeSeriesChangeEvent detectedChange,
            TimeSeriesDescription timeSeriesDescription)
        {
            Log.Info($"Fetching changes from '{timeSeriesDescription.Identifier}' FirstPointChanged={detectedChange.FirstPointChanged:O} HasAttributeChanged={detectedChange.HasAttributeChange} ...");

            var locationInfo = GetLocationInfo(timeSeriesDescription.LocationIdentifier);

            var period = GetTimeSeriesPeriod(timeSeriesDescription);

            var dataRequest = new TimeSeriesDataCorrectedServiceRequest
            {
                TimeSeriesUniqueId = timeSeriesDescription.UniqueId,
                QueryFrom = GetInitialQueryFrom(detectedChange),
                ApplyRounding = Context.ApplyRounding,
            };

            var existingSensor = Sos.FindExistingSensor(timeSeriesDescription);
            var deleteExistingSensor = clearExportedData && existingSensor != null;
            var assignedOffering = existingSensor?.Identifier;

            var timeSeries = FetchMinimumTimeSeries(detectedChange, timeSeriesDescription, existingSensor, dataRequest, ref deleteExistingSensor, ref period);

            var createSensor = existingSensor == null || deleteExistingSensor;

            TimeSeriesPointFilter.FilterTimeSeriesPoints(timeSeries);

            var exportSummary = $"{timeSeries.NumPoints} points [{timeSeries.Points.FirstOrDefault()?.Timestamp.DateTimeOffset:O} to {timeSeries.Points.LastOrDefault()?.Timestamp.DateTimeOffset:O}] from '{timeSeriesDescription.Identifier}' with Frequency={period}";

            ExportedTimeSeriesCount += 1;
            ExportedPointCount += timeSeries.NumPoints ?? 0;

            if (Context.DryRun)
            {
                if (deleteExistingSensor)
                    LogDryRun($"Would delete existing sensor '{existingSensor?.Identifier}'");

                if (createSensor)
                    LogDryRun($"Would create new sensor for '{timeSeriesDescription.Identifier}'");

                LogDryRun($"Would export {exportSummary}.");
                return;
            }

            Log.Info($"Exporting {exportSummary} ...");

            if (deleteExistingSensor)
            {
                Sos.DeleteSensor(timeSeries);
                Sos.DeleteDeletedObservations();
            }

            if (createSensor)
            {
                var sensor = Sos.InsertSensor(timeSeries);

                assignedOffering = sensor.AssignedOffering;
            }

            Sos.InsertObservation(assignedOffering, locationInfo.LocationData, locationInfo.LocationDescription, timeSeries, timeSeriesDescription);
        }

        private static DateTimeOffset? GetInitialQueryFrom(TimeSeriesChangeEvent detectedChange)
        {
            // When a derived time-series is reported as changed, the first point changed is always the beginning of time.
            // Rather than always pull the whole signal (which can be expensive with rounded values), only to trim most points before exporting,
            // just treat a re-derived series event like an initial sync, so we'll "walk backwards" from the current time.
            //
            // If and when partial-re-derivation is implemented, this condition will no longer be triggered.
            return detectedChange.FirstPointChanged == DateTimeOffset.MinValue
                ? null
                : detectedChange.FirstPointChanged;
        }

        private TimeSeriesDataServiceResponse FetchMinimumTimeSeries(
            TimeSeriesChangeEvent detectedChange,
            TimeSeriesDescription timeSeriesDescription,
            SensorInfo existingSensor,
            TimeSeriesDataCorrectedServiceRequest dataRequest,
            ref bool deleteExistingSensor,
            ref ComputationPeriod period)
        {
            TimeSeriesDataServiceResponse timeSeries;

            if (!deleteExistingSensor && GetLastSensorTime(existingSensor) < dataRequest.QueryFrom)
            {
                // All the changed points have occurred after the last sensor point which exists in the SOS server.
                // This is the preferred code path, since we only need to export the new points.
                timeSeries = Aquarius.Publish.Get(dataRequest);

                if (period == ComputationPeriod.Unknown)
                {
                    // We may have just fetched enough recent points to determine the time-series frequency
                    period = ComputationPeriodEstimator.InferPeriodFromRecentPoints(timeSeries);
                }

                TrimEarlyPoints(timeSeriesDescription, timeSeries, period);

                return timeSeries;
            }

            if (GetLastSensorTime(existingSensor) >= detectedChange.FirstPointChanged)
            {
                // A point has changed before the last known observation, so we'll need to throw out the entire sensor
                deleteExistingSensor = true;

                // We'll also need to fetch more data again
                dataRequest.QueryFrom = null;
            }

            timeSeries = FetchRecentSignal(timeSeriesDescription, dataRequest, ref period);

            if (GetLastSensorTime(existingSensor) >= detectedChange.FirstPointChanged)
            {
                // A point has changed before the last known observation, so we'll need to throw out the entire sensor
                deleteExistingSensor = true;

                // We'll also need to fetch more data again
                dataRequest.QueryFrom = null;
                timeSeries = FetchRecentSignal(timeSeriesDescription, dataRequest, ref period);
            }

            TrimEarlyPoints(timeSeriesDescription, timeSeries, period);

            return timeSeries;
        }

        private static DateTimeOffset? GetLastSensorTime(SensorInfo sensor)
        {
            return sensor?.PhenomenonTime.LastOrDefault();
        }

        private void TrimEarlyPoints(
            TimeSeriesDescription timeSeriesDescription,
            TimeSeriesDataServiceResponse timeSeries,
            ComputationPeriod period)
        {
            var maximumDaysToExport = Context.Config.MaximumPointDays[period];

            if (maximumDaysToExport <= 0 || !timeSeries.Points.Any())
                return;

            var earliestDayToUpload = SubtractTimeSpan(
                timeSeries.Points.Last().Timestamp.DateTimeOffset,
                TimeSpan.FromDays(maximumDaysToExport));

            var remainingPoints = timeSeries.Points
                .Where(p => p.Timestamp.DateTimeOffset >= earliestDayToUpload)
                .ToList();

            if (!RoughDailyPointCount.TryGetValue(period, out var expectedDailyPointCount))
            {
                expectedDailyPointCount = 1.0;
            }

            var roughPointLimit = Convert.ToInt32(maximumDaysToExport * expectedDailyPointCount * 1.5);

            if (remainingPoints.Count > roughPointLimit)
            {
                var limitExceededCount = remainingPoints.Count - roughPointLimit;

                Log.Warn($"Upper limit of {roughPointLimit} points exceeded by {limitExceededCount} points for Frequency={period} and MaximumPointDays={maximumDaysToExport} in '{timeSeriesDescription.Identifier}'.");

                remainingPoints = remainingPoints
                    .Skip(limitExceededCount)
                    .ToList();
            }

            var trimmedPointCount = timeSeries.NumPoints - remainingPoints.Count;

            Log.Info(
                $"Trimming '{timeSeriesDescription.Identifier}' {trimmedPointCount} points before {earliestDayToUpload:O} with {remainingPoints.Count} points remaining with Frequency={period}");

            timeSeries.Points = remainingPoints;
            timeSeries.NumPoints = timeSeries.Points.Count;
        }

        private static readonly Dictionary<ComputationPeriod, double> RoughDailyPointCount = new Dictionary<ComputationPeriod, double>
        {
            {ComputationPeriod.Monthly, 1.0 / 30 },
            {ComputationPeriod.Weekly, 1.0 / 7 },
            {ComputationPeriod.Daily, 1.0 },
            {ComputationPeriod.Hourly, 24 },
            {ComputationPeriod.QuarterHourly, 24 * 4 },
            {ComputationPeriod.Minutes, 24 * 60 },
        };

        private (LocationDescription LocationDescription, LocationDataServiceResponse LocationData) GetLocationInfo(string locationIdentifier)
        {
            if (LocationInfoCache.TryGetValue(locationIdentifier, out var locationInfo))
                return locationInfo;

            var locationDescription = Aquarius.Publish.Get(new LocationDescriptionListServiceRequest
            {
                LocationIdentifier = locationIdentifier
            }).LocationDescriptions.Single();

            var locationData = Aquarius.Publish.Get(new LocationDataServiceRequest
            {
                LocationIdentifier = locationIdentifier
            });

            locationInfo = (locationDescription, locationData);

            LocationInfoCache.Add(locationIdentifier, locationInfo);

            return locationInfo;
        }

        private
            Dictionary<string, (LocationDescription LocationDescription, LocationDataServiceResponse LocationData)>
            LocationInfoCache { get; } =
                new Dictionary<string, (LocationDescription LocationDescription, LocationDataServiceResponse LocationData)>();

        private static DateTimeOffset SubtractTimeSpan(DateTimeOffset dateTimeOffset, TimeSpan timeSpan)
        {
            return dateTimeOffset.Subtract(DateTimeOffset.MinValue) <= timeSpan
                ? DateTimeOffset.MinValue
                : dateTimeOffset.Subtract(timeSpan);
        }

        private ComputationPeriod GetTimeSeriesPeriod(TimeSeriesDescription timeSeriesDescription)
        {
            if (Enum.TryParse<ComputationPeriod>(timeSeriesDescription.ComputationPeriodIdentifier, true, out var period))
            {
                if (period == ComputationPeriod.WaterYear)
                    period = ComputationPeriod.Annual; // WaterYear and Annual are the same frequency

                if (Context.Config.MaximumPointDays.ContainsKey(period))
                    return period;
            }

            // Otherwise fall back to the "I don't know" setting
            return ComputationPeriod.Unknown;
        }

        private TimeSeriesDataServiceResponse FetchRecentSignal(
            TimeSeriesDescription timeSeriesDescription,
            TimeSeriesDataCorrectedServiceRequest dataRequest,
            ref ComputationPeriod period)
        {
            var maximumDaysToExport = Context.Config.MaximumPointDays[period];

            // If we find ourselves here, we will need to do a least one fetch of data to see if we have enough to satisfy the export request.
            var utcNow = DateTime.UtcNow.Date;
            var startOfToday = new DateTimeOffset(utcNow.Year, utcNow.Month, utcNow.Day, 0, 0, 0,
                timeSeriesDescription.UtcOffsetIsoDuration.ToTimeSpan());

            var queryStartPoint = dataRequest.QueryFrom ?? startOfToday.Subtract(TimeSpan.FromDays(90));
            dataRequest.QueryFrom = queryStartPoint;

            TimeSeriesDataServiceResponse timeSeries;

            if (period == ComputationPeriod.Unknown)
            {
                // We don't know the time-series period, so we'll need to load the most recent data until we can make a stronger inference about the frequency
                timeSeries = FetchRecentSignal(
                    timeSeriesDescription,
                    dataRequest,
                    ts => ts.NumPoints >= ComputationPeriodEstimator.MinimumPointCount,
                    "to determine signal frequency");

                period = ComputationPeriodEstimator.InferPeriodFromRecentPoints(timeSeries);
                maximumDaysToExport = Context.Config.MaximumPointDays[period];

                if (dataRequest.QueryFrom == null)
                {
                    // We've already asked for all the data
                    return timeSeries;
                }

                if (GetRetrievedDuration(timeSeries, dataRequest) >= GetMaximumRetrievalDuration(maximumDaysToExport))
                {
                    // We have enough known points to satisfy the export request
                    return timeSeries;
                }
            }

            // We've seen enough of the most recent points to know the time-series period,
            // but we still don't know how much data to fetch.
            // Stop when we've retrieved at least this much data
            var maximumRetrievalDuration = GetMaximumRetrievalDuration(maximumDaysToExport);

            timeSeries = FetchRecentSignal(
                timeSeriesDescription,
                dataRequest,
                ts => GetRetrievedDuration(ts, dataRequest) >= maximumRetrievalDuration,
                $"with Frequency={period}");

            return timeSeries;
        }

        private static TimeSpan GetMaximumRetrievalDuration(int maximumDaysToExport)
        {
            return maximumDaysToExport > 0
                ? TimeSpan.FromDays(maximumDaysToExport)
                : TimeSpan.MaxValue;
        }

        private static TimeSpan GetRetrievedDuration(
            TimeSeriesDataServiceResponse timeSeries,
            TimeSeriesDataCorrectedServiceRequest dataRequest)
        {
            return dataRequest.QueryFrom.HasValue
                ? timeSeries.NumPoints <= 0
                    ? TimeSpan.MinValue
                    : timeSeries.Points.Last().Timestamp.DateTimeOffset
                        .Subtract(dataRequest.QueryFrom.Value)
                : TimeSpan.MaxValue;
        }

        private TimeSeriesDataServiceResponse FetchRecentSignal(
            TimeSeriesDescription timeSeriesDescription,
            TimeSeriesDataCorrectedServiceRequest dataRequest,
            Func<TimeSeriesDataServiceResponse,bool> isDataFetchComplete,
            string progressMessage)
        {
            TimeSeriesDataServiceResponse timeSeries = null;

            if (timeSeriesDescription.RawEndTime < dataRequest.QueryFrom)
            {
                dataRequest.QueryFrom = timeSeriesDescription.RawEndTime;
            }

            foreach (var timeSpan in PeriodsToFetch)
            {
                if (timeSpan == TimeSpan.MaxValue)
                {
                    dataRequest.QueryFrom = null;
                }

                Log.Info($"Fetching more than changed points from '{timeSeriesDescription.Identifier}' with QueryFrom={dataRequest.QueryFrom:O} {progressMessage} ...");

                timeSeries = Aquarius.Publish.Get(dataRequest);

                if (timeSpan == TimeSpan.MaxValue || timeSeriesDescription.RawStartTime > dataRequest.QueryFrom || isDataFetchComplete(timeSeries) )
                    break;

                dataRequest.QueryFrom -= timeSpan;
            }

            if (timeSeries == null)
                throw new Exception($"Logic error: Can't fetch time-series data of '{timeSeriesDescription.Identifier}' {progressMessage}");

            return timeSeries;
        }

        private static readonly TimeSpan[] PeriodsToFetch =
            Enumerable.Repeat(TimeSpan.FromDays(90), 3)
                .Concat(Enumerable.Repeat(TimeSpan.FromDays(365), 4))
                .Concat(Enumerable.Repeat(TimeSpan.FromDays(5 * 365), 4))
                .Concat(new[] {TimeSpan.MaxValue})
                .ToArray();
    }
}
