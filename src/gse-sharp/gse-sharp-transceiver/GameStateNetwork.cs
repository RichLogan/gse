using System;

namespace gs.sharp.transceiver
{
    /// <summary>
    /// Represents an object that can send and receive
    /// GameState messages.
    /// </summary>
    public interface IGameStateTransport
    {
        /// <summary>
        /// Fired when a new GameState message is available.
        /// </summary>
        event EventHandler<IMessage> OnMessageReceived;

        /// <summary>
        /// Send a game state message.
        /// </summary>
        /// <param name="toSend">The message to send.</param>
        void Send(IMessage toSend);
    }
}
