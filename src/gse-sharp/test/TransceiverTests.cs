using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Threading;

namespace gs.sharp.test;

using System.Diagnostics.CodeAnalysis;
using transceiver;

[TestClass]
public class TransceiverTests
{
    private const int EXPIRY_MS = 1000;

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

    public class MockRetransmitReason : IRetransmitReasons
    {
        public int ExpiredRemotes { get; private set; }
        public int NoRemotes { get; private set; }
        public int NewerLocals { get; private set; }
        public int NoLocals { get; private set; }
        public int NoRecentRemotes { get; private set; }

        public void YesExpiredRemote() => ExpiredRemotes++;
        public void YesNoRemote() => NoRemotes++;
        public void YesNewerLocal() => NewerLocals++;
        public void NoNoLocal() => NoLocals++;
        public void NoRecentRemote() => NoRecentRemotes++;
    }

    [TestMethod]
    public void TestRenderLocal()
    {
        // A recent local update should take precedence
        // over a remote update.
        var gsm = new GameStateManager(new MockTransport());

        // TODO: I should be able to declare T as MockData.
        var transceiver = new GameStateMesageTransceiver<IMessage>(EXPIRY_MS);
        gsm.Register("1".AsIObject(), transceiver);

        // Old remote.
        var remote = new MockData(DateTimeOffset.UtcNow.Subtract(TimeSpan.FromMinutes(1)), 1);
        transceiver.Remote = remote;

        // As this is a new update, we're expecting a
        // send event.
        bool fired = false;
        transceiver.MessageToSend += (_, __) => fired = true;

        // New local.
        var local = new MockData(DateTimeOffset.UtcNow, 1);
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
        var transceiver = new GameStateMesageTransceiver<IMessage>(EXPIRY_MS);
        gsm.Register("2".AsIObject(), transceiver);
        var render = transceiver.Render;
        Assert.AreEqual(default, render);
    }

    [TestMethod]
    public void TestRenderRemote()
    {
        // A recent remote update should take precedence
        // over a local update.
        var gsm = new GameStateManager(new MockTransport());
        var transceiver = new GameStateMesageTransceiver<IMessage>(EXPIRY_MS);
        gsm.Register("1".AsIObject(), transceiver);

        // Old local.
        var local = new MockData(DateTimeOffset.UtcNow.Subtract(TimeSpan.FromMinutes(1)), 1);
        transceiver.Local = local;

        // As this is a new remote update, we're not expecting a
        // send event.
        bool fired = false;
        transceiver.MessageToSend += (_, __) => fired = true;

        // New remote.
        var remote = new MockData(DateTimeOffset.UtcNow, 1);
        transceiver.Remote = remote;

        // Execute.
        Assert.IsFalse(fired);

        var captured = transceiver.Render;
        Assert.AreNotEqual(default, captured);
        Assert.AreNotEqual(local, captured);
        Assert.AreEqual(remote, captured);
        Assert.AreEqual(default, transceiver.Render);
    }

    private class MockTransceiver : IGameStateTransceiver<IMessage>
    {
        public IMessage Local { get; set; }
        public IMessage Remote { get; set; }
        public IMessage Render { get; }
        public TransceiveType Type { get; }
        public event EventHandler<LogEventArgs> Log;
        public event EventHandler<IMessage> MessageToSend;
        public bool Retransmit() => false;
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

    #region Retransmission Tests

    [TestMethod]
    public void RetransmitNoData()
    {
        // Setup and proc skipped retransmit.
        var transv = new GameStateMesageTransceiver<MockData>(EXPIRY_MS, true);
        transv.Log += (_, args) => Console.WriteLine(args.Message);
        transv.Retransmit();

        // No data, no retransmit.
        Assert.IsFalse(transv.Retransmit());
    }

    [TestMethod]
    public void RetransmitLocalOnly()
    {
        // Setup and proc skipped retransmit.
        var reasons = new MockRetransmitReason();
        var transv = new GameStateMesageTransceiver<MockData>(EXPIRY_MS, true, reasons);
        transv.Log += (_, args) => Console.WriteLine(args.Message);
        transv.Retransmit();

        var local = new MockData(DateTime.UtcNow, 1);
        transv.Local = local;

        // Local only should retransmit.
        Assert.AreEqual(0, reasons.NoRemotes);
        Assert.IsTrue(transv.Retransmit());
        Assert.AreEqual(1, reasons.NoRemotes);
    }

