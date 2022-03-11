using System;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace gs.sharp.test
{
    [TestClass]
    public class TestUtilities
    {
        [TestMethod]
        public void TestSetThenGet()
        {
            var now = DateTimeOffset.UtcNow.Subtract(TimeSpan.FromSeconds(20));
            var object1 = new Object1(1, now, new Loc1(1, 2, 3), new Rot1(4, 5, 6), new Loc1(7, 8, 9));
            Assert.AreEqual(now.ToUnixTimeMilliseconds(), object1.Timestamp.ToUnixTimeMilliseconds());
        }

        [TestMethod]
        public void TestTimeExtensions()
        {
            var now = DateTimeOffset.UtcNow.Subtract(TimeSpan.FromSeconds(20));
            var sw = new Stopwatch();
            sw.Start();
            var object1 = new Object1(1, now, new Loc1(1, 2, 3), new Rot1(4, 5, 6), new Loc1(7, 8, 9));
            var encoder = new Encoder(1500);
            encoder.Encode(object1);
            var decoder = new Decoder(encoder.GetDataLength(), encoder.DataBuffer);
            (object decoded, Type type)? decoded = decoder.Decode();
            sw.Stop();
            Assert.AreEqual(now.ToUnixTimeMilliseconds(), object1.Timestamp.ToUnixTimeMilliseconds());
        }
    }
}
