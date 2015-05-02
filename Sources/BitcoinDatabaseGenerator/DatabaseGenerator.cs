﻿//-----------------------------------------------------------------------
// <copyright file="DatabaseGenerator.cs">
// Copyright © Ladislau Molnar. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace BitcoinDatabaseGenerator
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.Linq;
    using System.Threading.Tasks;
    using BitcoinBlockchain.Parser;
    using BitcoinDataLayerAdoNet;
    using BitcoinDataLayerAdoNet.DataSets;
    using ZeroHelpers;
    using ZeroHelpers.Exceptions;
    using DBData = BitcoinDataLayerAdoNet.Data;
    using ParserData = BitcoinBlockchain.Data;

    public class DatabaseGenerator
    {
        public const long BtcToSatoshi = 100000000;

        private readonly IDatabaseGeneratorParameters parameters;
        private readonly DatabaseConnection databaseConnection;
        private readonly ProcessingStatistics processingStatistics;
        private readonly Func<IBlockchainParser> blockchainParserFactory;

        private int lastReportedPercentage;
        private string currentBlockchainFile;

        public DatabaseGenerator(IDatabaseGeneratorParameters parameters, DatabaseConnection databaseConnection, Func<IBlockchainParser> blockchainParserFactory = null)
        {
            this.parameters = parameters;
            this.blockchainParserFactory = blockchainParserFactory;

            this.databaseConnection = databaseConnection;
            this.processingStatistics = new ProcessingStatistics();
        }

        public async Task GenerateAndPopulateDatabase()
        {
            bool newDatabase = false;

            this.processingStatistics.PreprocessingStarting();

            if (this.parameters.SkipDbManagement == false)
            {
                newDatabase = this.PrepareDatabase();
            }

            string lastKnownBlockchainFileName = null;
            lastKnownBlockchainFileName = this.GetLastKnownBlockchainFileName();

            if (lastKnownBlockchainFileName != null)
            {
                Console.WriteLine("Deleting from database information about blockchain file: {0}", lastKnownBlockchainFileName);
                await this.DeleteLastBlockFileAsync();
            }

            Console.WriteLine();
            await this.TransferBlockchainDataAsync(lastKnownBlockchainFileName, newDatabase);

            this.processingStatistics.PostProcessingStarting();

            Console.WriteLine();

            if (newDatabase)
            {
                this.CreateDatabaseIndexes();
            }

            this.DeleteOrphanBlocks();

            this.UpdateTransactionSourceOutputId();

            this.processingStatistics.ProcessingCompleted();

            this.processingStatistics.DisplayStatistics();
            this.DisplayDatabaseStatistics();
        }

        private static void ConvertParserBlockToBlockInfo(BlockInfo blockInfo, DatabaseIdManager databaseIdManager, ParserData.Block parserBlock)
        {
            long blockId = databaseIdManager.GetNextBlockId();

            blockInfo.BlockDataTable.AddBlockRow(
                blockId,
                databaseIdManager.CurrentBlockFileId,
                (int)parserBlock.BlockHeader.BlockVersion,
                parserBlock.BlockHeader.BlockHash.ToArray(),
                parserBlock.BlockHeader.PreviousBlockHash.ToArray(),
                parserBlock.BlockHeader.BlockTimestamp);

            foreach (ParserData.Transaction parserTransaction in parserBlock.Transactions)
            {
                long bitcoinTransactionId = databaseIdManager.GetNextTransactionId();

                blockInfo.BitcoinTransactionDataTable.AddBitcoinTransactionRow(
                    bitcoinTransactionId,
                    blockId,
                    parserTransaction.TransactionHash.ToArray(),
                    (int)parserTransaction.TransactionVersion,
                    (int)parserTransaction.TransactionLockTime);

                foreach (ParserData.TransactionInput parserTransactionInput in parserTransaction.Inputs)
                {
                    long transactionInput = databaseIdManager.GetNextTransactionInputId();

                    blockInfo.TransactionInputDataTable.AddTransactionInputRow(
                        transactionInput,
                        bitcoinTransactionId,
                        DBData.TransactionInput.SourceTransactionOutputIdUnknown);

                    blockInfo.TransactionInputSourceDataTable.AddTransactionInputSourceRow(
                            transactionInput,
                            parserTransactionInput.SourceTransactionHash.ToArray(),
                            (int)parserTransactionInput.SourceTransactionOutputIndex);
                }

                for (int outputIndex = 0; outputIndex < parserTransaction.Outputs.Count; outputIndex++)
                {
                    ParserData.TransactionOutput parserTransactionOutput = parserTransaction.Outputs[outputIndex];
                    long transactionOutputId = databaseIdManager.GetNextTransactionOutputId();

                    blockInfo.TransactionOutputDataTable.AddTransactionOutputRow(
                        transactionOutputId,
                        bitcoinTransactionId,
                        outputIndex,
                        (decimal)parserTransactionOutput.OutputValueSatoshi / BtcToSatoshi,
                        parserTransactionOutput.OutputScript.ToArray());
                }
            }
        }

        private static List<long> GetOrphanBlockIds(BitcoinDataLayer bitcoinDataLayer)
        {
            SummaryBlockDataSet summaryBlockDataSet = bitcoinDataLayer.GetSummaryBlockDataSet();

            // KEY:     The 256-bit hash of the block
            // VALUE:   The summary block data as represented by an instance of DBData.SummaryBlockDataSet.BlockRow.
            Dictionary<ParserData.ByteArray, SummaryBlockDataSet.SummaryBlockRow> blockDictionary = summaryBlockDataSet.SummaryBlock.ToDictionary(
                b => new ParserData.ByteArray(b.BlockHash),
                b => b);

            SummaryBlockDataSet.SummaryBlockRow lastBlock = summaryBlockDataSet.SummaryBlock.OrderByDescending(b => b.BlockId).First();
            ParserData.ByteArray previousBlockHash = ParserData.ByteArray.Empty;
            ParserData.ByteArray currentBlockHash = new ParserData.ByteArray(lastBlock.BlockHash);

            // A hashset containing the IDs of all active blocks. Active as in non orphan.
            HashSet<long> activeBlockIds = new HashSet<long>();

            // Loop through blocks starting from the last one and going from one block to the next as indicated by PreviousBlockHash.
            // Collect all the block IDs for the blocks that we go through in activeBlockIds.
            // After this loop, blocks that we did not loop through are orphan blocks.
            while (currentBlockHash.IsZeroArray() == false)
            {
                SummaryBlockDataSet.SummaryBlockRow summaryBlockRow;
                if (blockDictionary.TryGetValue(currentBlockHash, out summaryBlockRow))
                {
                    // The current block was found in the list of blocks. 
                    activeBlockIds.Add(summaryBlockRow.BlockId);
                    previousBlockHash = currentBlockHash;
                    currentBlockHash = new ParserData.ByteArray(summaryBlockRow.PreviousBlockHash);
                }
                else
                {
                    // The current block was not found in the list of blocks. 
                    // This should never happen for a valid blockchain content.
                    throw new InvalidBlockchainContentException(string.Format(CultureInfo.InvariantCulture, "Block with hash [{0}] makes a reference to an unknown block with hash: [{1}]", previousBlockHash, currentBlockHash));
                }
            }

            // At this point activeBlockIds  contains the IDs of all active blocks.
            // Parse the list of all blocks and collect those whose IDs are not in activeBlockIds.
            return (from sumaryBlockRow in summaryBlockDataSet.SummaryBlock
                    where activeBlockIds.Contains(sumaryBlockRow.BlockId) == false
                    select sumaryBlockRow.BlockId).ToList();
        }

        private void DisplayDatabaseStatistics()
        {
            using (BitcoinDataLayer bitcoinDataLayer = new BitcoinDataLayer(this.databaseConnection.ConnectionString))
            {
                int blockFileCount;
                int blockCount;
                int transactionCount;
                int transactionInputCount;
                int transactionOutputCount;

                bitcoinDataLayer.GetDatabaseEntitiesCount(out blockFileCount, out blockCount, out transactionCount, out transactionInputCount, out transactionOutputCount);

                Console.WriteLine();
                Console.WriteLine("Database information:");
                Console.WriteLine();
                Console.WriteLine("               Block Files: {0,14:n0}", blockFileCount);
                Console.WriteLine("                    Blocks: {0,14:n0}", blockCount);
                Console.WriteLine("              Transactions: {0,14:n0}", transactionCount);
                Console.WriteLine("        Transaction Inputs: {0,14:n0}", transactionInputCount);
                Console.WriteLine("       Transaction Outputs: {0,14:n0}", transactionOutputCount);
            }
        }

        /// <summary>
        /// Prepares the database ensuring it exists. 
        /// If the command line parameters so indicate, it will drop and recreate the database.
        /// </summary>
        /// <returns>
        /// True  - We start from a new database.
        /// False - We start from an existing database.
        /// </returns>
        private bool PrepareDatabase()
        {
            DatabaseManager databaseManager = new DatabaseManager(this.databaseConnection);

            if (this.parameters.DropDb)
            {
                if (databaseManager.DeleteDatabaseIfExists())
                {
                    Console.WriteLine("Database {0} was deleted.", this.databaseConnection.DatabaseName);
                }
            }

            if (databaseManager.EnsureDatabaseExists())
            {
                Console.WriteLine("Database {0} was created.", this.databaseConnection.DatabaseName);
                return true;
            }
            else
            {
                Console.WriteLine("Database {0} will be updated.", this.databaseConnection.DatabaseName);
                return false;
            }
        }

        private void CreateDatabaseIndexes()
        {
            Stopwatch createDatabaseIndexesWatch = new Stopwatch();
            createDatabaseIndexesWatch.Start();

            Console.Write("Create database indexes...");

            DatabaseManager databaseManager = new DatabaseManager(this.databaseConnection);
            databaseManager.CreateDatabaseIndexes();

            createDatabaseIndexesWatch.Stop();

            Console.WriteLine("\rDatabase indexes created successfully in {0:#.000} seconds", createDatabaseIndexesWatch.Elapsed.TotalSeconds);
        }

        private void UpdateTransactionSourceOutputId()
        {
            Stopwatch updateTransactionSourceOutputWatch = new Stopwatch();
            updateTransactionSourceOutputWatch.Start();

            Console.Write("Updating Transaction input source information...");

            using (BitcoinDataLayer bitcoinDataLayer = new BitcoinDataLayer(this.databaseConnection.ConnectionString, 3600))
            {
                long rowsToUpdateCommand = bitcoinDataLayer.GetTransactionSourceOutputRowsToUpdate();

                long totalRowsUpdated = bitcoinDataLayer.UpdateNullTransactionSources();
                Console.Write("\rUpdating Transaction Input Source information... {0}%", 95 * totalRowsUpdated / rowsToUpdateCommand);

                int rowsUpdated;
                while ((rowsUpdated = bitcoinDataLayer.UpdateTransactionSourceBatch()) > 0)
                {
                    totalRowsUpdated += rowsUpdated;
                    Console.Write("\rUpdating Transaction Input Source information... {0}%", 95 * totalRowsUpdated / rowsToUpdateCommand);
                }

                bitcoinDataLayer.FixupTransactionSourceOutputIdForDuplicateTransactionHash();
            }

            updateTransactionSourceOutputWatch.Stop();

            Console.WriteLine("\rUpdating Transaction Input Source information completed in {0:#.000} seconds", updateTransactionSourceOutputWatch.Elapsed.TotalSeconds);
        }

        private void DeleteOrphanBlocks()
        {
            Stopwatch deleteOrphanBlocksWatch = new Stopwatch();
            deleteOrphanBlocksWatch.Start();

            Console.Write("Searching for orphan blocks in the database...");

            using (BitcoinDataLayer bitcoinDataLayer = new BitcoinDataLayer(this.databaseConnection.ConnectionString))
            {
                List<long> orphanBlocksIds = GetOrphanBlockIds(bitcoinDataLayer);

                if (orphanBlocksIds.Count > 0)
                {
                    // Now delete all orphan blocks
                    bitcoinDataLayer.DeleteBlocks(orphanBlocksIds);

                    // Update the block IDs after deleting the orphan blocks so that the block IDs are forming a consecutive sequence.
                    bitcoinDataLayer.CompactBlockIds(orphanBlocksIds);
                }

                deleteOrphanBlocksWatch.Stop();

                if (orphanBlocksIds.Count == 0)
                {
                    Console.WriteLine("\rNo orphan blocks were found. The search took {0:#.000} seconds.", deleteOrphanBlocksWatch.Elapsed.TotalSeconds);
                }
                else
                {
                    Console.WriteLine("\r{0} orphan blocks were found and deleted in {1:#.000} seconds.", orphanBlocksIds.Count, deleteOrphanBlocksWatch.Elapsed.TotalSeconds);
                }
            }
        }

        private string GetLastKnownBlockchainFileName()
        {
            using (BitcoinDataLayer bitcoinDataLayer = new BitcoinDataLayer(this.databaseConnection.ConnectionString))
            {
                return bitcoinDataLayer.GetLastKnownBlockchainFileName();
            }
        }

        /// <summary>
        /// Deletes asynchronously from the database all the information associated with the last blockchain file.
        /// </summary>
        /// <returns>
        /// A task representing the asynchronous operation.
        /// </returns>
        private async Task DeleteLastBlockFileAsync()
        {
            using (BitcoinDataLayer bitcoinDataLayer = new BitcoinDataLayer(this.databaseConnection.ConnectionString))
            {
                await bitcoinDataLayer.DeleteLastBlockFileAsync();
            }
        }

        private async Task TransferBlockchainDataAsync(string lastKnownBlockchainFileName, bool newDatabase)
        {
            DatabaseIdManager databaseIdManager = this.GetDatabaseIdManager();
            TaskDispatcher taskDispatcher = new TaskDispatcher(this.parameters.Threads);    // What if we use 1 thread now that we use bulk copy?

            IBlockchainParser blockchainParser;
            if (this.blockchainParserFactory == null)
            {
                blockchainParser = new BlockchainParser(this.parameters.BlockchainPath, lastKnownBlockchainFileName);
            }
            else
            {
                blockchainParser = this.blockchainParserFactory();
            }

            this.processingStatistics.ProcessingBlockchainStarting();

            Stopwatch currentBlockchainFileStopwatch = new Stopwatch();
            currentBlockchainFileStopwatch.Start();

            BlockInfo blockInfo = new BlockInfo();

            foreach (ParserData.Block block in blockchainParser.ParseBlockchain())
            {
                if (this.currentBlockchainFile != block.BlockchainFileName)
                {
                    if (this.currentBlockchainFile != null)
                    {
                        await this.FinalizeBlockchainFileProcessing(taskDispatcher, currentBlockchainFileStopwatch);
                        currentBlockchainFileStopwatch.Restart();
                    }

                    this.lastReportedPercentage = -1;

                    this.ProcessBlockchainFile(block.BlockchainFileName, databaseIdManager);
                    this.currentBlockchainFile = block.BlockchainFileName;
                }

                this.ReportProgressReport(block.BlockchainFileName, block.PercentageOfCurrentBlockchainFile);

                // Note: We need to keep the call to ConvertParserBlockToBlockInfo outside of the
                //       parallel threads that are used to actually transfer the data to the DB.
                //       This is important if we want to ensure that the database primary keys are generated in a certain order.
                //       For example, with the current implementation, the block ID will be the block depth as reported
                //       by http://blockchain.info/. If we moved ConvertParserBlockToBlockInfo in the thread that 
                //       does the actually transfer to the DB, the IDs for the DB primary keys will be generated with a different pattern.
                ConvertParserBlockToBlockInfo(blockInfo, databaseIdManager, block);

                if (blockInfo.IsFull)
                {
                    BlockInfo blockInfo2 = blockInfo;
                    blockInfo = new BlockInfo();

                    // At this point we have an instance: blockInfo that needs to be transferred into the DB.
                    // We will dispatch the processing of that blockInfo on one of the threads managed by taskDispatcher.
                    await taskDispatcher.DispatchWorkAsync(() => this.ProcessBlock(blockInfo2));
                }
            }

            this.ProcessBlock(blockInfo);

            await this.FinalizeBlockchainFileProcessing(taskDispatcher, currentBlockchainFileStopwatch);
        }

        private void ReportProgressReport(string fileName, int percentage)
        {
            if (this.lastReportedPercentage != percentage)
            {
                Console.Write("\r    File: {0}. Processing: {1,3:n0}%", fileName, percentage);
                this.lastReportedPercentage = percentage;
            }
        }

        private async Task FinalizeBlockchainFileProcessing(TaskDispatcher taskDispatcher, Stopwatch currentBlockchainFileStopwatch)
        {
            await taskDispatcher.WaitForAllWorkToComplete();

            currentBlockchainFileStopwatch.Stop();
            Console.Write(
                "\r    File: {0}. Processing: {1,3:n0}%. Completed in {2,7:0.000} seconds.",
                this.currentBlockchainFile,
                100,
                currentBlockchainFileStopwatch.Elapsed.TotalSeconds);

            Console.WriteLine();
        }

        private DatabaseIdManager GetDatabaseIdManager()
        {
            using (BitcoinDataLayer bitcoinDataLayer = new BitcoinDataLayer(this.databaseConnection.ConnectionString))
            {
                int blockFileId;
                long blockId;
                long bitcoinTransactionId;
                long transactionInputId;
                long transactionOutputId;

                bitcoinDataLayer.GetMaximumIdValues(out blockFileId, out blockId, out bitcoinTransactionId, out transactionInputId, out transactionOutputId);

                return new DatabaseIdManager(blockFileId, blockId, bitcoinTransactionId, transactionInputId, transactionOutputId);
            }
        }

        private void ProcessBlockchainFile(string blockchainFileName, DatabaseIdManager databaseIdManager)
        {
            int blockFileId = databaseIdManager.GetNextBlockFileId();

            using (BitcoinDataLayer bitcoinDataLayer = new BitcoinDataLayer(this.databaseConnection.ConnectionString))
            {
                bitcoinDataLayer.AddBlockFile(new DBData.BlockchainFile(blockFileId, blockchainFileName));
                this.processingStatistics.AddBlockFilesCount(1);
            }
        }

        // @@@ rename once we process more than a block.
        private void ProcessBlock(BlockInfo blockInfo)
        {
            int transactionsCount = 0;
            int inputsCount = 0;
            int outputsCount = 0;

            using (BitcoinDataLayer bitcoinDataLayer = new BitcoinDataLayer(this.databaseConnection.ConnectionString))
            {
                bitcoinDataLayer.AddBlock(blockInfo.BlockDataTable);
                this.processingStatistics.AddBlocksCount(1);

                bitcoinDataLayer.AddTransactions(blockInfo.BitcoinTransactionDataTable);
                this.processingStatistics.AddTransactionsCount(blockInfo.BitcoinTransactionDataTable.Rows.Count);
                transactionsCount += blockInfo.BitcoinTransactionDataTable.Rows.Count;

                bitcoinDataLayer.AddTransactionInputs(blockInfo.TransactionInputDataTable);
                this.processingStatistics.AddTransactionInputsCount(blockInfo.TransactionInputDataTable.Rows.Count);
                inputsCount += blockInfo.TransactionInputDataTable.Rows.Count;

                bitcoinDataLayer.AddTransactionInputSources(blockInfo.TransactionInputSourceDataTable);

                if (blockInfo.TransactionInputDataTable.Rows.Count != blockInfo.TransactionInputSourceDataTable.Rows.Count)
                {
                    throw new InternalErrorException(string.Format(
                        CultureInfo.InvariantCulture,
                        "Tables TransactionInput and TransactionInputSource should contain the same number of entries. A mismatch was detected transferring data into database. Rows inserted in TransactionInput: {0}. Rows inserted in TransactionInputSource: {1}.",
                        blockInfo.TransactionInputDataTable.Rows.Count,
                        blockInfo.TransactionInputSourceDataTable.Rows.Count));
                }

                bitcoinDataLayer.AddTransactionOutputs(blockInfo.TransactionOutputDataTable);
                this.processingStatistics.AddTransactionOutputsCount(blockInfo.TransactionOutputDataTable.Rows.Count);
                outputsCount += blockInfo.TransactionOutputDataTable.Rows.Count;

                Debug.WriteLine(
                    "Data batch was processed. Transactions: {0}. Inputs: {1}. Outputs: {2}",
                    transactionsCount,
                    inputsCount,
                    outputsCount);
            }
        }
    }
}
