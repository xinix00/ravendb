//-----------------------------------------------------------------------
// <copyright file="InMemoryDocumentSessionOperations.cs" company="Hibernating Rhinos LTD">
//     Copyright (c) Hibernating Rhinos LTD. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Dynamic;
using System.Linq;
using System.Reflection;
using Raven.Client.Document.Batches;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Raven.Abstractions.Extensions;
using Raven.Abstractions.Util;
using Raven.Abstractions.Commands;
using Raven.Abstractions.Data;
using Raven.Abstractions.Logging;
using Raven.Client.Connection;
using Raven.Client.Data;
using Raven.Client.Exceptions;
using Raven.Client.Http;
using Raven.Client.Linq;
using Raven.Client.Util;
using Raven.Imports.Newtonsoft.Json.Linq;
using Raven.Imports.Newtonsoft.Json.Utilities;
using Raven.Json.Linq;
using Sparrow.Json;

namespace Raven.Client.Document
{
    /// <summary>
    /// Abstract implementation for in memory session operations
    /// </summary>
    public abstract class InMemoryDocumentSessionOperations : IDisposable
    {
        public readonly RequestExecuter RequestExecuter;
        private readonly IDisposable _releaseOperationContext;
        protected readonly JsonOperationContext Context;

        private static readonly ILog log = LogManager.GetLogger(typeof(InMemoryDocumentSessionOperations));

        protected readonly List<ILazyOperation> pendingLazyOperations = new List<ILazyOperation>();
        protected readonly Dictionary<ILazyOperation, Action<object>> onEvaluateLazy = new Dictionary<ILazyOperation, Action<object>>();

        private static int _instancesCounter;
        private readonly int _hash = Interlocked.Increment(ref _instancesCounter);

        protected bool GenerateDocumentKeysOnStore = true;

        /// <summary>
        /// The session id 
        /// </summary>
        public Guid Id { get; private set; }

        /// <summary>
        /// The entities waiting to be deleted
        /// </summary>
        protected readonly HashSet<object> DeletedEntities = new HashSet<object>(ObjectReferenceEqualityComparer<object>.Default);

        /// <summary>
        /// Entities whose id we already know do not exists, because they are a missing include, or a missing load, etc.
        /// </summary>
        protected readonly HashSet<string> KnownMissingIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private Dictionary<string, object> externalState;

        public IDictionary<string, object> ExternalState
        {
            get { return externalState ?? (externalState = new Dictionary<string, object>()); }
        }


        /// <summary>
        /// hold the data required to manage the data for RavenDB's Unit of Work
        /// </summary>
        protected readonly Dictionary<object, DocumentMetadata> entitiesAndMetadata =
            new Dictionary<object, DocumentMetadata>(ObjectReferenceEqualityComparer<object>.Default);

        protected readonly Dictionary<string, JsonDocument> includedDocumentsByKey = new Dictionary<string, JsonDocument>(StringComparer.OrdinalIgnoreCase);

        internal readonly Dictionary<string, BlittableJsonReaderObject> DocumentsById = new Dictionary<string, BlittableJsonReaderObject>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Translate between a key and its associated entity
        /// </summary>
        internal readonly Dictionary<string, object> EntitiesById = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<object, string> IdByEntities = new Dictionary<object, string>(ObjectReferenceEqualityComparer<object>.Default);

        protected readonly string databaseName;
        private readonly DocumentStoreBase documentStore;

        public string DatabaseName => databaseName;

        /// <summary>
        /// all the listeners for this session
        /// </summary>
        protected readonly DocumentSessionListeners theListeners;

        /// <summary>
        /// all the listeners for this session
        /// </summary>
        public DocumentSessionListeners Listeners
        {
            get { return theListeners; }
        }

        ///<summary>
        /// The document store associated with this session
        ///</summary>
        public IDocumentStore DocumentStore
        {
            get { return documentStore; }
        }


        /// <summary>
        /// Gets the number of requests for this session
        /// </summary>
        /// <value></value>
        public int NumberOfRequests { get; private set; }

