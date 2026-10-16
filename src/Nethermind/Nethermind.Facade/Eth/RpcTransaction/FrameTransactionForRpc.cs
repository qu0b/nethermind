// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.Facade.Eth.RpcTransaction;

/// <summary>JSON-RPC view of an EIP-8141 frame transaction: the EIP-1559 fee fields plus the frame and hoisted signature lists.</summary>
[GenerateJsonWriter(RegisterWithSerializer = false)]
[RepopulatableTransaction]
public class FrameTransactionForRpc : EIP1559TransactionForRpc, IFromTransaction<FrameTransactionForRpc>
{
    public new static TxType TxType => TxType.FrameTx;

    public override TxType? Type => TxType;

    /// <summary>EIP-8250 <c>nonce_keys</c>, sharing the sequence number reported as <c>nonce</c>.</summary>
    /// <remarks>Absent for an envelope nonce, which is a different signing payload from the key set <c>[0]</c>.</remarks>
    public UInt256[]? NonceKeys { get; set; }

    /// <summary>EIP-8141 <c>frames</c>, in execution order; the field that discriminates this envelope.</summary>
    /// <remarks>The transaction's <c>gas</c> is the whole budget derived from them, so a frame's own budget is read
    /// here rather than from that total.</remarks>
    [JsonDiscriminator]
    public FrameForRpc[]? Frames { get; set; }

    /// <summary>EIP-8141 <c>signatures</c>: the entries hoisted out of the frames, verified before any frame
    /// runs and read by frame code through <c>SIGPARAM</c> by index into this list.</summary>
    /// <remarks>RPC simulation accepts an entry with empty signature bytes as a placeholder and prices it as signed, so
    /// supply one per entry the signed transaction will carry; entries with signature bytes are still verified.</remarks>
    public FrameSignatureForRpc[]? Signatures { get; set; }

    /// <summary><c>max_fee_per_blob_gas</c>, an unconditional field of the signed payload.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public UInt256? MaxFeePerBlobGas { get; set; }

    /// <summary><c>blob_versioned_hashes</c>, an unconditional field of the signed payload.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public byte[][]? BlobVersionedHashes { get; set; }

    /// <summary>Omitted from the response: a frame transaction carries no access list.</summary>
    /// <remarks>Hides rather than overrides the base list, which also discriminates the access-list types.</remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public new AccessListForRpc? AccessList { get; set; }

    // A frame transaction carries no ECDSA envelope signature (its entries are in Signatures), so the zeros the
    // base types would report read as a real one; these are omitted from the response.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public override UInt256? YParity { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public override UInt256? V { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(NullableUInt256Converter))]
    public override UInt256? R { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(NullableUInt256Converter))]
    public override UInt256? S { get; set; }

    [JsonConstructor]
    public FrameTransactionForRpc() { }

    public FrameTransactionForRpc(Transaction transaction, in TransactionForRpcContext extraData)
        : base(transaction, extraData) { }

    internal override void Populate(Transaction transaction, in TransactionForRpcContext extraData)
    {
        base.Populate(transaction, extraData);
        AccessList = null;
        YParity = null;
        V = null;
        R = null;
        S = null;
        // Transaction.GasLimit carries only the frame limits; gas reports max_gas, the budget the payer is charged
        // against, when the context names the fork to price it at.
        Gas = extraData.Spec is { } spec && FrameTxValidation.TryCalculateGasBudget(transaction, spec, out _, out _, out ulong maxGas)
            ? maxGas
            : transaction.GasLimit;
        NonceKeys = transaction.NonceKeys;
        Frames = FrameForRpc.FromFrames(transaction.Frames);
        Signatures = FrameSignatureForRpc.FromSignatures(transaction.FrameSignatures);

        // Covered by the sig hash, so always reported: a consumer must be able to rebuild the payload.
        MaxFeePerBlobGas = transaction.MaxFeePerBlobGas ?? 0;
        BlobVersionedHashes = transaction.BlobVersionedHashes ?? [];
    }

    public override Result<Transaction> ToTransaction(bool validateUserInput = false, ulong? gasCap = null, IReleaseSpec? spec = null)
    {
        Result<Transaction> baseResult = base.ToTransaction(validateUserInput, gasCap, spec);
        if (baseResult.IsError) return baseResult;

        if (!FrameForRpc.TryToFrames(Frames, out TxFrame[]? frames))
            return RpcTransactionErrors.NullEntryIn("frames");

        if (!FrameSignatureForRpc.TryToSignatures(Signatures, out TxFrameSignature[]? signatures))
            return RpcTransactionErrors.NullEntryIn("signatures");

        // The caller's gas field is not what this type spends, so the cap the base applied to
        // Transaction.GasLimit leaves the work a frame transaction asks for unbounded.
        ulong effectiveCap = gasCap.EffectiveGasCap();
        ulong totalFrameGas = FrameTxValidation.TotalGasLimit(frames);
        // A bound on work, not the on-chain price TryCalculateGasBudget derives: the processor verifies every
        // entry before any budget exists, so an unpriced signature list buys recoveries no frame pays for.
        ulong signatureGas = FrameTxValidation.SignatureVerificationWorkGas(signatures);
        ulong reservedGas = totalFrameGas > ulong.MaxValue - signatureGas ? ulong.MaxValue : totalFrameGas + signatureGas;
        if (reservedGas > effectiveCap)
            return RpcTransactionErrors.FrameGasAboveCap(totalFrameGas, signatureGas, effectiveCap);

        Transaction tx = baseResult.Data;
        // The invariant FrameTxDecoder establishes for a decoded frame tx, so GasLimit readers see the same
        // value whichever path built it. The processor still derives the real budget from the frames.
        tx.GasLimit = totalFrameGas;
        tx.NonceKeys = NonceKeys;
        tx.Frames = frames;
        tx.FrameSignatures = signatures;
        tx.MaxFeePerBlobGas = MaxFeePerBlobGas;
        tx.BlobVersionedHashes = BlobVersionedHashes;
        return tx;
    }

    public new static FrameTransactionForRpc FromTransaction(Transaction tx, in TransactionForRpcContext extraData)
        => new(tx, extraData);
}
