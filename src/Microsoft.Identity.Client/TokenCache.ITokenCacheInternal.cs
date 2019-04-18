﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Identity.Client.Cache;
using Microsoft.Identity.Client.Cache.Items;
using Microsoft.Identity.Client.Cache.Keys;
using Microsoft.Identity.Client.Core;
using Microsoft.Identity.Client.Instance;
using Microsoft.Identity.Client.Internal.Requests;
using Microsoft.Identity.Client.Mats.Internal.Events;
using Microsoft.Identity.Client.OAuth2;
using Microsoft.Identity.Client.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Microsoft.Identity.Client
{
    public sealed partial class TokenCache : ITokenCacheInternal
    {
        async Task<Tuple<MsalAccessTokenCacheItem, MsalIdTokenCacheItem>> ITokenCacheInternal.SaveTokenResponseAsync(
            AuthenticationRequestParameters requestParams,
            MsalTokenResponse response)
        {
            var tenantId = Authority
                .CreateAuthority(ServiceBundle, requestParams.TenantUpdatedCanonicalAuthority)
                .GetTenantId();

            IdToken idToken = IdToken.Parse(response.IdToken);

            // The preferred_username value cannot be null or empty in order to comply with the ADAL/MSAL Unified cache schema.
            // It will be set to "preferred_username not in idtoken"
            var preferredUsername = !string.IsNullOrWhiteSpace(idToken?.PreferredUsername)
                ? idToken.PreferredUsername
                : NullPreferredUsernameDisplayLabel;

            var instanceDiscoveryMetadataEntry = GetCachedAuthorityMetaData(requestParams.TenantUpdatedCanonicalAuthority);

            var environmentAliases = GetEnvironmentAliases(
                requestParams.TenantUpdatedCanonicalAuthority,
                instanceDiscoveryMetadataEntry);

            var preferredEnvironmentHost = GetPreferredEnvironmentHost(
                requestParams.AuthorityInfo.Host,
                instanceDiscoveryMetadataEntry);

            var msalAccessTokenCacheItem =
                new MsalAccessTokenCacheItem(preferredEnvironmentHost, requestParams.ClientId, response, tenantId)
                {
                    UserAssertionHash = requestParams.UserAssertion?.AssertionHash
                };

            MsalRefreshTokenCacheItem msalRefreshTokenCacheItem = null;
            MsalIdTokenCacheItem msalIdTokenCacheItem = null;
            if (idToken != null)
            {
                msalIdTokenCacheItem = new MsalIdTokenCacheItem(
                    preferredEnvironmentHost,
                    requestParams.ClientId,
                    response,
                    tenantId);
            }

            await _semaphoreSlim.WaitAsync().ConfigureAwait(false);
            try
            {
                try
                {
                    var args = new TokenCacheNotificationArgs
                    {
                        TokenCache = new NoLockTokenCacheProxy(this),
                        ClientId = ClientId,
                        Account = msalAccessTokenCacheItem.HomeAccountId != null
                            ? new Account(msalAccessTokenCacheItem.HomeAccountId, preferredUsername, preferredEnvironmentHost)
                            : null,
                        HasStateChanged = true
                    };

#pragma warning disable CS0618 // Type or member is obsolete
                    HasStateChanged = true;
#pragma warning restore CS0618 // Type or member is obsolete

                    await OnBeforeAccessAsync(args).ConfigureAwait(false);
                    try
                    {
                        await OnBeforeWriteAsync(args).ConfigureAwait(false);

                        DeleteAccessTokensWithIntersectingScopes(
                            requestParams,
                            environmentAliases,
                            tenantId,
                            msalAccessTokenCacheItem.ScopeSet,
                            msalAccessTokenCacheItem.HomeAccountId);

                        _accessor.SaveAccessToken(msalAccessTokenCacheItem);

                        if (idToken != null)
                        {
                            _accessor.SaveIdToken(msalIdTokenCacheItem);
                            var msalAccountCacheItem = new MsalAccountCacheItem(
                                preferredEnvironmentHost,
                                response,
                                preferredUsername,
                                tenantId);

                            _accessor.SaveAccount(msalAccountCacheItem);
                        }

                        // if server returns the refresh token back, save it in the cache.
                        if (response.RefreshToken != null)
                        {
                            msalRefreshTokenCacheItem = new MsalRefreshTokenCacheItem(
                                preferredEnvironmentHost,
                                requestParams.ClientId,
                                response);

                            if (!_featureFlags.IsFociEnabled)
                            {
                                msalRefreshTokenCacheItem.FamilyId = null;
                            }

                            requestParams.RequestContext.Logger.Info("Saving RT in cache...");
                            _accessor.SaveRefreshToken(msalRefreshTokenCacheItem);
                        }

                        UpdateAppMetadata(requestParams.ClientId, preferredEnvironmentHost, response.FamilyId);

                        // save RT in ADAL cache for public clients
                        // do not save RT in ADAL cache for MSAL B2C scenarios
                        if (!requestParams.IsClientCredentialRequest && !requestParams.AuthorityInfo.AuthorityType.Equals(AuthorityType.B2C))
                        {
                            CacheFallbackOperations.WriteAdalRefreshToken(
                                Logger,
                                LegacyCachePersistence,
                                msalRefreshTokenCacheItem,
                                msalIdTokenCacheItem,
                                Authority.CreateAuthorityUriWithHost(
                                    requestParams.TenantUpdatedCanonicalAuthority,
                                    preferredEnvironmentHost),
                                msalIdTokenCacheItem.IdToken.ObjectId, response.Scope);
                        }

                    }
                    finally
                    {
                        await OnAfterAccessAsync(args).ConfigureAwait(false);
                    }

                    return Tuple.Create(msalAccessTokenCacheItem, msalIdTokenCacheItem);
                }
                finally
                {
#pragma warning disable CS0618 // Type or member is obsolete
                    HasStateChanged = false;
#pragma warning restore CS0618 // Type or member is obsolete
                }
            }
            finally
            {
                _semaphoreSlim.Release();
            }
        }

        async Task<MsalAccessTokenCacheItem> ITokenCacheInternal.FindAccessTokenAsync(AuthenticationRequestParameters requestParams)
        {
            ISet<string> environmentAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string preferredEnvironmentAlias = null;

            if (requestParams.AuthorityInfo != null)
            {
                var instanceDiscoveryMetadataEntry = await GetCachedOrDiscoverAuthorityMetaDataAsync(
                    requestParams.AuthorityInfo.CanonicalAuthority,
                    requestParams.RequestContext).ConfigureAwait(false);

                environmentAliases.UnionWith(GetEnvironmentAliases(
                    requestParams.AuthorityInfo.CanonicalAuthority,
                    instanceDiscoveryMetadataEntry));

                if (requestParams.AuthorityInfo.AuthorityType != AuthorityType.B2C)
                {
                    preferredEnvironmentAlias = instanceDiscoveryMetadataEntry.PreferredCache;
                }
            }

            // no authority passed
            if (!environmentAliases.Any())
            {
                requestParams.RequestContext.Logger.Warning("No authority provided. Skipping cache lookup ");
                return null;
            }

            await _semaphoreSlim.WaitAsync().ConfigureAwait(false);
            try
            {
                requestParams.RequestContext.Logger.Info("Looking up access token in the cache.");
                MsalAccessTokenCacheItem msalAccessTokenCacheItem;
                TokenCacheNotificationArgs args = new TokenCacheNotificationArgs
                {
                    TokenCache = new NoLockTokenCacheProxy(this),
                    ClientId = ClientId,
                    Account = requestParams.Account
                };

                List<MsalAccessTokenCacheItem> tokenCacheItems;

                await OnBeforeAccessAsync(args).ConfigureAwait(false);
                try
                {
                    // filtered by client id.
                    tokenCacheItems = GetAllAccessTokensWithNoLocks(true).ToList();
                }
                finally
                {
                    await OnAfterAccessAsync(args).ConfigureAwait(false);
                }

                // this is OBO flow. match the cache entry with assertion hash,
                // Authority, ScopeSet and client Id.
                if (requestParams.UserAssertion != null)
                {
                    requestParams.RequestContext.Logger.Info("Filtering by user assertion...");
                    tokenCacheItems =
                        tokenCacheItems.Where(
                                item =>
                                    !string.IsNullOrEmpty(item.UserAssertionHash) &&
                                    item.UserAssertionHash.Equals(requestParams.UserAssertion.AssertionHash, StringComparison.OrdinalIgnoreCase))
                            .ToList();
                }
                else
                {
                    if (!requestParams.IsClientCredentialRequest)
                    {
                        requestParams.RequestContext.Logger.Info("Filtering by user identifier...");
                        // filter by identifier of the user instead
                        tokenCacheItems =
                            tokenCacheItems
                                .Where(item => item.HomeAccountId.Equals(requestParams.Account?.HomeAccountId.Identifier, StringComparison.OrdinalIgnoreCase))
                                .ToList();
                    }

                    tokenCacheItems = FilterToTenantIdSpecifiedByAuthenticationRequest(requestParams, tokenCacheItems).ToList();
                }

                // no match found after initial filtering
                if (!tokenCacheItems.Any())
                {
                    requestParams.RequestContext.Logger.Info("No matching entry found for user or assertion");
                    return null;
                }

                requestParams.RequestContext.Logger.Info("Matching entry count -" + tokenCacheItems.Count);

                IEnumerable<MsalAccessTokenCacheItem> filteredItems =
                    tokenCacheItems.Where(item => ScopeHelper.ScopeContains(item.ScopeSet, requestParams.Scope)).ToList();

                requestParams.RequestContext.Logger.Info("Matching entry count after filtering by scopes - " + filteredItems.Count());

                // filter by authority
                var filteredByPreferredAlias =
                    filteredItems.Where
                    (item => item.Environment.Equals(preferredEnvironmentAlias, StringComparison.OrdinalIgnoreCase)).ToList();

                if (filteredByPreferredAlias.Any())
                {
                    filteredItems = filteredByPreferredAlias;
                }
                else
                {
                    filteredItems = filteredItems.Where(
                        item => environmentAliases.Contains(item.Environment) &&
                        item.TenantId.Equals(requestParams.Authority.GetTenantId(), StringComparison.OrdinalIgnoreCase));
                }

                // no match
                if (!filteredItems.Any())
                {
                    requestParams.RequestContext.Logger.Info("No tokens found for matching authority, client_id, user and scopes.");
                    return null;
                }

                // if only one cached token found
                if (filteredItems.Count() == 1)
                {
                    msalAccessTokenCacheItem = filteredItems.First();
                }
                else
                {
                    requestParams.RequestContext.Logger.Error("Multiple tokens found for matching authority, client_id, user and scopes.");

                    throw new MsalClientException(
                        MsalError.MultipleTokensMatchedError,
                        MsalErrorMessage.MultipleTokensMatched);
                }

                if (msalAccessTokenCacheItem != null)
                {
                    if (msalAccessTokenCacheItem.ExpiresOn >
                        DateTime.UtcNow + TimeSpan.FromMinutes(DefaultExpirationBufferInMinutes))
                    {
                        requestParams.RequestContext.Logger.Info(
                            "Access token is not expired. Returning the found cache entry. " +
                            GetAccessTokenExpireLogMessageContent(msalAccessTokenCacheItem));
                        return msalAccessTokenCacheItem;
                    }

                    if (ServiceBundle.Config.IsExtendedTokenLifetimeEnabled && msalAccessTokenCacheItem.ExtendedExpiresOn >
                        DateTime.UtcNow + TimeSpan.FromMinutes(DefaultExpirationBufferInMinutes))
                    {
                        requestParams.RequestContext.Logger.Info(
                            "Access token is expired.  IsExtendedLifeTimeEnabled=TRUE and ExtendedExpiresOn is not exceeded.  Returning the found cache entry. " +
                            GetAccessTokenExpireLogMessageContent(msalAccessTokenCacheItem));

                        msalAccessTokenCacheItem.IsExtendedLifeTimeToken = true;
                        return msalAccessTokenCacheItem;
                    }

                    requestParams.RequestContext.Logger.Info(
                        "Access token has expired or about to expire. " +
                        GetAccessTokenExpireLogMessageContent(msalAccessTokenCacheItem));
                }

                return null;
            }
            finally
            {
                _semaphoreSlim.Release();
            }
        }

        async Task<MsalRefreshTokenCacheItem> ITokenCacheInternal.FindRefreshTokenAsync(
            AuthenticationRequestParameters requestParams,
            string familyId)
        {
            var cacheEvent = new CacheEvent(
                CacheEvent.TokenCacheLookup,
                requestParams.RequestContext.TelemetryCorrelationId)
            {
                TokenType = CacheEvent.TokenTypes.RT
            };

            using (ServiceBundle.TelemetryManager.CreateTelemetryHelper(cacheEvent))
            {
                if (requestParams.Authority == null)
                {
                    return null;
                }

                var instanceDiscoveryMetadataEntry = await GetCachedOrDiscoverAuthorityMetaDataAsync(
                    requestParams.AuthorityInfo.CanonicalAuthority,
                    requestParams.RequestContext).ConfigureAwait(false);

                var environmentAliases = GetEnvironmentAliases(
                    requestParams.AuthorityInfo.CanonicalAuthority,
                    instanceDiscoveryMetadataEntry);

                var preferredEnvironmentHost = GetPreferredEnvironmentHost(
                    requestParams.AuthorityInfo.Host,
                    instanceDiscoveryMetadataEntry);

                await _semaphoreSlim.WaitAsync().ConfigureAwait(false);
                try
                {
                    requestParams.RequestContext.Logger.Info("Looking up refresh token in the cache..");

                    TokenCacheNotificationArgs args = new TokenCacheNotificationArgs
                    {
                        TokenCache = new NoLockTokenCacheProxy(this),
                        ClientId = ClientId,
                        Account = requestParams.Account
                    };

                    // make sure to check preferredEnvironmentHost first
                    var allEnvAliases = new List<string>() { preferredEnvironmentHost };
                    allEnvAliases.AddRange(environmentAliases);

                    var keysAcrossEnvs = allEnvAliases.Select(ea => new MsalRefreshTokenCacheKey(
                        ea,
                        requestParams.ClientId,
                        requestParams.Account?.HomeAccountId?.Identifier,
                        familyId));

                    await OnBeforeAccessAsync(args).ConfigureAwait(false);
                    try
                    {
                        // Try to load from all env aliases, but stop at the first valid one
                        MsalRefreshTokenCacheItem msalRefreshTokenCacheItem = keysAcrossEnvs
                            .Select(key => _accessor.GetRefreshToken(key))
                            .FirstOrDefault(item => item != null);

                        requestParams.RequestContext.Logger.Info("Refresh token found in the cache? - " + (msalRefreshTokenCacheItem != null));

                        if (msalRefreshTokenCacheItem != null)
                        {
                            return msalRefreshTokenCacheItem;
                        }

                        requestParams.RequestContext.Logger.Info("Checking ADAL cache for matching RT");

                        // ADAL legacy cache does not store FRTs
                        if (requestParams.Account != null && string.IsNullOrEmpty(familyId))
                        {
                            return CacheFallbackOperations.GetAdalEntryForMsal(
                                Logger,
                                LegacyCachePersistence,
                                preferredEnvironmentHost,
                                environmentAliases,
                                requestParams.ClientId,
                                requestParams.LoginHint,
                                requestParams.Account.HomeAccountId?.Identifier,
                                null);
                        }

                        return null;

                    }
                    finally
                    {
                        await OnAfterAccessAsync(args).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _semaphoreSlim.Release();
                }
            }
        }

        async Task<bool?> ITokenCacheInternal.IsFociMemberAsync(AuthenticationRequestParameters requestParams, string familyId)
        {
            var logger = requestParams.RequestContext.Logger;
            if (requestParams?.AuthorityInfo?.CanonicalAuthority == null)
            {
                logger.Warning("No authority details, can't check app metadta. Returning unkown");
                return null;
            }

            var instanceDiscoveryMetadataEntry = await GetCachedOrDiscoverAuthorityMetaDataAsync(
                   requestParams.AuthorityInfo.CanonicalAuthority,
                   requestParams.RequestContext).ConfigureAwait(false);

            var environmentAliases = GetEnvironmentAliases(
                requestParams.AuthorityInfo.CanonicalAuthority,
                instanceDiscoveryMetadataEntry);

            TokenCacheNotificationArgs args = new TokenCacheNotificationArgs
            {
                TokenCache = new NoLockTokenCacheProxy(this),
                ClientId = ClientId,
                Account = requestParams?.Account,
                HasStateChanged = false
            };

            //TODO: bogavril - is the env ok here? Can I cache it or pass it in?
            MsalAppMetadataCacheItem appMetadata;
            await _semaphoreSlim.WaitAsync().ConfigureAwait(false);
            try
            {
                await OnBeforeAccessAsync(args).ConfigureAwait(false);

                appMetadata =
                    environmentAliases
                    .Select(env => _accessor.GetAppMetadata(new MsalAppMetadataCacheKey(ClientId, env)))
                    .FirstOrDefault(item => item != null);

                await OnAfterAccessAsync(args).ConfigureAwait(false);
            }
            finally
            {
                _semaphoreSlim.Release();
            }

            if (appMetadata == null)
            {
                logger.Warning("No app metadata found. Returning unkown");
                return null;
            }

            return appMetadata.FamilyId == familyId;
        }

        async Task<MsalIdTokenCacheItem> ITokenCacheInternal.GetIdTokenCacheItemAsync(MsalIdTokenCacheKey msalIdTokenCacheKey, RequestContext requestContext)
        {
            await _semaphoreSlim.WaitAsync().ConfigureAwait(false);
            try
            {
                TokenCacheNotificationArgs args = new TokenCacheNotificationArgs
                {
                    TokenCache = new NoLockTokenCacheProxy(this),
                    ClientId = ClientId,
                    Account = null
                };

                await OnBeforeAccessAsync(args).ConfigureAwait(false);
                try
                {
                    var idToken = _accessor.GetIdToken(msalIdTokenCacheKey);
                    return idToken;
                }
                finally
                {
                    await OnAfterAccessAsync(args).ConfigureAwait(false);
                }
            }
            finally
            {
                _semaphoreSlim.Release();
            }
        }

        /// <remarks>
        /// Get accounts should not make a network call, if possible.
        /// </remarks>
        async Task<IEnumerable<IAccount>> ITokenCacheInternal.GetAccountsAsync(string authority, RequestContext requestContext)
        {
            var environment = Authority.GetEnviroment(authority);

            // FetchAllAccountItemsFromCacheAsync...
            IEnumerable<MsalRefreshTokenCacheItem> rtCacheItems;
            IEnumerable<MsalAccountCacheItem> accountCacheItems;
            AdalUsersForMsal adalUsersResult;

            bool filterByClientId = !_featureFlags.IsFociEnabled;

            await _semaphoreSlim.WaitAsync().ConfigureAwait(false);
            try
            {
                var args = new TokenCacheNotificationArgs
                {
                    TokenCache = new NoLockTokenCacheProxy(this),
                    ClientId = ClientId,
                    Account = null
                };

                await OnBeforeAccessAsync(args).ConfigureAwait(false);
                try
                {
                    rtCacheItems = GetAllRefreshTokensWithNoLocks(filterByClientId);
                    accountCacheItems = _accessor.GetAllAccounts();

                    adalUsersResult = CacheFallbackOperations.GetAllAdalUsersForMsal(
                        Logger,
                        LegacyCachePersistence,
                        ClientId);
                }
                finally
                {
                    await OnAfterAccessAsync(args).ConfigureAwait(false);
                }
            }
            finally
            {
                _semaphoreSlim.Release();
            }

            // Multi-cloud support - must filter by env.
            // Use all env aliases to filter, in case PreferredCacheEnv changes in the future
            ISet<string> existingEnvs = new HashSet<string>(
                accountCacheItems.Select(aci => aci.Environment),
                StringComparer.OrdinalIgnoreCase);

            var aliases = await GetEnvAliasesTryAvoidNetworkCallAsync(
                authority,
                adalUsersResult.GetAdalUserEnviroments(),
                existingEnvs,
                requestContext)
                .ConfigureAwait(false);

            rtCacheItems = rtCacheItems.Where(rt => aliases.ContainsOrdinalIgnoreCase(rt.Environment));
            accountCacheItems = accountCacheItems.Where(acc => aliases.ContainsOrdinalIgnoreCase(acc.Environment));

            IDictionary<string, Account> clientInfoToAccountMap = new Dictionary<string, Account>();
            foreach (MsalRefreshTokenCacheItem rtItem in rtCacheItems)
            {
                foreach (MsalAccountCacheItem account in accountCacheItems)
                {
                    if (RtMatchesAccount(rtItem, account))
                    {
                        clientInfoToAccountMap[rtItem.HomeAccountId] = new Account(
                            account.HomeAccountId,
                            account.PreferredUsername,
                            environment);  // Preserve the env passed in by the user

                        break;
                    }
                }
            }

            List<IAccount> accounts = UpdateWithAdalAccounts(
                environment,
                aliases,
                adalUsersResult,
                clientInfoToAccountMap);

            return accounts;
        }

        async Task<IEnumerable<MsalRefreshTokenCacheItem>> ITokenCacheInternal.GetAllRefreshTokensAsync(bool filterByClientId)
        {
            await _semaphoreSlim.WaitAsync().ConfigureAwait(false);
            try
            {
                return GetAllRefreshTokensWithNoLocks(filterByClientId);
            }
            finally
            {
                _semaphoreSlim.Release();
            }
        }

        async Task<IEnumerable<MsalAccessTokenCacheItem>> ITokenCacheInternal.GetAllAccessTokensAsync(bool filterByClientId)
        {
            await _semaphoreSlim.WaitAsync().ConfigureAwait(false);
            try
            {
                return GetAllAccessTokensWithNoLocks(filterByClientId);
            }
            finally
            {
                _semaphoreSlim.Release();
            }
        }

        async Task<IEnumerable<MsalIdTokenCacheItem>> ITokenCacheInternal.GetAllIdTokensAsync(bool filterByClientId)
        {
            await _semaphoreSlim.WaitAsync().ConfigureAwait(false);
            try
            {
                return GetAllIdTokensWithNoLocks(filterByClientId);
            }
            finally
            {
                _semaphoreSlim.Release();
            }
        }

        async Task<IEnumerable<MsalAccountCacheItem>> ITokenCacheInternal.GetAllAccountsAsync()
        {
            await _semaphoreSlim.WaitAsync().ConfigureAwait(false);
            try
            {
                return _accessor.GetAllAccounts();
            }
            finally
            {
                _semaphoreSlim.Release();
            }
        }

        async Task ITokenCacheInternal.RemoveAccountAsync(IAccount account, RequestContext requestContext)
        {
            await _semaphoreSlim.WaitAsync().ConfigureAwait(false);
            try
            {
                requestContext.Logger.Info("Removing user from cache..");

                try
                {
                    var args = new TokenCacheNotificationArgs
                    {
                        TokenCache = new NoLockTokenCacheProxy(this),
                        ClientId = ClientId,
                        Account = account,
                        HasStateChanged = true
                    };

                    await OnBeforeAccessAsync(args).ConfigureAwait(false);
                    try
                    {
                        await OnBeforeWriteAsync(args).ConfigureAwait(false);

                        ((ITokenCacheInternal)this).RemoveMsalAccountWithNoLocks(account, requestContext);
                        RemoveAdalUser(account);
                    }
                    finally
                    {
                        await OnAfterAccessAsync(args).ConfigureAwait(false);
                    }
                }
                finally
                {
#pragma warning disable CS0618 // Type or member is obsolete
                    HasStateChanged = false;
#pragma warning restore CS0618 // Type or member is obsolete
                }
            }
            finally
            {
                _semaphoreSlim.Release();
            }
        }

        void ITokenCacheInternal.RemoveMsalAccountWithNoLocks(IAccount account, RequestContext requestContext)
        {
            if (account.HomeAccountId == null)
            {
                // adalv3 account
                return;
            }

            var allRefreshTokens = GetAllRefreshTokensWithNoLocks(false)
                .Where(item => item.HomeAccountId.Equals(account.HomeAccountId.Identifier, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // To maintain backward compatiblity with other MSALs, filter all credentials by clientID if
            // Foci is disabled or if an FRT is not present
            bool filterByClientId = !_featureFlags.IsFociEnabled || !FrtExists(allRefreshTokens);

            // Delete all credentials associated with this IAccount
            var refreshTokensToDelete = filterByClientId ?
                allRefreshTokens.Where(x => x.ClientId.Equals(ClientId, StringComparison.OrdinalIgnoreCase)) :
                allRefreshTokens;

            foreach (MsalRefreshTokenCacheItem refreshTokenCacheItem in refreshTokensToDelete)
            {
                _accessor.DeleteRefreshToken(refreshTokenCacheItem.GetKey());
            }

            requestContext.Logger.Info("Deleted refresh token count - " + allRefreshTokens.Count);
            IList<MsalAccessTokenCacheItem> allAccessTokens = GetAllAccessTokensWithNoLocks(filterByClientId)
                .Where(item => item.HomeAccountId.Equals(account.HomeAccountId.Identifier, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (MsalAccessTokenCacheItem accessTokenCacheItem in allAccessTokens)
            {
                _accessor.DeleteAccessToken(accessTokenCacheItem.GetKey());
            }

            requestContext.Logger.Info("Deleted access token count - " + allAccessTokens.Count);

            var allIdTokens = GetAllIdTokensWithNoLocks(filterByClientId)
                .Where(item => item.HomeAccountId.Equals(account.HomeAccountId.Identifier, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (MsalIdTokenCacheItem idTokenCacheItem in allIdTokens)
            {
                _accessor.DeleteIdToken(idTokenCacheItem.GetKey());
            }

            requestContext.Logger.Info("Deleted Id token count - " + allIdTokens.Count);

            _accessor.GetAllAccounts()
                .Where(item => item.HomeAccountId.Equals(account.HomeAccountId.Identifier, StringComparison.OrdinalIgnoreCase) &&
                               item.PreferredUsername.Equals(account.Username, StringComparison.OrdinalIgnoreCase))
                .ToList()
                .ForEach(accItem => _accessor.DeleteAccount(accItem.GetKey()));
        }

        async Task ITokenCacheInternal.ClearAsync()
        {
            await _semaphoreSlim.WaitAsync().ConfigureAwait(false);
            try
            {
                TokenCacheNotificationArgs args = new TokenCacheNotificationArgs
                {
                    TokenCache = new NoLockTokenCacheProxy(this),
                    ClientId = ClientId,
                    Account = null,
                    HasStateChanged = true
                };

                await OnBeforeAccessAsync(args).ConfigureAwait(false);
                try
                {
                    await OnBeforeWriteAsync(args).ConfigureAwait(false);

                    ((ITokenCacheInternal)this).ClearMsalCache();
                    ((ITokenCacheInternal)this).ClearAdalCache();
                }
                finally
                {
                    await OnAfterAccessAsync(args).ConfigureAwait(false);
#pragma warning disable CS0618 // Type or member is obsolete
                    HasStateChanged = false;
#pragma warning restore CS0618 // Type or member is obsolete
                }
            }
            finally
            {
                _semaphoreSlim.Release();
            }
        }

        void ITokenCacheInternal.ClearAdalCache()
        {
            IDictionary<AdalTokenCacheKey, AdalResultWrapper> dictionary = AdalCacheOperations.Deserialize(Logger, LegacyCachePersistence.LoadCache());
            dictionary.Clear();
            LegacyCachePersistence.WriteCache(AdalCacheOperations.Serialize(Logger, dictionary));
        }

        void ITokenCacheInternal.ClearMsalCache()
        {
            _accessor.Clear();
        }
    }
}

