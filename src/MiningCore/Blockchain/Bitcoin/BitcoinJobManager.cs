﻿using System;
using System.Linq;
using System.Threading.Tasks;
using Autofac;
using CodeContracts;
using Microsoft.Extensions.Logging;
using MiningCore.Blockchain.Bitcoin.Commands;
using MiningCore.Configuration.Extensions;
using MiningCore.Extensions;
using MiningCore.MininigPool;
using MiningCore.Stratum;
using Newtonsoft.Json.Linq;

// TODO:
// - detectCoinDataAsync

namespace MiningCore.Blockchain.Bitcoin
{
    public class BitcoinJobManager : JobManagerBase,
        IBlockchainJobManager
    {
        public BitcoinJobManager(
            IComponentContext ctx, 
            ILogger<BitcoinJobManager> logger,
            BlockchainDemon daemon,
            ExtraNonceProvider extraNonceProvider) : base(ctx, logger, daemon)
        {
            this.extraNonceProvider = extraNonceProvider;
        }

        private readonly ExtraNonceProvider extraNonceProvider;
        private readonly NetworkStats networkStats = new NetworkStats();
        private bool isPoS;
        private bool isTestNet;
        private bool isRegTestNet;
        private bool hasSubmitBlockMethod;

        private const string GetInfoCommand = "getinfo";
        private const string GetMiningInfoCommand = "getmininginfo";
        private const string GetPeerInfoCommand = "getpeerinfo";
        private const string ValidateAddressCommand = "validateaddress";
        private const string GetDifficultyCommand = "getdifficulty";
        private const string GetBlockTemplateCommand = "getblocktemplate";
        private const string SubmitBlockCommand = "submitblock";
        private const string GetBlockchainInfoCommand = "getblockchaininfo";

        private static readonly object[] getBlockTemplateParams = 
        {
            new
            {
                capabilities = new[] {"coinbasetxn", "workid", "coinbase/append"},
                rules = new[] {"segwit"}
            },
        };

        private readonly object blockTemplateLock = new object();
        private GetBlockTemplateResponse blockTemplate;

        #region API-Surface

        public async Task<bool> ValidateAddressAsync(string address)
        {
            Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(address), $"{nameof(address)} must not be empty");

            var result = await daemon.ExecuteCmdAnyAsync<string[], ValidateAddressResponse>(ValidateAddressCommand, new[] { address });

            return result.Response != null && result.Response.IsValid;
        }

        public Task<object[]> HandleWorkerSubscribeAsync(StratumClient worker)
        {
            Contract.RequiresNonNull(worker, nameof(worker));
            
            // setup worker context
            var job = new BitcoinWorkerContext
            {
                ExtraNonce1 = extraNonceProvider.Next().ToBigEndian().ToString("x4"),
            };

            worker.WorkerContext = job;

            // setup response data
            var responseData = new object[]
            {
                job.ExtraNonce1,
                extraNonceProvider.Size
            };

            return Task.FromResult(responseData);
        }

        public Task<bool> HandleWorkerSubmitAsync(StratumClient worker, object submission)
        {
            Contract.RequiresNonNull(worker, nameof(worker));
            Contract.RequiresNonNull(submission, nameof(submission));

            return Task.FromResult(true);
        }

        public NetworkStats NetworkStats => networkStats;

        #endregion // API-Surface

        #region Overrides

        protected override async Task<bool> IsDaemonHealthy()
        {
            var responses = await daemon.ExecuteCmdAllAsync<GetInfoResponse>(GetInfoCommand);

            return responses.All(x => x.Error == null);
        }

        protected override async Task EnsureDaemonsSynchedAsync()
        {
            var syncPendingNotificationShown = false;

            while (true)
            {
                var responses = await daemon.ExecuteCmdAllAsync<object[], GetBlockTemplateResponse>(
                    GetBlockTemplateCommand, getBlockTemplateParams);

                var isSynched = responses.All(x => x.Error == null || x.Error.Code != -10);

                if (isSynched)
                {
                    logger.Info(() => $"[{poolConfig.Coin.Name}] Daemons are fully synched with blockchain");
                    break;
                }

                if(!syncPendingNotificationShown)
                { 
                    logger.Info(() => $"[{poolConfig.Coin.Name}] Daemons still syncing with network (download blockchain) - manager will be started once synced");
                    syncPendingNotificationShown = true;
                }

                await ShowDaemonSyncProgressAsync();

                // delay retry by 5s
                await Task.Delay(5000);
            }
        }

