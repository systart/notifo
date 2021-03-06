﻿// ==========================================================================
//  Notifo.io
// ==========================================================================
//  Copyright (c) Sebastian Stehle
//  All rights reserved. Licensed under the MIT license.
// ==========================================================================

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using NodaTime;
using Notifo.Infrastructure;
using Notifo.Infrastructure.Initialization;
using Notifo.Infrastructure.MongoDb;

namespace Notifo.Domain.UserNotifications.MongoDb
{
    public sealed class MongoDbUserNotificationRepository : MongoDbRepository<UserNotification>, IUserNotificationRepository, IInitializable
    {
        static MongoDbUserNotificationRepository()
        {
            BsonClassMap.RegisterClassMap<UserNotification>(cm =>
            {
                cm.AutoMap();

                cm.MapIdProperty(x => x.Id)
                    .SetSerializer(new GuidSerializer(BsonType.String));

                cm.MapProperty(x => x.IsSeen)
                    .SetIgnoreIfNull(true);

                cm.MapProperty(x => x.IsConfirmed)
                    .SetIgnoreIfNull(true);
            });
        }

        public MongoDbUserNotificationRepository(IMongoDatabase database)
            : base(database)
        {
        }

        protected override string CollectionName()
        {
            return "Notifications";
        }

        protected override async Task SetupCollectionAsync(IMongoCollection<UserNotification> collection, CancellationToken ct)
        {
            await Collection.Indexes.CreateOneAsync(
                new CreateIndexModel<UserNotification>(
                    IndexKeys
                        .Ascending(x => x.UserId)
                        .Ascending(x => x.EventId)),
                null, ct);

            await Collection.Indexes.CreateOneAsync(
                new CreateIndexModel<UserNotification>(
                    IndexKeys
                        .Ascending(x => x.AppId)
                        .Ascending(x => x.UserId)
                        .Ascending(x => x.Updated)
                        .Descending(x => x.Created)),
                null, ct);
        }

        public async Task<bool> IsConfirmedOrHandled(Guid id, string channel)
        {
            var filter =
               Filter.And(
                   Filter.Eq(x => x.Id, id),
                   Filter.Ne($"Sending.{channel}.Status", ProcessStatus.Attempt),
                   Filter.Exists(x => x.IsConfirmed, false));

            var count =
                await Collection.Find(filter).Limit(1)
                    .CountDocumentsAsync();

            return count == 1;
        }

        public Task<List<UserNotification>> QueryAsync(string appId, string userId, int count, Instant after, CancellationToken ct)
        {
            return Collection.Find(x => x.AppId == appId && x.UserId == userId && x.Updated > after).SortByDescending(x => x.Created).Limit(count)
                .ToListAsync(ct);
        }

        public async Task<UserNotification?> FindAsync(Guid id)
        {
            var entity = await Collection.Find(x => x.Id == id).FirstOrDefaultAsync();

            return entity;
        }

        public async Task TrackSeenAsync(IEnumerable<Guid> ids, HandledInfo handle)
        {
            var writes = new List<WriteModel<UserNotification>>();

            foreach (var id in ids)
            {
                writes.Add(new UpdateOneModel<UserNotification>(
                    Filter.And(
                        Filter.Eq(x => x.Id, id),
                        Filter.Exists(x => x.IsSeen, false)),
                    Update
                        .Set(x => x.IsSeen, handle)
                        .Set(x => x.Updated, handle.Timestamp)));

                writes.Add(new UpdateOneModel<UserNotification>(
                    Filter.And(
                        Filter.Eq(x => x.Id, id),
                        Filter.Eq(x => x.Formatting.ConfirmMode, ConfirmMode.Seen),
                        Filter.Exists(x => x.IsConfirmed, false)),
                    Update
                        .Set(x => x.IsConfirmed, handle)
                        .Set(x => x.Updated, handle.Timestamp)));
            }

            if (writes.Count == 0)
            {
                return;
            }

            await Collection.BulkWriteAsync(writes);
        }

        public async Task<UserNotification?> TrackConfirmedAsync(Guid id, HandledInfo handle)
        {
            var entity =
                await Collection.FindOneAndUpdateAsync(
                    Filter.And(
                        Filter.Eq(x => x.Id, id),
                        Filter.Eq(x => x.Formatting.ConfirmMode, ConfirmMode.Explicit),
                        Filter.Exists(x => x.IsConfirmed, false)),
                    Update
                        .Set(x => x.IsConfirmed, handle)
                        .Set(x => x.Updated, handle.Timestamp));

            if (entity != null)
            {
                entity.IsConfirmed = handle;

                entity.Updated = handle.Timestamp;
            }

            return entity;
        }

        public async Task UpdateAsync(IEnumerable<(Guid Id, string Channel, ChannelSendInfo Info)> updates, CancellationToken ct)
        {
            var writes = new List<WriteModel<UserNotification>>();

            foreach (var (id, channel, info) in updates)
            {
                writes.Add(new UpdateOneModel<UserNotification>(
                    Filter.Eq(x => x.Id, id),
                    Update
                        .Set($"Sending.{channel}.Detail", info.Detail)
                        .Set($"Sending.{channel}.Status", info.Status)
                        .Set($"Sending.{channel}.LastUpdate", info.LastUpdate)));
            }

            if (writes.Count == 0)
            {
                return;
            }

            await Collection.BulkWriteAsync(writes, cancellationToken: ct);
        }

        public async Task InsertAsync(UserNotification notification, CancellationToken ct)
        {
            try
            {
                await Collection.InsertOneAsync(notification, null, ct);
            }
            catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                throw new UniqueConstraintException();
            }
        }
    }
}
