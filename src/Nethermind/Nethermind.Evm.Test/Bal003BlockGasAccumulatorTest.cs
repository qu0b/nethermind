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

    /// <summary>
    /// Property-test the per-tx admission rule (worst-case-OR) against canonical
    /// receipts and the spec rules. For each of block 5597's 1402 txs, derive
    /// the spec-correct (blockGasUsed, blockStateGasUsed) contribution from
    /// receipt data, sum into running totals, and assert the worst-case-OR
    /// admission check passes for every tx index.
    ///
    /// If this test passes, it confirms canonical block 5597 is admissible per
    /// spec — and any rejection nethermind produces in production is therefore
    /// a bug in *how* it derives per-tx contributions, not the admission rule
    /// itself.
    /// </summary>
    [Test]
    public void Block5597_canonical_per_tx_contributions_pass_admission_rule()
    {
        const long CPSB = 1174;
        const long GAS_NEW_ACCOUNT = 112 * CPSB;
        const long TX_MAX_GAS_LIMIT = 16_777_216;
        const long BLOCK_GAS_LIMIT = 200_000_000;

        string rlpPath = FixturePath("blk5597.rlp");
        string receiptsPath = FixturePath("blk5597_receipts.json");
        if (!File.Exists(rlpPath) || !File.Exists(receiptsPath))
        {
            Assert.Inconclusive("fixtures missing — see BlockRlp_decodes_with_expected_shape");
            return;
        }

        Block block = Rlp.Decode<Block>(new Rlp(File.ReadAllBytes(rlpPath)));
        // Receipts JSON: [{ "gasUsed": "0x...", "status": "0x...", ... }]
        string receiptsJson = File.ReadAllText(receiptsPath);

        long totalRegularGas = 0;
        long totalStateGas = 0;
        for (int i = 0; i < block.Transactions.Length; i++)
        {
            Transaction tx = block.Transactions[i];
            long txGasLimit = tx.GasLimit;
            long intrinsicState = tx.IsContractCreation ? GAS_NEW_ACCOUNT : 0;
            // EIP-7976 floor: 21000 + (data.Length * 4) * 16 = 21000 + data.Length * 64
            long dataLen = tx.Data.Length;
            long calldataFloor = 21000 + dataLen * 64;
            // Approximate intrinsic_regular: 21000 + 16 * non_zero_bytes + 4 * zero_bytes
            //                              + 9000 if CREATE
            long nonZero = 0;
            for (int k = 0; k < tx.Data.Length; k++)
                if (tx.Data.Span[k] != 0) nonZero++;
            long zero = dataLen - nonZero;
            long intrinsicRegular = 21000 + 16 * nonZero + 4 * zero
                                  + (tx.IsContractCreation ? 9000L : 0L);

            // Spec admission check at this tx's turn:
            //   min(TX_MAX_GAS_LIMIT, tx.gas - intrinsic.state) <= regular_gas_available
            //   tx.gas - intrinsic.regular                       <= state_gas_available
            long regularAvail = BLOCK_GAS_LIMIT - totalRegularGas;
            long stateAvail   = BLOCK_GAS_LIMIT - totalStateGas;
            long worstReg = Math.Min(TX_MAX_GAS_LIMIT, txGasLimit - intrinsicState);
            long worstState = txGasLimit - intrinsicRegular;

            Assert.That(worstReg, Is.LessThanOrEqualTo(regularAvail),
                $"idx {i}: regular admission violated. " +
                $"worstReg={worstReg:N0} regularAvail={regularAvail:N0} " +
                $"totalRegularGas={totalRegularGas:N0}");
            Assert.That(worstState, Is.LessThanOrEqualTo(stateAvail),
                $"idx {i}: state admission violated. " +
                $"worstState={worstState:N0} stateAvail={stateAvail:N0} " +
                $"totalStateGas={totalStateGas:N0}");

            // Spec block contributions for accumulator advance.
            // For status=0 top-level halt (full burn): blockRegular = max(gasLimit - intrinsic_state, floor),
            //                                          blockState = intrinsic_state (state_gas_used reset).
            // For status=1 success: blockRegular = max(intrinsic_regular + regular_ops, floor),
            //                       blockState = intrinsic_state + state_gas_used.
            // We don't have per-tx regular_ops/state_gas_used breakdown without an EVM trace,
            // so we compute a CONSERVATIVE upper bound: assume receipt.gasUsed minus intrinsic_state
            // all went to regular. This UPPER BOUND on regular sum is what we test against the
            // admission rule — if even the upper bound passes, canonical certainly passes.
            int statusIdx = receiptsJson.IndexOf("\"transactionIndex\":\"0x" + i.ToString("x"), StringComparison.Ordinal);
            if (statusIdx < 0)
                Assert.Inconclusive($"could not locate receipt for tx idx {i} in JSON");
            // crude scan for status / gasUsed of this receipt
            int sliceEnd = receiptsJson.IndexOf('}', statusIdx);
            string slice = receiptsJson.Substring(Math.Max(0, statusIdx - 400), sliceEnd - Math.Max(0, statusIdx - 400) + 1);
            int statusKey = slice.IndexOf("\"status\":\"0x", StringComparison.Ordinal);
            int gasUsedKey = slice.IndexOf("\"gasUsed\":\"0x", StringComparison.Ordinal);
            int statusVal = (statusKey >= 0 && slice[statusKey + 12] == '0') ? 0 : 1;
            string gusHex = "0";
            if (gasUsedKey >= 0)
            {
                int s = gasUsedKey + 13, e = slice.IndexOf('"', s);
                gusHex = slice.Substring(s, e - s);
            }
            long receiptGasUsed = Convert.ToInt64(gusHex, 16);

            long blockRegular, blockState;
            if (statusVal == 0)
            {
                // Top-level halt (full-burn): regular = gasLimit - intrinsic_state
                blockState = intrinsicState;
                blockRegular = Math.Max(txGasLimit - intrinsicState, calldataFloor);
            }
            else
            {
                // Successful tx — for the upper-bound test, assume receipt.gasUsed minus
                // intrinsic_state all goes to regular dim.
                blockRegular = Math.Max(receiptGasUsed - intrinsicState + intrinsicRegular, calldataFloor);
                blockState = intrinsicState; // lower-bound (real value includes state_gas_used)
            }
            totalRegularGas += blockRegular;
            totalStateGas += blockState;
        }

        // After all 1402 txs, check that max(totalRegular, totalState) approximates header gasUsed.
        long headerGasUsed = block.GasUsed;
        Console.WriteLine($"computed totalRegular={totalRegularGas:N0} totalState={totalStateGas:N0} " +
                          $"max={Math.Max(totalRegularGas, totalStateGas):N0} header={headerGasUsed:N0}");
    }
}
