﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

using Tzkt.Data.Models;
using Tzkt.Data.Models.Base;

namespace Tzkt.Sync.Protocols.Proto1
{
    class TransactionsCommit : ProtocolCommit
    {
        public TransactionOperation Transaction { get; private set; }

        public TransactionsCommit(ProtocolHandler protocol) : base(protocol) { }

        public virtual async Task Apply(Block block, JsonElement op, JsonElement content)
        {
            #region init
            var sender = await Cache.Accounts.GetAsync(content.RequiredString("source"));
            sender.Delegate ??= Cache.Accounts.GetDelegate(sender.DelegateId);

            var target = await Cache.Accounts.GetAsync(content.OptionalString("destination"))
                ?? block.Originations?.FirstOrDefault(x => x.Contract.Address == content.OptionalString("destination"))?.Contract;

            if (target != null)
                target.Delegate ??= Cache.Accounts.GetDelegate(target.DelegateId);

            var result = content.Required("metadata").Required("operation_result");

            var transaction = new TransactionOperation
            {
                Id = Cache.AppState.NextOperationId(),
                Block = block,
                Level = block.Level,
                Timestamp = block.Timestamp,
                OpHash = op.RequiredString("hash"),
                Amount = content.RequiredInt64("amount"),
                BakerFee = content.RequiredInt64("fee"),
                Counter = content.RequiredInt32("counter"),
                GasLimit = content.RequiredInt32("gas_limit"),
                StorageLimit = content.RequiredInt32("storage_limit"),
                Sender = sender,
                Target = target,
                Parameters = content.TryGetProperty("parameters", out var param)
                    ? OperationParameters.Parse(param)
                    : null,
                Status = result.RequiredString("status") switch
                {
                    "applied" => OperationStatus.Applied,
                    "backtracked" => OperationStatus.Backtracked,
                    "failed" => OperationStatus.Failed,
                    "skipped" => OperationStatus.Skipped,
                    _ => throw new NotImplementedException()
                },
                Errors = result.TryGetProperty("errors", out var errors)
                    ? OperationErrors.Parse(errors)
                    : null,
                GasUsed = result.OptionalInt32("consumed_gas") ?? 0,
                StorageUsed = result.OptionalInt32("paid_storage_size_diff") ?? 0,
                StorageFee = result.OptionalInt32("paid_storage_size_diff") > 0
                    ? result.OptionalInt32("paid_storage_size_diff") * block.Protocol.ByteCost
                    : null,
                AllocationFee = HasAllocated(result)
                    ? (long?)block.Protocol.OriginationSize * block.Protocol.ByteCost
                    : null
            };
            #endregion

            #region entities
            //var block = transaction.Block;
            var blockBaker = block.Baker;

            //var sender = transaction.Sender;
            var senderDelegate = sender.Delegate ?? sender as Data.Models.Delegate;

            //var target = transaction.Target;
            var targetDelegate = target?.Delegate ?? target as Data.Models.Delegate;

            //Db.TryAttach(block);
            Db.TryAttach(blockBaker);
            Db.TryAttach(sender);
            Db.TryAttach(senderDelegate);
            Db.TryAttach(target);
            Db.TryAttach(targetDelegate);
            #endregion

            #region apply operation
            await Spend(sender, transaction.BakerFee);
            if (senderDelegate != null) senderDelegate.StakingBalance -= transaction.BakerFee;
            blockBaker.FrozenFees += transaction.BakerFee;
            blockBaker.Balance += transaction.BakerFee;
            blockBaker.StakingBalance += transaction.BakerFee;

            sender.TransactionsCount++;
            if (target != null && target != sender) target.TransactionsCount++;

            block.Events |= GetBlockEvents(target);
            block.Operations |= Operations.Transactions;
            block.Fees += transaction.BakerFee;

            sender.Counter = Math.Max(sender.Counter, transaction.Counter);
            #endregion

            #region apply result
            if (transaction.Status == OperationStatus.Applied)
            {
                await Spend(sender,
                    transaction.Amount +
                    (transaction.StorageFee ?? 0) +
                    (transaction.AllocationFee ?? 0));

                if (senderDelegate != null)
                {
                    senderDelegate.StakingBalance -= transaction.Amount;
                    senderDelegate.StakingBalance -= transaction.StorageFee ?? 0;
                    senderDelegate.StakingBalance -= transaction.AllocationFee ?? 0;
                }

                target.Balance += transaction.Amount;

                if (targetDelegate != null)
                {
                    targetDelegate.StakingBalance += transaction.Amount;
                }

                await ResetGracePeriod(transaction);
            }
            #endregion

            Db.TransactionOps.Add(transaction);
            Transaction = transaction;
        }

