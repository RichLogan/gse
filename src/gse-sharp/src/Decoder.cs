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

#pragma warning disable IDE0049 // Ignore name simplification warnings so native type sizes are explicit.

using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace gs.sharp
{
    /// <summary>
    /// Interface to libgse decoder.
    /// </summary>
    public class Decoder : IDisposable
    {
        /// <summary>
        /// Provides a view into the underlying decoder buffer.
        /// </summary>
        public IntPtr DataBuffer { get; }

        private readonly DecoderContextHandle _context;
        private bool _disposedValue;

        /// <summary>
        /// Create a new decoder with the given buffer size.
        /// </summary>
        /// <param name="bufferSize">Size of the provided buffer in bytes.</param>
        /// <param name="buffer">The provided buffer to decode into.</param>
        /// <exception cref="InvalidOperationException">Underlying decoder failure.</exception>
        public Decoder(int bufferSize, IntPtr buffer)
        {
            // External buffer.
            DataBuffer = buffer;
            var returnCode = NativeMethods.GSDecoderInit(out IntPtr handle, DataBuffer, (UInt64)bufferSize);
            _context = new DecoderContextHandle(handle);
            if (returnCode != 0)
            {
                throw new InvalidOperationException($"GSDecoderInit returned {returnCode}");
            }
        }

        /// <summary>
        /// Attempt to fetch a decoded object.
        /// </summary>
        /// <returns>Tuple of object and type, if any available.</returns>
        public GSObject Decode()
        {
            // Attempt decode.
            int decoded = NativeMethods.GSDecodeObject(_context, out GSObject gsObject);

            // Check return type.
            switch (decoded)
            {
                case -1:
                    var error = Marshal.PtrToStringAnsi(NativeMethods.GetDecoderError(_context));
                    throw new InvalidOperationException($"Decode failed: {error}");
                case 0:
                    return default;
                case 1:
                    break;
                default:
                    throw new InvalidOperationException("Unexpected decode return call: " + decoded);
            }

            // Resolve timestamp as soon as possible, if appropriate.
            var message = IsMessage(gsObject);
            if (!Default.Is(message)) message.SaveTimestamp();

            // Return.
            return gsObject;
        }

        private static IMessage IsMessage(in GSObject gsObject)
        {
            switch (gsObject.Type)
            {
                case (ulong)Tag.Head1:
                    return gsObject.Head1;
                case (ulong)Tag.Hand1:
                    return gsObject.Hand1;
                case (ulong)Tag.Hand2:
                    return gsObject.Hand2;
                case (ulong)Tag.Object1:
                    return gsObject.Object1;
                default:
                    return default;
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    _context?.Dispose();
                }
                _disposedValue = true;
            }
        }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        private class DecoderContextHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public DecoderContextHandle(IntPtr handle) : base(true) => SetHandle(handle);
            public DecoderContextHandle() : base(true) { }
            protected override bool ReleaseHandle() => NativeMethods.GSDecoderDestroy(handle) == 0;
        }

        private class NativeMethods
        {
            private const string GSE_LIB_NAME = "gse";

            [DllImport(GSE_LIB_NAME)]
            public static extern int GSDecoderInit(out IntPtr context, IntPtr buffer, System.UInt64 length);

            [DllImport(GSE_LIB_NAME)]
            public static extern int GSDecodeObject(DecoderContextHandle context, out GSObject gsObjects);

            [DllImport(GSE_LIB_NAME)]
            public static extern IntPtr GetDecoderError(DecoderContextHandle context);

            [DllImport(GSE_LIB_NAME)]
            internal static extern int GSDecoderDestroy(IntPtr context);
        }
    }
}

#pragma warning restore IDE0049
