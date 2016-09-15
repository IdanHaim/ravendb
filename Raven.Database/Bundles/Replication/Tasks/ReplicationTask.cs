//-----------------------------------------------------------------------
// <copyright file="ReplicationTask.cs" company="Hibernating Rhinos LTD">
//     Copyright (c) Hibernating Rhinos LTD. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Raven.Abstractions;
using Raven.Abstractions.Connection;
using Raven.Abstractions.Data;
using Raven.Abstractions.Exceptions;
using Raven.Abstractions.Extensions;
using Raven.Abstractions.Logging;
using Raven.Abstractions.Replication;
using Raven.Abstractions.Util;
using Raven.Bundles.Replication.Data;
using Raven.Database;
using Raven.Database.Data;
using Raven.Database.Impl;
using Raven.Database.Plugins;
using Raven.Database.Prefetching;
using Raven.Database.Server;
using Raven.Database.Storage;
using Raven.Json.Linq;
using Raven.Database.Extensions;

namespace Raven.Bundles.Replication.Tasks
{
	using Database.Indexing;

	[ExportMetadata("Bundle", "Replication")]
	[InheritedExport(typeof(IStartupTask))]
	public class ReplicationTask : IStartupTask, IDisposable
	{
		public class IntHolder
		{
			public int Value;
		}

		public const int SystemDocsLimitForRemoteEtagUpdate = 15;
		public const int DestinationDocsLimitForRemoteEtagUpdate = 15;

		public readonly ConcurrentQueue<Task> activeTasks = new ConcurrentQueue<Task>();

		private readonly ConcurrentDictionary<string, DestinationStats> destinationStats =
			new ConcurrentDictionary<string, DestinationStats>(StringComparer.OrdinalIgnoreCase);

		private DocumentDatabase docDb;
		private readonly static ILog log = LogManager.GetCurrentClassLogger();
		private bool firstTimeFoundNoReplicationDocument = true;
        private bool wrongReplicationSourceAlertSent = false;
		private readonly ConcurrentDictionary<string, IntHolder> activeReplicationTasks = new ConcurrentDictionary<string, IntHolder>();

		public ConcurrentDictionary<string, DestinationStats> DestinationStats
		{
			get { return destinationStats; }
		}

		public ConcurrentDictionary<string, DateTime> Heartbeats
		{
			get { return heartbeatDictionary; }
		}

		private int replicationAttempts;
		private int workCounter;
		private HttpRavenRequestFactory httpRavenRequestFactory;

		private IndependentBatchSizeAutoTuner autoTuner;
		private readonly ConcurrentDictionary<string, PrefetchingBehavior> prefetchingBehaviors = new ConcurrentDictionary<string, PrefetchingBehavior>();

		public void Execute(DocumentDatabase database)
		{
			docDb = database;
			var replicationRequestTimeoutInMs =
				docDb.Configuration.GetConfigurationValue<int>("Raven/Replication/ReplicationRequestTimeout") ??
				60 * 1000;
			
			autoTuner = new IndependentBatchSizeAutoTuner(docDb.WorkContext);
			httpRavenRequestFactory = new HttpRavenRequestFactory { RequestTimeoutInMs = replicationRequestTimeoutInMs };

            var task = new Task(Execute, TaskCreationOptions.LongRunning);
			var disposableAction = new DisposableAction(task.Wait);
			// make sure that the doc db waits for the replication task shutdown
			docDb.ExtensionsState.GetOrAdd(Guid.NewGuid().ToString(), s => disposableAction);
			task.Start();
		}

		private void Execute()
		{
			using (LogContext.WithDatabase(docDb.Name))
			{
				var name = GetType().Name;

				var timeToWaitInMinutes = TimeSpan.FromMinutes(5);
				bool runningBecauseOfDataModifications = false;
				var context = docDb.WorkContext;
				NotifySiblings();
				while (context.DoWork)
				{
					try
					{
						using (docDb.DisableAllTriggersForCurrentThread())
						{
							var destinations = GetReplicationDestinations();

							if (destinations.Length == 0)
							{
								WarnIfNoReplicationTargetsWereFound();
							}
							else
							{
								var currentReplicationAttempts = Interlocked.Increment(ref replicationAttempts);

								var copyOfrunningBecauseOfDataModifications = runningBecauseOfDataModifications;
								var destinationForReplication = destinations
									.Where(dest =>
									{
										if (copyOfrunningBecauseOfDataModifications == false)
											return true;
										return IsNotFailing(dest, currentReplicationAttempts);
									}).ToList();

								CleanupPrefetchingBehaviors(destinations.Select(x => x.ConnectionStringOptions.Url),
									destinations.Except(destinationForReplication).Select(x => x.ConnectionStringOptions.Url));

								var startedTasks = new List<Task>();

								foreach (var dest in destinationForReplication)
								{
									var destination = dest;
									var holder = activeReplicationTasks.GetOrAdd(destination.ConnectionStringOptions.Url, new IntHolder());
									if (Thread.VolatileRead(ref holder.Value) == 1)
										continue;
									Thread.VolatileWrite(ref holder.Value, 1);
									var replicationTask = Task.Factory.StartNew(() =>
									{
										using (LogContext.WithDatabase(docDb.Name))
										{
											try
											{
												if (ReplicateTo(destination))
													docDb.WorkContext.NotifyAboutWork();
											}
											catch (Exception e)
											{
												log.ErrorException("Could not replicate to " + destination, e);
											}
										}
									});

									startedTasks.Add(replicationTask);

									activeTasks.Enqueue(replicationTask);
									replicationTask.ContinueWith(_ =>
									{
										// here we purge all the completed tasks at the head of the queue
										Task task;
										while (activeTasks.TryPeek(out task))
										{
											if (!task.IsCompleted && !task.IsCanceled && !task.IsFaulted)
												break;
											activeTasks.TryDequeue(out task); // remove it from end
										}
									});
								}

#if NET45
								Task
#else
								TaskEx
#endif								
									.WhenAll(startedTasks.ToArray()).ContinueWith(t =>
									{
										if (destinationStats.Count == 0) 
											return;

										foreach (var stats in destinationStats.Where(stats => stats.Value.LastReplicatedEtag != null))
										{
											PrefetchingBehavior prefetchingBehavior;

											if (prefetchingBehaviors.TryGetValue(stats.Key, out prefetchingBehavior))
											{
												prefetchingBehavior.CleanupDocuments(stats.Value.LastReplicatedEtag);
											}
										}
									}).AssertNotFailed();
							}
						}
					}
					catch (Exception e)
					{
						log.ErrorException("Failed to perform replication", e);
					}

					runningBecauseOfDataModifications = context.WaitForWork(timeToWaitInMinutes, ref workCounter, name);
					timeToWaitInMinutes = runningBecauseOfDataModifications
											? TimeSpan.FromSeconds(30)
											: TimeSpan.FromMinutes(5);
				}
			}
		}

