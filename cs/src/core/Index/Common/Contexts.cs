﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace FASTER.core
{
    internal enum OperationType
    {
        READ,
        RMW,
        UPSERT,
        DELETE
    }

    [Flags]
    internal enum OperationStatus
    {
        // Completed Status codes

        /// <summary>
        /// Operation completed successfully, and a record with the specified key was found.
        /// </summary>
        SUCCESS = StatusCode.Found,

        /// <summary>
        /// Operation completed successfully, and a record with the specified key was not found; the operation may have created a new one.
        /// </summary>
        NOTFOUND = StatusCode.NotFound,

        /// <summary>
        /// Operation was canceled by the client.
        /// </summary>
        CANCELED = StatusCode.Canceled,

        /// <summary>
        /// The maximum range that directly maps to the <see cref="StatusCode"/> enumeration; the operation completed. 
        /// This is an internal code to reserve ranges in the <see cref="OperationStatus"/> enumeration.
        /// </summary>
        MAX_MAP_TO_COMPLETED_STATUSCODE = CANCELED,

        // Not-completed Status codes

        /// <summary>
        /// Retry operation immediately, within the current epoch. This is only used in situations where another thread does not need to do another operation 
        /// to bring things into a consistent state.
        /// </summary>
        RETRY_NOW,

        /// <summary>
        /// Retry operation immediately, after refreshing the epoch. This is used in situations where another thread may have done an operation that requires it
        /// to do a subsequent operation to bring things into a consistent state; that subsequent operation may require <see cref="LightEpoch.BumpCurrentEpoch()"/>.
        /// </summary>
        RETRY_LATER,

        /// <summary>
        /// I/O has been enqueued and the caller must go through <see cref="IFasterContext{Key, Value, Input, Output, Context}.CompletePending(bool, bool)"/> or
        /// <see cref="IFasterContext{Key, Value, Input, Output, Context}.CompletePendingWithOutputs(out CompletedOutputIterator{Key, Value, Input, Output, Context}, bool, bool)"/>,
        /// or one of the Async forms.
        /// </summary>
        RECORD_ON_DISK,

        /// <summary>
        /// A checkpoint is in progress so the operation must be retried internally after refreshing the epoch and updating the session context version.
        /// </summary>
        CPR_SHIFT_DETECTED,

        /// <summary>
        /// Allocation failed, due to a need to flush pages. Clients do not see this status directly; they see <see cref="Status.IsPending"/>.
        /// <list type="bullet">
        ///   <item>For Sync operations we retry this as part of <see cref="FasterKV{Key, Value}.HandleImmediateRetryStatus{Input, Output, Context, FasterSession}(OperationStatus, FasterKV{Key, Value}.FasterExecutionContext{Input, Output, Context}, FasterKV{Key, Value}.FasterExecutionContext{Input, Output, Context}, FasterSession, ref FasterKV{Key, Value}.PendingContext{Input, Output, Context})"/>.</item>
        ///   <item>For Async operations we retry this as part of the ".Complete(...)" or ".CompleteAsync(...)" operation on the appropriate "*AsyncResult{}" object.</item>
        /// </list>
        /// </summary>
        ALLOCATE_FAILED,

        /// <summary>
        /// An internal code to reserve ranges in the <see cref="OperationStatus"/> enumeration.
        /// </summary>
        BASIC_MASK = 0xFF,      // Leave plenty of space for future expansion

        ADVANCED_MASK = 0x700,  // Coordinate any changes with OperationStatusUtils.OpStatusToStatusCodeShif
        CREATED_RECORD = StatusCode.CreatedRecord << OperationStatusUtils.OpStatusToStatusCodeShift,
        INPLACE_UPDATED_RECORD = StatusCode.InPlaceUpdatedRecord << OperationStatusUtils.OpStatusToStatusCodeShift,
        COPY_UPDATED_RECORD = StatusCode.CopyUpdatedRecord << OperationStatusUtils.OpStatusToStatusCodeShift,
        COPIED_RECORD = StatusCode.CopiedRecord << OperationStatusUtils.OpStatusToStatusCodeShift,
        COPIED_RECORD_TO_READ_CACHE = StatusCode.CopiedRecordToReadCache << OperationStatusUtils.OpStatusToStatusCodeShift,
        // unused (StatusCode)0x60,
        // unused (StatusCode)0x70,
        EXPIRED = StatusCode.Expired << OperationStatusUtils.OpStatusToStatusCodeShift
    }

    internal static class OperationStatusUtils
    {
        // StatusCode has this in the high nybble of the first (only) byte; put it in the low nybble of the second byte here).
        // Coordinate any changes with OperationStatus.ADVANCED_MASK.
        internal const int OpStatusToStatusCodeShift = 4;

        internal static OperationStatus BasicOpCode(OperationStatus status) => status & OperationStatus.BASIC_MASK;

        internal static OperationStatus AdvancedOpCode(OperationStatus status, StatusCode advancedStatusCode) => status | (OperationStatus)((int)advancedStatusCode << OpStatusToStatusCodeShift);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool TryConvertToCompletedStatusCode(OperationStatus advInternalStatus, out Status statusCode)
        {
            var internalStatus = BasicOpCode(advInternalStatus);
            if (internalStatus <= OperationStatus.MAX_MAP_TO_COMPLETED_STATUSCODE)
            {
                statusCode = new(advInternalStatus);
                return true;
            }
            statusCode = default;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool IsAppend(OperationStatus internalStatus)
        {
            var advInternalStatus = internalStatus & OperationStatus.ADVANCED_MASK;
            return advInternalStatus == OperationStatus.CREATED_RECORD || advInternalStatus == OperationStatus.COPY_UPDATED_RECORD;
        }
    }

    public partial class FasterKV<Key, Value> : FasterBase, IFasterKV<Key, Value>
    {
        internal struct PendingContext<Input, Output, Context>
        {
            // User provided information
            internal OperationType type;
            internal IHeapContainer<Key> key;
            internal IHeapContainer<Value> value;
            internal IHeapContainer<Input> input;
            internal Output output;
            internal Context userContext;

            // Some additional information about the previous attempt
            internal long id;
            internal long version;
            internal long logicalAddress;
            internal long serialNum;
            internal HashBucketEntry entry;

            internal ushort operationFlags;
            internal RecordInfo recordInfo;
            internal long minAddress;
            internal LockOperation lockOperation;

            // For flushing head pages on tail allocation.
            internal CompletionEvent flushEvent;

            // For RMW if an allocation caused the source record for a copy to go from readonly to below HeadAddress, or for any operation with CAS failure.
            internal long retryNewLogicalAddress;

            // BEGIN Must be kept in sync with corresponding ReadFlags enum values
            internal const ushort kDisableReadCacheUpdates = 0x0001;
            internal const ushort kDisableReadCacheReads = 0x0002;
            internal const ushort kCopyReadsToTail = 0x0004;
            internal const ushort kCopyFromDeviceOnly = 0x0008;
            internal const ushort kResetModifiedBit = 0x0010;
            // END  Must be kept in sync with corresponding ReadFlags enum values

            internal const ushort kNoKey = 0x0100;
            internal const ushort kIsAsync = 0x0200;

            // Flags for various operations passed at multiple levels, e.g. through RETRY_NOW.
            internal const ushort kUnused1 = 0x1000;
            internal const ushort kUnused2 = 0x2000;
            internal const ushort kUnused3 = 0x4000;
            internal const ushort kHasExpiration = 0x8000;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal IHeapContainer<Key> DetachKey()
            {
                var tempKeyContainer = this.key;
                this.key = default; // transfer ownership
                return tempKeyContainer;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal IHeapContainer<Input> DetachInput()
            {
                var tempInputContainer = this.input;
                this.input = default; // transfer ownership
                return tempInputContainer;
            }

            static PendingContext()
            {
                Debug.Assert((ushort)ReadFlags.DisableReadCacheUpdates >> 1 == kDisableReadCacheUpdates);
                Debug.Assert((ushort)ReadFlags.DisableReadCacheReads >> 1 == kDisableReadCacheReads);
                Debug.Assert((ushort)ReadFlags.CopyReadsToTail >> 1 == kCopyReadsToTail);
                Debug.Assert((ushort)ReadFlags.CopyFromDeviceOnly >> 1 == kCopyFromDeviceOnly);
                Debug.Assert((ushort)ReadFlags.ResetModifiedBit >> 1 == kResetModifiedBit);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static ushort GetOperationFlags(ReadFlags readFlags) 
                => (ushort)((int)(readFlags & (ReadFlags.DisableReadCacheUpdates | ReadFlags.DisableReadCacheReads | ReadFlags.CopyReadsToTail | ReadFlags.CopyFromDeviceOnly | ReadFlags.ResetModifiedBit)) >> 1);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static ushort GetOperationFlags(ReadFlags readFlags, bool noKey)
            {
                ushort flags = GetOperationFlags(readFlags);
                if (noKey)
                    flags |= kNoKey;
                return flags;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal void SetOperationFlags(ReadFlags readFlags, ref ReadOptions readOptions) => this.SetOperationFlags(GetOperationFlags(readFlags), readOptions.StopAddress);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal void SetOperationFlags(ReadFlags readFlags, ref ReadOptions readOptions, bool noKey) => this.SetOperationFlags(GetOperationFlags(readFlags, noKey), readOptions.StopAddress);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal void SetOperationFlags(ReadFlags readFlags) => this.operationFlags = GetOperationFlags(readFlags);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal void SetOperationFlags(ushort flags, long stopAddress)
            {
                // The async flag is often set when the PendingContext is created, so preserve that.
                this.operationFlags = (ushort)(flags | (this.operationFlags & kIsAsync));
                this.minAddress = stopAddress;
            }

            internal bool NoKey
            {
                get => (operationFlags & kNoKey) != 0;
                set => operationFlags = value ? (ushort)(operationFlags | kNoKey) : (ushort)(operationFlags & ~kNoKey);
            }

            internal bool DisableReadCacheUpdates => (operationFlags & kDisableReadCacheUpdates) != 0;

            internal bool DisableReadCacheReads => (operationFlags & kDisableReadCacheReads) != 0;

            internal bool CopyReadsToTail => (operationFlags & kCopyReadsToTail) != 0;

            internal bool CopyReadsToTailFromReadOnly => (operationFlags & (kCopyReadsToTail | kCopyFromDeviceOnly)) == kCopyReadsToTail;

            internal bool CopyFromDeviceOnly => (operationFlags & kCopyFromDeviceOnly) != 0;

            internal bool ResetModifiedBit => (operationFlags & kResetModifiedBit) != 0;

            internal bool HasMinAddress => this.minAddress != Constants.kInvalidAddress;

            internal bool IsAsync
            {
                get => (operationFlags & kIsAsync) != 0;
                set => operationFlags = value ? (ushort)(operationFlags | kIsAsync) : (ushort)(operationFlags & ~kIsAsync);
            }

            internal long PrevHighestKeyHashAddress
            {
                get => recordInfo.PreviousAddress;
                set => recordInfo.PreviousAddress = value;
            }

            internal long PrevLatestLogicalAddress
            {
                get => entry.word;
                set => entry.word = value;
            }

            public void Dispose()
            {
                key?.Dispose();
                key = default;
                value?.Dispose();
                value = default;
                input?.Dispose();
                input = default;
            }
        }

        internal sealed class FasterExecutionContext<Input, Output, Context>
        {
            internal int sessionID;
            internal string sessionName;

            // Control Read operations. These flags override flags specified at the FasterKV level, but may be overridden on the individual Read() operations
            internal ReadFlags ReadFlags;

            internal long version;
            internal long serialNum;
            public Phase phase;

            public bool[] markers;
            public long totalPending;
            public Dictionary<long, PendingContext<Input, Output, Context>> ioPendingRequests;
            public AsyncCountDown pendingReads;
            public AsyncQueue<AsyncIOContext<Key, Value>> readyResponses;
            public List<long> excludedSerialNos;
            public int asyncPendingCount;
            public ISynchronizationStateMachine threadStateMachine;

            public int SyncIoPendingCount => ioPendingRequests.Count - asyncPendingCount;

            public bool HasNoPendingRequests
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get
                {
                    return SyncIoPendingCount == 0;
                }
            }

            public void WaitPending(LightEpoch epoch)
            {
                if (SyncIoPendingCount > 0)
                {
                    try
                    {
                        epoch.Suspend();
                        readyResponses.WaitForEntry();
                    }
                    finally
                    {
                        epoch.Resume();
                    }
                }
            }

            public async ValueTask WaitPendingAsync(CancellationToken token = default)
            {
                if (SyncIoPendingCount > 0)
                    await readyResponses.WaitForEntryAsync(token).ConfigureAwait(false);
            }

            public bool InNewVersion => phase < Phase.REST;

            public FasterExecutionContext<Input, Output, Context> prevCtx;
        }
    }

    /// <summary>
    /// Descriptor for a CPR commit point
    /// </summary>
    public struct CommitPoint
    {
        /// <summary>
        /// Serial number until which we have committed
        /// </summary>
        public long UntilSerialNo;

        /// <summary>
        /// List of operation serial nos excluded from commit
        /// </summary>
        public List<long> ExcludedSerialNos;
    }

    /// <summary>
    /// Recovery info for hybrid log
    /// </summary>
    public struct HybridLogRecoveryInfo
    {
        const int CheckpointVersion = 4;

        /// <summary>
        /// Guid
        /// </summary>
        public Guid guid;
        /// <summary>
        /// Use snapshot file
        /// </summary>
        public int useSnapshotFile;
        /// <summary>
        /// Version
        /// </summary>
        public long version;
        /// <summary>
        /// Next Version
        /// </summary>
        public long nextVersion;
        /// <summary>
        /// Flushed logical address; indicates the latest immutable address on the main FASTER log at recovery time.
        /// </summary>
        public long flushedLogicalAddress;
        /// <summary>
        /// Start logical address
        /// </summary>
        public long startLogicalAddress;
        /// <summary>
        /// Final logical address
        /// </summary>
        public long finalLogicalAddress;
        /// <summary>
        /// Snapshot end logical address: snaphot is [startLogicalAddress, snapshotFinalLogicalAddress)
        /// Note that finalLogicalAddress may be higher due to delta records
        /// </summary>
        public long snapshotFinalLogicalAddress;
        /// <summary>
        /// Head address
        /// </summary>
        public long headAddress;
        /// <summary>
        /// Begin address
        /// </summary>
        public long beginAddress;

        /// <summary>
        /// If true, there was at least one IFasterContext implementation active that did manual locking at some point during the checkpoint;
        /// these pages must be scanned for lock cleanup.
        /// </summary>
        public bool manualLockingActive;

        /// <summary>
        /// Commit tokens per session restored during Restore()
        /// </summary>
        public ConcurrentDictionary<int, (string, CommitPoint)> continueTokens;

        /// <summary>
        /// Map of session name to session ID restored during Restore()
        /// </summary>
        public ConcurrentDictionary<string, int> sessionNameMap;

        /// <summary>
        /// Commit tokens per session created during Checkpoint
        /// </summary>
        public ConcurrentDictionary<int, (string, CommitPoint)> checkpointTokens;

        /// <summary>
        /// Max session ID
        /// </summary>
        public int maxSessionID;

        /// <summary>
        /// Object log segment offsets
        /// </summary>
        public long[] objectLogSegmentOffsets;


        /// <summary>
        /// Tail address of delta file
        /// </summary>
        public long deltaTailAddress;

        /// <summary>
        /// Initialize
        /// </summary>
        /// <param name="token"></param>
        /// <param name="_version"></param>
        public void Initialize(Guid token, long _version)
        {
            guid = token;
            useSnapshotFile = 0;
            version = _version;
            flushedLogicalAddress = 0;
            startLogicalAddress = 0;
            finalLogicalAddress = 0;
            snapshotFinalLogicalAddress = 0;
            deltaTailAddress = 0;
            headAddress = 0;

            checkpointTokens = new();

            objectLogSegmentOffsets = null;
        }

        /// <summary>
        /// Initialize from stream
        /// </summary>
        /// <param name="reader"></param>
        public void Initialize(StreamReader reader)
        {
            continueTokens = new();

            string value = reader.ReadLine();
            var cversion = int.Parse(value);

            value = reader.ReadLine();
            var checksum = long.Parse(value);

            value = reader.ReadLine();
            guid = Guid.Parse(value);

            value = reader.ReadLine();
            useSnapshotFile = int.Parse(value);

            value = reader.ReadLine();
            version = long.Parse(value);

            value = reader.ReadLine();
            nextVersion = long.Parse(value);

            value = reader.ReadLine();
            flushedLogicalAddress = long.Parse(value);

            value = reader.ReadLine();
            startLogicalAddress = long.Parse(value);

            value = reader.ReadLine();
            finalLogicalAddress = long.Parse(value);

            value = reader.ReadLine();
            snapshotFinalLogicalAddress = long.Parse(value);

            value = reader.ReadLine();
            headAddress = long.Parse(value);

            value = reader.ReadLine();
            beginAddress = long.Parse(value);

            value = reader.ReadLine();
            deltaTailAddress = long.Parse(value);

            value = reader.ReadLine();
            manualLockingActive = bool.Parse(value);

            value = reader.ReadLine();
            var numSessions = int.Parse(value);

            for (int i = 0; i < numSessions; i++)
            {
                var sessionID = int.Parse(reader.ReadLine());
                var sessionName = reader.ReadLine();
                if (sessionName == "") sessionName = null;
                var serialno = long.Parse(reader.ReadLine());

                var exclusions = new List<long>();
                var exclusionCount = int.Parse(reader.ReadLine());
                for (int j = 0; j < exclusionCount; j++)
                    exclusions.Add(long.Parse(reader.ReadLine()));

                continueTokens.TryAdd(sessionID, (sessionName, new CommitPoint
                {
                    UntilSerialNo = serialno,
                    ExcludedSerialNos = exclusions
                }));
                if (sessionName != null)
                {
                    sessionNameMap ??= new();
                    sessionNameMap.TryAdd(sessionName, sessionID);
                }
                if (sessionID > maxSessionID) maxSessionID = sessionID;
            }

            // Read object log segment offsets
            value = reader.ReadLine();
            var numSegments = int.Parse(value);
            if (numSegments > 0)
            {
                objectLogSegmentOffsets = new long[numSegments];
                for (int i = 0; i < numSegments; i++)
                {
                    value = reader.ReadLine();
                    objectLogSegmentOffsets[i] = long.Parse(value);
                }
            }

            if (cversion != CheckpointVersion)
                throw new FasterException("Invalid version");

            if (checksum != Checksum(continueTokens.Count))
                throw new FasterException("Invalid checksum for checkpoint");
        }

        /// <summary>
        ///  Recover info from token
        /// </summary>
        /// <param name="token"></param>
        /// <param name="checkpointManager"></param>
        /// <param name="deltaLog"></param>
        /// <param name = "scanDelta">
        /// whether to scan the delta log to obtain the latest info contained in an incremental snapshot checkpoint.
        /// If false, this will recover the base snapshot info but avoid potentially expensive scans.
        /// </param>
        /// <param name="recoverTo"> specific version to recover to, if using delta log</param>
        internal void Recover(Guid token, ICheckpointManager checkpointManager, DeltaLog deltaLog = null, bool scanDelta = false, long recoverTo = -1)
        {
            var metadata = checkpointManager.GetLogCheckpointMetadata(token, deltaLog, scanDelta, recoverTo);
            if (metadata == null)
                throw new FasterException("Invalid log commit metadata for ID " + token.ToString());
            using StreamReader s = new(new MemoryStream(metadata));
            Initialize(s);
        }
        
        /// <summary>
        ///  Recover info from token
        /// </summary>
        /// <param name="token"></param>
        /// <param name="checkpointManager"></param>
        /// <param name="deltaLog"></param>
        /// <param name="commitCookie"> Any user-specified commit cookie written as part of the checkpoint </param>
        /// <param name = "scanDelta">
        /// whether to scan the delta log to obtain the latest info contained in an incremental snapshot checkpoint.
        /// If false, this will recover the base snapshot info but avoid potentially expensive scans.
        /// </param>
        /// <param name="recoverTo"> specific version to recover to, if using delta log</param>

        internal void Recover(Guid token, ICheckpointManager checkpointManager, out byte[] commitCookie, DeltaLog deltaLog = null, bool scanDelta = false, long recoverTo = -1)
        {
            var metadata = checkpointManager.GetLogCheckpointMetadata(token, deltaLog, scanDelta, recoverTo);
            if (metadata == null)
                throw new FasterException("Invalid log commit metadata for ID " + token.ToString());
            using StreamReader s = new(new MemoryStream(metadata));
            Initialize(s);
            var cookie = s.ReadToEnd();
            commitCookie =  cookie.Length == 0 ? null : Convert.FromBase64String(cookie);
        }

        /// <summary>
        /// Write info to byte array
        /// </summary>
        public byte[] ToByteArray()
        {
            using (MemoryStream ms = new())
            {
                using (StreamWriter writer = new(ms))
                {
                    writer.WriteLine(CheckpointVersion); // checkpoint version
                    writer.WriteLine(Checksum(checkpointTokens.Count)); // checksum

                    writer.WriteLine(guid);
                    writer.WriteLine(useSnapshotFile);
                    writer.WriteLine(version);
                    writer.WriteLine(nextVersion);
                    writer.WriteLine(flushedLogicalAddress);
                    writer.WriteLine(startLogicalAddress);
                    writer.WriteLine(finalLogicalAddress);
                    writer.WriteLine(snapshotFinalLogicalAddress);
                    writer.WriteLine(headAddress);
                    writer.WriteLine(beginAddress);
                    writer.WriteLine(deltaTailAddress);
                    writer.WriteLine(manualLockingActive);

                    writer.WriteLine(checkpointTokens.Count);
                    foreach (var kvp in checkpointTokens)
                    {
                        writer.WriteLine(kvp.Key);
                        writer.WriteLine(kvp.Value.Item1);
                        writer.WriteLine(kvp.Value.Item2.UntilSerialNo);
                        writer.WriteLine(kvp.Value.Item2.ExcludedSerialNos.Count);
                        foreach (long item in kvp.Value.Item2.ExcludedSerialNos)
                            writer.WriteLine(item);
                    }

                    // Write object log segment offsets
                    writer.WriteLine(objectLogSegmentOffsets == null ? 0 : objectLogSegmentOffsets.Length);
                    if (objectLogSegmentOffsets != null)
                    {
                        for (int i = 0; i < objectLogSegmentOffsets.Length; i++)
                        {
                            writer.WriteLine(objectLogSegmentOffsets[i]);
                        }
                    }
                }
                return ms.ToArray();
            }
        }

        private readonly long Checksum(int checkpointTokensCount)
        {
            var bytes = guid.ToByteArray();
            var long1 = BitConverter.ToInt64(bytes, 0);
            var long2 = BitConverter.ToInt64(bytes, 8);
            return long1 ^ long2 ^ version ^ flushedLogicalAddress ^ startLogicalAddress ^ finalLogicalAddress ^ snapshotFinalLogicalAddress ^ headAddress ^ beginAddress
                ^ checkpointTokensCount ^ (objectLogSegmentOffsets == null ? 0 : objectLogSegmentOffsets.Length);
        }

        /// <summary>
        /// Print checkpoint info for debugging purposes
        /// </summary>
        public readonly void DebugPrint(ILogger logger)
        {
            logger?.LogInformation("******** HybridLog Checkpoint Info for {guid} ********", guid);
            logger?.LogInformation("Version: {version}", version);
            logger?.LogInformation("Next Version: {nextVersion}", nextVersion);
            logger?.LogInformation("Is Snapshot?: {useSnapshotFile}", useSnapshotFile == 1);
            logger?.LogInformation("Flushed LogicalAddress: {flushedLogicalAddress}", flushedLogicalAddress);
            logger?.LogInformation("Start Logical Address: {startLogicalAddress}", startLogicalAddress);
            logger?.LogInformation("Final Logical Address: {finalLogicalAddress}", finalLogicalAddress);
            logger?.LogInformation("Snapshot Final Logical Address: {snapshotFinalLogicalAddress}", snapshotFinalLogicalAddress);
            logger?.LogInformation("Head Address: {headAddress}", headAddress);
            logger?.LogInformation("Begin Address: {beginAddress}", beginAddress);
            logger?.LogInformation("Delta Tail Address: {deltaTailAddress}", deltaTailAddress);
            logger?.LogInformation("Manual Locking Active: {manualLockingActive}", manualLockingActive);
            logger?.LogInformation("Num sessions recovered: {continueTokensCount}", continueTokens.Count);
            logger?.LogInformation("Recovered sessions: ");
            foreach (var sessionInfo in continueTokens.Take(10))
            {
                logger?.LogInformation("{sessionInfo.Key}: {sessionInfo.Value}", sessionInfo.Key, sessionInfo.Value);
            }

            if (continueTokens.Count > 10)
                logger?.LogInformation("... {continueTokensSkipped} skipped", continueTokens.Count - 10);
        }
    }

    internal struct HybridLogCheckpointInfo : IDisposable
    {
        public HybridLogRecoveryInfo info;
        public IDevice snapshotFileDevice;
        public IDevice snapshotFileObjectLogDevice;
        public IDevice deltaFileDevice;
        public DeltaLog deltaLog;
        public SemaphoreSlim flushedSemaphore;
        public long prevVersion;

        public void Initialize(Guid token, long _version, ICheckpointManager checkpointManager)
        {
            info.Initialize(token, _version);
            checkpointManager.InitializeLogCheckpoint(token);
        }

        public void Dispose()
        {
            snapshotFileDevice?.Dispose();
            snapshotFileObjectLogDevice?.Dispose();
            deltaLog?.Dispose();
            deltaFileDevice?.Dispose();
            this = default;
        }

        public HybridLogCheckpointInfo Transfer()
        {
            // Ownership transfer of handles across struct copies
            var dest = this;
            dest.snapshotFileDevice = default;
            dest.snapshotFileObjectLogDevice = default;
            this.deltaLog = default;
            this.deltaFileDevice = default;
            return dest;
        }

        public void Recover(Guid token, ICheckpointManager checkpointManager, int deltaLogPageSizeBits,
            bool scanDelta = false, long recoverTo = -1)
        {
            deltaFileDevice = checkpointManager.GetDeltaLogDevice(token);
            if (deltaFileDevice is not null)
            {
                deltaFileDevice.Initialize(-1);
                if (deltaFileDevice.GetFileSize(0) > 0)
                {
                    deltaLog = new DeltaLog(deltaFileDevice, deltaLogPageSizeBits, -1);
                    deltaLog.InitializeForReads();
                    info.Recover(token, checkpointManager, deltaLog, scanDelta, recoverTo);
                    return;
                }
            }
            info.Recover(token, checkpointManager, null);
        }

        public void Recover(Guid token, ICheckpointManager checkpointManager, int deltaLogPageSizeBits,
            out byte[] commitCookie, bool scanDelta = false, long recoverTo = -1)
        {
            deltaFileDevice = checkpointManager.GetDeltaLogDevice(token);
            if (deltaFileDevice is not null)
            {
                deltaFileDevice.Initialize(-1);
                if (deltaFileDevice.GetFileSize(0) > 0)
                {
                    deltaLog = new DeltaLog(deltaFileDevice, deltaLogPageSizeBits, -1);
                    deltaLog.InitializeForReads();
                    info.Recover(token, checkpointManager, out commitCookie, deltaLog, scanDelta, recoverTo);
                    return;
                }
            }
            info.Recover(token, checkpointManager, out commitCookie);
        }

        public bool IsDefault()
        {
            return info.guid == default;
        }
    }

    internal struct IndexRecoveryInfo
    {
        const int CheckpointVersion = 1;
        public Guid token;
        public long table_size;
        public ulong num_ht_bytes;
        public ulong num_ofb_bytes;
        public int num_buckets;
        public long startLogicalAddress;
        public long finalLogicalAddress;

        public void Initialize(Guid token, long _size)
        {
            this.token = token;
            table_size = _size;
            num_ht_bytes = 0;
            num_ofb_bytes = 0;
            startLogicalAddress = 0;
            finalLogicalAddress = 0;
            num_buckets = 0;
        }

        public void Initialize(StreamReader reader)
        {
            string value = reader.ReadLine();
            var cversion = int.Parse(value);

            value = reader.ReadLine();
            var checksum = long.Parse(value);

            value = reader.ReadLine();
            token = Guid.Parse(value);

            value = reader.ReadLine();
            table_size = long.Parse(value);

            value = reader.ReadLine();
            num_ht_bytes = ulong.Parse(value);

            value = reader.ReadLine();
            num_ofb_bytes = ulong.Parse(value);

            value = reader.ReadLine();
            num_buckets = int.Parse(value);

            value = reader.ReadLine();
            startLogicalAddress = long.Parse(value);

            value = reader.ReadLine();
            finalLogicalAddress = long.Parse(value);

            if (cversion != CheckpointVersion)
                throw new FasterException("Invalid version");

            if (checksum != Checksum())
                throw new FasterException("Invalid checksum for checkpoint");
        }

        public void Recover(Guid guid, ICheckpointManager checkpointManager)
        {
            this.token = guid;
            var metadata = checkpointManager.GetIndexCheckpointMetadata(guid);
            if (metadata == null)
                throw new FasterException("Invalid index commit metadata for ID " + guid.ToString());
            using (StreamReader s = new(new MemoryStream(metadata)))
                Initialize(s);
        }

        public readonly byte[] ToByteArray()
        {
            using (MemoryStream ms = new())
            {
                using (StreamWriter writer = new(ms))
                {
                    writer.WriteLine(CheckpointVersion); // checkpoint version
                    writer.WriteLine(Checksum()); // checksum

                    writer.WriteLine(token);
                    writer.WriteLine(table_size);
                    writer.WriteLine(num_ht_bytes);
                    writer.WriteLine(num_ofb_bytes);
                    writer.WriteLine(num_buckets);
                    writer.WriteLine(startLogicalAddress);
                    writer.WriteLine(finalLogicalAddress);
                }
                return ms.ToArray();
            }
        }

        private readonly long Checksum()
        {
            var bytes = token.ToByteArray();
            var long1 = BitConverter.ToInt64(bytes, 0);
            var long2 = BitConverter.ToInt64(bytes, 8);
            return long1 ^ long2 ^ table_size ^ (long)num_ht_bytes ^ (long)num_ofb_bytes
                        ^ num_buckets ^ startLogicalAddress ^ finalLogicalAddress;
        }

        public readonly void DebugPrint(ILogger logger)
        {
            logger?.LogInformation("******** Index Checkpoint Info for {token} ********", token);
            logger?.LogInformation("Table Size: {table_size}", table_size);
            logger?.LogInformation("Main Table Size (in GB): {num_ht_bytes}", ((double)num_ht_bytes) / 1000.0 / 1000.0 / 1000.0);
            logger?.LogInformation("Overflow Table Size (in GB): {num_ofb_bytes}", ((double)num_ofb_bytes) / 1000.0 / 1000.0 / 1000.0);
            logger?.LogInformation("Num Buckets: {num_buckets}", num_buckets);
            logger?.LogInformation("Start Logical Address: {startLogicalAddress}", startLogicalAddress);
            logger?.LogInformation("Final Logical Address: {finalLogicalAddress}", finalLogicalAddress);
        }

        public void Reset()
        {
            token = default;
            table_size = 0;
            num_ht_bytes = 0;
            num_ofb_bytes = 0;
            num_buckets = 0;
            startLogicalAddress = 0;
            finalLogicalAddress = 0;
        }
    }

    internal struct IndexCheckpointInfo
    {
        public IndexRecoveryInfo info;
        public IDevice main_ht_device;

        public void Initialize(Guid token, long _size, ICheckpointManager checkpointManager)
        {
            info.Initialize(token, _size);
            checkpointManager.InitializeIndexCheckpoint(token);
            main_ht_device = checkpointManager.GetIndexDevice(token);
        }

        public void Recover(Guid token, ICheckpointManager checkpointManager)
        {
            info.Recover(token, checkpointManager);
        }

        public void Reset()
        {
            info = default;
            main_ht_device?.Dispose();
            main_ht_device = null;
        }

        public bool IsDefault()
        {
            return info.token == default;
        }
    }
}