        public virtual async Task ApplyInternal(Block block, TransactionOperation parent, JsonElement content)
        {
            #region init
            var id = Cache.AppState.NextOperationId();

            var sender = await Cache.Accounts.GetAsync(content.RequiredString("source"))
                ?? block.Originations?.FirstOrDefault(x => x.Contract.Address == content.RequiredString("source"))?.Contract;

            sender.Delegate ??= Cache.Accounts.GetDelegate(sender.DelegateId);

            var target = await Cache.Accounts.GetAsync(content.OptionalString("destination"))
                ?? block.Originations?.FirstOrDefault(x => x.Contract.Address == content.OptionalString("destination"))?.Contract;

            if (target != null)
                target.Delegate ??= Cache.Accounts.GetDelegate(target.DelegateId);

            var result = content.Required("result");

            var transaction = new TransactionOperation
            {
                Id = id,
                Initiator = parent.Sender,
                Block = parent.Block,
                Level = parent.Block.Level,
                Timestamp = parent.Timestamp,
                OpHash = parent.OpHash,
                Counter = parent.Counter,
                Amount = content.RequiredInt64("amount"),
                Nonce = content.RequiredInt32("nonce"),
                Sender = sender, 
                Target = target,
                Parameters = content.TryGetProperty("parameters", out var param)
                    ? OperationParameters.Parse(param)
                    : null,
                Status = result.RequiredString("status") switch
                {
                    "applied" => OperationStatus.Applied,
                    "backtracked" => OperationStatus.Backtracked,
                    "failed" => OperationStatus.Failed,
                    "skipped" => OperationStatus.Skipped,
                    _ => throw new NotImplementedException()
                },
                Errors = result.TryGetProperty("errors", out var errors)
                    ? OperationErrors.Parse(errors)
                    : null,
                GasUsed = result.OptionalInt32("consumed_gas") ?? 0,
                StorageUsed = result.OptionalInt32("paid_storage_size_diff") ?? 0,
                StorageFee = result.OptionalInt32("paid_storage_size_diff") > 0
                    ? result.OptionalInt32("paid_storage_size_diff") * block.Protocol.ByteCost
                    : null,
                AllocationFee = HasAllocated(result)
                    ? (long?)block.Protocol.OriginationSize * block.Protocol.ByteCost
                    : null
            };
            #endregion

            #region entities
            //var block = transaction.Block;
            var parentTx = parent;
            var parentSender = parentTx.Sender;
            var parentDelegate = parentSender.Delegate ?? parentSender as Data.Models.Delegate;
            //var sender = transaction.Sender;
            var senderDelegate = sender.Delegate ?? sender as Data.Models.Delegate;
            //var target = transaction.Target;
            var targetDelegate = target?.Delegate ?? target as Data.Models.Delegate;

            //Db.TryAttach(block);
            //Db.TryAttach(parentTx);
            //Db.TryAttach(parentSender);
            //Db.TryAttach(parentDelegate);
            Db.TryAttach(sender);
            Db.TryAttach(senderDelegate);
            Db.TryAttach(target);
            Db.TryAttach(targetDelegate);
            #endregion

            #region apply operation
            parentTx.InternalOperations = (parentTx.InternalOperations ?? InternalOperations.None) | InternalOperations.Transactions;

            sender.TransactionsCount++;
            if (target != null && target != sender) target.TransactionsCount++;
            if (parentSender != sender && parentSender != target) parentSender.TransactionsCount++;

            block.Events |= GetBlockEvents(target);
            block.Operations |= Operations.Transactions;
            #endregion

            #region apply result
            if (transaction.Status == OperationStatus.Applied)
            {
                await Spend(parentSender,
                    (transaction.StorageFee ?? 0) +
                    (transaction.AllocationFee ?? 0));

                if (parentDelegate != null)
                {
                    parentDelegate.StakingBalance -= transaction.StorageFee ?? 0;
                    parentDelegate.StakingBalance -= transaction.AllocationFee ?? 0;
                }

                sender.Balance -= transaction.Amount;

                if (senderDelegate != null)
                {
                    senderDelegate.StakingBalance -= transaction.Amount;
                }

                target.Balance += transaction.Amount;

                if (targetDelegate != null)
                {
                    targetDelegate.StakingBalance += transaction.Amount;
                }

                await ResetGracePeriod(transaction);
            }
            #endregion

            Db.TransactionOps.Add(transaction);
            Transaction = transaction;
        }