		private void CleanupPrefetchingBehaviors(IEnumerable<string> allDestinations, IEnumerable<string> failingDestinations)
		{
			PrefetchingBehavior prefetchingBehaviorToDispose;

			// remove prefetching behaviors for non-existing destinations
			foreach (var removedDestination in prefetchingBehaviors.Keys.Except(allDestinations))
			{
				if (prefetchingBehaviors.TryRemove(removedDestination, out prefetchingBehaviorToDispose))
				{
					prefetchingBehaviorToDispose.Dispose();
				}
			}

			// also remove prefetchers if the destination is failing for a long time
			foreach (var failingDestination in failingDestinations)
			{
				DestinationStats stats;
				if (prefetchingBehaviors.ContainsKey(failingDestination) == false || destinationStats.TryGetValue(failingDestination, out stats) == false)
					continue;

				if (stats.FirstFailureInCycleTimestamp != null && stats.LastFailureTimestamp != null && 
					stats.LastFailureTimestamp - stats.FirstFailureInCycleTimestamp >= TimeSpan.FromMinutes(3))
				{
					if (prefetchingBehaviors.TryRemove(failingDestination, out prefetchingBehaviorToDispose))
					{
						prefetchingBehaviorToDispose.Dispose();
					}
				}
			}
		}

		private void NotifySiblings()
		{
			var notifications = new BlockingCollection<RavenConnectionStringOptions>();

			Task.Factory.StartNew(() => NotifySibling(notifications));

			int skip = 0;
			var replicationDestinations = GetReplicationDestinations();
			foreach (var replicationDestination in replicationDestinations)
			{
				notifications.TryAdd(replicationDestination.ConnectionStringOptions, 15 * 1000);
			}

			while (true)
			{
				var docs = docDb.GetDocumentsWithIdStartingWith(Constants.RavenReplicationSourcesBasePath, null, null, skip, 128, CancellationToken.None);
				if (docs.Length == 0)
				{
					notifications.TryAdd(null, 15 * 1000); // marker to stop notify this
					return;
				}

				skip += docs.Length;

				foreach (RavenJObject doc in docs)
				{
					var sourceReplicationInformation = doc.JsonDeserialization<SourceReplicationInformation>();
					if (string.IsNullOrEmpty(sourceReplicationInformation.Source))
						continue;

					var match = replicationDestinations.FirstOrDefault(x =>
														   string.Equals(x.ConnectionStringOptions.Url,
																		 sourceReplicationInformation.Source,
																		 StringComparison.OrdinalIgnoreCase));

					if (match != null)
					{
						notifications.TryAdd(match.ConnectionStringOptions, 15 * 1000);
					}
					else
					{
						notifications.TryAdd(new RavenConnectionStringOptions
						{
							Url = sourceReplicationInformation.Source
						}, 15 * 1000);
					}
				}
			}
		}

		private void NotifySibling(BlockingCollection<RavenConnectionStringOptions> collection)
		{
			using (LogContext.WithDatabase(docDb.Name))
				while (true)
				{
					RavenConnectionStringOptions connectionStringOptions;
					try
					{
						collection.TryTake(out connectionStringOptions, 15 * 1000, docDb.WorkContext.CancellationToken);
						if (connectionStringOptions == null)
							return;
					}
					catch (Exception e)
					{
						log.ErrorException("Could not get connection string options to notify sibling servers about restart", e);
						return;
					}
					try
					{
						var url = connectionStringOptions.Url + "/replication/heartbeat?from=" + UrlEncodedServerUrl() + "&dbid=" + docDb.TransactionalStorage.Id;
						var request = httpRavenRequestFactory.Create(url, "POST", connectionStringOptions);
						request.WebRequest.ContentLength = 0;
						request.ExecuteRequest();
					}
					catch (Exception e)
					{
						log.WarnException("Could not notify " + connectionStringOptions.Url + " about sibling server being up & running", e);
					}
				}
		}

		private bool IsNotFailing(ReplicationStrategy dest, int currentReplicationAttempts)
		{
			var jsonDocument = docDb.Get(Constants.RavenReplicationDestinationsBasePath + EscapeDestinationName(dest.ConnectionStringOptions.Url), null);
			if (jsonDocument == null)
				return true;
			var failureInformation = jsonDocument.DataAsJson.JsonDeserialization<DestinationFailureInformation>();
			if (failureInformation.FailureCount > 1000)
			{
				var shouldReplicateTo = currentReplicationAttempts % 10 == 0;
				log.Debug("Failure count for {0} is {1}, skipping replication: {2}",
					dest, failureInformation.FailureCount, shouldReplicateTo == false);
				return shouldReplicateTo;
			}
			if (failureInformation.FailureCount > 100)
			{
				var shouldReplicateTo = currentReplicationAttempts % 5 == 0;
				log.Debug("Failure count for {0} is {1}, skipping replication: {2}",
					dest, failureInformation.FailureCount, shouldReplicateTo == false);
				return shouldReplicateTo;
			}
			if (failureInformation.FailureCount > 10)
			{
				var shouldReplicateTo = currentReplicationAttempts % 2 == 0;
				log.Debug("Failure count for {0} is {1}, skipping replication: {2}",
					dest, failureInformation.FailureCount, shouldReplicateTo == false);
				return shouldReplicateTo;
			}
			return true;
		}

