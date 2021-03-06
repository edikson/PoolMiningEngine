﻿/* 
Copyright 2017 Coin Foundry (coinfoundry.org)
Authors: Oliver Weichhold (oliver@weichhold.com)

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the "Software"), to deal in the Software without restriction, 
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, 
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, 
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial 
portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT 
LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. 
IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, 
WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE 
SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Autofac.Features.Metadata;
using AutoMapper;
using MiningCore.Blockchain.Monero.StratumRequests;
using MiningCore.Blockchain.Monero.StratumResponses;
using MiningCore.Configuration;
using MiningCore.JsonRpc;
using MiningCore.Mining;
using MiningCore.Notifications;
using MiningCore.Persistence;
using MiningCore.Persistence.Repositories;
using MiningCore.Stratum;
using Newtonsoft.Json;

namespace MiningCore.Blockchain.Monero
{
    [CoinMetadata(CoinType.XMR)]
    public class MoneroPool : PoolBase<MoneroWorkerContext>
    {
        public MoneroPool(IComponentContext ctx,
            JsonSerializerSettings serializerSettings,
            IConnectionFactory cf,
            IStatsRepository statsRepo,
            IMapper mapper,
            NotificationService notificationService) :
            base(ctx, serializerSettings, cf, statsRepo, mapper, notificationService)
        {
        }

        private long currentJobId;

        private MoneroJobManager manager;
        private static readonly TimeSpan maxShareAge = TimeSpan.FromSeconds(5);

        private void OnLogin(StratumClient<MoneroWorkerContext> client, Timestamped<JsonRpcRequest> tsRequest)
        {
            var request = tsRequest.Value;

            if (request.Id == null)
            {
                client.RespondError(StratumError.MinusOne, "missing request id", request.Id);
                return;
            }

            var loginRequest = request.ParamsAs<MoneroLoginRequest>();

            if (string.IsNullOrEmpty(loginRequest?.Login))
            {
                client.RespondError(StratumError.MinusOne, "missing login", request.Id);
                return;
            }

            // extract worker/miner/paymentid
            var split = loginRequest.Login.Split('.');
            client.Context.MinerName = split[0];
            client.Context.WorkerName = split.Length > 1 ? split[1] : null;
            client.Context.UserAgent = loginRequest.UserAgent;

            // extract paymentid
            var index = client.Context.MinerName.IndexOf('#');
            if (index != -1)
            {
                client.Context.PaymentId = client.Context.MinerName.Substring(index + 1);
                client.Context.MinerName = client.Context.MinerName.Substring(0, index);
            }

            // validate login
            var result = manager.ValidateAddress(client.Context.MinerName);

            client.Context.IsSubscribed = result;
            client.Context.IsAuthorized = result;

            if (!client.Context.IsAuthorized)
            {
                client.RespondError(StratumError.MinusOne, "invalid login", request.Id);
                return;
            }

            // respond
            var loginResponse = new MoneroLoginResponse
            {
                Id = client.ConnectionId,
                Job = CreateWorkerJob(client)
            };

            client.Respond(loginResponse, request.Id);
        }

        private void OnGetJob(StratumClient<MoneroWorkerContext> client, Timestamped<JsonRpcRequest> tsRequest)
        {
            var request = tsRequest.Value;

            if (request.Id == null)
            {
                client.RespondError(StratumError.MinusOne, "missing request id", request.Id);
                return;
            }

            var getJobRequest = request.ParamsAs<MoneroGetJobRequest>();

            // validate worker
            if (client.ConnectionId != getJobRequest?.WorkerId || !client.Context.IsAuthorized)
            {
                client.RespondError(StratumError.MinusOne, "unauthorized", request.Id);
                return;
            }

            // respond
            var job = CreateWorkerJob(client);
            client.Respond(job, request.Id);
        }

        private MoneroJobParams CreateWorkerJob(StratumClient<MoneroWorkerContext> client)
        {
            var job = new MoneroWorkerJob(NextJobId(), client.Context.Difficulty);

            manager.PrepareWorkerJob(job, out var blob, out var target);

            // should never happen
            if (string.IsNullOrEmpty(blob) || string.IsNullOrEmpty(blob))
                return null;

            var result = new MoneroJobParams
            {
                JobId = job.Id,
                Blob = blob,
                Target = target
            };

            // update context
            lock (client.Context)
            {
                client.Context.AddJob(job);
            }

            return result;
        }

        private async Task OnSubmitAsync(StratumClient<MoneroWorkerContext> client, Timestamped<JsonRpcRequest> tsRequest)
        {
            var request = tsRequest.Value;

            try
            {
                if (request.Id == null)
                    throw new StratumException(StratumError.MinusOne, "missing request id");

                // check age of submission (aged submissions are usually caused by high server load)
                var requestAge = DateTime.UtcNow - tsRequest.Timestamp.UtcDateTime;

                if (requestAge > maxShareAge)
                {
                    logger.Debug(() => $"[{LogCat}] [{client.ConnectionId}] Dropping stale share submission request (not client's fault)");
                    return;
                }

                // check request
                var submitRequest = request.ParamsAs<MoneroSubmitShareRequest>();

                // validate worker
                if (client.ConnectionId != submitRequest?.WorkerId || !client.Context.IsAuthorized)
                    throw new StratumException(StratumError.MinusOne, "unauthorized");

                // recognize activity
                client.Context.LastActivity = DateTime.UtcNow;

                MoneroWorkerJob job;

                lock (client.Context)
                {
                    var jobId = submitRequest?.JobId;

                    if (string.IsNullOrEmpty(jobId) ||
                        (job = client.Context.ValidJobs.FirstOrDefault(x => x.Id == jobId)) == null)
                        throw new StratumException(StratumError.MinusOne, "invalid jobid");
                }

                // dupe check
                var nonceLower = submitRequest.Nonce.ToLower();

                lock (job)
                {
                    if (job.Submissions.Contains(nonceLower))
                        throw new StratumException(StratumError.MinusOne, "duplicate share");

                    job.Submissions.Add(nonceLower);
                }

                var poolEndpoint = poolConfig.Ports[client.PoolEndpoint.Port];

                var share = await manager.SubmitShareAsync(client, submitRequest, job, poolEndpoint.Difficulty);

                // success
                client.Respond(new MoneroResponseBase(), request.Id);
                shareSubject.OnNext(Tuple.Create((object) client, share));

                logger.Info(() => $"[{LogCat}] [{client.ConnectionId}] Share accepted: D={Math.Round(share.StratumDifficulty, 3)}");

                // update pool stats
                if (share.IsBlockCandidate)
                    poolStats.LastPoolBlockTime = DateTime.UtcNow;

                // update client stats
                client.Context.Stats.ValidShares++;
            }

            catch (StratumException ex)
            {
                client.RespondError(ex.Code, ex.Message, request.Id, false);

                // update client stats
                client.Context.Stats.InvalidShares++;
                logger.Info(() => $"[{LogCat}] [{client.ConnectionId}] Share rejected: {ex.Code}");

                // banning
                if (poolConfig.Banning?.Enabled == true)
                    ConsiderBan(client, client.Context, poolConfig.Banning);
            }
        }

        private string NextJobId()
        {
            return Interlocked.Increment(ref currentJobId).ToString(CultureInfo.InvariantCulture);
        }

        private void OnNewJob()
        {
            ForEachClient(client =>
            {
                if (client.Context.IsSubscribed)
                {
                    // check alive
                    var lastActivityAgo = DateTime.UtcNow - client.Context.LastActivity;

                    if (poolConfig.ClientConnectionTimeout > 0 &&
                        lastActivityAgo.TotalSeconds > poolConfig.ClientConnectionTimeout)
                    {
                        logger.Info(() => $"[{LogCat}] [{client.ConnectionId}] Booting zombie-worker (idle-timeout exceeded)");
                        DisconnectClient(client);
                        return;
                    }

                    // send job
                    var job = CreateWorkerJob(client);
                    client.Notify(MoneroStratumMethods.JobNotify, job);
                }
            });
        }

        #region Overrides

        protected override async Task SetupJobManager()
        {
            manager = ctx.Resolve<MoneroJobManager>();
            manager.Configure(poolConfig, clusterConfig);

            await manager.StartAsync();

            disposables.Add(manager.Blocks.Subscribe(_ => OnNewJob()));

            // we need work before opening the gates
            await manager.Blocks.Take(1).ToTask();
        }

        protected override async Task OnRequestAsync(StratumClient<MoneroWorkerContext> client,
            Timestamped<JsonRpcRequest> tsRequest)
        {
            var request = tsRequest.Value;

            switch (request.Method)
            {
                case MoneroStratumMethods.Login:
                    OnLogin(client, tsRequest);
                    break;

                case MoneroStratumMethods.GetJob:
                    OnGetJob(client, tsRequest);
                    break;

                case MoneroStratumMethods.Submit:
                    await OnSubmitAsync(client, tsRequest);
                    break;

                case MoneroStratumMethods.KeepAlive:
                    // recognize activity
                    client.Context.LastActivity = DateTime.UtcNow;
                    break;

                default:
                    logger.Debug(() => $"[{LogCat}] [{client.ConnectionId}] Unsupported RPC request: {JsonConvert.SerializeObject(request, serializerSettings)}");

                    client.RespondError(StratumError.Other, $"Unsupported request {request.Method}", request.Id);
                    break;
            }
        }

        protected override void SetupStats()
        {
            base.SetupStats();

            // Pool Hashrate
            var poolHashRateSampleIntervalSeconds = 60 * 10;

            disposables.Add(Shares
                .Buffer(TimeSpan.FromSeconds(poolHashRateSampleIntervalSeconds))
                .Do(shares => UpdateMinerHashrates(shares, poolHashRateSampleIntervalSeconds))
                .Select(shares =>
                {
                    if (!shares.Any())
                        return 0ul;

                    try
                    {
                        return HashrateFromShares(shares, poolHashRateSampleIntervalSeconds);
                    }

                    catch (Exception ex)
                    {
                        logger.Error(ex);
                        return 0ul;
                    }
                })
                .Subscribe(hashRate => poolStats.PoolHashRate = hashRate));
        }

        protected override ulong HashrateFromShares(IEnumerable<Tuple<object, IShare>> shares, int interval)
        {
            var result = Math.Ceiling(shares.Sum(share => share.Item2.StratumDifficulty) / interval);
            return (ulong)result;
        }

        protected override void OnVarDiffUpdate(StratumClient<MoneroWorkerContext> client, double newDiff)
        {
            base.OnVarDiffUpdate(client, newDiff);

            // apply immediately and notify client
            if (client.Context.HasPendingDifficulty)
            {
                client.Context.ApplyPendingDifficulty();

                // re-send job
                var job = CreateWorkerJob(client);
                client.Notify(MoneroStratumMethods.JobNotify, job);
            }
        }

        protected override async Task UpdateBlockChainStatsAsync()
        {
            await manager.UpdateNetworkStatsAsync();

            blockchainStats = manager.BlockchainStats;
        }

        #endregion // Overrides
    }
}