        public virtual async Task Revert(Block block, TransactionOperation transaction)
        {
            #region init
            transaction.Block ??= block;
            transaction.Block.Protocol ??= await Cache.Protocols.GetAsync(block.ProtoCode);
            transaction.Block.Baker ??= Cache.Accounts.GetDelegate(block.BakerId);

            transaction.Sender = await Cache.Accounts.GetAsync(transaction.SenderId);
            transaction.Sender.Delegate ??= Cache.Accounts.GetDelegate(transaction.Sender.DelegateId);
            transaction.Target = await Cache.Accounts.GetAsync(transaction.TargetId);

            if (transaction.Target != null)
                transaction.Target.Delegate ??= Cache.Accounts.GetDelegate(transaction.Target.DelegateId);

            if (transaction.InitiatorId != null)
            {
                transaction.Initiator = await Cache.Accounts.GetAsync(transaction.InitiatorId);
                transaction.Initiator.Delegate ??= Cache.Accounts.GetDelegate(transaction.Initiator.DelegateId);
            }
            #endregion

            #region entities
            //var block = transaction.Block;
            var blockBaker = block.Baker;
            var sender = transaction.Sender;
            var senderDelegate = sender.Delegate ?? sender as Data.Models.Delegate;
            var target = transaction.Target;
            var targetDelegate = target?.Delegate ?? target as Data.Models.Delegate;

            //Db.TryAttach(block);
            Db.TryAttach(blockBaker);
            Db.TryAttach(sender);
            Db.TryAttach(senderDelegate);
            Db.TryAttach(target);
            Db.TryAttach(targetDelegate);
            #endregion

            #region revert result
            if (transaction.Status == OperationStatus.Applied)
            {
                target.Balance -= transaction.Amount;

                if (targetDelegate != null)
                {
                    targetDelegate.StakingBalance -= transaction.Amount;
                }

                if (target is Data.Models.Delegate delegat)
                {
                    if (transaction.ResetDeactivation != null)
                    {
                        if (transaction.ResetDeactivation <= transaction.Level)
                            await UpdateDelegate(delegat, false);

                        delegat.DeactivationLevel = (int)transaction.ResetDeactivation;
                    }
                }

                await Return(sender,
                    transaction.Amount +
                    (transaction.StorageFee ?? 0) +
                    (transaction.AllocationFee ?? 0));

                if (senderDelegate != null)
                {
                    senderDelegate.StakingBalance += transaction.Amount;
                    senderDelegate.StakingBalance += transaction.StorageFee ?? 0;
                    senderDelegate.StakingBalance += transaction.AllocationFee ?? 0;
                }
            }
            #endregion

            #region revert operation
            await Return(sender, transaction.BakerFee);
            if (senderDelegate != null) senderDelegate.StakingBalance += transaction.BakerFee;
            blockBaker.FrozenFees -= transaction.BakerFee;
            blockBaker.Balance -= transaction.BakerFee;
            blockBaker.StakingBalance -= transaction.BakerFee;

            sender.TransactionsCount--;
            if (target != null && target != sender) target.TransactionsCount--;

            sender.Counter = Math.Min(sender.Counter, transaction.Counter - 1);
            #endregion

            Db.TransactionOps.Remove(transaction);
            Cache.AppState.ReleaseManagerCounter();
        }

