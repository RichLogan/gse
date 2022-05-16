﻿/*
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
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace gs.sharp
{
    /// <summary>
    /// Interface to libgse encoder.
    /// </summary>
    public class Encoder : IDisposable
    {
        /// <summary>
        /// Provides a view into the underlying encoder buffer.
        /// </summary>
        public IntPtr DataBuffer { get; private set; }

        private readonly EncoderContextHandle _context;
        private bool _disposedValue;

        /// <summary>
        /// Create a new encoder with the given buffer size.
        /// </summary>
        /// <param name="bufferSize">Size of the buffer to create in bytes.</param>
        /// <exception cref="InvalidOperationException">Underlying encoder failure.</exception>
        public Encoder(int bufferSize)
        {
            DataBuffer = Marshal.AllocHGlobal(bufferSize);
            var returnCode = NativeMethods.GSEncoderInit(out IntPtr handle, DataBuffer, (System.UInt64)bufferSize);
            _context = new EncoderContextHandle(handle);
            if (returnCode != 0)
            {
                throw new InvalidOperationException($"GSEncoderInit returned {returnCode}");
            }
        }

        /// <summary>
        /// Return the current encoder data length.
        /// </summary>
        /// <returns>Encoder data length in bytes.</returns>
        public int GetDataLength() => NativeMethods.GSEncoderDataLength(_context);

        /// <summary>
        /// Encode the given object.
        /// </summary>
        /// <typeparam name="T">Type of object to encoded.</typeparam>
        /// <param name="toEncode">Instance to encode.</param>
        /// <exception cref="InvalidOperationException">Underlying failure.</exception>
        /// <exception cref="ArgumentException">Unsupported type.</exception>
        public void Encode(GSObject toEncode)
        {
            // var obj = GSObjectFactory.Make(toEncode);
            int result = -1;
            var handle = GCHandle.Alloc(toEncode, GCHandleType.Pinned);
            try
            {
                result = NativeMethods.GSEncodeObject(_context, handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }

            // Parse return codes.
            switch (result)
            {
                case -1:
                    // Unspecified failure.
                    var error = Marshal.PtrToStringAnsi(NativeMethods.GetEncoderError(_context));
                    throw new InvalidOperationException($"GSE failed encode: {error}");
                case 0:
                    // No more space.
                    throw new InvalidOperationException("The buffer has no more space for this object");
                case 1:
                    // Okay!
                    return;
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    _context?.Dispose();
                    Marshal.FreeHGlobal(DataBuffer);
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

        private class EncoderContextHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public EncoderContextHandle(IntPtr handle) : base(true) => SetHandle(handle);
            public EncoderContextHandle() : base(true) { }
            protected override bool ReleaseHandle() => NativeMethods.GSEncoderDestroy(handle) == 0;
        }

        private class NativeMethods
        {
            private const string GSE_LIB_NAME = "gse";

            [DllImport(GSE_LIB_NAME, EntryPoint = "GSEncoderInit", BestFitMapping = true, ExactSpelling = false)]
            public static extern int GSEncoderInit(out IntPtr context, IntPtr buffer, System.UInt64 length);

            [DllImport(GSE_LIB_NAME)]
            public static extern int GSEncoderDataLength(EncoderContextHandle context);

            [DllImport(GSE_LIB_NAME)]
            public static extern int GSEncodeObject(EncoderContextHandle context, IntPtr toEncode);

            [DllImport(GSE_LIB_NAME)]
            public static extern IntPtr GetEncoderError(EncoderContextHandle context);

            [DllImport(GSE_LIB_NAME)]
            internal static extern int GSEncoderDestroy(IntPtr context);
        }
    }
}

#pragma warning restore IDE0049
