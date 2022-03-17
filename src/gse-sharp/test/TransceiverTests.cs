using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Threading;

namespace gs.sharp.test;

using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
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
        var transceiver = new GameStateTransceiver(EXPIRY_MS);
        gsm.Register("1".AsIObject(), transceiver);

        // Old remote.
        var remote = new Object1(1, DateTimeOffset.UtcNow.Subtract(TimeSpan.FromMinutes(1)), new Loc1(), new Rot1(), new Loc1());
        transceiver.Remote = new GSObject(remote);

        // As this is a new update, we're expecting a
        // send event.
        bool fired = false;
        transceiver.MessageToSend += (_, __) => fired = true;

        // New local.
        var local = new Object1(1, DateTimeOffset.UtcNow, new Loc1(), new Rot1(), new Loc1());
        transceiver.Local = new GSObject(local);

        // Execute.
        Assert.IsTrue(fired);

        // Validate.
        Object1 captured = transceiver.Render.Object1;
        Assert.AreNotEqual(default, captured);
        Assert.AreNotEqual(remote, captured);
        Assert.AreEqual(local, captured);
        Assert.AreEqual(default, transceiver.Render);
    }

    [TestMethod]
    public void TestRenderNoData()
    {
        var gsm = new GameStateManager(new MockTransport());
        var transceiver = new GameStateTransceiver(EXPIRY_MS);
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
        var transceiver = new GameStateTransceiver(EXPIRY_MS);
        gsm.Register("1".AsIObject(), transceiver);

        // Old local.
        var local = new Object1(1, DateTimeOffset.UtcNow.Subtract(TimeSpan.FromMinutes(1)), new Loc1(), new Rot1(), new Loc1());
        transceiver.Local = new GSObject(local);

        // As this is a new remote update, we're not expecting a
        // send event.
        bool fired = false;
        transceiver.MessageToSend += (_, __) => fired = true;

        // New remote.
        var remote = new Object1(1, DateTimeOffset.UtcNow, new Loc1(), new Rot1(), new Loc1());
        transceiver.Remote = new GSObject(remote);

        // Execute.
        Assert.IsFalse(fired);

        var captured = transceiver.Render;
        Assert.AreNotEqual(default, captured);
        var capturedObject = captured.Object1;
        Assert.AreNotEqual(local, capturedObject);
        Assert.AreEqual(remote, capturedObject);
        Assert.AreEqual(default, transceiver.Render);
    }

    private class MockTransceiver : IGameStateTransceiver
    {
        public GSObject Local { get; set; }
        public GSObject Remote { get; set; }
        public GSObject Render { get; }
        public TransceiveType Type { get; }
        public event EventHandler<LogEventArgs> Log;
        public event EventHandler<GSObject> MessageToSend;
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
        encoder.Encode(new GSObject(fakeMessage));
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
        Assert.AreEqual(fakeMessage.ID, mockTransceiver.Remote.Object1.ID);
    }

    [TestMethod]
    public void TestUnknownObjectNotification()
    {
        var data = new byte[] { 0x01, 0x02, };
        IntPtr ptr = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, ptr, data.Length);
        var obj = new UnknownObject(0x20, (ulong)data.Length, ptr);
        var transport = new MockTransport();
        var gsm = new GameStateManager(transport, true);
        gsm.Log += (_, log) => Console.WriteLine(log.Message);
        bool gotUnknownEvent = false;
        gsm.OnUnregisteredUnknown += (_, message) =>
        {
            gotUnknownEvent = true;
            Assert.AreEqual(obj.Tag, message.Tag);
            Assert.AreEqual(obj.DataLength, message.DataLength);
        };
        var encoder = new Encoder(1500);
        encoder.Encode(new GSObject(obj));
        var encodedMessage = new EncodedMessage(encoder.DataBuffer, encoder.GetDataLength());

        // Should fire the event.
        transport.MockArrival(encodedMessage);
        Assert.IsTrue(gotUnknownEvent);

        // Registering should stop it firing.
        gotUnknownEvent = false;
        var mockTransceiver = new GameStateTransceiver(expiryMs: 1);
        gsm.Register(obj.Tag, mockTransceiver);
        transport.MockArrival(encodedMessage);
        Assert.IsFalse(gotUnknownEvent);

        // That message should have made it through.
        Assert.AreEqual(obj.Tag, mockTransceiver.Render.UnknownObject.Tag);

        // Free the test object.
        Marshal.FreeHGlobal(ptr);
    }

    #region Retransmission Tests

    [TestMethod]
    public void RetransmitNoData()
    {
        // Setup and proc skipped retransmit.
        var transv = new GameStateTransceiver(EXPIRY_MS, true);
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
        var transv = new GameStateTransceiver(EXPIRY_MS, true, reasons);
        transv.Log += (_, args) => Console.WriteLine(args.Message);
        transv.Retransmit();

        var local = new Object1(1, DateTimeOffset.UtcNow, new Loc1(), new Rot1(), new Loc1());
        transv.Local = new GSObject(local);

        // Local only should retransmit.
        Assert.AreEqual(0, reasons.NoRemotes);
        Assert.IsTrue(transv.Retransmit());
        Assert.AreEqual(1, reasons.NoRemotes);
    }

    [TestMethod]
    public void RetransmitRemoteOnly()
    {
        // Setup and proc skipped retransmit.
        var transv = new GameStateTransceiver(EXPIRY_MS, true);
        transv.Log += (_, args) => Console.WriteLine(args.Message);
        transv.Retransmit();

        var remote = new Object1(1, DateTimeOffset.UtcNow, new Loc1(), new Rot1(), new Loc1());
        transv.Remote = new GSObject(remote);

        // Recent remove only should not retransmit.
        Assert.IsFalse(transv.Retransmit());
    }

    [TestMethod]
    public void RetransmitLocalNewer()
    {
        // Setup and proc skipped retransmit.
        var reasons = new MockRetransmitReason();
        var transv = new GameStateTransceiver(EXPIRY_MS, true, reasons);
        transv.Log += (_, args) => Console.WriteLine(args.Message);
        transv.Retransmit();

        var remote = new Object1(1, DateTimeOffset.UtcNow, new Loc1(), new Rot1(), new Loc1());
        transv.Remote = new GSObject(remote);
        var local = new Object1(1, DateTimeOffset.UtcNow, new Loc1(), new Rot1(), new Loc1());
        transv.Local = new GSObject(local);

        // We own the latest local update, so we should retransmit.
        Assert.AreEqual(0, reasons.NewerLocals);
        Assert.IsTrue(transv.Retransmit());
        Assert.AreEqual(1, reasons.NewerLocals);
    }

    [TestMethod]
    public void RetransmitRemoteNewer()
    {
        // Setup and proc skipped retransmit.
        var reasons = new MockRetransmitReason();
        var transv = new GameStateTransceiver(EXPIRY_MS, true, reasons);
        transv.Log += (_, args) => Console.WriteLine(args.Message);
        transv.Retransmit();

        var local = new Object1(1, DateTimeOffset.UtcNow.Subtract(TimeSpan.FromMilliseconds(1)), new Loc1(), new Rot1(), new Loc1());
        transv.Local = new GSObject(local);
        var remote = new Object1(1, DateTimeOffset.UtcNow, new Loc1(), new Rot1(), new Loc1());
        transv.Remote = new GSObject(remote);

        // A new remote update arrived recently, so we shouldn't retransmit.
        Assert.IsFalse(transv.Retransmit());
    }

    [TestMethod]
    public void ExpiredRemoteUpdateNoLocal()
    {
        // Setup and proc skipped retransmit.
        var reasons = new MockRetransmitReason();
        var transv = new GameStateTransceiver(EXPIRY_MS, true, reasons);
        transv.Log += (_, args) => Console.WriteLine(args.Message);
        transv.Retransmit();

        var remote = new Object1(1, DateTimeOffset.UtcNow.Subtract(TimeSpan.FromSeconds(30)), new Loc1(), new Rot1(), new Loc1());
        transv.Remote = new GSObject(remote);
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
        var transv = new GameStateTransceiver(EXPIRY_MS, true, reasons);
        transv.Log += (_, args) => Console.WriteLine(args.Message);
        transv.Retransmit();

        var local = new Object1(1, DateTimeOffset.UtcNow.Subtract(TimeSpan.FromSeconds(31)), new Loc1(), new Rot1(), new Loc1());
        var remote = new Object1(1, DateTimeOffset.UtcNow.Subtract(TimeSpan.FromSeconds(30)), new Loc1(), new Rot1(), new Loc1());
        transv.Local = new GSObject(local);
        transv.Remote = new GSObject(remote);
        Thread.Sleep(EXPIRY_MS);

        // The remote update hasn't been seen in a while, and is newer than our local, should takeover.
        Assert.AreEqual(0, reasons.ExpiredRemotes);
        Assert.IsTrue(transv.Retransmit());
        Assert.AreEqual(1, reasons.ExpiredRemotes);
    }

    #endregion
}