		public static string EscapeDestinationName(string url)
		{
			return Uri.EscapeDataString(url.Replace("http://", "").Replace("/", "").Replace(":", ""));
		}

		private void WarnIfNoReplicationTargetsWereFound()
		{
			if (firstTimeFoundNoReplicationDocument)
			{
				firstTimeFoundNoReplicationDocument = false;
				log.Warn("Replication bundle is installed, but there is no destination in 'Raven/Replication/Destinations'.\r\nReplication results in NO-OP");
			}
		}

		private bool ReplicateTo(ReplicationStrategy destination)
		{
			try
			{
				if (docDb.Disposed)
					return false;

				using (docDb.DisableAllTriggersForCurrentThread())
				using (var stats = new ReplicationStatisticsRecorder(destination, destinationStats))
				{
					SourceReplicationInformation destinationsReplicationInformationForSource;
					using (var scope = stats.StartRecording("Destination"))
					{
						try
						{
							destinationsReplicationInformationForSource = GetLastReplicatedEtagFrom(destination);
							if (destinationsReplicationInformationForSource == null)
								return false;

							scope.Record(RavenJObject.FromObject(destinationsReplicationInformationForSource));
						}
						catch (Exception e)
						{
							scope.RecordError(e);
							log.WarnException("Failed to replicate to: " + destination, e);
							return false;
						}
					}

					bool? replicated = null;

					using (var scope = stats.StartRecording("Documents"))
					{
						switch (ReplicateDocuments(destination, destinationsReplicationInformationForSource, scope))
						{
							case true:
								replicated = true;
								break;
							case false:
								return false;
						}
					}

					using (var scope = stats.StartRecording("Attachments"))
					{
						switch (ReplicateAttachments(destination, destinationsReplicationInformationForSource, scope))
						{
							case true:
								replicated = true;
								break;
							case false:
								return false;
						}
					}

					return replicated ?? false;
				}
			}
			finally
			{
				var holder = activeReplicationTasks.GetOrAdd(destination.ConnectionStringOptions.Url, new IntHolder());
				Thread.VolatileWrite(ref holder.Value, 0);
			}
		}

		private bool? ReplicateAttachments(ReplicationStrategy destination, SourceReplicationInformation destinationsReplicationInformationForSource, ReplicationStatisticsRecorder.ReplicationStatisticsRecorderScope recorder)
		{
			Tuple<RavenJArray, Etag> tuple;
			RavenJArray attachments;

			using (var scope = recorder.StartRecording("Get"))
			{
				tuple = GetAttachments(destinationsReplicationInformationForSource, destination, scope);
				attachments = tuple.Item1;

				if (attachments == null || attachments.Length == 0)
				{
					if (tuple.Item2 != destinationsReplicationInformationForSource.LastAttachmentEtag)
					{
						SetLastReplicatedEtagForServer(destination, lastAttachmentEtag: tuple.Item2);
					}
					return null;
				}
			}

			using (var scope = recorder.StartRecording("Send"))
			{
				string lastError;
				if (TryReplicationAttachments(destination, attachments, out lastError) == false) // failed to replicate, start error handling strategy
				{
					if (IsFirstFailure(destination.ConnectionStringOptions.Url))
					{
						log.Info("This is the first failure for {0}, assuming transient failure and trying again", destination);
						if (TryReplicationAttachments(destination, attachments, out lastError)) // success on second fail
						{
							RecordSuccess(destination.ConnectionStringOptions.Url, lastReplicatedEtag: tuple.Item2, forDocuments: false);
							return true;
						}
					}

					scope.RecordError(lastError);
					RecordFailure(destination.ConnectionStringOptions.Url, lastError);
					return false;
				}
			}

			RecordSuccess(destination.ConnectionStringOptions.Url,
				lastReplicatedEtag: tuple.Item2, forDocuments: false);

			return true;
		}

		private bool? ReplicateDocuments(ReplicationStrategy destination, SourceReplicationInformation destinationsReplicationInformationForSource, ReplicationStatisticsRecorder.ReplicationStatisticsRecorderScope recorder)
		{
		    JsonDocumentsToReplicate documentsToReplicate = null;
            Stopwatch sp = Stopwatch.StartNew();

			var prefetchingBehavior = prefetchingBehaviors.GetOrAdd(destination.ConnectionStringOptions.Url,
				x => docDb.Prefetcher.CreatePrefetchingBehavior(PrefetchingUser.Replicator, autoTuner));

		    try
		    {
                using (var scope = recorder.StartRecording("Get"))
                {
                    documentsToReplicate = GetJsonDocuments(destinationsReplicationInformationForSource, destination, prefetchingBehavior, scope);
                    if (documentsToReplicate.Documents == null || documentsToReplicate.Documents.Length == 0)
                    {
                        if (documentsToReplicate.LastEtag != destinationsReplicationInformationForSource.LastDocumentEtag)
                        {
                            // we don't notify remote server about updates to system docs, see: RavenDB-715
                            if (documentsToReplicate.CountOfFilteredDocumentsWhichAreSystemDocuments == 0
                                || documentsToReplicate.CountOfFilteredDocumentsWhichAreSystemDocuments > SystemDocsLimitForRemoteEtagUpdate
                                || documentsToReplicate.CountOfFilteredDocumentsWhichOriginFromDestination > DestinationDocsLimitForRemoteEtagUpdate) // see RavenDB-1555
                            {
                                using (scope.StartRecording("Notify"))
                                {
                                    SetLastReplicatedEtagForServer(destination, lastDocEtag: documentsToReplicate.LastEtag);
                                    scope.Record(new RavenJObject
								             {
									             { "LastDocEtag", documentsToReplicate.LastEtag.ToString() }
								             });
                                }
                            }
                        }
                        RecordLastEtagChecked(destination.ConnectionStringOptions.Url, documentsToReplicate.LastEtag);
                        return null;
                    }
                }

                // if the db is idling in all respect except sending out replication, let us keep it that way.
                docDb.WorkContext.UpdateFoundWork(); 

                using (var scope = recorder.StartRecording("Send"))
                {
                    string lastError;
                    if (TryReplicationDocuments(destination, documentsToReplicate.Documents, documentsToReplicate.StartEtag, documentsToReplicate.LastEtag, out lastError) == false) // failed to replicate, start error handling strategy
                    {
                        if (IsFirstFailure(destination.ConnectionStringOptions.Url))
                        {
                            log.Info(
                                "This is the first failure for {0}, assuming transient failure and trying again",
                                destination);
                            if (TryReplicationDocuments(destination, documentsToReplicate.Documents, documentsToReplicate.StartEtag, documentsToReplicate.LastEtag, out lastError)) // success on second fail
                            {
                                RecordSuccess(destination.ConnectionStringOptions.Url, documentsToReplicate.LastEtag, documentsToReplicate.LastLastModified);
                                return true;
                            }
                        }
                        // if we had an error sending to this endpoint, it might be because we are sending too much data, or because
                        // the request timed out. This will let us know that the next time we try, we'll use just the initial doc counts
                        // and we'll be much more conservative with increasing the sizes
                        prefetchingBehavior.OutOfMemoryExceptionHappened();
                        scope.RecordError(lastError);
                        RecordFailure(destination.ConnectionStringOptions.Url, lastError);
                        return false;
                    }
                }
		    }
		    finally
		    {
		        if (documentsToReplicate != null && documentsToReplicate.LoadedDocs != null)
		            prefetchingBehavior.UpdateAutoThrottler(documentsToReplicate.LoadedDocs, sp.Elapsed);
		    }

			RecordSuccess(destination.ConnectionStringOptions.Url, documentsToReplicate.LastEtag, documentsToReplicate.LastLastModified);
			return true;
		}