    [TestMethod]
    public void RetransmitRemoteOnly()
    {
        // Setup and proc skipped retransmit.
        var transv = new GameStateMesageTransceiver<MockData>(EXPIRY_MS, true);
        transv.Log += (_, args) => Console.WriteLine(args.Message);
        transv.Retransmit();

        var remote = new MockData(DateTime.UtcNow, 1);
        transv.Remote = remote;

        // Recent remove only should not retransmit.
        Assert.IsFalse(transv.Retransmit());
    }

    [TestMethod]
    public void RetransmitLocalNewer()
    {
        // Setup and proc skipped retransmit.
        var reasons = new MockRetransmitReason();
        var transv = new GameStateMesageTransceiver<MockData>(EXPIRY_MS, true, reasons);
        transv.Log += (_, args) => Console.WriteLine(args.Message);
        transv.Retransmit();

        var remote = new MockData(DateTimeOffset.UtcNow, 1);
        transv.Remote = remote;
        var local = new MockData(DateTimeOffset.UtcNow, 1);
        transv.Local = local;

        // We own the latest local update, so we should retransmit.
        Assert.AreEqual(0, reasons.NewerLocals);
        Assert.IsTrue(transv.Retransmit());
        Assert.AreEqual(1, reasons.NewerLocals);
    }

    [TestMethod]
    public void RetransmitRemoteNewer()
    {
        // Setup and proc skipped retransmit.
        var transv = new GameStateMesageTransceiver<MockData>(EXPIRY_MS, true);
        transv.Log += (_, args) => Console.WriteLine(args.Message);
        transv.Retransmit();

        var local = new MockData(DateTimeOffset.UtcNow, 1);
        transv.Local = local;
        var remote = new MockData(DateTime.UtcNow, 1);
        transv.Remote = remote;

        // A new remote update arrived recently, so we should retransmit.
        Assert.IsFalse(transv.Retransmit());
    }

    [TestMethod]
    public void ExpiredRemoteUpdateNoLocal()
    {
        // Setup and proc skipped retransmit.
        var reasons = new MockRetransmitReason();
        var transv = new GameStateMesageTransceiver<MockData>(EXPIRY_MS, true, reasons);
        transv.Log += (_, args) => Console.WriteLine(args.Message);
        transv.Retransmit();

        var remote = new MockData(DateTime.UtcNow.Subtract(TimeSpan.FromSeconds(30)), 1);
        transv.Remote = remote;
        Thread.Sleep(EXPIRY_MS);

        // The remote update hasn't been seen in a while, should takeover.
        Assert.AreEqual(0, reasons.ExpiredRemotes);
        Assert.IsTrue(transv.Retransmit());
        Assert.AreEqual(1, reasons.ExpiredRemotes);
    }

    [TestMethod]
    public void ExpiredRemoteUpdateOldLocal()
    {
        // Setup and proc skipped retransmit.
        var reasons = new MockRetransmitReason();
        var transv = new GameStateMesageTransceiver<MockData>(EXPIRY_MS, true, reasons);
        transv.Log += (_, args) => Console.WriteLine(args.Message);
        transv.Retransmit();

        var local = new MockData(DateTime.UtcNow.Subtract(TimeSpan.FromSeconds(31)), 1);
        var remote = new MockData(DateTime.UtcNow.Subtract(TimeSpan.FromSeconds(30)), 1);
        transv.Local = local;
        transv.Remote = remote;
        Thread.Sleep(EXPIRY_MS);

        // The remote update hasn't been seen in a while, and is newer than our local, should takeover.
        Assert.AreEqual(0, reasons.ExpiredRemotes);
        Assert.IsTrue(transv.Retransmit());
        Assert.AreEqual(1, reasons.ExpiredRemotes);
    }

    #endregion
}
