using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;

namespace gs.sharp.test;

using transceiver;

[TestClass]
public class TransceiverTests
{
    private class MockTransport : IGameStateTransport
    {
        public event EventHandler<IMessage>? OnMessageReceived;
        public void Send(IMessage toSend) { }
        public void MockArrival(IMessage message) => OnMessageReceived?.Invoke(this, message);
    }

    private class MockData : IMessage
    {
        public DateTimeOffset Timestamp { get; private set; }
        public ulong ID { get; private set; }

        public MockData(DateTimeOffset time, ulong id)
        {
            Timestamp = time;
            ID = id;
        }
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

        // New local.
        var local = new MockData(DateTimeOffset.UtcNow, 2);
        transceiver.Local = local;

        // As this is a new update, we're expecting a
        // send event.
        bool fired = false;
        transceiver.MessageToSend += (_, __) => fired = true;

        // Execute.
        gsm.CalculateAll();
        Assert.IsTrue(fired);

        // Validate.
        var captured = transceiver.Render;
        Assert.AreEqual(local, captured);
        Assert.AreNotEqual(remote, captured);
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
        gsm.CalculateAll();
        Assert.IsFalse(fired);

        var captured = transceiver.Render;
        Assert.AreEqual(remote, captured);
        Assert.AreNotEqual(local, captured);
    }
    
    [TestMethod]
    public void TestRenderConsume()
    {
        var gsm = new GameStateManager(new MockTransport());
        var transceiver = new GameStateTransceiver<MockData>();
        gsm.Register("1".AsIObject(), transceiver);

        var local = new MockData(DateTimeOffset.UtcNow, 2);
        transceiver.Local = local;

        // Execute.
        gsm.CalculateAll();

        // Validate.
        Assert.AreNotEqual(default(MockData), transceiver.Render);
        Assert.AreEqual(default(MockData), transceiver.Render);
    }


    [TestMethod]
    public void CheckUnknownNotification()
    {
        var fakeMessage = new MockData(DateTimeOffset.UtcNow, 1);
        var transport = new MockTransport();
        var gsm = new GameStateManager(transport);
        bool gotUnknownEvent = false;
        gsm.OnUnregisteredUpdate += (_, message) =>
        {
            gotUnknownEvent = true;
            Assert.AreEqual(fakeMessage.ID, message.ID);
            Assert.AreEqual(fakeMessage.Timestamp, message.Timestamp);
        };
        transport.MockArrival(fakeMessage);
        Assert.IsTrue(gotUnknownEvent);
    }
}
