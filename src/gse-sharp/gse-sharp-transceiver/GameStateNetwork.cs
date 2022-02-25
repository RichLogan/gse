﻿using System;

namespace gs.sharp.transceiver
{
    public readonly struct EncodedMessage
    {
        public readonly IntPtr Buffer;
        public readonly int Length;
        public EncodedMessage(IntPtr buffer, int length)
        {
            Buffer = buffer;
            Length = length;
        }
    }

    /// <summary>
    /// Represents an object that can send and receive
    /// GameState messages.
    /// </summary>
    public interface IGameStateTransport
    {
        /// <summary>
        /// Fired when a new GameState message is available.
        /// </summary>
        event EventHandler<EncodedMessage> OnMessageReceived;

        /// <summary>
        /// Send a game state message.
        /// </summary>
        /// <param name="toSend">The message to send.</param>
        void Send(in EncodedMessage toSend);
    }
}
