// Copyright (c) Six Labors and contributors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SixLabors.ImageSharp.Memory;

namespace SixLabors.ImageSharp.Formats.Png.Zlib
{
    /// <summary>
    /// Performs Deflate Huffman encoding.
    /// </summary>
    internal sealed unsafe class DeflaterHuffman : IDisposable
    {
        private const int BufferSize = 1 << (DeflaterConstants.DEFAULT_MEM_LEVEL + 6);

        // The number of literal codes.
        private const int LiteralNumber = 286;

        // Number of distance codes
        private const int DistanceNumber = 30;

        // Number of codes used to transfer bit lengths
        private const int BitLengthNumber = 19;

        // Repeat previous bit length 3-6 times (2 bits of repeat count)
        private const int Repeat3To6 = 16;

        // Repeat a zero length 3-10 times  (3 bits of repeat count)
        private const int Repeat3To10 = 17;

        // Repeat a zero length 11-138 times  (7 bits of repeat count)
        private const int Repeat11To138 = 18;

        private const int EofSymbol = 256;

        // The lengths of the bit length codes are sent in order of decreasing
        // probability, to avoid transmitting the lengths for unused bit length codes.
        private static readonly int[] BitLengthOrder = { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };

        private static readonly byte[] Bit4Reverse = { 0, 8, 4, 12, 2, 10, 6, 14, 1, 9, 5, 13, 3, 11, 7, 15 };

        private static readonly short[] StaticLCodes;
        private static readonly byte[] StaticLLength;
        private static readonly short[] StaticDCodes;
        private static readonly byte[] StaticDLength;

        private Tree literalTree;
        private Tree distTree;
        private Tree blTree;

        // Buffer for distances
        private readonly IMemoryOwner<short> distanceManagedBuffer;
        private readonly short* pinnedDistanceBuffer;
        private MemoryHandle distanceBufferHandle;

        private readonly IMemoryOwner<short> literalManagedBuffer;
        private readonly short* pinnedLiteralBuffer;
        private MemoryHandle literalBufferHandle;

        private int lastLiteral;
        private int extraBits;
        private bool isDisposed;

