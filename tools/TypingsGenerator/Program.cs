﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Raven.Client.Documents.Changes;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;
using Raven.Client.Documents.Replication;
using Raven.Client.Documents.Smuggler;
using Raven.Client.Documents.Subscriptions;
using Raven.Client.Documents.Transformers;
using Raven.Client.Server;
using Raven.Client.Server.Operations;
using Raven.Server.Commercial;
using Raven.Server.Documents;
using Raven.Server.Documents.ETL;
using Raven.Server.Documents.ETL.Providers.SQL;
using Raven.Server.Documents.ETL.Providers.SQL.Connections;
using Raven.Server.Documents.ETL.Providers.SQL.RelationalWriters;
using Raven.Server.Documents.Handlers;
using Raven.Server.Documents.Indexes.Debugging;
using Raven.Server.Documents.Operations;
using Raven.Server.Documents.Patch;
using Raven.Server.Documents.Versioning;
using Raven.Server.Documents.PeriodicExport;
using Raven.Server.Documents.Subscriptions;
using Raven.Server.Web.System;
using Raven.Server.NotificationCenter.Notifications;
using Raven.Server.NotificationCenter.Notifications.Server;
using Raven.Server.Smuggler.Documents.Data;
using Sparrow;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using TypeScripter;
using TypeScripter.TypeScript;
using Voron.Data.BTrees;
using Voron.Debugging;
using PatchRequest = Raven.Server.Documents.Patch.PatchRequest;

namespace TypingsGenerator
{
    public class Program
    {

        public const string TargetDirectory = "../../src/Raven.Studio/typings/server/";
        public static void Main(string[] args)
        {
            Directory.CreateDirectory(TargetDirectory);

            var scripter = new CustomScripter()
                .UsingFormatter(new TsFormatter
                {
                    EnumsAsString = true
                });

            scripter
                .WithTypeMapping(TsPrimitive.String, typeof(Guid))
                .WithTypeMapping(TsPrimitive.String, typeof(TimeSpan))
                .WithTypeMapping(new TsInterface(new TsName("Array")), typeof(HashSet<>))
                .WithTypeMapping(new TsInterface(new TsName("Array")), typeof(List<>))
                .WithTypeMapping(new TsInterface(new TsName("Array")), typeof(IEnumerable<>))
                .WithTypeMapping(new TsInterface(new TsName("Array")), typeof(IReadOnlyList<>))
                .WithTypeMapping(new TsInterface(new TsName("Array")), typeof(IReadOnlyCollection<>))
                .WithTypeMapping(TsPrimitive.Any, typeof(TreePage))
                .WithTypeMapping(TsPrimitive.String, typeof(DateTime))
                .WithTypeMapping(new TsArray(TsPrimitive.Any, 1), typeof(BlittableJsonReaderArray))
                .WithTypeMapping(new TsArray(TsPrimitive.Any, 1), typeof(DynamicJsonArray))
                .WithTypeMapping(new TsArray(TsPrimitive.Any, 1), typeof(IEnumerable))
                .WithTypeMapping(TsPrimitive.Any, typeof(BlittableJsonReaderObject));

            scripter = ConfigureTypes(scripter);
            Directory.Delete(TargetDirectory, true);
            Directory.CreateDirectory(TargetDirectory);
            scripter
                .SaveToDirectory(TargetDirectory);
        }

        private static Scripter ConfigureTypes(Scripter scripter)
        {
            var ignoredTypes = new HashSet<Type>
            {
                typeof(IEquatable<>)
            };

            scripter.UsingTypeFilter(type => ignoredTypes.Contains(type) == false);
            scripter.UsingTypeReader(new TypeReaderWithIgnoreMethods());
            scripter.AddType(typeof(CollectionStatistics));

            scripter.AddType(typeof(BatchRequestParser.CommandData));

            scripter.AddType(typeof(DatabaseDocument));
            scripter.AddType(typeof(DatabaseStatistics));
            scripter.AddType(typeof(IndexDefinition));
            scripter.AddType(typeof(PutIndexResult));

            // attachments
            scripter.AddType(typeof(AttachmentResult));

            // notifications
            scripter.AddType(typeof(AlertRaised));
            scripter.AddType(typeof(NotificationUpdated));
            scripter.AddType(typeof(OperationChanged));
            scripter.AddType(typeof(DatabaseChanged));
            scripter.AddType(typeof(DatabaseStatsChanged));
            scripter.AddType(typeof(PerformanceHint));

            // subscriptions
            scripter.AddType(typeof(SubscriptionCriteria));
            scripter.AddType(typeof(SubscriptionConnectionStats));
            scripter.AddType(typeof(SubscriptionConnectionOptions));

            // changes
            scripter.AddType(typeof(OperationStatusChange));
            scripter.AddType(typeof(DeterminateProgress));
            scripter.AddType(typeof(IndeterminateProgress));
            scripter.AddType(typeof(OperationExceptionResult));
            scripter.AddType(typeof(DocumentChange));
            scripter.AddType(typeof(IndexChange));
            scripter.AddType(typeof(TransformerChange));
            scripter.AddType(typeof(DatabaseOperations.Operation));
            
            // indexes
            scripter.AddType(typeof(IndexStats));
            scripter.AddType(typeof(IndexingStatus));
            scripter.AddType(typeof(IndexPerformanceStats));
            scripter.AddType(typeof(IndexDefinition));
            scripter.AddType(typeof(TermsQueryResult));

            // query 
            scripter.AddType(typeof(QueryResult<>));

            // transformers
            scripter.AddType(typeof(TransformerDefinition));

            // patch
            scripter.AddType(typeof(PatchRequest));
            scripter.AddType(typeof(PatchResult));
            scripter.AddType(typeof(PatchDebugActions));

            scripter.AddType(typeof(DatabasesInfo));

            // smuggler
            scripter.AddType(typeof(DatabaseSmugglerOptions));
            scripter.AddType(typeof(SmugglerResult));

            // versioning
            scripter.AddType(typeof(VersioningConfiguration));

            // replication 
            scripter.AddType(typeof(ReplicationDocument<>));

            // sql replication 
            scripter.AddType(typeof(SqlConnections));
            scripter.AddType(typeof(SqlEtlConfiguration));
            scripter.AddType(typeof(EtlProcessStatistics));
            scripter.AddType(typeof(SimulateSqlEtl));

            // periodic export
            scripter.AddType(typeof(PeriodicExportConfiguration));

            // storage report
            scripter.AddType(typeof(StorageReport));
            scripter.AddType(typeof(DetailedStorageReport));

            // map reduce visualizer
            scripter.AddType(typeof(ReduceTree));

            // license 
            scripter.AddType(typeof(License));
            scripter.AddType(typeof(UserRegistrationInfo));
            scripter.AddType(typeof(LicenseStatus));

            // database admin
            scripter.AddType(typeof(DatabaseDeleteResult));

            // io metrics stats
            scripter.AddType(typeof(IOMetricsHistoryStats));
            scripter.AddType(typeof(IOMetricsRecentStats));
            scripter.AddType(typeof(IOMetricsRecentStatsAdditionalTypes));
            scripter.AddType(typeof(IOMetricsFileStats));
            scripter.AddType(typeof(IOMetricsEnvironment));
            scripter.AddType(typeof(IOMetricsResponse));
            scripter.AddType(typeof(FileStatus));
            scripter.AddType(typeof(IoMetrics.MeterType));

            return scripter;
        }
    }
}