		private void SetLastReplicatedEtagForServer(ReplicationStrategy destination, Etag lastDocEtag = null, Etag lastAttachmentEtag = null)
		{
			try
			{
				var url = destination.ConnectionStringOptions.Url + "/replication/lastEtag?from=" + UrlEncodedServerUrl() +
						  "&dbid=" + docDb.TransactionalStorage.Id;
				if (lastDocEtag != null)
					url += "&docEtag=" + lastDocEtag;
				if (lastAttachmentEtag != null)
					url += "&attachmentEtag=" + lastAttachmentEtag;

				var request = httpRavenRequestFactory.Create(url, "PUT", destination.ConnectionStringOptions);
				request.Write(new byte[0]);
				request.ExecuteRequest();
			}
			catch (WebException e)
			{
				var response = e.Response as HttpWebResponse;
				if (response != null && (response.StatusCode == HttpStatusCode.BadRequest || response.StatusCode == HttpStatusCode.NotFound))
					log.WarnException("Replication is not enabled on: " + destination, e);
				else
					log.WarnException("Failed to contact replication destination: " + destination, e);
			}
			catch (Exception e)
			{
				log.WarnException("Failed to contact replication destination: " + destination, e);
			}
		}

		private void RecordFailure(string url, string lastError)
		{
			var stats = destinationStats.GetOrAdd(url, new DestinationStats { Url = url });
			Interlocked.Increment(ref stats.FailureCountInternal);
			stats.LastFailureTimestamp = SystemTime.UtcNow;

			if (stats.FirstFailureInCycleTimestamp == null)
				stats.FirstFailureInCycleTimestamp = SystemTime.UtcNow;

			if (string.IsNullOrWhiteSpace(lastError) == false)
				stats.LastError = lastError;

			var jsonDocument = docDb.Get(Constants.RavenReplicationDestinationsBasePath + EscapeDestinationName(url), null);
			var failureInformation = new DestinationFailureInformation { Destination = url };
			if (jsonDocument != null)
			{
				failureInformation = jsonDocument.DataAsJson.JsonDeserialization<DestinationFailureInformation>();
			}
			failureInformation.FailureCount += 1;
			docDb.Put(Constants.RavenReplicationDestinationsBasePath + EscapeDestinationName(url), null,
					  RavenJObject.FromObject(failureInformation), new RavenJObject(), null);
		}

		private void RecordLastEtagChecked(string url, Etag lastEtagChecked)
		{
			var stats = destinationStats.GetOrDefault(url, new DestinationStats { Url = url });
			stats.LastEtagCheckedForReplication = lastEtagChecked;
		}

		private void RecordSuccess(string url,
			Etag lastReplicatedEtag = null, DateTime? lastReplicatedLastModified = null,
			DateTime? lastHeartbeatReceived = null, string lastError = null, bool forDocuments = true)
		{
			var stats = destinationStats.GetOrAdd(url, new DestinationStats { Url = url });
			Interlocked.Exchange(ref stats.FailureCountInternal, 0);
			stats.LastSuccessTimestamp = SystemTime.UtcNow;
			stats.FirstFailureInCycleTimestamp = null;

			if (lastReplicatedEtag != null)
			{
				stats.LastEtagCheckedForReplication = lastReplicatedEtag;
				if (forDocuments)
					stats.LastReplicatedEtag = lastReplicatedEtag;
				else
					stats.LastReplicatedAttachmentEtag = lastReplicatedEtag;
			}

			if (lastReplicatedLastModified.HasValue)
				stats.LastReplicatedLastModified = lastReplicatedLastModified;

			if (lastHeartbeatReceived.HasValue)
				stats.LastHeartbeatReceived = lastHeartbeatReceived;

			if (!string.IsNullOrWhiteSpace(lastError))
				stats.LastError = lastError;

			docDb.Delete(Constants.RavenReplicationDestinationsBasePath + EscapeDestinationName(url), null, null);
		}

		private bool IsFirstFailure(string url)
		{
			var destStats = destinationStats.GetOrAdd(url, new DestinationStats { Url = url });
			return destStats.FailureCount == 0;
		}

