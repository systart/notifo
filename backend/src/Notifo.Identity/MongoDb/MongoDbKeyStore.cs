﻿// ==========================================================================
//  Notifo.io
// ==========================================================================
//  Copyright (c) Sebastian Stehle
//  All rights reserved. Licensed under the MIT license.
// ==========================================================================

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading.Tasks;
using IdentityModel;
using IdentityServer4.Models;
using IdentityServer4.Stores;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Driver;
using Notifo.Infrastructure.MongoDb;

namespace Notifo.Identity.MongoDb
{
    public sealed class MongoDbKeyStore : MongoDbRepository<MongoDbKey>, ISigningCredentialStore, IValidationKeysStore
    {
        private SigningCredentials? cachedKey;
        private SecurityKeyInfo[]? cachedKeyInfo;

        public MongoDbKeyStore(IMongoDatabase database)
            : base(database)
        {
        }

        protected override string CollectionName()
        {
            return "Identity_Key";
        }

        public async Task<SigningCredentials> GetSigningCredentialsAsync()
        {
            var (_, key) = await GetOrCreateKeyAsync();

            return key;
        }

        public async Task<IEnumerable<SecurityKeyInfo>> GetValidationKeysAsync()
        {
            var (info, _) = await GetOrCreateKeyAsync();

            return info;
        }

        private async Task<(SecurityKeyInfo[], SigningCredentials)> GetOrCreateKeyAsync()
        {
            if (cachedKey != null && cachedKeyInfo != null)
            {
                return (cachedKeyInfo, cachedKey);
            }

            var key = await Collection.Find(x => x.Id == "Default").FirstOrDefaultAsync();

            RsaSecurityKey securityKey;

            if (key == null)
            {
                securityKey = new RsaSecurityKey(RSA.Create(2048))
                {
                    KeyId = CryptoRandom.CreateUniqueId(16)
                };

                key = new MongoDbKey { Id = "Default", Key = securityKey.KeyId };

                if (securityKey.Rsa != null)
                {
                    var parameters = securityKey.Rsa.ExportParameters(includePrivateParameters: true);

                    key.Parameters = MongoDbKeyParameters.Create(parameters);
                }
                else
                {
                    key.Parameters = MongoDbKeyParameters.Create(securityKey.Parameters);
                }

                try
                {
                    await Collection.InsertOneAsync(key);

                    return CreateCredentialsPair(securityKey);
                }
                catch (MongoWriteException ex)
                {
                    if (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
                    {
                        key = await Collection.Find(x => x.Id == "Default").FirstOrDefaultAsync();
                    }
                    else
                    {
                        throw;
                    }
                }
            }

            if (key == null)
            {
                throw new InvalidOperationException("Cannot read key.");
            }

            securityKey = new RsaSecurityKey(key.Parameters.ToParameters())
            {
                KeyId = key.Key
            };

            return CreateCredentialsPair(securityKey);
        }

        private (SecurityKeyInfo[], SigningCredentials) CreateCredentialsPair(RsaSecurityKey securityKey)
        {
            cachedKey = new SigningCredentials(securityKey, SecurityAlgorithms.RsaSha256);

            cachedKeyInfo = new[]
            {
                new SecurityKeyInfo { Key = cachedKey.Key, SigningAlgorithm = cachedKey.Algorithm }
            };

            return (cachedKeyInfo, cachedKey);
        }
    }
}