        protected override async Task PostStartInitAsync()
        {
            var tasks = new Task[] 
            {
                daemon.ExecuteCmdAnyAsync<string[], ValidateAddressResponse>(ValidateAddressCommand, 
                    new[] { poolConfig.Address }),
                daemon.ExecuteCmdAnyAsync<JToken>(GetDifficultyCommand),
                daemon.ExecuteCmdAnyAsync<GetInfoResponse>(GetInfoCommand),
                daemon.ExecuteCmdAnyAsync<GetMiningInfoResponse>(GetMiningInfoCommand),
                daemon.ExecuteCmdAnyAsync<object>(SubmitBlockCommand),
                daemon.ExecuteCmdAnyAsync<GetBlockchainInfoResponse>(GetBlockchainInfoCommand),
            };

            var batchTask = Task.WhenAll(tasks);
            await batchTask;

            if (!batchTask.IsCompletedSuccessfully)
            {
                logger.Error(()=> $"[{poolConfig.Coin.Name}] Init RPC failed", batchTask.Exception);
                throw new PoolStartupAbortException();
            }

            // extract results
            var validateAddressResponse = ((Task<DaemonResponse<ValidateAddressResponse>>) tasks[0]).Result;
            var difficultyResponse = ((Task<DaemonResponse<JToken>>)tasks[1]).Result;
            var infoResponse = ((Task<DaemonResponse<GetInfoResponse>>)tasks[2]).Result;
            var miningInfoResponse = ((Task<DaemonResponse<GetMiningInfoResponse>>)tasks[3]).Result;
            var submitBlockResponse = ((Task<DaemonResponse<object>>)tasks[4]).Result;
            var blockchainInfoResponse = ((Task<DaemonResponse<GetBlockchainInfoResponse>>)tasks[5]).Result;

            // validate pool-address for pool-fee payout
            if (!validateAddressResponse.Response.IsValid)
                throw new PoolStartupAbortException($"[{poolConfig.Coin.Name}] Daemon reports pool-address '{poolConfig.Address}' as invalid");

            if (!validateAddressResponse.Response.IsMine)
                throw new PoolStartupAbortException($"[{poolConfig.Coin.Name}] Daemon does not own pool-address");

            isPoS = difficultyResponse.Response.Values().Any(x=> x.Path == "proof-of-stake");

            // POS coins must use the pubkey in coinbase transaction, and pubkey is only given if address is owned by wallet
            if (isPoS && string.IsNullOrEmpty(validateAddressResponse.Response.PubKey))
                throw new PoolStartupAbortException($"[{poolConfig.Coin.Name}] The pool-address is not from the daemon wallet - this is required for POS coins");

            //if (!isPoS)
            //    poolAddressScript = pubkeyToScript(validateAddressResponse.Response.PubKey);
            //else
            //    poolAddressScript = addressToScript(validateAddressResponse.Response.Address);

            // chain detection
            isTestNet = blockchainInfoResponse.Response.Chain == "testnet";
            isRegTestNet = blockchainInfoResponse.Response.Chain == "regtest";

            // block submission RPC method
            if (submitBlockResponse.Error?.Message?.ToLower() == "method not found")
                hasSubmitBlockMethod = false;
            else if (submitBlockResponse.Error?.Code == -1)
                hasSubmitBlockMethod = true;
            else
                throw new PoolStartupAbortException($"[{poolConfig.Coin.Name}] Unable detect block submission RPC method");

            // update stats
            networkStats.BlockHeight = infoResponse.Response.Blocks;
            networkStats.Difficulty = miningInfoResponse.Response.Difficulty;
            networkStats.HashRate = miningInfoResponse.Response.NetworkHashps;
            networkStats.ConnectedPeers = infoResponse.Response.Connections;
            networkStats.RewardType = !isPoS ? "POW" : "POS";
        }

        protected override async Task<bool> UpdateJobFromDaemon()
        {
            var result = await GetBlockTemplateAsync();

            lock (blockTemplateLock)
            {
                var isNew = blockTemplate == null && result != null || 
                    blockTemplate?.PreviousBlockhash != result?.PreviousBlockhash;

                if (isNew)
                {
                    blockTemplate = result;

                    // update stats
                    networkStats.LastBlockTime = DateTime.UtcNow;
                }

                return isNew;
            }
        }

        protected override object GetJobParamsForStratum()
        {
            return new object();
        }

        #endregion // Overrides

        private async Task<GetBlockTemplateResponse> GetBlockTemplateAsync()
        {
            var result = await daemon.ExecuteCmdAnyAsync<object[], GetBlockTemplateResponse>(
                GetBlockTemplateCommand, getBlockTemplateParams);

            return result.Response;
        }

        private async Task ShowDaemonSyncProgressAsync()
        {
            var infos = await daemon.ExecuteCmdAllAsync<GetInfoResponse>(GetInfoCommand);

            if (infos.Length > 0)
            {
                var blockCount = infos
                    .Max(x => x.Response?.Blocks);

                if (blockCount.HasValue)
                {
                    // get list of peers and their highest block height to compare to ours
                    var peerInfo = await daemon.ExecuteCmdAnyAsync<PeerInfo[]>(GetPeerInfoCommand);
                    var peers = peerInfo.Response;

                    if (peers != null && peers.Length > 0)
                    {
                        var totalBlocks = peers
                            .OrderBy(x => x.StartingHeight)
                            .First().StartingHeight;

                        var percent = ((double)totalBlocks / blockCount) * 100;
                        this.logger.Info(() => $"[{poolConfig.Coin.Name}] Daemons have downloaded {percent}% of blockchain from {peers.Length} peers");
                    }
                }
            }
        }
    }
}