        // TODO: These should be pre-generated array/readonlyspans.
        static DeflaterHuffman()
        {
            // See RFC 1951 3.2.6
            // Literal codes
            StaticLCodes = new short[LiteralNumber];
            StaticLLength = new byte[LiteralNumber];

            int i = 0;
            while (i < 144)
            {
                StaticLCodes[i] = BitReverse((0x030 + i) << 8);
                StaticLLength[i++] = 8;
            }

            while (i < 256)
            {
                StaticLCodes[i] = BitReverse((0x190 - 144 + i) << 7);
                StaticLLength[i++] = 9;
            }

            while (i < 280)
            {
                StaticLCodes[i] = BitReverse((0x000 - 256 + i) << 9);
                StaticLLength[i++] = 7;
            }

            while (i < LiteralNumber)
            {
                StaticLCodes[i] = BitReverse((0x0c0 - 280 + i) << 8);
                StaticLLength[i++] = 8;
            }

            // Distance codes
            StaticDCodes = new short[DistanceNumber];
            StaticDLength = new byte[DistanceNumber];
            for (i = 0; i < DistanceNumber; i++)
            {
                StaticDCodes[i] = BitReverse(i << 11);
                StaticDLength[i] = 5;
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DeflaterHuffman"/> class.
        /// </summary>
        /// <param name="memoryAllocator">The memory allocator to use for buffer allocations.</param>
        public DeflaterHuffman(MemoryAllocator memoryAllocator)
        {
            this.Pending = new DeflaterPendingBuffer(memoryAllocator);

            this.literalTree = new Tree(memoryAllocator, LiteralNumber, 257, 15);
            this.distTree = new Tree(memoryAllocator, DistanceNumber, 1, 15);
            this.blTree = new Tree(memoryAllocator, BitLengthNumber, 4, 7);

            this.distanceManagedBuffer = memoryAllocator.Allocate<short>(BufferSize);
            this.distanceBufferHandle = this.distanceManagedBuffer.Memory.Pin();
            this.pinnedDistanceBuffer = (short*)this.distanceBufferHandle.Pointer;

            this.literalManagedBuffer = memoryAllocator.Allocate<short>(BufferSize);
            this.literalBufferHandle = this.literalManagedBuffer.Memory.Pin();
            this.pinnedLiteralBuffer = (short*)this.literalBufferHandle.Pointer;
        }

        /// <summary>
        /// Gets the pending buffer to use.
        /// </summary>
        public DeflaterPendingBuffer Pending { get; private set; }

        /// <summary>
        /// Reset internal state
        /// </summary>
        [MethodImpl(InliningOptions.ShortMethod)]
        public void Reset()
        {
            this.lastLiteral = 0;
            this.extraBits = 0;
            this.literalTree.Reset();
            this.distTree.Reset();
            this.blTree.Reset();
        }

        /// <summary>
        /// Write all trees to pending buffer
        /// </summary>
        /// <param name="blTreeCodes">The number/rank of treecodes to send.</param>
        public void SendAllTrees(int blTreeCodes)
        {
            this.blTree.BuildCodes();
            this.literalTree.BuildCodes();
            this.distTree.BuildCodes();
            this.Pending.WriteBits(this.literalTree.NumCodes - 257, 5);
            this.Pending.WriteBits(this.distTree.NumCodes - 1, 5);
            this.Pending.WriteBits(blTreeCodes - 4, 4);
            for (int rank = 0; rank < blTreeCodes; rank++)
            {
                this.Pending.WriteBits(this.blTree.Length[BitLengthOrder[rank]], 3);
            }

            this.literalTree.WriteTree(this.Pending, this.blTree);
            this.distTree.WriteTree(this.Pending, this.blTree);
        }

        /// <summary>
        /// Compress current buffer writing data to pending buffer
        /// </summary>
        public void CompressBlock()
        {
            DeflaterPendingBuffer pendingBuffer = this.Pending;
            short* pinnedDistance = this.pinnedDistanceBuffer;
            short* pinnedLiteral = this.pinnedLiteralBuffer;

            for (int i = 0; i < this.lastLiteral; i++)
            {
                int litlen = pinnedLiteral[i] & 0xFF;
                int dist = pinnedDistance[i];
                if (dist-- != 0)
                {
                    int lc = Lcode(litlen);
                    this.literalTree.WriteSymbol(pendingBuffer, lc);

                    int bits = (lc - 261) / 4;
                    if (bits > 0 && bits <= 5)
                    {
                        this.Pending.WriteBits(litlen & ((1 << bits) - 1), bits);
                    }

                    int dc = Dcode(dist);
                    this.distTree.WriteSymbol(pendingBuffer, dc);

                    bits = (dc >> 1) - 1;
                    if (bits > 0)
                    {
                        this.Pending.WriteBits(dist & ((1 << bits) - 1), bits);
                    }
                }
                else
                {
                    this.literalTree.WriteSymbol(pendingBuffer, litlen);
                }
            }

            this.literalTree.WriteSymbol(pendingBuffer, EofSymbol);
        }

        /// <summary>
        /// Flush block to output with no compression
        /// </summary>
        /// <param name="stored">Data to write</param>
        /// <param name="storedOffset">Index of first byte to write</param>
        /// <param name="storedLength">Count of bytes to write</param>
        /// <param name="lastBlock">True if this is the last block</param>
        [MethodImpl(InliningOptions.ShortMethod)]
        public void FlushStoredBlock(byte[] stored, int storedOffset, int storedLength, bool lastBlock)
        {
            this.Pending.WriteBits((DeflaterConstants.STORED_BLOCK << 1) + (lastBlock ? 1 : 0), 3);
            this.Pending.AlignToByte();
            this.Pending.WriteShort(storedLength);
            this.Pending.WriteShort(~storedLength);
            this.Pending.WriteBlock(stored, storedOffset, storedLength);
            this.Reset();
        }

        /// <summary>
        /// Flush block to output with compression
        /// </summary>
        /// <param name="stored">Data to flush</param>
        /// <param name="storedOffset">Index of first byte to flush</param>
        /// <param name="storedLength">Count of bytes to flush</param>
        /// <param name="lastBlock">True if this is the last block</param>
        public void FlushBlock(byte[] stored, int storedOffset, int storedLength, bool lastBlock)
        {
            this.literalTree.Frequencies[EofSymbol]++;

            // Build trees
            this.literalTree.BuildTree();
            this.distTree.BuildTree();

            // Calculate bitlen frequency
            this.literalTree.CalcBLFreq(this.blTree);
            this.distTree.CalcBLFreq(this.blTree);

            // Build bitlen tree
            this.blTree.BuildTree();

            int blTreeCodes = 4;
            for (int i = 18; i > blTreeCodes; i--)
            {
                if (this.blTree.Length[BitLengthOrder[i]] > 0)
                {
                    blTreeCodes = i + 1;
                }
            }

            int opt_len = 14 + (blTreeCodes * 3) + this.blTree.GetEncodedLength()
                + this.literalTree.GetEncodedLength() + this.distTree.GetEncodedLength()
                + this.extraBits;

            int static_len = this.extraBits;
            ref byte staticLLengthRef = ref MemoryMarshal.GetReference<byte>(StaticLLength);
            for (int i = 0; i < LiteralNumber; i++)
            {
                static_len += this.literalTree.Frequencies[i] * Unsafe.Add(ref staticLLengthRef, i);
            }

            ref byte staticDLengthRef = ref MemoryMarshal.GetReference<byte>(StaticDLength);
            for (int i = 0; i < DistanceNumber; i++)
            {
                static_len += this.distTree.Frequencies[i] * Unsafe.Add(ref staticDLengthRef, i);
            }

            if (opt_len >= static_len)
            {
                // Force static trees
                opt_len = static_len;
            }

            if (storedOffset >= 0 && storedLength + 4 < opt_len >> 3)
            {
                // Store Block
                this.FlushStoredBlock(stored, storedOffset, storedLength, lastBlock);
            }
            else if (opt_len == static_len)
            {
                // Encode with static tree
                this.Pending.WriteBits((DeflaterConstants.STATIC_TREES << 1) + (lastBlock ? 1 : 0), 3);
                this.literalTree.SetStaticCodes(StaticLCodes, StaticLLength);
                this.distTree.SetStaticCodes(StaticDCodes, StaticDLength);
                this.CompressBlock();
                this.Reset();
            }
            else
            {
                // Encode with dynamic tree
                this.Pending.WriteBits((DeflaterConstants.DYN_TREES << 1) + (lastBlock ? 1 : 0), 3);
                this.SendAllTrees(blTreeCodes);
                this.CompressBlock();
                this.Reset();
            }
        }

        /// <summary>
        /// Get value indicating if internal buffer is full
        /// </summary>
        /// <returns>true if buffer is full</returns>
        [MethodImpl(InliningOptions.ShortMethod)]
        public bool IsFull() => this.lastLiteral >= BufferSize;

        /// <summary>
        /// Add literal to buffer
        /// </summary>
        /// <param name="literal">Literal value to add to buffer.</param>
        /// <returns>Value indicating internal buffer is full</returns>
        [MethodImpl(InliningOptions.ShortMethod)]
        public bool TallyLit(int literal)
        {
            this.pinnedDistanceBuffer[this.lastLiteral] = 0;
            this.pinnedLiteralBuffer[this.lastLiteral++] = (byte)literal;
            this.literalTree.Frequencies[literal]++;
            return this.IsFull();
        }

        /// <summary>
        /// Add distance code and length to literal and distance trees
        /// </summary>
        /// <param name="distance">Distance code</param>
        /// <param name="length">Length</param>
        /// <returns>Value indicating if internal buffer is full</returns>
        [MethodImpl(InliningOptions.ShortMethod)]
        public bool TallyDist(int distance, int length)
        {
            this.pinnedDistanceBuffer[this.lastLiteral] = (short)distance;
            this.pinnedLiteralBuffer[this.lastLiteral++] = (byte)(length - 3);

            int lc = Lcode(length - 3);
            this.literalTree.Frequencies[lc]++;
            if (lc >= 265 && lc < 285)
            {
                this.extraBits += (lc - 261) / 4;
            }

            int dc = Dcode(distance - 1);
            this.distTree.Frequencies[dc]++;
            if (dc >= 4)
            {
                this.extraBits += (dc >> 1) - 1;
            }

            return this.IsFull();
        }

        /// <summary>
        /// Reverse the bits of a 16 bit value.
        /// </summary>
        /// <param name="toReverse">Value to reverse bits</param>
        /// <returns>Value with bits reversed</returns>
        [MethodImpl(InliningOptions.ShortMethod)]
        public static short BitReverse(int toReverse)
        {
            return (short)(Bit4Reverse[toReverse & 0xF] << 12
                           | Bit4Reverse[(toReverse >> 4) & 0xF] << 8
                           | Bit4Reverse[(toReverse >> 8) & 0xF] << 4
                           | Bit4Reverse[toReverse >> 12]);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (!this.isDisposed)
            {
                this.Pending.Dispose();
                this.distanceBufferHandle.Dispose();
                this.distanceManagedBuffer.Dispose();
                this.literalBufferHandle.Dispose();
                this.literalManagedBuffer.Dispose();

                this.literalTree.Dispose();
                this.blTree.Dispose();
                this.distTree.Dispose();

                this.Pending = null;
                this.literalTree = null;
                this.blTree = null;
                this.distTree = null;
                this.isDisposed = true;
            }

            GC.SuppressFinalize(this);
        }

        [MethodImpl(InliningOptions.ShortMethod)]
        private static int Lcode(int length)
        {
            if (length == 255)
            {
                return 285;
            }

            int code = 257;
            while (length >= 8)
            {
                code += 4;
                length >>= 1;
            }

            return code + length;
        }

        [MethodImpl(InliningOptions.ShortMethod)]
        private static int Dcode(int distance)
        {
            int code = 0;
            while (distance >= 4)
            {
                code += 2;
                distance >>= 1;
            }

            return code + distance;
        }

        private sealed class Tree : IDisposable
        {
            private readonly int minNumCodes;
            private readonly int[] bitLengthCounts;
            private readonly int maxLength;
            private bool isDisposed;

            private readonly int elementCount;

            private readonly MemoryAllocator memoryAllocator;

            private IMemoryOwner<short> codesMemoryOwner;
            private MemoryHandle codesMemoryHandle;
            private readonly short* codes;

            private IMemoryOwner<short> frequenciesMemoryOwner;
            private MemoryHandle frequenciesMemoryHandle;

            private IManagedByteBuffer lengthsMemoryOwner;
            private MemoryHandle lengthsMemoryHandle;

            public Tree(MemoryAllocator memoryAllocator, int elements, int minCodes, int maxLength)
            {
                this.memoryAllocator = memoryAllocator;
                this.elementCount = elements;
                this.minNumCodes = minCodes;
                this.maxLength = maxLength;

                this.frequenciesMemoryOwner = memoryAllocator.Allocate<short>(elements);
                this.frequenciesMemoryHandle = this.frequenciesMemoryOwner.Memory.Pin();
                this.Frequencies = (short*)this.frequenciesMemoryHandle.Pointer;

                this.lengthsMemoryOwner = memoryAllocator.AllocateManagedByteBuffer(elements);
                this.lengthsMemoryHandle = this.lengthsMemoryOwner.Memory.Pin();
                this.Length = (byte*)this.lengthsMemoryHandle.Pointer;

                this.codesMemoryOwner = memoryAllocator.Allocate<short>(elements);
                this.codesMemoryHandle = this.codesMemoryOwner.Memory.Pin();
                this.codes = (short*)this.codesMemoryHandle.Pointer;

                // Maxes out at 15.
                this.bitLengthCounts = new int[maxLength];
            }

            public int NumCodes { get; private set; }

            public short* Frequencies { get; }

            public byte* Length { get; }

            /// <summary>
            /// Resets the internal state of the tree
            /// </summary>
            [MethodImpl(InliningOptions.ShortMethod)]
            public void Reset()
            {
                this.frequenciesMemoryOwner.Memory.Span.Clear();
                this.lengthsMemoryOwner.Memory.Span.Clear();
                this.codesMemoryOwner.Memory.Span.Clear();
            }

            [MethodImpl(InliningOptions.ShortMethod)]
            public void WriteSymbol(DeflaterPendingBuffer pendingBuffer, int code)
                => pendingBuffer.WriteBits(this.codes[code] & 0xFFFF, this.Length[code]);

            /// <summary>
            /// Set static codes and length
            /// </summary>
            /// <param name="staticCodes">new codes</param>
            /// <param name="staticLengths">length for new codes</param>
            [MethodImpl(InliningOptions.ShortMethod)]
            public void SetStaticCodes(ReadOnlySpan<short> staticCodes, ReadOnlySpan<byte> staticLengths)
            {
                staticCodes.CopyTo(this.codesMemoryOwner.Memory.Span);
                staticLengths.CopyTo(this.lengthsMemoryOwner.Memory.Span);
            }

            /// <summary>
            /// Build dynamic codes and lengths
            /// </summary>
            public void BuildCodes()
            {
                // Maxes out at 15 * 4
                Span<int> nextCode = stackalloc int[this.maxLength];
                ref int nextCodeRef = ref MemoryMarshal.GetReference(nextCode);
                ref int bitLengthCountsRef = ref MemoryMarshal.GetReference<int>(this.bitLengthCounts);

                int code = 0;
                for (int bits = 0; bits < this.maxLength; bits++)
                {
                    Unsafe.Add(ref nextCodeRef, bits) = code;
                    code += Unsafe.Add(ref bitLengthCountsRef, bits) << (15 - bits);
                }

                for (int i = 0; i < this.NumCodes; i++)
                {
                    int bits = this.Length[i];
                    if (bits > 0)
                    {
                        this.codes[i] = BitReverse(Unsafe.Add(ref nextCodeRef, bits - 1));
                        Unsafe.Add(ref nextCodeRef, bits - 1) += 1 << (16 - bits);
                    }
                }
            }

            public void BuildTree()
            {
                int numSymbols = this.elementCount;

                // heap is a priority queue, sorted by frequency, least frequent
                // nodes first.  The heap is a binary tree, with the property, that
                // the parent node is smaller than both child nodes.  This assures
                // that the smallest node is the first parent.
                //
                // The binary tree is encoded in an array:  0 is root node and
                // the nodes 2*n+1, 2*n+2 are the child nodes of node n.
                // Maxes out at 286 * 4 so too large for the stack.
                using (IMemoryOwner<int> heapMemoryOwner = this.memoryAllocator.Allocate<int>(numSymbols))
                {
                    ref int heapRef = ref MemoryMarshal.GetReference(heapMemoryOwner.Memory.Span);

                    int heapLen = 0;
                    int maxCode = 0;
                    for (int n = 0; n < numSymbols; n++)
                    {
                        int freq = this.Frequencies[n];
                        if (freq != 0)
                        {
                            // Insert n into heap
                            int pos = heapLen++;
                            int ppos;
                            while (pos > 0 && this.Frequencies[Unsafe.Add(ref heapRef, ppos = (pos - 1) >> 1)] > freq)
                            {
                                Unsafe.Add(ref heapRef, pos) = Unsafe.Add(ref heapRef, ppos);
                                pos = ppos;
                            }

                            Unsafe.Add(ref heapRef, pos) = n;

                            maxCode = n;
                        }
                    }

                    // We could encode a single literal with 0 bits but then we
                    // don't see the literals.  Therefore we force at least two
                    // literals to avoid this case.  We don't care about order in
                    // this case, both literals get a 1 bit code.
                    while (heapLen < 2)
                    {
                        Unsafe.Add(ref heapRef, heapLen++) = maxCode < 2 ? ++maxCode : 0;
                    }

                    this.NumCodes = Math.Max(maxCode + 1, this.minNumCodes);

                    int numLeafs = heapLen;
                    int childrenLength = (4 * heapLen) - 2;
                    using (IMemoryOwner<int> childrenMemoryOwner = this.memoryAllocator.Allocate<int>(childrenLength))
                    using (IMemoryOwner<int> valuesMemoryOwner = this.memoryAllocator.Allocate<int>((2 * heapLen) - 1))
                    {
                        ref int childrenRef = ref MemoryMarshal.GetReference(childrenMemoryOwner.Memory.Span);
                        ref int valuesRef = ref MemoryMarshal.GetReference(valuesMemoryOwner.Memory.Span);
                        int numNodes = numLeafs;

                        for (int i = 0; i < heapLen; i++)
                        {
                            int node = Unsafe.Add(ref heapRef, i);
                            int i2 = 2 * i;
                            Unsafe.Add(ref childrenRef, i2) = node;
                            Unsafe.Add(ref childrenRef, i2 + 1) = -1;
                            Unsafe.Add(ref valuesRef, i) = this.Frequencies[node] << 8;
                            Unsafe.Add(ref heapRef, i) = i;
                        }

                        // Construct the Huffman tree by repeatedly combining the least two
                        // frequent nodes.
                        do
                        {
                            int first = Unsafe.Add(ref heapRef, 0);
                            int last = Unsafe.Add(ref heapRef, --heapLen);

                            // Propagate the hole to the leafs of the heap
                            int ppos = 0;
                            int path = 1;

                            while (path < heapLen)
                            {
                                if (path + 1 < heapLen && Unsafe.Add(ref valuesRef, Unsafe.Add(ref heapRef, path)) > Unsafe.Add(ref valuesRef, Unsafe.Add(ref heapRef, path + 1)))
                                {
                                    path++;
                                }

                                Unsafe.Add(ref heapRef, ppos) = Unsafe.Add(ref heapRef, path);
                                ppos = path;
                                path = (path * 2) + 1;
                            }

                            // Now propagate the last element down along path.  Normally
                            // it shouldn't go too deep.
                            int lastVal = Unsafe.Add(ref valuesRef, last);
                            while ((path = ppos) > 0
                                    && Unsafe.Add(ref valuesRef, Unsafe.Add(ref heapRef, ppos = (path - 1) >> 1)) > lastVal)
                            {
                                Unsafe.Add(ref heapRef, path) = Unsafe.Add(ref heapRef, ppos);
                            }

                            Unsafe.Add(ref heapRef, path) = last;

                            int second = Unsafe.Add(ref heapRef, 0);

                            // Create a new node father of first and second
                            last = numNodes++;
                            Unsafe.Add(ref childrenRef, 2 * last) = first;
                            Unsafe.Add(ref childrenRef, (2 * last) + 1) = second;
                            int mindepth = Math.Min(Unsafe.Add(ref valuesRef, first) & 0xFF, Unsafe.Add(ref valuesRef, second) & 0xFF);
                            Unsafe.Add(ref valuesRef, last) = lastVal = Unsafe.Add(ref valuesRef, first) + Unsafe.Add(ref valuesRef, second) - mindepth + 1;

                            // Again, propagate the hole to the leafs
                            ppos = 0;
                            path = 1;

                            while (path < heapLen)
                            {
                                if (path + 1 < heapLen
                                    && Unsafe.Add(ref valuesRef, Unsafe.Add(ref heapRef, path)) > Unsafe.Add(ref valuesRef, Unsafe.Add(ref heapRef, path + 1)))
                                {
                                    path++;
                                }

                                Unsafe.Add(ref heapRef, ppos) = Unsafe.Add(ref heapRef, path);
                                ppos = path;
                                path = (ppos * 2) + 1;
                            }

                            // Now propagate the new element down along path
                            while ((path = ppos) > 0 && Unsafe.Add(ref valuesRef, Unsafe.Add(ref heapRef, ppos = (path - 1) >> 1)) > lastVal)
                            {
                                Unsafe.Add(ref heapRef, path) = Unsafe.Add(ref heapRef, ppos);
                            }

                            Unsafe.Add(ref heapRef, path) = last;
                        }
                        while (heapLen > 1);

                        if (Unsafe.Add(ref heapRef, 0) != (childrenLength >> 1) - 1)
                        {
                            DeflateThrowHelper.ThrowHeapViolated();
                        }

                        this.BuildLength(childrenMemoryOwner.Memory.Span);
                    }
                }
            }

            /// <summary>
            /// Get encoded length
            /// </summary>
            /// <returns>Encoded length, the sum of frequencies * lengths</returns>
            [MethodImpl(InliningOptions.ShortMethod)]
            public int GetEncodedLength()
            {
                int len = 0;
                for (int i = 0; i < this.elementCount; i++)
                {
                    len += this.Frequencies[i] * this.Length[i];
                }

                return len;
            }

            /// <summary>
            /// Scan a literal or distance tree to determine the frequencies of the codes
            /// in the bit length tree.
            /// </summary>
            public void CalcBLFreq(Tree blTree)
            {
                int maxCount;                // max repeat count
                int minCount;                // min repeat count
                int count;                   // repeat count of the current code
                int curLen = -1;             // length of current code

                int i = 0;
                while (i < this.NumCodes)
                {
                    count = 1;
                    int nextlen = this.Length[i];
                    if (nextlen == 0)
                    {
                        maxCount = 138;
                        minCount = 3;
                    }
                    else
                    {
                        maxCount = 6;
                        minCount = 3;
                        if (curLen != nextlen)
                        {
                            blTree.Frequencies[nextlen]++;
                            count = 0;
                        }
                    }

                    curLen = nextlen;
                    i++;

                    while (i < this.NumCodes && curLen == this.Length[i])
                    {
                        i++;
                        if (++count >= maxCount)
                        {
                            break;
                        }
                    }

                    if (count < minCount)
                    {
                        blTree.Frequencies[curLen] += (short)count;
                    }
                    else if (curLen != 0)
                    {
                        blTree.Frequencies[Repeat3To6]++;
                    }
                    else if (count <= 10)
                    {
                        blTree.Frequencies[Repeat3To10]++;
                    }
                    else
                    {
                        blTree.Frequencies[Repeat11To138]++;
                    }
                }
            }

            /// <summary>
            /// Write the tree values.
            /// </summary>
            /// <param name="pendingBuffer">The pending buffer.</param>
            /// <param name="bitLengthTree">The tree to write.</param>
            public void WriteTree(DeflaterPendingBuffer pendingBuffer, Tree bitLengthTree)
            {
                int maxCount;               // max repeat count
                int minCount;               // min repeat count
                int count;                  // repeat count of the current code
                int curLen = -1;            // length of current code

                int i = 0;
                while (i < this.NumCodes)
                {
                    count = 1;
                    int nextlen = this.Length[i];
                    if (nextlen == 0)
                    {
                        maxCount = 138;
                        minCount = 3;
                    }
                    else
                    {
                        maxCount = 6;
                        minCount = 3;
                        if (curLen != nextlen)
                        {
                            bitLengthTree.WriteSymbol(pendingBuffer, nextlen);
                            count = 0;
                        }
                    }

                    curLen = nextlen;
                    i++;

                    while (i < this.NumCodes && curLen == this.Length[i])
                    {
                        i++;
                        if (++count >= maxCount)
                        {
                            break;
                        }
                    }

                    if (count < minCount)
                    {
                        while (count-- > 0)
                        {
                            bitLengthTree.WriteSymbol(pendingBuffer, curLen);
                        }
                    }
                    else if (curLen != 0)
                    {
                        bitLengthTree.WriteSymbol(pendingBuffer, Repeat3To6);
                        pendingBuffer.WriteBits(count - 3, 2);
                    }
                    else if (count <= 10)
                    {
                        bitLengthTree.WriteSymbol(pendingBuffer, Repeat3To10);
                        pendingBuffer.WriteBits(count - 3, 3);
                    }
                    else
                    {
                        bitLengthTree.WriteSymbol(pendingBuffer, Repeat11To138);
                        pendingBuffer.WriteBits(count - 11, 7);
                    }
                }
            }

            private void BuildLength(ReadOnlySpan<int> children)
            {
                byte* lengthPtr = this.Length;
                ref int childrenRef = ref MemoryMarshal.GetReference(children);
                ref int bitLengthCountsRef = ref MemoryMarshal.GetReference<int>(this.bitLengthCounts);

                int maxLen = this.maxLength;
                int numNodes = children.Length >> 1;
                int numLeafs = (numNodes + 1) >> 1;
                int overflow = 0;

                Array.Clear(this.bitLengthCounts, 0, maxLen);

                // First calculate optimal bit lengths
                using (IMemoryOwner<int> lengthsMemoryOwner = this.memoryAllocator.Allocate<int>(numNodes, AllocationOptions.Clean))
                {
                    ref int lengthsRef = ref MemoryMarshal.GetReference(lengthsMemoryOwner.Memory.Span);

                    for (int i = numNodes - 1; i >= 0; i--)
                    {
                        if (children[(2 * i) + 1] != -1)
                        {
                            int bitLength = Unsafe.Add(ref lengthsRef, i) + 1;
                            if (bitLength > maxLen)
                            {
                                bitLength = maxLen;
                                overflow++;
                            }

                            Unsafe.Add(ref lengthsRef, Unsafe.Add(ref childrenRef, 2 * i)) = Unsafe.Add(ref lengthsRef, Unsafe.Add(ref childrenRef, (2 * i) + 1)) = bitLength;
                        }
                        else
                        {
                            // A leaf node
                            int bitLength = Unsafe.Add(ref lengthsRef, i);
                            Unsafe.Add(ref bitLengthCountsRef, bitLength - 1)++;
                            lengthPtr[Unsafe.Add(ref childrenRef, 2 * i)] = (byte)Unsafe.Add(ref lengthsRef, i);
                        }
                    }
                }

                if (overflow == 0)
                {
                    return;
                }

                int incrBitLen = maxLen - 1;
                do
                {
                    // Find the first bit length which could increase:
                    while (Unsafe.Add(ref bitLengthCountsRef, --incrBitLen) == 0)
                    {
                    }

                    // Move this node one down and remove a corresponding
                    // number of overflow nodes.
                    do
                    {
                        Unsafe.Add(ref bitLengthCountsRef, incrBitLen)--;
                        Unsafe.Add(ref bitLengthCountsRef, ++incrBitLen)++;
                        overflow -= 1 << (maxLen - 1 - incrBitLen);
                    }
                    while (overflow > 0 && incrBitLen < maxLen - 1);
                }
                while (overflow > 0);

                // We may have overshot above.  Move some nodes from maxLength to
                // maxLength-1 in that case.
                Unsafe.Add(ref bitLengthCountsRef, maxLen - 1) += overflow;
                Unsafe.Add(ref bitLengthCountsRef, maxLen - 2) -= overflow;

                // Now recompute all bit lengths, scanning in increasing
                // frequency.  It is simpler to reconstruct all lengths instead of
                // fixing only the wrong ones. This idea is taken from 'ar'
                // written by Haruhiko Okumura.
                //
                // The nodes were inserted with decreasing frequency into the childs
                // array.
                int nodeIndex = 2 * numLeafs;
                for (int bits = maxLen; bits != 0; bits--)
                {
                    int n = Unsafe.Add(ref bitLengthCountsRef, bits - 1);
                    while (n > 0)
                    {
                        int childIndex = 2 * Unsafe.Add(ref childrenRef, nodeIndex++);
                        if (Unsafe.Add(ref childrenRef, childIndex + 1) == -1)
                        {
                            // We found another leaf
                            lengthPtr[Unsafe.Add(ref childrenRef, childIndex)] = (byte)bits;
                            n--;
                        }
                    }
                }
            }

            public void Dispose()
            {
                if (!this.isDisposed)
                {
                    this.frequenciesMemoryHandle.Dispose();
                    this.frequenciesMemoryOwner.Dispose();

                    this.lengthsMemoryHandle.Dispose();
                    this.lengthsMemoryOwner.Dispose();

                    this.codesMemoryHandle.Dispose();
                    this.codesMemoryOwner.Dispose();

                    this.frequenciesMemoryOwner = null;
                    this.lengthsMemoryOwner = null;
                    this.codesMemoryOwner = null;

                    this.isDisposed = true;
                }

                GC.SuppressFinalize(this);
            }
        }
    }
}