		private bool TryReplicationAttachments(ReplicationStrategy destination, RavenJArray jsonAttachments, out string errorMessage)
		{
			try
			{
				var url = destination.ConnectionStringOptions.Url + "/replication/replicateAttachments?from=" +
						  UrlEncodedServerUrl() + "&dbid=" + docDb.TransactionalStorage.Id;

				var sp = Stopwatch.StartNew();
				var request = httpRavenRequestFactory.Create(url, "POST", destination.ConnectionStringOptions);

				request.WriteBson(jsonAttachments);
				request.ExecuteRequest(docDb.WorkContext.CancellationToken);
				log.Info("Replicated {0} attachments to {1} in {2:#,#;;0} ms", jsonAttachments.Length, destination, sp.ElapsedMilliseconds);
				errorMessage = "";
				return true;
			}
			catch (WebException e)
			{
				var response = e.Response as HttpWebResponse;
				if (response != null)
				{
					try
					{
						using (var streamReader = new StreamReader(response.GetResponseStreamWithHttpDecompression()))
						{
							var error = streamReader.ReadToEnd();
							try
							{
								var ravenJObject = RavenJObject.Parse(error);
								log.WarnException("Replication to " + destination + " had failed\r\n" + ravenJObject.Value<string>("Error"), e);
								errorMessage = error;
								return false;
							}
							catch (Exception)
							{
							}

							log.WarnException("Replication to " + destination + " had failed\r\n" + error, e);
							errorMessage = error;
						}
					}
					catch (Exception)
					{
						errorMessage = response.StatusDescription;
						log.WarnException("Replication to " + destination + " had failed. Network code " + e.Status + ", HTTP Code: " + response.StatusCode, e);
					}
				}
				else
				{
					log.WarnException("Replication to " + destination + " had failed", e);
					errorMessage = e.Message;
				}
				return false;
			}
			catch (Exception e)
			{
				log.WarnException("Replication to " + destination + " had failed", e);
				errorMessage = e.Message;
				return false;
			}
		}

		private bool TryReplicationDocuments(ReplicationStrategy destination, RavenJArray jsonDocuments, Etag startEtag, Etag lastEtag, out string lastError)
		{
			try
			{
				log.Debug("Starting to replicate {0} documents to {1}", jsonDocuments.Length, destination);
			    var url = destination.ConnectionStringOptions.Url + "/replication/replicateDocs?from=" + UrlEncodedServerUrl()
			              + "&dbid=" + docDb.TransactionalStorage.Id +
			              "&count=" + jsonDocuments.Length;

				var sp = Stopwatch.StartNew();

				var request = httpRavenRequestFactory.Create(url, "POST", destination.ConnectionStringOptions);
				request.Write(jsonDocuments);
				request.ExecuteRequest(docDb.WorkContext.CancellationToken);

				log.Info("Replicated {0} documents to {1} in {2:#,#;;0} ms, start etag: {3}, last etag: {4}", 
                    jsonDocuments.Length, destination, sp.ElapsedMilliseconds, startEtag, lastEtag);
				lastError = "";
				return true;
			}
			catch (WebException e)
			{
				var response = e.Response as HttpWebResponse;
				if (response != null)
				{
					Stream responseStream = response.GetResponseStream();
					if (responseStream != null)
					{
						using (var streamReader = new StreamReader(responseStream))
						{
							var error = streamReader.ReadToEnd();
							log.WarnException("Replication to " + destination + " had failed\r\n" + error, e);
						}
					}
					else
					{
						log.WarnException("Replication to " + destination + " had failed", e);
					}
				}
				else
				{
					log.WarnException("Replication to " + destination + " had failed", e);
				}
				lastError = e.Message;
				return false;
			}
			catch (Exception e)
			{
				log.WarnException("Replication to " + destination + " had failed", e);
				lastError = e.Message;
				return false;
			}
		}

	    private class JsonDocumentsToReplicate
		{
            public Etag StartEtag { get; set; }
			public Etag LastEtag { get; set; }
			public DateTime LastLastModified { get; set; }
			public RavenJArray Documents { get; set; }
			public int CountOfFilteredDocumentsWhichAreSystemDocuments { get; set; }
			public int CountOfFilteredDocumentsWhichOriginFromDestination { get; set; }
		    public List<JsonDocument> LoadedDocs { get; set; }
		}

		private JsonDocumentsToReplicate GetJsonDocuments(SourceReplicationInformation destinationsReplicationInformationForSource, ReplicationStrategy destination, PrefetchingBehavior prefetchingBehavior, ReplicationStatisticsRecorder.ReplicationStatisticsRecorderScope scope)
		{
			var result = new JsonDocumentsToReplicate();
		    try
		    {
		        var destinationId = destinationsReplicationInformationForSource.ServerInstanceId.ToString();

		        docDb.TransactionalStorage.Batch(actions =>
		        {
		            var lastEtag = destinationsReplicationInformationForSource.LastDocumentEtag;

		            int docsSinceLastReplEtag = 0;
		            List<JsonDocument> docsToReplicate;
		            List<JsonDocument> filteredDocsToReplicate;
		            result.LastEtag = lastEtag;

		            while (true)
		            {
		                docsToReplicate = GetDocsToReplicate(actions, prefetchingBehavior, result);

		                filteredDocsToReplicate =
		                    docsToReplicate
		                        .Where(document =>
		                        {
		                            var info = docDb.GetRecentTouchesFor(document.Key);
		                            if (info != null)
		                            {
		                                if (info.TouchedEtag.CompareTo(result.LastEtag) > 0)
		                                {
		                                    log.Debug(
		                                        "Will not replicate document '{0}' to '{1}' because the updates after etag {2} are related document touches",
		                                        document.Key, destinationId, info.TouchedEtag);
		                                    return false;
		                                }
		                            }

		                            return destination.FilterDocuments(destinationId, document.Key, document.Metadata) &&
		                                   prefetchingBehavior.FilterDocuments(document);
		                        })
		                        .ToList();

		                docsSinceLastReplEtag += docsToReplicate.Count;
		                result.CountOfFilteredDocumentsWhichAreSystemDocuments +=
		                    docsToReplicate.Count(doc => destination.IsSystemDocumentId(doc.Key));
		                result.CountOfFilteredDocumentsWhichOriginFromDestination +=
		                    docsToReplicate.Count(doc => destination.OriginsFromDestination(destinationId, doc.Metadata));

		                if (docsToReplicate.Count > 0)
		                {
		                    var lastDoc = docsToReplicate.Last();
		                    Debug.Assert(lastDoc.Etag != null);
		                    result.LastEtag = lastDoc.Etag;
		                    if (lastDoc.LastModified.HasValue)
		                        result.LastLastModified = lastDoc.LastModified.Value;
		                }

		                if (docsToReplicate.Count == 0 || filteredDocsToReplicate.Count != 0)
		                {
		                    break;
		                }

		                log.Debug("All the docs were filtered, trying another batch from etag [>{0}]", result.LastEtag);
		            }

		            log.Debug(() =>
		            {
		                if (docsSinceLastReplEtag == 0)
		                    return string.Format("No documents to replicate to {0} - last replicated etag: {1}", destination,
		                        lastEtag);

		                if (docsSinceLastReplEtag == filteredDocsToReplicate.Count)
		                    return string.Format("Replicating {0} docs [>{1}] to {2}.",
		                        docsSinceLastReplEtag,
		                        lastEtag,
		                        destination);

		                var diff = docsToReplicate.Except(filteredDocsToReplicate).Select(x => x.Key);
		                return string.Format("Replicating {1} docs (out of {0}) [>{4}] to {2}. [Not replicated: {3}]",
		                    docsSinceLastReplEtag,
		                    filteredDocsToReplicate.Count,
		                    destination,
		                    string.Join(", ", diff),
		                    lastEtag);
		            });

		            scope.Record(new RavenJObject
		            {
		                {"StartEtag", lastEtag.ToString()},
		                {"EndEtag", result.LastEtag.ToString()},
		                {"Count", docsSinceLastReplEtag},
		                {"FilteredCount", filteredDocsToReplicate.Count}
		            });

                    result.StartEtag = lastEtag;
		            result.LoadedDocs = filteredDocsToReplicate;
		            result.Documents = new RavenJArray(filteredDocsToReplicate
		                .Select(x =>
		                {
		                    DocumentRetriever.EnsureIdInMetadata(x);
		                    return x;
		                })
		                .Select(x => x.ToJson()));
		        });
		    }
		    catch (Exception e)
		    {
		        scope.RecordError(e);
		        log.WarnException(
		            "Could not get documents to replicate after: " +
		            destinationsReplicationInformationForSource.LastDocumentEtag, e);
		    }
			return result;
		}

