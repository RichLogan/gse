/*
 *  BSD 2-Clause License
 *
 *  Copyright (c) 2022, Cisco Systems
 *  All rights reserved.
 *
 *  Redistribution and use in source and binary forms, with or without
 *  modification, are permitted provided that the following conditions are met:
 *
 *  1. Redistributions of source code must retain the above copyright notice,
      this list of conditions and the following disclaimer.
 *
 *  2. Redistributions in binary form must reproduce the above copyright notice,
 *     this list of conditions and the following disclaimer in the documentation
 *     and/or other materials provided with the distribution.
 *
 *  THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
 *  AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
 *  IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
 *  ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE
 *  LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
 *  CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
 *  SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
 *  INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
 *  CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
 *  ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
 *  POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace gs.sharp.test;

[TestClass]
public class DecoderTests
{
    [TestMethod]
    public void TestDecodeHead()
    {
        var head1 = PerformDecodeTest(new byte[] {
            0x01, 0x21, 0x00, 0x05, 0x00, 0x3f, 0x8c, 0xcc,
            0xcd, 0x3e, 0x4c, 0xcc, 0xcd, 0x41, 0xf0, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00
        });
        Assert.IsNotNull(head1);
        Assert.IsFalse(head1.Head1.IPDPresent);
        head1.Head1.Dispose();
    }

    [TestMethod]
    public void TestDecodeHeadIPD()
    {
        var gsObject = PerformDecodeTest(new byte[]
        {
            0x01, 0x27, 0x00, 0x05, 0x00, 0x3f, 0x8c, 0xcc,
            0xcd, 0x3e, 0x4c, 0xcc, 0xcd, 0x41, 0xf0, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00,

            // ipd
            0xc0, 0x80, 0x02, 0x02, 0x42, 0x48
        });
        Assert.IsFalse(Default.Is(gsObject));
        Head1 head = gsObject.Head1;
        Assert.IsTrue(head.IPDPresent);
        gsObject.Head1.Dispose();
    }

    [TestMethod]
    public void TestDecodeUnknown()
    {
        var gsObject = PerformDecodeTest(new byte[]
        {
            0x20, 0x21, 0x00, 0x05, 0x00, 0x3f, 0x8c, 0xcc,
            0xcd, 0x3e, 0x4c, 0xcc, 0xcd, 0x41, 0xf0, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00
        });
        Assert.IsFalse(Default.Is(gsObject));
        Assert.IsFalse(Default.Is(gsObject.Object1));
        gsObject.Object1.Dispose();
    }

    [TestMethod]
    public void TestDecodeMultiple()
    {
    }

    [TestMethod]
    public void TestDecodeHand2()
    {
        var gsObject = PerformDecodeTest(new byte[]
        {
            // tag
            0xc0, 0x80, 0x01,

            // Length
            0x80, 0xb8,

            // id
            0x0c,

            // time
            0x05, 0x00,

            // left
            0x01,

            // location
            0x3f, 0x8c, 0xcc, 0xcd, 0x3e, 0x4c, 0xcc, 0xcd,
            0x41, 0xf0, 0x00, 0x00, 0x42, 0x48, 0x00, 0x00,
            0x00, 0x00,

            // rotoration
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x42, 0x48,

            // wrist
            0x00, 0x00, 0x42, 0x48, 0x00, 0x00,

            // thumb
            0x00, 0x00, 0x42, 0x48, 0x00, 0x00, 0x00, 0x00,
            0x42, 0x48, 0x00, 0x00, 0x00, 0x00, 0x42, 0x48,
            0x00, 0x00, 0x00, 0x00, 0x42, 0x48, 0x00, 0x00,

            // index
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00,

            // middle
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00,

            // ring
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00,

            // pinky
            0x42, 0x48, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00

        });
        Assert.IsFalse(Default.Is(gsObject));
        Assert.IsFalse(Default.Is(gsObject.Hand2));
        gsObject.Hand2.Dispose();
    }

    [TestMethod]
    public void TestEncodeDecodeObject()
    {
        var obj = new Object1(5, DateTimeOffset.UtcNow, new Loc1(1, 2, 3), new Rot1(4, 5, 6), new Loc1(7, 8, 9), 10);
        var encoder = new Encoder(1500);
        encoder.Encode(new GSObject(obj));
        var decoder = new Decoder(1500, encoder.DataBuffer);
        GSObject result = decoder.Decode();

        Assert.IsFalse(Default.Is(result));
        Assert.IsFalse(Default.Is(result.Object1));
        Assert.AreEqual(obj, result.Object1);
        result.Object1.Dispose();
    }

    private readonly struct Example
    {
        public readonly int Test;
        public Example(int test) => Test = test;
    }

    [TestMethod]
    public void TestEncodeDecodeUnknown()
    {
        var data = new Example(1234);
        var size = Marshal.SizeOf(data);
        IntPtr ptr = Marshal.AllocHGlobal(size);
        Marshal.StructureToPtr(data, ptr, false);

        // Bytes for later comparison.
        var expected = new byte[size];
        Marshal.Copy(ptr, expected, 0, size);

        // Encode.
        var encoder = new Encoder(1500);
        var obj = new GSObject(new UnknownObject(0x20, (ulong)size, ptr));
        encoder.Encode(obj);
        obj.Object1.Dispose();
        var writtenBytes = new byte[size + 2];
        Marshal.Copy(encoder.DataBuffer, writtenBytes, 0, size + 2);
        for (var i = 2; i <= size; i++)
        {
            Assert.AreEqual(expected[i - 2], writtenBytes[i]);
        }

        var decoder = new Decoder(1500, encoder.DataBuffer);
        var decoded = decoder.Decode().UnknownObject;
        Assert.IsFalse(Default.Is(decoded));
        var output = Marshal.PtrToStructure<Example>(decoded.Data);
        Assert.AreEqual(data, output);
        Assert.AreEqual(data.Test, output.Test);
    }

    private GSObject PerformDecodeTest(byte[] expected)
    {
        GCHandle pin = GCHandle.Alloc(expected, GCHandleType.Pinned);
        try
        {
            // Decode.
            var decoder = new Decoder(expected.Length, pin.AddrOfPinnedObject());
            var result = decoder.Decode();
            Assert.IsFalse(Default.Is(result));

            // Encode.
            var encoder = new Encoder(expected.Length);
            encoder.Encode(result);

            // Validate.
            Assert.AreEqual(expected.Length, encoder.GetDataLength());

            // Check bytes.
            var encoded = new byte[expected.Length];
            Marshal.Copy(encoder.DataBuffer, encoded, 0, expected.Length);
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.AreEqual(expected[i], encoded[i]);
            }

            // Verify next decode returns null.
            var nextDecode = decoder.Decode();
            Assert.IsTrue(Default.Is(nextDecode));

            // Destroy decoder.
            decoder.Dispose();

            // Return the decoded object.
            return result;
        }
        finally
        {
            pin.Free();
        }
    }
}