        /// <summary>
        /// Gets the number of entities held in memory to manage Unit of Work
        /// </summary>
        public int NumberOfEntitiesInUnitOfWork
        {
            get { return entitiesAndMetadata.Count; }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="InMemoryDocumentSessionOperations"/> class.
        /// </summary>
        protected InMemoryDocumentSessionOperations(
            string databaseName,
            DocumentStoreBase documentStore,
            RequestExecuter requestExecuter,
            DocumentSessionListeners listeners,
            Guid id)
        {
            Id = id;
            this.databaseName = databaseName;
            this.documentStore = documentStore;
            RequestExecuter = requestExecuter;
            _releaseOperationContext = requestExecuter.ContextPool.AllocateOperationContext(out Context);
            this.theListeners = listeners;
            UseOptimisticConcurrency = documentStore.Conventions.DefaultUseOptimisticConcurrency;
            MaxNumberOfRequestsPerSession = documentStore.Conventions.MaxNumberOfRequestsPerSession;
            GenerateEntityIdOnTheClient = new GenerateEntityIdOnTheClient(documentStore.Conventions, GenerateKey);
            EntityToJson = new EntityToJson(documentStore, listeners);
        }

        /// <summary>
        /// Gets the store identifier for this session.
        /// The store identifier is the identifier for the particular RavenDB instance.
        /// </summary>
        /// <value>The store identifier.</value>
        public string StoreIdentifier
        {
            get { return documentStore.Identifier + ";" + DatabaseName; }
        }

        /// <summary>
        /// Gets the conventions used by this session
        /// </summary>
        /// <value>The conventions.</value>
        /// <remarks>
        /// This instance is shared among all sessions, changes to the <see cref="DocumentConvention"/> should be done
        /// via the <see cref="IDocumentStore"/> instance, not on a single session.
        /// </remarks>
        public DocumentConvention Conventions
        {
            get { return documentStore.Conventions; }
        }


        /// <summary>
        /// Gets or sets the max number of requests per session.
        /// If the <see cref="NumberOfRequests"/> rise above <see cref="MaxNumberOfRequestsPerSession"/>, an exception will be thrown.
        /// </summary>
        /// <value>The max number of requests per session.</value>
        public int MaxNumberOfRequestsPerSession { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the session should use optimistic concurrency.
        /// When set to <c>true</c>, a check is made so that a change made behind the session back would fail
        /// and raise <see cref="ConcurrencyException"/>.
        /// </summary>
        /// <value></value>
        public bool UseOptimisticConcurrency { get; set; }

        /// <summary>
        /// Gets the ETag for the specified entity.
        /// If the entity is transient, it will load the etag from the store
        /// and associate the current state of the entity with the etag from the server.
        /// </summary>
        /// <param name="instance">The instance.</param>
        /// <returns></returns>
        public long? GetEtagFor<T>(T instance)
        {
            return GetDocumentMetadata(instance).ETag;
        }

        /// <summary>
        /// Gets the metadata for the specified entity.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="instance">The instance.</param>
        /// <returns></returns>
        public RavenJObject GetMetadataFor<T>(T instance)
        {
            return GetDocumentMetadata(instance).Metadata;
        }

        private DocumentMetadata GetDocumentMetadata<T>(T instance)
        {
            DocumentMetadata value;
            if (entitiesAndMetadata.TryGetValue(instance, out value) == false)
            {
                string id;
                if (GenerateEntityIdOnTheClient.TryGetIdFromInstance(instance, out id)
                    || (instance is IDynamicMetaObjectProvider &&
                        GenerateEntityIdOnTheClient.TryGetIdFromDynamic(instance, out id))
                )
                {
                    AssertNoNonUniqueInstance(instance, id);

                    var jsonDocument = GetJsonDocument(id);
                    value = GetDocumentMetadataValue(instance, id, jsonDocument);
                }
                else
                {
                    throw new InvalidOperationException("Could not find the document key for " + instance);
                }
            }
            return value;
        }

        /// <summary>
        /// Get the json document by key from the store
        /// </summary>
        protected abstract JsonDocument GetJsonDocument(string documentKey);

        protected DocumentMetadata GetDocumentMetadataValue<T>(T instance, string id, JsonDocument jsonDocument)
        {
            EntitiesById[id] = instance;
            return entitiesAndMetadata[instance] = new DocumentMetadata
            {
                ETag = UseOptimisticConcurrency ? (long?) 0 : null,
                Key = id,
                OriginalMetadata = jsonDocument.Metadata,
                Metadata = (RavenJObject) jsonDocument.Metadata.CloneToken(),
                OriginalValue = new RavenJObject()
            };
        }


        /// <summary>
        /// Returns whatever a document with the specified id is loaded in the 
        /// current session
        /// </summary>
        public bool IsLoaded(string id)
        {
            if (IsDeleted(id))
                return false;
            return IsLoadedOrDeleted(id);
        }

        internal bool IsLoadedOrDeleted(string id)
        {
            return EntitiesById.ContainsKey(id) || DocumentsById.ContainsKey(id) || includedDocumentsByKey.ContainsKey(id) || IsDeleted(id);
        }

        /// <summary>
        /// Returns whatever a document with the specified id is deleted 
        /// or known to be missing
        /// </summary>
        public bool IsDeleted(string id)
        {
            return KnownMissingIds.Contains(id);
        }

        /// <summary>
        /// Gets the document id.
        /// </summary>
        /// <param name="instance">The instance.</param>
        /// <returns></returns>
        public string GetDocumentId(object instance)
        {
            if (instance == null)
                return null;
            DocumentMetadata value;
            if (entitiesAndMetadata.TryGetValue(instance, out value) == false)
                return null;
            return value.Key;
        }

        /// <summary>
        /// Gets a value indicating whether any of the entities tracked by the session has changes.
        /// </summary>
        /// <value></value>
        public bool HasChanges
        {
            get
            {

                return DeletedEntities.Count > 0 ||
                       entitiesAndMetadata.Any(pair => EntityChanged(pair.Key, pair.Value, null));
            }
        }



        /// <summary>
        /// Determines whether the specified entity has changed.
        /// </summary>
        /// <param name="entity">The entity.</param>
        /// <returns>
        /// 	<c>true</c> if the specified entity has changed; otherwise, <c>false</c>.
        /// </returns>
        public bool HasChanged(object entity)
        {
            DocumentMetadata value;
            if (entitiesAndMetadata.TryGetValue(entity, out value) == false)
                return false;
            return EntityChanged(entity, value, null);
        }

        public void IncrementRequestCount()
        {
            if (++NumberOfRequests > MaxNumberOfRequestsPerSession)
                throw new InvalidOperationException($@"The maximum number of requests ({MaxNumberOfRequestsPerSession}) allowed for this session has been reached.
Raven limits the number of remote calls that a session is allowed to make as an early warning system. Sessions are expected to be short lived, and 
Raven provides facilities like Load(string[] keys) to load multiple documents at once and batch saves (call SaveChanges() only once).
You can increase the limit by setting DocumentConvention.MaxNumberOfRequestsPerSession or MaxNumberOfRequestsPerSession, but it is
advisable that you'll look into reducing the number of remote calls first, since that will speed up your application significantly and result in a 
more responsive application.
");
        }

        /// <summary>
        /// Tracks the entity inside the unit of work
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="documentFound">The document found.</param>
        /// <returns></returns>
        public T TrackEntity<T>(JsonDocument documentFound)
        {
            return (T) TrackEntity(typeof(T), documentFound);
        }

        /// <summary>
        /// Tracks the entity.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="key">The key.</param>
        /// <param name="document">The document.</param>
        /// <param name="metadata">The metadata.</param>'
        /// <param name="noTracking"></param>
        /// <returns></returns>
        public T TrackEntity<T>(string key, RavenJObject document, RavenJObject metadata, bool noTracking)
        {
            var entity = TrackEntity(typeof(T), key, document, metadata, noTracking);
            try
            {
                return (T) entity;
            }
            catch (InvalidCastException e)
            {
                var actual = typeof(T).Name;
                var expected = entity.GetType().Name;
                var message = string.Format("The query results type is '{0}' but you expected to get results of type '{1}'. " +
                                            "If you want to return a projection, you should use .ProjectFromIndexFieldsInto<{1}>() (for Query) or .SelectFields<{1}>() (for DocumentQuery) before calling to .ToList().", expected, actual);
                throw new InvalidOperationException(message, e);
            }
        }

        /// <summary>
        /// Tracks the entity inside the unit of work
        /// </summary>
        /// <param name="entityType"></param>
        /// <param name="documentFound">The document found.</param>
        /// <returns></returns>
        public object TrackEntity(Type entityType, JsonDocument documentFound)
        {
            if (documentFound.Metadata.Value<bool?>(Constants.Headers.RavenDocumentDoesNotExists) == true)
            {
                return GetDefaultValue(entityType); // document is not really there.
            }
            if (documentFound.Etag != null && !documentFound.Metadata.ContainsKey("@etag"))
            {
                documentFound.Metadata["@etag"] = documentFound.Etag.ToString();
            }
            if (!documentFound.Metadata.ContainsKey(Constants.Headers.LastModified))
            {
                documentFound.Metadata[Constants.Headers.LastModified] = documentFound.LastModified;
            }

            return TrackEntity(entityType, documentFound.Key, documentFound.DataAsJson, documentFound.Metadata, noTracking: false);
        }

        /// <summary>
        /// Tracks the entity.
        /// </summary>
        /// <param name="entityType">The entity type.</param>
        /// <param name="key">The key.</param>
        /// <param name="document">The document.</param>
        /// <param name="metadata">The metadata.</param>
        /// <param name="noTracking">Entity tracking is enabled if true, disabled otherwise.</param>
        /// <returns></returns>
        object TrackEntity(Type entityType, string key, RavenJObject document, RavenJObject metadata, bool noTracking)
        {
            if (string.IsNullOrEmpty(key))
            {
                return JsonObjectToClrInstancesWithoutTracking(entityType, document);
            }
            document.Remove("@metadata");
            object entity;
            if (EntitiesById.TryGetValue(key, out entity) == false)
            {
                entity = ConvertToEntity(entityType, key, document, metadata);
            }
            else
            {
                // the local instance may have been changed, we adhere to the current Unit of Work
                // instance, and return that, ignoring anything new.
                return entity;
            }
            var etag = metadata.Value<string>("@etag");

            if (noTracking == false)
            {
                entitiesAndMetadata[entity] = new DocumentMetadata
                {
                    OriginalValue = document,
                    Metadata = metadata,
                    OriginalMetadata = (RavenJObject) metadata.CloneToken(),
                    ETag = HttpExtensions.EtagHeaderToEtag(etag),
                    Key = key
                };

                EntitiesById[key] = entity;
            }

            return entity;
        }

        /// <summary>
        /// Converts the json document to an entity.
        /// </summary>
        /// <param name="entityType"></param>
        /// <param name="id">The id.</param>
        /// <param name="documentFound">The document found.</param>
        /// <param name="metadata">The metadata.</param>
        /// <returns></returns>
        public object ConvertToEntity(Type entityType, string id, RavenJObject documentFound, RavenJObject metadata)
        {
            try
            {
                if (entityType == typeof(RavenJObject))
                    return documentFound.CloneToken();

                foreach (var extendedDocumentConversionListener in theListeners.ConversionListeners)
                {
                    extendedDocumentConversionListener.BeforeConversionToEntity(id, documentFound, metadata);
                }

                var defaultValue = GetDefaultValue(entityType);
                var entity = defaultValue;
                EnsureNotReadVetoed(metadata);

                IDisposable disposable = null;
                var defaultRavenContractResolver = Conventions.JsonContractResolver as DefaultRavenContractResolver;
                if (defaultRavenContractResolver != null && Conventions.PreserveDocumentPropertiesNotFoundOnModel)
                {
                    disposable = defaultRavenContractResolver.RegisterForExtensionData(RegisterMissingProperties);
                }

                using (disposable)
                {
                    var documentType = Conventions.GetClrType(id, documentFound, metadata);
                    if (documentType != null)
                    {
                        var type = Type.GetType(documentType);
                        if (type != null)
                            entity = documentFound.Deserialize(type, Conventions);
                    }

                    if (Equals(entity, defaultValue))
                    {
                        entity = documentFound.Deserialize(entityType, Conventions);
                        var document = entity as RavenJObject;
                        if (document != null)
                        {
                            entity = (object) (new DynamicJsonObject(document));
                        }
                    }
                    GenerateEntityIdOnTheClient.TrySetIdentity(entity, id);

                    foreach (var extendedDocumentConversionListener in theListeners.ConversionListeners)
                    {
                        extendedDocumentConversionListener.AfterConversionToEntity(id, documentFound, metadata, entity);
                    }

                    return entity;
                }
            }
            catch (ReadVetoException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Could not convert document " + id + " to entity of type " + entityType,
                    ex);
            }
        }

        /// <summary>
        /// Converts the json document to an entity.
        /// </summary>
        /// <param name="entityType"></param>
        /// <param name="id">The id.</param>
        /// <param name="documentFound">The document found.</param>
        /// <returns></returns>
        public object ConvertToEntity(Type entityType, string id, BlittableJsonReaderObject documentFound)
        {
            try
            {
                var defaultValue = GetDefaultValue(entityType);
                var entity = defaultValue;

                var documentType = Conventions.GetClrType(id, documentFound);
                if (documentType != null)
                {
                    var type = Type.GetType(documentType);
                    if (type != null)
                        entity = Conventions.JsonDeserialize(type, documentFound);
                }

                if (Equals(entity, defaultValue))
                {
                    entity = Conventions.JsonDeserialize(entityType, documentFound);
                }
                GenerateEntityIdOnTheClient.TrySetIdentity(entity, id);

                return entity;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Could not convert document {id} to entity of type {entityType}", ex);
            }
        }

        private void RegisterMissingProperties(object o, string key, object value)
        {
            Dictionary<string, JToken> dictionary;
            if (EntityToJson.MissingDictionary.TryGetValue(o, out dictionary) == false)
            {
                EntityToJson.MissingDictionary[o] = dictionary = new Dictionary<string, JToken>();
            }

            dictionary[key] = ConvertValueToJToken(value);
        }

        private JToken ConvertValueToJToken(object value)
        {
            var jToken = value as JToken;
            if (jToken != null)
                return jToken;

            try
            {
                // convert object value to JToken so it is compatible with dictionary
                // could happen because of primitive types, type name handling and references
                jToken = (value != null) ? JToken.FromObject(value) : JValue.CreateNull();
                return jToken;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("This is a bug. Value should be JToken.", ex);
            }
        }

        /// <summary>
        /// Gets the default value of the specified type.
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        static object GetDefaultValue(Type type)
        {
            return type.IsValueType() ? Activator.CreateInstance(type) : null;
        }

        /// <summary>
        /// Marks the specified entity for deletion. The entity will be deleted when SaveChanges is called.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="entity">The entity.</param>
        public void Delete<T>(T entity)
        {
            if (ReferenceEquals(entity, null))
                throw new ArgumentNullException("entity");

            try
            {
                string id;
                if (IdByEntities.TryGetValue(entity, out id) == false)
                {
                    throw new InvalidOperationException(entity + " is not associated with the session, cannot delete unknown entity instance");
                }
                DeletedEntities.Add(entity);
                KnownMissingIds.Add(id);
            }
            catch (Exception)
            {
                /*entitiesAndMetadata should be removed;*/
                DocumentMetadata value;
                if (entitiesAndMetadata.TryGetValue(entity, out value) == false)
                    throw new InvalidOperationException(entity + " is not associated with the session, cannot delete unknown entity instance");
                if (value.OriginalMetadata.ContainsKey(Constants.Headers.RavenReadOnly) && value.OriginalMetadata.Value<bool>(Constants.Headers.RavenReadOnly))
                    throw new InvalidOperationException(entity + " is marked as read only and cannot be deleted");
                DeletedEntities.Add(entity);
                KnownMissingIds.Add(value.Key);
            }
        }

        /// <summary>
        /// Marks the specified entity for deletion. The entity will be deleted when <see cref="IDocumentSession.SaveChanges"/> is called.
        /// WARNING: This method will not call beforeDelete listener!
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="id">The entity.</param>
        public void Delete<T>(ValueType id)
        {
            Delete(Conventions.FindFullDocumentKeyFromNonStringIdentifier(id, typeof(T), false));
        }

        /// <summary>
        /// Marks the specified entity for deletion. The entity will be deleted when <see cref="IDocumentSession.SaveChanges"/> is called.
        /// WARNING: This method will not call beforeDelete listener!
        /// </summary>
        /// <param name="id"></param>
        public void Delete(string id)
        {
            if (id == null) throw new ArgumentNullException("id");
            object entity;
            if (EntitiesById.TryGetValue(id, out entity))
            {
                if (EntityChanged(entity, entitiesAndMetadata[entity]))
                {
                    throw new InvalidOperationException("Can't delete changed entity using identifier. Use Delete<T>(T entity) instead.");
                }
                Delete(entity);
                return;
            }
            includedDocumentsByKey.Remove(id);
            KnownMissingIds.Add(id);

            Defer(new DeleteCommandData { Key = id });
        }

        internal void EnsureNotReadVetoed(RavenJObject metadata)
        {
            var readVeto = metadata["Raven-Read-Veto"] as RavenJObject;
            if (readVeto == null)
                return;

            var s = readVeto.Value<string>("Reason");
            throw new ReadVetoException(
                "Document could not be read because of a read veto." + Environment.NewLine +
                "The read was vetoed by: " + readVeto.Value<string>("Trigger") + Environment.NewLine +
                "Veto reason: " + s
                );
        }

        /// <summary>
        /// Stores the specified entity in the session. The entity will be saved when SaveChanges is called.
        /// </summary>
        public void Store(object entity)
        {
            string id;
            var hasId = GenerateEntityIdOnTheClient.TryGetIdFromInstance(entity, out id);
            StoreInternal(entity, null, null, forceConcurrencyCheck: hasId == false);
        }

        /// <summary>
        /// Stores the specified entity in the session. The entity will be saved when SaveChanges is called.
        /// </summary>
        public void Store(object entity, long? etag)
        {
            StoreInternal(entity, etag, null, forceConcurrencyCheck: true);
        }

        /// <summary>
        /// Stores the specified entity in the session, explicitly specifying its Id. The entity will be saved when SaveChanges is called.
        /// </summary>
        public void Store(object entity, string id)
        {
            StoreInternal(entity, null, id, forceConcurrencyCheck: false);
        }

        /// <summary>
        /// Stores the specified entity in the session, explicitly specifying its Id. The entity will be saved when SaveChanges is called.
        /// </summary>
        public void Store(object entity, long? etag, string id)
        {
            StoreInternal(entity, etag, id, forceConcurrencyCheck: true);
        }

        private void StoreInternal(object entity, long? etag, string id, bool forceConcurrencyCheck)
        {
            if (null == entity)
                throw new ArgumentNullException("entity");

            DocumentMetadata value;
            if (entitiesAndMetadata.TryGetValue(entity, out value))
            {
                value.ETag = etag ?? value.ETag;
                value.ForceConcurrencyCheck = forceConcurrencyCheck;
                return;
            }

            if (id == null)
            {
                if (GenerateDocumentKeysOnStore)
                {
                    id = GenerateEntityIdOnTheClient.GenerateDocumentKeyForStorage(entity);
                }
                else
                {
                    RememberEntityForDocumentKeyGeneration(entity);
                }
            }
            else
            {
                // Store it back into the Id field so the client has access to to it                    
                GenerateEntityIdOnTheClient.TrySetIdentity(entity, id);
            }

            if (deferedCommands.Any(c => c.Key == id))
                throw new InvalidOperationException("Can't store document, there is a deferred command registered for this document in the session. Document id: " + id);

            if (DeletedEntities.Contains(entity))
                throw new InvalidOperationException("Can't store object, it was already deleted in this session.  Document id: " + id);

            // we make the check here even if we just generated the key
            // users can override the key generation behavior, and we need
            // to detect if they generate duplicates.
            AssertNoNonUniqueInstance(entity, id);

            var metadata = new RavenJObject();
            var tag = documentStore.Conventions.GetDynamicTagName(entity);
            if (tag != null)
                metadata.Add(Constants.Headers.RavenEntityName, tag);
            if (id != null)
                KnownMissingIds.Remove(id);
            StoreEntityInUnitOfWork(id, entity, etag, metadata, forceConcurrencyCheck);
        }

        public Task StoreAsync(object entity, CancellationToken token = default(CancellationToken))
        {
            string id;
            var hasId = GenerateEntityIdOnTheClient.TryGetIdFromInstance(entity, out id);

            return StoreAsyncInternal(entity, null, null, forceConcurrencyCheck: hasId == false, token: token);
        }

        public Task StoreAsync(object entity, long? etag, CancellationToken token = default(CancellationToken))
        {
            return StoreAsyncInternal(entity, etag, null, forceConcurrencyCheck: true, token: token);
        }

        public Task StoreAsync(object entity, long? etag, string id, CancellationToken token = default(CancellationToken))
        {
            return StoreAsyncInternal(entity, etag, id, forceConcurrencyCheck: true, token: token);
        }

        public Task StoreAsync(object entity, string id, CancellationToken token = default(CancellationToken))
        {
            return StoreAsyncInternal(entity, null, id, forceConcurrencyCheck: false, token: token);
        }

        private async Task StoreAsyncInternal(object entity, long? etag, string id, bool forceConcurrencyCheck, CancellationToken token = default(CancellationToken))
        {
            if (null == entity)
                throw new ArgumentNullException("entity");

            if (id == null)
            {
                id = await GenerateDocumentKeyForStorageAsync(entity).WithCancellation(token).ConfigureAwait(false);
            }

            StoreInternal(entity, etag, id, forceConcurrencyCheck);
        }

        protected abstract string GenerateKey(object entity);

        protected virtual void RememberEntityForDocumentKeyGeneration(object entity)
        {
            throw new NotImplementedException("You cannot set GenerateDocumentKeysOnStore to false without implementing RememberEntityForDocumentKeyGeneration");
        }

        protected internal async Task<string> GenerateDocumentKeyForStorageAsync(object entity)
        {
            if (entity is IDynamicMetaObjectProvider)
            {
                string id;
                if (GenerateEntityIdOnTheClient.TryGetIdFromDynamic(entity, out id))
                    return id;

                var key = await GenerateKeyAsync(entity).ConfigureAwait(false);
                // If we generated a new id, store it back into the Id field so the client has access to to it                    
                if (key != null)
                    GenerateEntityIdOnTheClient.TrySetIdOnDynamic(entity, key);
                return key;
            }

            var result = await GetOrGenerateDocumentKeyAsync(entity).ConfigureAwait(false);
            GenerateEntityIdOnTheClient.TrySetIdentity(entity, result);
            return result;
        }

        protected abstract Task<string> GenerateKeyAsync(object entity);

        protected virtual void StoreEntityInUnitOfWork(string id, object entity, long? etag, RavenJObject metadata, bool forceConcurrencyCheck)
        {
            DeletedEntities.Remove(entity);
            if (id != null)
                KnownMissingIds.Remove(id);

            entitiesAndMetadata.Add(entity, new DocumentMetadata
            {
                Key = id,
                Metadata = metadata,
                OriginalMetadata = new RavenJObject(),
                ETag = etag,
                OriginalValue = new RavenJObject(),
                ForceConcurrencyCheck = forceConcurrencyCheck
            });
            if (id != null)
                EntitiesById[id] = entity;
        }

        protected virtual void AssertNoNonUniqueInstance(object entity, string id)
        {
            if (id == null || id.EndsWith("/") || !EntitiesById.ContainsKey(id) || ReferenceEquals(EntitiesById[id], entity))
                return;

            throw new NonUniqueObjectException("Attempted to associate a different object with id '" + id + "'.");
        }



        protected async Task<string> GetOrGenerateDocumentKeyAsync(object entity)
        {
            string id;
            GenerateEntityIdOnTheClient.TryGetIdFromInstance(entity, out id);

            Task<string> generator =
                id != null
                ? CompletedTask.With(id)
                : GenerateKeyAsync(entity);

            var result = await generator.ConfigureAwait(false);
            if (result != null && result.StartsWith("/"))
                throw new InvalidOperationException("Cannot use value '" + id + "' as a document id because it begins with a '/'");

            return result;
        }

        /// <summary>
        /// Creates the put entity command.
        /// </summary>
        /// <param name="entity">The entity.</param>
        /// <param name="documentMetadata">The document metadata.</param>
        /// <returns></returns>
        protected ICommandData CreatePutEntityCommand(object entity, DocumentMetadata documentMetadata)
        {
            string id;
            if (GenerateEntityIdOnTheClient.TryGetIdFromInstance(entity, out id) &&
                documentMetadata.Key != null &&
                documentMetadata.Key.Equals(id, StringComparison.OrdinalIgnoreCase) == false)
            {
                throw new InvalidOperationException("Entity " + entity.GetType().FullName + " had document key '" +
                                                    documentMetadata.Key + "' but now has document key property '" + id + "'." +
                                                    Environment.NewLine +
                                                    "You cannot change the document key property of a entity loaded into the session");
            }

            var json = EntityToJson.ConvertEntityToJson(documentMetadata.Key, entity, documentMetadata.Metadata);
            json[Constants.Metadata] = documentMetadata.Metadata.CloneToken();
            var etag = UseOptimisticConcurrency || documentMetadata.ForceConcurrencyCheck
                           ? (long?)(documentMetadata.ETag ?? 0)
                           : null;

            return new PutCommandData
            {
                Document = json,
                Etag = etag,
                Key = documentMetadata.Key,
            };
        }

        /// <summary>
        /// Updates the batch results.
        /// </summary>
        protected void UpdateBatchResults(IList<BatchResult> batchResults, SaveChangesData saveChangesData)
        {
            if (documentStore.HasJsonRequestFactory && Conventions.ShouldSaveChangesForceAggressiveCacheCheck && batchResults.Count != 0)
            {
                documentStore.JsonRequestFactory.ExpireItemsFromCache(DatabaseName ?? Constants.SystemDatabase);
            }

            for (var i = saveChangesData.DeferredCommandsCount; i < batchResults.Count; i++)
            {
                var batchResult = batchResults[i];
                if (batchResult.Method != "PUT")
                    continue;

                var entity = saveChangesData.Entities[i - saveChangesData.DeferredCommandsCount];
                DocumentMetadata documentMetadata;
                if (entitiesAndMetadata.TryGetValue(entity, out documentMetadata) == false)
                    continue;

                batchResult.Metadata["@etag"] = new RavenJValue(batchResult.Etag);
                EntitiesById[batchResult.Key] = entity;
                documentMetadata.ETag = batchResult.Etag;
                documentMetadata.Key = batchResult.Key;
                documentMetadata.OriginalMetadata = (RavenJObject)batchResult.Metadata.CloneToken();
                documentMetadata.Metadata = batchResult.Metadata;
                documentMetadata.OriginalValue = EntityToJson.ConvertEntityToJson(documentMetadata.Key, entity, documentMetadata.Metadata);

                GenerateEntityIdOnTheClient.TrySetIdentity(entity, batchResult.Key);

                foreach (var documentStoreListener in theListeners.StoreListeners)
                {
                    documentStoreListener.AfterStore(batchResult.Key, entity, batchResult.Metadata);
                }
            }

            var lastPut = batchResults.LastOrDefault(x => x.Method == "PUT");
            if (lastPut == null)
                return;

            documentStore.LastEtagHolder.UpdateLastWrittenEtag(lastPut.Etag);
        }

        /// <summary>
        /// Prepares for save changes.
        /// </summary>
        /// <returns></returns>
        protected SaveChangesData PrepareForSaveChanges()
        {
            EntityToJson.CachedJsonDocs.Clear();
            var result = new SaveChangesData
            {
                Entities = new List<object>(),
                Commands = new List<ICommandData>(deferedCommands),
                DeferredCommandsCount = deferedCommands.Count
            };
            deferedCommands.Clear();

            PrepareForEntitiesDeletion(result, null);
            PrepareForEntitiesPuts(result);

            return result;
        }

        public IDictionary<string, DocumentsChanges[]> WhatChanged()
        {
            using (EntityToJson.EntitiesToJsonCachingScope())
            {
                var changes = new Dictionary<string, DocumentsChanges[]>();
                PrepareForEntitiesDeletion(null, changes);
                GetAllEntitiesChanges(changes);
                return changes;
            }
        }

        private void PrepareForEntitiesPuts(SaveChangesData result)
        {
            foreach (var entity in entitiesAndMetadata.Where(pair => EntityChanged(pair.Key, pair.Value)).ToArray())
            {
                foreach (var documentStoreListener in theListeners.StoreListeners)
                {
                    if (documentStoreListener.BeforeStore(entity.Value.Key, entity.Key, entity.Value.Metadata, entity.Value.OriginalValue))
                        EntityToJson.CachedJsonDocs.Remove(entity.Key);
                }
                result.Entities.Add(entity.Key);
                if (entity.Value.Key != null)
                    EntitiesById.Remove(entity.Value.Key);
                result.Commands.Add(CreatePutEntityCommand(entity.Key, entity.Value));
            }
        }

        private void GetAllEntitiesChanges(IDictionary<string, DocumentsChanges[]> changes)
        {


            foreach (var pair in entitiesAndMetadata)
            {
                if (pair.Value.OriginalValue.Count == 0)
                {
                    var docChanges = new List<DocumentsChanges>() { };
                    var change = new DocumentsChanges()
                    {

                        Change = DocumentsChanges.ChangeType.DocumentAdded
                    };

                    docChanges.Add(change);
                    changes[pair.Value.Key] = docChanges.ToArray();
                    continue;

                }
                EntityChanged(pair.Key, pair.Value, changes);
            }

        }

        private void PrepareForEntitiesDeletion(SaveChangesData result, IDictionary<string, DocumentsChanges[]> changes)
        {
            string id = null;
            var keysToDelete = DeletedEntities.Where(deletedEntity => IdByEntities.TryGetValue(deletedEntity, out id))
                .Select(deletedEntity => id)
                .ToList();

            foreach (var key in keysToDelete)
            {
                if (changes != null)
                {
                    var docChanges = new List<DocumentsChanges>() { };
                    var change = new DocumentsChanges()
                    {
                        FieldNewValue = string.Empty,
                        FieldOldValue = string.Empty,
                        Change = DocumentsChanges.ChangeType.DocumentDeleted
                    };

                    docChanges.Add(change);
                    changes[key] = docChanges.ToArray();
                }
                else
                {
                    long? etag = null;
                    object existingEntity;
                    DocumentMetadata metadata = null;
                    if (EntitiesById.TryGetValue(key, out existingEntity))
                    {
                        if (entitiesAndMetadata.TryGetValue(existingEntity, out metadata))
                            etag = metadata.ETag;
                        entitiesAndMetadata.Remove(existingEntity);
                        EntitiesById.Remove(key);
                    }

                    etag = UseOptimisticConcurrency ? etag : null;
                    result.Entities.Add(existingEntity);

                    foreach (var deleteListener in theListeners.DeleteListeners)
                    {
                        deleteListener.BeforeDelete(key, existingEntity, metadata != null ? metadata.Metadata : null);
                    }

                    result.Commands.Add(new DeleteCommandData
                    {
                        Etag = etag,
                        Key = key,
                    });
                }

            }
            if (changes == null)
                DeletedEntities.Clear();
        }

        /// <summary>
        /// Mark the entity as read only, change tracking won't apply 
        /// to such an entity. This can be done as an optimization step, so 
        /// we don't need to check the entity for changes.
        /// This flag is persisted in the document metadata and subsequent modifications of the document will not be possible.
        /// If you want the session to ignore this entity, consider using the Evict() method.
        /// </summary>
        public void MarkReadOnly(object entity)
        {
            GetMetadataFor(entity)[Constants.Headers.RavenReadOnly] = true;
        }

        /// <summary>
        /// Mark the entity as one that should be ignore for change tracking purposes,
        /// it still takes part in the session, but is ignored for SaveChanges.
        /// </summary>
        public void IgnoreChangesFor(object entity)
        {
            GetDocumentMetadata(entity).IgnoreChanges = true;
        }


        /// <summary>
        /// Determines if the entity have changed.
        /// </summary>
        /// <param name="entity">The entity.</param>
        /// <param name="documentMetadata">The document metadata.</param>
        /// <param name="changes">The dictionary of changes.</param>
        protected bool EntityChanged(object entity, DocumentMetadata documentMetadata, IDictionary<string, DocumentsChanges[]> changes = null)
        {
            if (documentMetadata == null)
                return true;

            if (documentMetadata.IgnoreChanges)
                return false;

            string id;
            if (GenerateEntityIdOnTheClient.TryGetIdFromInstance(entity, out id) &&
                string.Equals(documentMetadata.Key, id, StringComparison.OrdinalIgnoreCase) == false)
                return true;

            // prevent saves of a modified read only entity
            if (documentMetadata.OriginalMetadata.ContainsKey(Constants.Headers.RavenReadOnly) &&
                documentMetadata.OriginalMetadata.Value<bool>(Constants.Headers.RavenReadOnly) &&
                documentMetadata.Metadata.ContainsKey(Constants.Headers.RavenReadOnly) &&
                documentMetadata.Metadata.Value<bool>(Constants.Headers.RavenReadOnly))
                return false;

            var newObj = EntityToJson.ConvertEntityToJson(documentMetadata.Key, entity, documentMetadata.Metadata);
            var changedData = changes != null ? new List<DocumentsChanges>() : null;

            var isObjectEquals = RavenJToken.DeepEquals(newObj, documentMetadata.OriginalValue, changedData);
            var isMetadataEquals = RavenJToken.DeepEquals(documentMetadata.Metadata, documentMetadata.OriginalMetadata, changedData);

            var changed = (isObjectEquals == false) || (isMetadataEquals == false);

            if (changes != null && changedData.Count > 0)
                changes[documentMetadata.Key] = changedData.ToArray();

            return changed;
        }

        /// <summary>
        /// Evicts the specified entity from the session.
        /// Remove the entity from the delete queue and stops tracking changes for this entity.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="entity">The entity.</param>
        public void Evict<T>(T entity)
        {
            DocumentMetadata value;
            if (entitiesAndMetadata.TryGetValue(entity, out value))
            {
                entitiesAndMetadata.Remove(entity);
                EntitiesById.Remove(value.Key);
            }
            string id;
            if (IdByEntities.TryGetValue(entity, out id))
            {
                EntitiesById.Remove(id);
            }
            DeletedEntities.Remove(entity);
        }

        /// <summary>
        /// Clears this instance.
        /// Remove all entities from the delete queue and stops tracking changes for all entities.
        /// </summary>
        public void Clear()
        {
            entitiesAndMetadata.Clear();
            DeletedEntities.Clear();
            EntitiesById.Clear();
            IdByEntities.Clear();
            KnownMissingIds.Clear();
        }

        private readonly List<ICommandData> deferedCommands = new List<ICommandData>();
        protected string _databaseName;
        public GenerateEntityIdOnTheClient GenerateEntityIdOnTheClient { get; private set; }
        public EntityToJson EntityToJson { get; private set; }

        /// <summary>
        /// Defer commands to be executed on SaveChanges()
        /// </summary>
        /// <param name="commands">The commands to be executed</param>
        public virtual void Defer(params ICommandData[] commands)
        {
            deferedCommands.AddRange(commands);
        }

        /// <summary>
        /// Version this entity when it is saved.  Use when Versioning bundle configured to ExcludeUnlessExplicit.
        /// </summary>
        /// <param name="entity">The entity.</param>
        public void ExplicitlyVersion(object entity)
        {
            var metadata = GetMetadataFor(entity);

            metadata[Constants.Versioning.RavenEnableVersioning] = true;
        }

        private void Dispose(bool isDisposing)
        {
            if (isDisposing)
                GC.SuppressFinalize(this);
            _releaseOperationContext.Dispose();
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public virtual void Dispose()
        {
            Dispose(true);
        }

        ~InMemoryDocumentSessionOperations()
        {
            Dispose(false);

#if DEBUG
            Debug.WriteLine("Disposing a session for finalizer! It should be disposed by calling session.Dispose()!");
#endif
        }

        /// <summary>
        /// Metadata held about an entity by the session
        /// </summary>
        public class DocumentMetadata
        {
            /// <summary>
            /// Gets or sets the original value.
            /// </summary>
            /// <value>The original value.</value>
            public RavenJObject OriginalValue { get; set; }
            /// <summary>
            /// Gets or sets the metadata.
            /// </summary>
            /// <value>The metadata.</value>
            public RavenJObject Metadata { get; set; }
            /// <summary>
            /// Gets or sets the ETag.
            /// </summary>
            /// <value>The ETag.</value>
            public long? ETag { get; set; }
            /// <summary>
            /// Gets or sets the key.
            /// </summary>
            /// <value>The key.</value>
            public string Key { get; set; }
            /// <summary>
            /// Gets or sets the original metadata.
            /// </summary>
            /// <value>The original metadata.</value>
            public RavenJObject OriginalMetadata { get; set; }

            /// <summary>
            /// A concurrency check will be forced on this entity 
            /// even if UseOptimisticConcurrency is set to false
            /// </summary>
            public bool ForceConcurrencyCheck { get; set; }

            /// <summary>
            /// If set to true, the session will ignore this document
            /// when SaveChanges() is called, and won't perform and change tracking
            /// </summary>
            public bool IgnoreChanges { get; set; }
        }

        /// <summary>
        /// Data for a batch command to the server
        /// </summary>
        public class SaveChangesData
        {
            public SaveChangesData()
            {
                Commands = new List<ICommandData>();
                Entities = new List<object>();
            }

            /// <summary>
            /// Gets or sets the commands.
            /// </summary>
            /// <value>The commands.</value>
            public List<ICommandData> Commands { get; set; }

            public int DeferredCommandsCount { get; set; }

            /// <summary>
            /// Gets or sets the entities.
            /// </summary>
            /// <value>The entities.</value>
            public IList<object> Entities { get; set; }

        }

        protected void LogBatch(SaveChangesData data)
        {
            if (log.IsDebugEnabled)
            {
                var sb = new StringBuilder()
                    .AppendFormat("Saving {0} changes to {1}", data.Commands.Count, StoreIdentifier)
                    .AppendLine();
                foreach (var commandData in data.Commands)
                {
                    sb.AppendFormat("\t{0} {1}", commandData.Method, commandData.Key).AppendLine();
                }
                log.Debug(sb.ToString());
            }
        }

        public void RegisterMissing(string id)
        {
            KnownMissingIds.Add(id);
        }
        public void UnregisterMissing(string id)
        {
            KnownMissingIds.Remove(id);
        }

        public void RegisterMissingIncludes(IEnumerable<RavenJObject> results, ICollection<string> includes)
        {
            if (includes == null || includes.Any() == false)
                return;

            foreach (var result in results)
            {
                foreach (var include in includes)
                {
                    IncludesUtil.Include(result, include, id =>
                    {
                        if (id == null)
                            return false;
                        if (IsLoaded(id) == false)
                        {
                            RegisterMissing(id);
                            return false;
                        }
                        return true;
                    });
                }
            }
        }

        public override int GetHashCode()
        {
            return _hash;
        }

        public override bool Equals(object obj)
        {
            return ReferenceEquals(obj, this);
        }

        internal void HandleInternalMetadata(RavenJObject result)
        {
            // Implant a property with "id" value ... if not exists
            var metadata = result.Value<RavenJObject>("@metadata");
            if (metadata == null || string.IsNullOrEmpty(metadata.Value<string>("@id")))
            {
                // if the item has metadata, then nested items will not have it, so we can skip recursing down
                foreach (var nested in result.Select(property => property.Value))
                {
                    var jObject = nested as RavenJObject;
                    if (jObject != null)
                        HandleInternalMetadata(jObject);
                    var jArray = nested as RavenJArray;
                    if (jArray == null)
                        continue;
                    foreach (var item in jArray.OfType<RavenJObject>())
                    {
                        HandleInternalMetadata(item);
                    }
                }
                return;
            }

            var entityName = metadata.Value<string>(Constants.Headers.RavenEntityName);

            var idPropName = Conventions.FindIdentityPropertyNameFromEntityName(entityName);
            if (result.ContainsKey(idPropName))
                return;

            result[idPropName] = new RavenJValue(metadata.Value<string>("@id"));
        }

        protected object JsonObjectToClrInstancesWithoutTracking(Type type, RavenJObject val)
        {
            if (val == null)
                return null;
            if (type.IsArray)
            {
                // Returns array, public APIs don't surface that yet though as we only support Transform
                // With a single Id
                var elementType = type.GetElementType();
                var array = val.Value<RavenJArray>("$values").Cast<RavenJObject>()
                               .Where(x => x != null)
                               .Select(y =>
                               {
                                   HandleInternalMetadata(y);

                                   return ProjectionToInstance(y, elementType);
                               })
                               .ToArray();

                var newArray = Array.CreateInstance(elementType, array.Length);
                Array.Copy(array, newArray, array.Length);
                return newArray;
            }

            var items = (val.Value<RavenJArray>("$values") ?? new RavenJArray(val))
                .Select(JsonExtensions.ToJObject)
                .Where(x => x != null)
                .Select(x =>
                {
                    HandleInternalMetadata(x);
                    return ProjectionToInstance(x, type);
                })
                .ToArray();

            if (items.Length == 1)
                return items[0];

            return items;
        }

        internal object ProjectionToInstance(RavenJObject y, Type type)
        {
            HandleInternalMetadata(y);
            foreach (var conversionListener in theListeners.ConversionListeners)
            {
                conversionListener.BeforeConversionToEntity(null, y, null);
            }
            var instance = y.Deserialize(type, Conventions);

            foreach (var conversionListener in theListeners.ConversionListeners)
            {
                conversionListener.AfterConversionToEntity(null, y, null, instance);
            }
            return instance;
        }

        public void TrackIncludedDocument(JsonDocument include)
        {
            includedDocumentsByKey[include.Key] = include;
        }

        public string CreateDynamicIndexName<T>()
        {
            var indexName = "dynamic";
            if (typeof(T).IsEntityType())
            {
                indexName += "/" + Conventions.GetTypeTagName(typeof(T));
            }
            return indexName;
        }

        public bool CheckIfIdAlreadyIncluded(string[] ids, KeyValuePair<string, Type>[] includes)
        {
            foreach (var id in ids)
            {
                if (KnownMissingIds.Contains(id))
                    continue;

                object data;
                if (EntitiesById.TryGetValue(id, out data) == false)
                    return false;
                DocumentMetadata value;
                if (entitiesAndMetadata.TryGetValue(data, out value) == false)
                    return false;
                foreach (var include in includes)
                {
                    var hasAll = true;
                    IncludesUtil.Include(value.OriginalValue, include.Key, s =>
                    {
                        hasAll &= IsLoaded(s);
                        return true;
                    });
                    if (hasAll == false)
                        return false;
                }
            }
            return true;
        }

        protected void RefreshInternal<T>(T entity, JsonDocument jsonDocument, DocumentMetadata value)
        {
            if (jsonDocument == null)
                throw new InvalidOperationException("Document '" + value.Key + "' no longer exists and was probably deleted");

            value.Metadata = jsonDocument.Metadata;
            value.OriginalMetadata = (RavenJObject)jsonDocument.Metadata.CloneToken();
            value.ETag = jsonDocument.Etag;
            value.OriginalValue = jsonDocument.DataAsJson;
            var newEntity = ConvertToEntity(typeof(T), value.Key, jsonDocument.DataAsJson, jsonDocument.Metadata);
            var type = entity.GetType();
            foreach (var property in ReflectionUtil.GetPropertiesAndFieldsFor(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var prop = property;

                if (prop.DeclaringType != type && prop.DeclaringType != null)
                    prop = prop.DeclaringType.GetProperty(prop.Name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ?? property;

                if (!prop.CanWrite() || !prop.CanRead() || prop.GetIndexParameters().Length != 0)
                    continue;
                prop.SetValue(entity, prop.GetValue(newEntity));
            }
        }

        protected static T GetOperationResult<T>(object result)
        {
            if (result == null)
                return default(T);

            if (result is T)
                return (T)result;

            var results = result as T[];
            if (results != null && results.Length > 0)
                return results[0];

            throw new InvalidCastException($"Unable to cast {result.GetType().Name} to {typeof(T).Name}");
        }
    }
}