		private List<JsonDocument> GetDocsToReplicate(IStorageActionsAccessor actions, PrefetchingBehavior prefetchingBehavior, JsonDocumentsToReplicate result)
		{
			var docsToReplicate = prefetchingBehavior.GetDocumentsBatchFrom(result.LastEtag);
			Etag lastEtag = null;
			if (docsToReplicate.Count > 0)
				lastEtag = docsToReplicate[docsToReplicate.Count - 1].Etag;

			var maxNumberOfTombstones = Math.Max(1024, docsToReplicate.Count);
			var tombstones = actions
				.Lists
				.Read(Constants.RavenReplicationDocsTombstones, result.LastEtag, lastEtag, maxNumberOfTombstones + 1)
				.Select(x => new JsonDocument
				{
					Etag = x.Etag,
					Key = x.Key,
					Metadata = x.Data,
					DataAsJson = new RavenJObject()
				})
				.ToList();

			var results = docsToReplicate.Concat(tombstones);

			if (tombstones.Count >= maxNumberOfTombstones + 1)
			{
				var lastTombstoneEtag = tombstones[tombstones.Count - 1].Etag;
				log.Info("Replication batch trimmed. Found more than '{0}' document tombstones. Last etag from prefetcher: '{1}'. Last tombstone etag: '{2}'.", maxNumberOfTombstones, lastEtag, lastTombstoneEtag);

				results = results.Where(x => EtagUtil.IsGreaterThan(x.Etag, lastTombstoneEtag) == false);
			}

			return results
				.OrderBy(x => x.Etag)
				.ToList();
		}

		private Tuple<RavenJArray, Etag> GetAttachments(SourceReplicationInformation destinationsReplicationInformationForSource, ReplicationStrategy destination, ReplicationStatisticsRecorder.ReplicationStatisticsRecorderScope scope)
		{
			RavenJArray attachments = null;
			Etag lastAttachmentEtag = Etag.Empty;
			try
			{
				var destinationId = destinationsReplicationInformationForSource.ServerInstanceId.ToString();

				docDb.TransactionalStorage.Batch(actions =>
				{
					int attachmentSinceLastEtag = 0;
					List<AttachmentInformation> attachmentsToReplicate;
					List<AttachmentInformation> filteredAttachmentsToReplicate;
					var startEtag = destinationsReplicationInformationForSource.LastAttachmentEtag;
					lastAttachmentEtag = startEtag;
					while (true)
					{
						attachmentsToReplicate = GetAttachmentsToReplicate(actions, lastAttachmentEtag);

						filteredAttachmentsToReplicate = attachmentsToReplicate.Where(attachment => destination.FilterAttachments(attachment, destinationId)).ToList();

						attachmentSinceLastEtag += attachmentsToReplicate.Count;

						if (attachmentsToReplicate.Count == 0 ||
							filteredAttachmentsToReplicate.Count != 0)
						{
							break;
						}

						AttachmentInformation jsonDocument = attachmentsToReplicate.Last();
						Etag attachmentEtag = jsonDocument.Etag;
						log.Debug("All the attachments were filtered, trying another batch from etag [>{0}]", attachmentEtag);
						lastAttachmentEtag = attachmentEtag;
					}

					log.Debug(() =>
					{
						if (attachmentSinceLastEtag == 0)
							return string.Format("No attachments to replicate to {0} - last replicated etag: {1}", destination,
												 destinationsReplicationInformationForSource.LastAttachmentEtag);

						if (attachmentSinceLastEtag == filteredAttachmentsToReplicate.Count)
							return string.Format("Replicating {0} attachments [>{1}] to {2}.",
											 attachmentSinceLastEtag,
											 destinationsReplicationInformationForSource.LastAttachmentEtag,
											 destination);

						var diff = attachmentsToReplicate.Except(filteredAttachmentsToReplicate).Select(x => x.Key);
						return string.Format("Replicating {1} attachments (out of {0}) [>{4}] to {2}. [Not replicated: {3}]",
											 attachmentSinceLastEtag,
											 filteredAttachmentsToReplicate.Count,
											 destination,
											 string.Join(", ", diff),
											 destinationsReplicationInformationForSource.LastAttachmentEtag);
					});

					scope.Record(new RavenJObject
					             {
						             {"StartEtag", startEtag.ToString()},
									 {"EndEtag", lastAttachmentEtag.ToString()},
									 {"Count", attachmentSinceLastEtag},
									 {"FilteredCount", filteredAttachmentsToReplicate.Count}
					             });

					attachments = new RavenJArray(filteredAttachmentsToReplicate
													  .Select(x =>
													  {
														  var data = new byte[0];
														  if (x.Size > 0)
														  {
															  data = actions.Attachments.GetAttachment(x.Key).Data().ReadData();
														  }
														  return new RavenJObject
							                                           {
								                                           {"@metadata", x.Metadata},
								                                           {"@id", x.Key},
								                                           {"@etag", x.Etag.ToByteArray()},
								                                           {"data", data}
							                                           };
													  }));
				});
			}
			catch (Exception e)
			{
				log.WarnException("Could not get attachments to replicate after: " + destinationsReplicationInformationForSource.LastAttachmentEtag, e);
			}
			return Tuple.Create(attachments, lastAttachmentEtag);
		}

