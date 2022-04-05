using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace gs.sharp.test
{
    [TestClass]
    public class TestUtilities
    {
        [TestMethod]
        public void TestSetThenGet()
        {
            var now = DateTimeOffset.UtcNow.Subtract(TimeSpan.FromSeconds(1));
            var object1 = new Object1(1, now, new Loc1(1, 2, 3), new Rot1(4, 5, 6), new Loc1(7, 8, 9));
            Assert.AreEqual(now.ToUnixTimeMilliseconds(), object1.Timestamp.ToUnixTimeMilliseconds());
            object1.Dispose();
        }

        [TestMethod]
        public void TestTimeExtensions()
        {
            var now = DateTimeOffset.UtcNow.Subtract(TimeSpan.FromSeconds(20));
            var sw = new Stopwatch();
            sw.Start();
            var object1 = new Object1(1, now, new Loc1(1, 2, 3), new Rot1(4, 5, 6), new Loc1(7, 8, 9));
            var encoder = new Encoder(1500);
            encoder.Encode(new GSObject(object1));
            var decoder = new Decoder(encoder.GetDataLength(), encoder.DataBuffer);
            GSObject decoded = decoder.Decode();
            sw.Stop();
            Assert.AreEqual(now.ToUnixTimeMilliseconds(), decoded.Object1.Timestamp.ToUnixTimeMilliseconds());
            object1.Dispose();
        }

        public readonly struct TimestampedObject : IMessage
        {
            public ulong ID => id;
            public ushort Short => time;
            public DateTimeOffset Timestamp => TimestampLookup.Get(this);

            private readonly ulong id;
            private readonly ushort time;

            public TimestampedObject(ulong id, DateTimeOffset time)
            {
                this.id = id;
                this.time = time.ToTime1();
                this.SaveTimestamp();
            }

            public TimestampedObject(ulong id, ushort time)
            {
                this.id = id;
                this.time = time;
            }

            public void Dispose() => TimestampLookup.Remove(this);
        }

        [TestMethod]
        public void TestIMessageEquality()
        {
            var a = new TimestampedObject(1, 1234);
            var b = new TimestampedObject(1, 1234);

            var dictionary = new Dictionary<IMessage, byte> {{a, 1}};
            Assert.IsTrue(dictionary.ContainsKey(b));
            Assert.AreEqual(a.ID, dictionary[a]);
            Assert.AreEqual(a.ID, dictionary[b]);

            a.Dispose();
            b.Dispose();
        }

        //[TestMethod]
        //public void TestTimeCacheAfter()
        //{
        //    var now = DateTimeOffset.UtcNow;
        //    var example = new TimestampedObject(1, now);
        //    Thread.Sleep(TimeSpan.FromSeconds(67 * 2));
        //    var diff = Math.Abs((example.Timestamp - now).TotalMilliseconds);
        //    Assert.IsTrue(diff < 1);
        //}

        //[TestMethod]
        //public void TestTimeCacheBefore()
        //{
        //    var now = DateTimeOffset.UtcNow;
        //    var example = new TimestampedObject(1, now);
        //    Thread.Sleep(TimeSpan.FromSeconds(67 * 2));
        //    Assert.IsFalse(Math.Abs((example.Short.ToDateTimeOffset() - now).TotalMilliseconds) < 1);
        //}

        [TestMethod]
        public void EncodeTime()
        {
            Stopwatch sw = new Stopwatch();
            sw.Start();
            for (int i = 0; i < 1000; i++)
            {
                var head1 = new Head1(1, 2, new Loc2(1, 2, 3, 4, 5, 6), new Rot2(1, 2, 3, 4, 5, 6));
                var obj = new GSObject(head1);
                var encoder = new Encoder(1500);
                encoder.Encode(obj);
                encoder.Dispose();
            }

            sw.Stop();
            Console.WriteLine(sw.ElapsedMilliseconds);
        }
    }
}