        public virtual async Task RevertInternal(Block block, TransactionOperation transaction)
        {
            #region init
            transaction.Block ??= block;
            transaction.Block.Protocol ??= await Cache.Protocols.GetAsync(block.ProtoCode);
            transaction.Block.Baker ??= Cache.Accounts.GetDelegate(block.BakerId);

            transaction.Sender = await Cache.Accounts.GetAsync(transaction.SenderId);
            transaction.Sender.Delegate ??= Cache.Accounts.GetDelegate(transaction.Sender.DelegateId);
            transaction.Target = await Cache.Accounts.GetAsync(transaction.TargetId);

            if (transaction.Target != null)
                transaction.Target.Delegate ??= Cache.Accounts.GetDelegate(transaction.Target.DelegateId);

            transaction.Initiator = await Cache.Accounts.GetAsync(transaction.InitiatorId);
            transaction.Initiator.Delegate ??= Cache.Accounts.GetDelegate(transaction.Initiator.DelegateId);
            #endregion

            #region entities
            var parentSender = transaction.Initiator;
            var parentDelegate = parentSender.Delegate ?? parentSender as Data.Models.Delegate;
            var sender = transaction.Sender;
            var senderDelegate = sender.Delegate ?? sender as Data.Models.Delegate;
            var target = transaction.Target;
            var targetDelegate = target?.Delegate ?? target as Data.Models.Delegate;

            //Db.TryAttach(block);
            //Db.TryAttach(parentTx);
            //Db.TryAttach(parentSender);
            //Db.TryAttach(parentDelegate);
            Db.TryAttach(sender);
            Db.TryAttach(senderDelegate);
            Db.TryAttach(target);
            Db.TryAttach(targetDelegate);
            #endregion

            #region revert result
            if (transaction.Status == OperationStatus.Applied)
            {
                target.Balance -= transaction.Amount;

                if (targetDelegate != null)
                {
                    targetDelegate.StakingBalance -= transaction.Amount;
                }

                if (target is Data.Models.Delegate delegat)
                {
                    if (transaction.ResetDeactivation != null)
                    {
                        if (transaction.ResetDeactivation <= transaction.Level)
                            await UpdateDelegate(delegat, false);

                        delegat.DeactivationLevel = (int)transaction.ResetDeactivation;
                    }
                }

                sender.Balance += transaction.Amount;

                if (senderDelegate != null)
                {
                    senderDelegate.StakingBalance += transaction.Amount;
                }

                await Return(parentSender,
                    (transaction.StorageFee ?? 0) +
                    (transaction.AllocationFee ?? 0));

                if (parentDelegate != null)
                {
                    parentDelegate.StakingBalance += transaction.StorageFee ?? 0;
                    parentDelegate.StakingBalance += transaction.AllocationFee ?? 0;
                }
            }
            #endregion

            #region revert operation
            sender.TransactionsCount--;
            if (target != null && target != sender) target.TransactionsCount--;
            if (parentSender != sender && parentSender != target) parentSender.TransactionsCount--;
            #endregion

            Db.TransactionOps.Remove(transaction);
        }

        protected virtual bool HasAllocated(JsonElement result) => false;

        protected virtual async Task ResetGracePeriod(TransactionOperation transaction)
        {
            if (transaction.Target is Data.Models.Delegate delegat)
            {
                var newDeactivationLevel = delegat.Staked ? GracePeriod.Reset(transaction.Block) : GracePeriod.Init(transaction.Block);
                if (delegat.DeactivationLevel < newDeactivationLevel)
                {
                    if (delegat.DeactivationLevel <= transaction.Level)
                        await UpdateDelegate(delegat, true);

                    transaction.ResetDeactivation = delegat.DeactivationLevel;
                    delegat.DeactivationLevel = newDeactivationLevel;
                }
            }
        }

        protected virtual BlockEvents GetBlockEvents(Account target)
        {
            return target is Contract c && c.Kind == ContractKind.SmartContract
                ? BlockEvents.SmartContracts
                : BlockEvents.None;
        }
    }
}