		private static List<AttachmentInformation> GetAttachmentsToReplicate(IStorageActionsAccessor actions, Etag lastAttachmentEtag)
		{
			var attachmentInformations = actions.Attachments.GetAttachmentsAfter(lastAttachmentEtag, 100, 1024 * 1024 * 10).ToList();

			Etag lastEtag = null;
			if (attachmentInformations.Count > 0)
				lastEtag = attachmentInformations[attachmentInformations.Count - 1].Etag;

			var maxNumberOfTombstones = Math.Max(100, attachmentInformations.Count);
			var tombstones = actions
				.Lists
				.Read(Constants.RavenReplicationAttachmentsTombstones, lastAttachmentEtag, lastEtag, maxNumberOfTombstones + 1)
				.Select(x => new AttachmentInformation
				{
					Key = x.Key,
					Etag = x.Etag,
					Metadata = x.Data,
					Size = 0,
				})
				.ToList();

			var results = attachmentInformations.Concat(tombstones);

			if (tombstones.Count >= maxNumberOfTombstones + 1)
			{
				var lastTombstoneEtag = tombstones[tombstones.Count - 1].Etag;
				log.Info("Replication batch trimmed. Found more than '{0}' attachment tombstones. Last attachment etag: '{1}'. Last tombstone etag: '{2}'.", maxNumberOfTombstones, lastEtag, lastTombstoneEtag);

				results = results.Where(x => EtagUtil.IsGreaterThan(x.Etag, lastTombstoneEtag) == false);
			}

			return results
				.OrderBy(x => x.Etag)
				.ToList();
		}

		private SourceReplicationInformation GetLastReplicatedEtagFrom(ReplicationStrategy destination)
		{
			try
			{
				Etag currentEtag = Etag.Empty;
				docDb.TransactionalStorage.Batch(accessor => currentEtag = accessor.Staleness.GetMostRecentDocumentEtag());
				var url = destination.ConnectionStringOptions.Url + "/replication/lastEtag?from=" + UrlEncodedServerUrl() +
						  "&currentEtag=" + currentEtag + "&dbid=" + docDb.TransactionalStorage.Id;
				var request = httpRavenRequestFactory.Create(url, "GET", destination.ConnectionStringOptions);
				return request.ExecuteRequest<SourceReplicationInformation>();
			}
			catch (WebException e)
			{
				var response = e.Response as HttpWebResponse;
				if (response != null && (response.StatusCode == HttpStatusCode.BadRequest || response.StatusCode == HttpStatusCode.NotFound))
					log.WarnException("Replication is not enabled on: " + destination, e);
				else
					log.WarnException("Failed to contact replication destination: " + destination, e);
				RecordFailure(destination.ConnectionStringOptions.Url, e.Message);
			}
			catch (Exception e)
			{
				log.WarnException("Failed to contact replication destination: " + destination, e);
				RecordFailure(destination.ConnectionStringOptions.Url, e.Message);
			}

			return null;
		}

		private string UrlEncodedServerUrl()
		{
			return Uri.EscapeDataString(docDb.ServerUrl);
		}

		private ReplicationStrategy[] GetReplicationDestinations()
		{
			var document = docDb.Get(Constants.RavenReplicationDestinations, null);
			if (document == null)
			{
				return new ReplicationStrategy[0];
			}
			ReplicationDocument jsonDeserialization;
			try
			{
				jsonDeserialization = document.DataAsJson.JsonDeserialization<ReplicationDocument>();
			}
			catch (Exception e)
			{
				log.Warn("Cannot get replication destinations", e);
				return new ReplicationStrategy[0];
			}

			if (string.IsNullOrWhiteSpace(jsonDeserialization.Source))
			{
				jsonDeserialization.Source = docDb.TransactionalStorage.Id.ToString();
				try
				{
					var ravenJObject = RavenJObject.FromObject(jsonDeserialization);
					ravenJObject.Remove("Id");
					docDb.Put(Constants.RavenReplicationDestinations, document.Etag, ravenJObject, document.Metadata, null);
				}
				catch (ConcurrencyException)
				{
					// we will get it next time
				}
			}

			if (jsonDeserialization.Source != docDb.TransactionalStorage.Id.ToString())
			{
			    if (!wrongReplicationSourceAlertSent)
			    {
			        var dbName = string.IsNullOrEmpty(docDb.Name) ? "<system>" : docDb.Name;

			        docDb.AddAlert(new Alert
			            {
			                AlertLevel = AlertLevel.Error,
			                CreatedAt = SystemTime.UtcNow,
			                Message = "Source of the ReplicationDestinations document is not the same as the database it is located in",
                            Title = "Wrong replication source: " + jsonDeserialization.Source + " instead of " + docDb.TransactionalStorage.Id + " in database " + dbName,
			                UniqueKey = "Wrong source: " + jsonDeserialization.Source + ", " + docDb.TransactionalStorage.Id
			            });

                    wrongReplicationSourceAlertSent = true;
			    }

			    return new ReplicationStrategy[0];
			}

            wrongReplicationSourceAlertSent = false;

			return jsonDeserialization
				.Destinations
				.Where(x => !x.Disabled)
				.Select(GetConnectionOptionsSafe)
				.Where(x => x != null)
				.ToArray();
		}

