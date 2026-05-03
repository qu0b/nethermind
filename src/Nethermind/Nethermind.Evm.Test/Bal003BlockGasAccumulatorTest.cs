// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// B-003 reproduction scaffold for ~/devnet-6/BUGS.md.
//
// Goal: exercise `BlockAccessListManager.IncrementalValidation`'s per-tx
// (totalRegularGas, totalStateGas) accumulator on a tightly-packed canonical
// block to surface the over-count that rejects bal-devnet-6 block 5597 with
// `worstCaseRegularContribution > regularGasAvailable` at idx 1400 when geth,
// besu, erigon, ethrex all accept the block.
//
// Inputs (in this directory):
//   blk5597.rlp                 — RLP-encoded canonical block 5597 (1402 txs)
//   blk5597_receipts.json       — eth_getBlockReceipts response from geth
//   canonical_5597_full.json    — eth_getBlockByNumber(0x15DD, true) with BAL
//
// Approach: load the block, set up an in-memory test chain seeded with the
// senders and parent state needed for the txs to execute, then run through the
// parallel block validator and assert the admission rule passes for all 1402
// txs. The current bug causes admission to throw at tx index 1400.
//
// Status: scaffold only. Real-state setup is non-trivial (block 5596 has
// thousands of accounts/contracts). A simplified path is to seed only the
// senders and any external dependencies referenced by the txs' init code, but
// that requires a tx-by-tx dependency walk which is left for follow-up.
//
// See ~/devnet-6/BUGS.md "B-003" for the full investigation.

using System;
using System.IO;
using NUnit.Framework;
using Nethermind.Core;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Evm.Test;

public class Bal003BlockGasAccumulatorTest
{
    private static string FixturePath(string name) =>
        Path.Combine(TestContext.CurrentContext.TestDirectory, name);

    /// <summary>
    /// Smoke test: verify the canonical block 5597 RLP fixture can be decoded
    /// into a Block with the expected structure (1402 txs, 199.876 M gasUsed).
    /// This is a precondition for the full reproduction test below.
    /// </summary>
    [Test]
    public void BlockRlp_decodes_with_expected_shape()
    {
        string rlpPath = FixturePath("blk5597.rlp");
        if (!File.Exists(rlpPath))
        {
            Assert.Inconclusive($"fixture missing: {rlpPath} — pull from " +
                $"bal-devnet-6-lighthouse-geth-1 via debug_getRawBlock(0x15DD).");
            return;
        }

        byte[] rlp = File.ReadAllBytes(rlpPath);
        Block block = Rlp.Decode<Block>(new Rlp(rlp));

        Assert.That(block, Is.Not.Null);
        Assert.That(block.Number, Is.EqualTo(5597));
        Assert.That(block.Transactions.Length, Is.EqualTo(1402));
        Assert.That(block.GasUsed, Is.EqualTo(199_875_954L));
        Assert.That(block.GasLimit, Is.EqualTo(200_000_000L));
        // 281 of the 1402 txs are CREATEs (per geth canonical count).
        int createCount = 0;
        foreach (Transaction tx in block.Transactions)
            if (tx.IsContractCreation) createCount++;
        Assert.That(createCount, Is.EqualTo(281));
    }

    /// <summary>
    /// Reproduction of B-003: the per-tx accumulator over-counts and the worst-
    /// case-OR admission check rejects tx 1400 even though geth/besu/erigon/ethrex
    /// all accept the block.
    ///
    /// Marked Explicit because it requires real block 5596 state, which isn't
    /// trivially available as a self-contained fixture. Future work:
    ///   1. Export block 5596 state from a node that has it (lighthouse-besu-1).
    ///   2. Convert to a nethermind-readable in-memory state seed.
    ///   3. Drop into this test and remove the [Explicit] attribute.
    /// </summary>
    [Test, Explicit("requires block 5596 state — see ~/devnet-6/BUGS.md B-003")]
    public void Block5597_admission_check_passes_for_all_1402_txs() =>
        // TODO: load block 5596 state into TestState, then process block 5597
        // through the parallel block validator and assert no `BlockGasLimitExceeded`
        // throw at any index. The QU0B-PERTX trace from
        // `qu0b/debug/bal6-pertx-accumulator` should be enabled while this runs
        // to capture the exact divergence index.
        Assert.Fail("not yet implemented — see test summary");
}
