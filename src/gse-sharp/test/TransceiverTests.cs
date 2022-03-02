using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;

namespace gs.sharp.test;

using System.Diagnostics.CodeAnalysis;
using transceiver;

[TestClass]
public class TransceiverTests
{
    private class MockTransport : IGameStateTransport
    {
        public event EventHandler<EncodedMessage>? OnMessageReceived;
        public void Send(in EncodedMessage toSend) { }
        public void MockArrival(in EncodedMessage message) => OnMessageReceived?.Invoke(this, message);
    }

    private readonly struct MockData : IMessage
    {
        public DateTimeOffset Timestamp => _timestamp;
        public ulong ID => _id;

        private readonly DateTimeOffset _timestamp;
        private readonly ulong _id;

        public MockData(DateTimeOffset time, ulong id)
        {
            _timestamp = time;
            _id = id;
        }

        public override bool Equals([NotNullWhen(true)] object? obj)
        {
            if (obj == null) return false;
            var other = (MockData)obj;
            return other.ID == ID && other.Timestamp == Timestamp;
        }

        public override int GetHashCode() => (int)((long)ID ^ Timestamp.ToUnixTimeMilliseconds());
    }

    [TestMethod]
    public void TestAlgorithmLocal()
    {
        // A recent local update should take precedence
        // over a remote update.
        var gsm = new GameStateManager(new MockTransport());
        var transceiver = new GameStateTransceiver<MockData>();
        gsm.Register("1".AsIObject(), transceiver);

        // Old remote.
        var remote = new MockData(DateTimeOffset.UtcNow.Subtract(TimeSpan.FromMinutes(1)), 1);
        transceiver.Remote = remote;

        // As this is a new update, we're expecting a
        // send event.
        bool fired = false;
        transceiver.MessageToSend += (_, __) => fired = true;

        // New local.
        var local = new MockData(DateTimeOffset.UtcNow, 2);
        transceiver.Local = local;

        // Execute.
        Assert.IsTrue(fired);

        // Validate.
        var captured = transceiver.Render;
        Assert.AreNotEqual(default, captured);
        Assert.AreNotEqual(remote, captured);
        Assert.AreEqual(local, captured);
        Assert.AreEqual(default, transceiver.Render);
    }

    [TestMethod]
    public void TestRenderNoData()
    {
        var gsm = new GameStateManager(new MockTransport());
        var transceiver = new GameStateTransceiver<MockData>();
        gsm.Register("2".AsIObject(), transceiver);
        var render = transceiver.Render;
        Assert.AreEqual(default, render);
    }

    [TestMethod]
    public void TestAlgorithmRemote()
    {
        // A recent remote update should take precedence
        // over a local update.
        var gsm = new GameStateManager(new MockTransport());
        var transceiver = new GameStateTransceiver<MockData>();
        gsm.Register("2".AsIObject(), transceiver);

        // Old local.
        var local = new MockData(DateTimeOffset.UtcNow.Subtract(TimeSpan.FromMinutes(1)), 1);
        transceiver.Local = local;

        // As this is a new remote update, we're not expecting a
        // send event.
        bool fired = false;
        transceiver.MessageToSend += (_, __) => fired = true;

        // New remote.
        var remote = new MockData(DateTimeOffset.UtcNow, 2);
        transceiver.Remote = remote;

        // Execute.
        Assert.IsFalse(fired);

        var captured = transceiver.Render;
        Assert.AreNotEqual(default, captured);
        Assert.AreNotEqual(local, captured);
        Assert.AreEqual(remote, captured);
        Assert.AreEqual(default, transceiver.Render);
    }

    private class MockTransceiver : IGameStateTransceiver
    {
        public IMessage Remote { get; set; }
        public event EventHandler<LogEventArgs> Log;
        public event EventHandler<IMessage> MessageToSend;
        public void Retransmit() { }
    }

    [TestMethod]
    public void CheckUnknownNotification()
    {
        var fakeMessage = new Object1(1, DateTimeOffset.UtcNow, new Loc1(1, 2, 3), new Rot1(4, 5, 6), new Loc1(7, 8, 9));
        var transport = new MockTransport();
        var gsm = new GameStateManager(transport);
        bool gotUnknownEvent = false;
        gsm.OnUnregisteredUpdate += (_, message) =>
        {
            gotUnknownEvent = true;
            Assert.AreEqual(fakeMessage.ID, message.ID);
            Assert.AreEqual(fakeMessage.Timestamp, message.Timestamp);
        };
        var encoder = new Encoder(1500);
        encoder.Encode(fakeMessage);
        var encodedMessage = new EncodedMessage(encoder.DataBuffer, encoder.GetDataLength());

        // Should fire the event.
        transport.MockArrival(encodedMessage);
        Assert.IsTrue(gotUnknownEvent);

        // Registering should stop it firing.
        gotUnknownEvent = false;
        var mockTransceiver = new MockTransceiver();
        gsm.Register(fakeMessage, mockTransceiver);
        transport.MockArrival(encodedMessage);
        Assert.IsFalse(gotUnknownEvent);

        // That message should have made it through.
        Assert.AreEqual(fakeMessage.ID, mockTransceiver.Remote.ID);
    }
}