		private ReplicationStrategy GetConnectionOptionsSafe(ReplicationDestination x)
		{
			try
			{
				return GetConnectionOptions(x);
			}
			catch (Exception e)
			{
				log.ErrorException(
					string.Format("IGNORING BAD REPLICATION CONFIG!{0}Could not figure out connection options for [Url: {1}, ClientVisibleUrl: {2}]",
					Environment.NewLine, x.Url, x.ClientVisibleUrl),
					e);

				return null;
			}
		}

		private ReplicationStrategy GetConnectionOptions(ReplicationDestination x)
		{
			var replicationStrategy = new ReplicationStrategy
			{
				ReplicationOptionsBehavior = x.TransitiveReplicationBehavior,
				CurrentDatabaseId = docDb.TransactionalStorage.Id.ToString()
			};
			return CreateReplicationStrategyFromDocument(x, replicationStrategy);
		}

		private static ReplicationStrategy CreateReplicationStrategyFromDocument(ReplicationDestination x, ReplicationStrategy replicationStrategy)
		{
			var url = x.Url;
			if (string.IsNullOrEmpty(x.Database) == false)
			{
				url = url + "/databases/" + x.Database;
			}
			replicationStrategy.ConnectionStringOptions = new RavenConnectionStringOptions
			{
				Url = url,
				ApiKey = x.ApiKey,
			};
			if (string.IsNullOrEmpty(x.Username) == false)
			{
				replicationStrategy.ConnectionStringOptions.Credentials = string.IsNullOrEmpty(x.Domain)
					? new NetworkCredential(x.Username, x.Password)
					: new NetworkCredential(x.Username, x.Password, x.Domain);
			}
			return replicationStrategy;
		}

		public void HandleHeartbeat(string src)
		{
			ResetFailureForHeartbeat(src);

			heartbeatDictionary.AddOrUpdate(src, SystemTime.UtcNow, (_, __) => SystemTime.UtcNow);
		}

		public bool IsHeartbeatAvailable(string src, DateTime lastCheck)
		{
			if (heartbeatDictionary.ContainsKey(src))
			{
				DateTime lastHeartbeat;
				if (heartbeatDictionary.TryGetValue(src, out lastHeartbeat))
				{
					return lastHeartbeat >= lastCheck;
				}
			}

			return false;
		}


		private void ResetFailureForHeartbeat(string src)
		{
			RecordSuccess(src, lastHeartbeatReceived: SystemTime.UtcNow);
			docDb.WorkContext.ShouldNotifyAboutWork(() => "Replication Heartbeat from " + src);
			docDb.WorkContext.NotifyAboutWork();
		}

		public void Dispose()
		{
			Task task;
			while (activeTasks.TryDequeue(out task))
			{
				task.Wait();
			}

			foreach (var prefetchingBehavior in prefetchingBehaviors)
			{
				prefetchingBehavior.Value.Dispose();
			}
		}
		private readonly ConcurrentDictionary<string, DateTime> heartbeatDictionary = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
	}

	internal class ReplicationStatisticsRecorder : IDisposable
	{
		private readonly ReplicationStrategy destination;

		private readonly ConcurrentDictionary<string, DestinationStats> destinationStats;

		private readonly RavenJObject record;

		private readonly RavenJArray records;

		private readonly Stopwatch watch;

		public ReplicationStatisticsRecorder(ReplicationStrategy destination, ConcurrentDictionary<string, DestinationStats> destinationStats)
		{
			this.destination = destination;
			this.destinationStats = destinationStats;
			watch = Stopwatch.StartNew();
			records = new RavenJArray();
			record = new RavenJObject
			         {
				         { "Url", destination.ConnectionStringOptions.Url },
						 { "StartTime", SystemTime.UtcNow},
						 { "Records", records }
			         };
		}

		public void Dispose()
		{
			record.Add("TotalExecutionTime", watch.Elapsed.ToString());

			var stats = destinationStats.GetOrDefault(destination.ConnectionStringOptions.Url, new DestinationStats { Url = destination.ConnectionStringOptions.Url });

			stats.LastStats.Insert(0, record);

			while (stats.LastStats.Length > 50)
				stats.LastStats.RemoveAt(stats.LastStats.Length - 1);
		}

		public ReplicationStatisticsRecorderScope StartRecording(string name)
		{
			var scopeRecord = new RavenJObject();
			records.Add(scopeRecord);
			return new ReplicationStatisticsRecorderScope(name, scopeRecord);
		}

		internal class ReplicationStatisticsRecorderScope : IDisposable
		{
			private readonly RavenJObject record;

			private readonly RavenJArray records;

			private readonly Stopwatch watch;

			public ReplicationStatisticsRecorderScope(string name, RavenJObject record)
			{
				this.record = record;
				records = new RavenJArray();

				record.Add("Name", name);
				record.Add("Records", records);

				watch = Stopwatch.StartNew();
			}

			public void Dispose()
			{
				record.Add("ExecutionTime", watch.Elapsed.ToString());
			}

			public void Record(RavenJObject value)
			{
				records.Add(value);
			}

			public void RecordError(Exception exception)
			{
				records.Add(new RavenJObject
				            {
					            { "Error", new RavenJObject
					                       {
						                       { "Type", exception.GetType().Name }, 
											   { "Message", exception.Message }
					                       } }
				            });
			}

			public void RecordError(string error)
			{
				records.Add(new RavenJObject
				            {
					            { "Error", error }
				            });
			}

			public ReplicationStatisticsRecorderScope StartRecording(string name)
			{
				var scopeRecord = new RavenJObject();
				records.Add(scopeRecord);
				return new ReplicationStatisticsRecorderScope(name, scopeRecord);
			}
		}
	}
}
