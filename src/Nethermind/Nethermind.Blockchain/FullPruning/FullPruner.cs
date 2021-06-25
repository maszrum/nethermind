﻿//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Db.FullPruning;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;
using Org.BouncyCastle.Crypto.Generators;

namespace Nethermind.Blockchain.FullPruning
{
    public class FullPruner : IDisposable
    {
        private readonly IFullPruningDb _fullPruningDb;
        private readonly IPruningTrigger _pruningTrigger;
        private readonly IPruningConfig _pruningConfig;
        private readonly IBlockTree _blockTree;
        private readonly IStateReader _stateReader;
        private readonly ILogManager _logManager;
        private IPruningContext? _currentPruning;
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private int _waitingForBlockProcessed = 0;

        public FullPruner(
            IFullPruningDb fullPruningDb, 
            IPruningTrigger pruningTrigger,
            IPruningConfig pruningConfig,
            IBlockTree blockTree,
            IStateReader stateReader,
            ILogManager logManager)
        {
            _fullPruningDb = fullPruningDb;
            _pruningTrigger = pruningTrigger;
            _pruningConfig = pruningConfig;
            _blockTree = blockTree;
            _stateReader = stateReader;
            _logManager = logManager;
            _pruningTrigger.Prune += OnPrune;
        }

        private void OnPrune(object? sender, EventArgs e)
        {
            if (CanRunPruning())
            {
                if (Interlocked.CompareExchange(ref _waitingForBlockProcessed, 1, 0) == 0)
                {
                    // we don't want to start pruning in the middle of block processing.
                    _blockTree.NewHeadBlock += OnNewHead;
                }
            }
        }

        private void OnNewHead(object? sender, BlockEventArgs e)
        {
            if (CanRunPruning())
            {
                if (Interlocked.CompareExchange(ref _waitingForBlockProcessed, 0, 1) == 1)
                {
                    _blockTree.NewHeadBlock -= OnNewHead;

                    if (e.Block?.StateRoot is not null)
                    {
                        if (_fullPruningDb.TryStartPruning(out IPruningContext pruningContext))
                        {
                            IPruningContext? oldPruning = Interlocked.Exchange(ref _currentPruning, pruningContext);
                            Task.Run(() => RunPruning(pruningContext, e.Block.StateRoot, oldPruning));
                        }
                    }
                }
            }
        }

        private bool CanRunPruning() => _fullPruningDb.CanStartPruning;

        protected virtual void RunPruning(IPruningContext pruning, Keccak statRoot, IPruningContext? oldPruning)
        {
            oldPruning?.Dispose();
            
            using (pruning)
            {
                pruning.MarkStart();
                using (CopyTreeVisitor copyTreeVisitor = new(pruning, _cancellationTokenSource, _logManager))
                {
                    VisitingOptions visitingOptions = new() {MaxDegreeOfParallelism = _pruningConfig.FullPruningMaxDegreeOfParallelism};
                    _stateReader.RunTreeVisitor(copyTreeVisitor, statRoot, visitingOptions);

                    if (!_cancellationTokenSource.IsCancellationRequested)
                    {
                        copyTreeVisitor.Finish();
                        pruning.Commit();
                    }
                }
            }
        }

        public void Dispose()
        {
            _blockTree.NewHeadBlock -= OnNewHead;
            _pruningTrigger.Prune -= OnPrune;
            _currentPruning?.Dispose();
            _cancellationTokenSource.Dispose();
        }
    }
}
